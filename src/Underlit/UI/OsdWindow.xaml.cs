using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Underlit.Display;
using Underlit.Settings;
using Underlit.Sys;
using Color = System.Windows.Media.Color; // disambiguate vs System.Drawing.Color

namespace Underlit.UI;

/// <summary>
/// OSD flyout for brightness / warmth. Modeled on the Windows 11 quick-change flyout.
///
/// Architecture (Win11 22H2+ path):
///   • Window is exactly the visible flyout's footprint — no transparent margin
///     around it — so the DWM acrylic backdrop fills only the menu's area.
///   • AllowsTransparency is FALSE so the modern DWM backdrop API works.
///   • Rounded corners + soft shadow are provided by DWM (DWMWA_WINDOW_CORNER_PREFERENCE
///     and the natural DWM window shadow).
///   • Slide animation moves Window.Top (entire window slides), not an internal transform —
///     this lets DWM keep the backdrop sampling live as the window moves.
///
/// Older Windows fall back to legacy ACCENT_ENABLE_ACRYLICBLURBEHIND (cached blur).
///
/// Three backdrop styles (chosen via settings):
///   • None         — opaque tinted background
///   • Acrylic      — DWM transient acrylic (live blur)
///   • LiquidGlass  — Acrylic + a top-edge specular highlight + lower tint alpha
/// </summary>
public partial class OsdWindow : Window
{
    public IntPtr Hwnd { get; private set; }
    public HwndSource? Source { get; private set; }

    private enum Mode { Brightness, Warmth }
    private Mode _mode = Mode.Brightness;

    private DispatcherTimer? _hideTimer;
    private const int ShowDurationMs = 1300;
    private const int EntryMs = 240;
    private const int ExitMs  = 180;
    /// <summary>How many DIPs the WHOLE WINDOW slides on entry/exit.</summary>
    private const double SlideDistance = 28;
    /// <summary>Distance from taskbar top to flyout's bottom edge at rest.</summary>
    private const double BottomMarginDip = 60;

    // ---- Theme palettes ----
    private sealed record Palette(
        Color Background,
        Color BackgroundTransparent,
        Color Border,
        Color Track,
        Color FillPositive, Color FillNegative,
        Color MidMarker, Color Icon,
        Color WarmthStart, Color WarmthEnd);

    private static readonly Palette Dark = new(
        Background:            Color.FromArgb(0xF2, 0x20, 0x20, 0x20),  // Off when no backdrop
        BackgroundTransparent: Color.FromArgb(0x66, 0x20, 0x20, 0x20),  // Acrylic — let blur through
        Border:                Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF),
        Track:                 Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF),
        FillPositive:          Color.FromRgb(0x60, 0xCD, 0xFF),
        FillNegative:          Color.FromRgb(0xE5, 0xB6, 0x75),
        MidMarker:             Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF),
        Icon:                  Color.FromRgb(0xFF, 0xFF, 0xFF),
        WarmthStart:           Color.FromRgb(0xFF, 0x5A, 0x00),
        WarmthEnd:             Color.FromRgb(0xFF, 0xF9, 0xFB)
    );

    private static readonly Palette Light = new(
        Background:            Color.FromArgb(0xF2, 0xF3, 0xF3, 0xF3),
        BackgroundTransparent: Color.FromArgb(0x66, 0xF3, 0xF3, 0xF3),
        Border:                Color.FromArgb(0x1F, 0x00, 0x00, 0x00),
        Track:                 Color.FromArgb(0x1A, 0x00, 0x00, 0x00),
        FillPositive:          Color.FromRgb(0x00, 0x5F, 0xB8),
        FillNegative:          Color.FromRgb(0xB0, 0x6B, 0x17),
        MidMarker:             Color.FromArgb(0x66, 0x00, 0x00, 0x00),
        Icon:                  Color.FromRgb(0x1F, 0x1F, 0x1F),
        WarmthStart:           Color.FromRgb(0xFF, 0x5A, 0x00),
        WarmthEnd:             Color.FromRgb(0x6B, 0xB0, 0xE0)
    );

    private Palette _palette = Dark;

    // ---- Visual settings (set by host) ----
    private bool _followWindowsAccent = true;
    private Color? _customAccent;
    private bool _useTransparency = true;
    private BackdropStyle _backdrop = BackdropStyle.Acrylic;

    // ---- Window position state ----
    private double _restTop;  // computed by PositionAboveTaskbar — the rest Y for animation

    // ---- Event subscriptions ----
    private readonly Action<bool> _themeHandler;
    private readonly Action<Color> _accentHandler;
    private readonly Action<bool> _transparencyHandler;

    public OsdWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) =>
        {
            ApplyTheme(ThemeInfo.IsDarkMode() ? Dark : Light);
            ApplyBackdrop();
        };

        _themeHandler = isDark => Dispatcher.BeginInvoke(() =>
        {
            ApplyTheme(isDark ? Dark : Light);
            ApplyBackdrop();
        });
        _accentHandler = _ => Dispatcher.BeginInvoke(() =>
        {
            if (_followWindowsAccent) ApplyTheme(_palette);
        });
        _transparencyHandler = _ => Dispatcher.BeginInvoke(ApplyBackdrop);

        ThemeInfo.ThemeChanged += _themeHandler;
        AccentColorReader.AccentChanged += _accentHandler;
        TransparencyPreference.Changed += _transparencyHandler;
        Closed += (_, _) =>
        {
            ThemeInfo.ThemeChanged -= _themeHandler;
            AccentColorReader.AccentChanged -= _accentHandler;
            TransparencyPreference.Changed -= _transparencyHandler;
        };
    }

    /// <summary>Called by the host whenever visual settings change.</summary>
    public void UpdateVisualSettings(bool followWindowsAccent, Color? customAccent,
                                     TransparencyMode transparencyMode, BackdropStyle backdrop)
    {
        _followWindowsAccent = followWindowsAccent;
        _customAccent = followWindowsAccent ? null : customAccent;

        _useTransparency = transparencyMode switch
        {
            TransparencyMode.On  => true,
            TransparencyMode.Off => false,
            _                    => TransparencyPreference.IsEnabled(),
        };

        _backdrop = backdrop;

        if (IsLoaded)
        {
            ApplyTheme(_palette);
            ApplyBackdrop();
        }
    }

    private Color CurrentAccent()
    {
        Color baseAccent = _customAccent ?? AccentColorReader.GetAccentColor();
        if (_palette == Dark) return Lerp(baseAccent, Color.FromRgb(255, 255, 255), 0.30);
        return baseAccent;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        Hwnd = new WindowInteropHelper(this).Handle;
        Source = HwndSource.FromHwnd(Hwnd);

        int ex = NativeMethods.GetWindowLong(Hwnd, NativeMethods.GWL_EXSTYLE);
        ex |= NativeMethods.WS_EX_TOOLWINDOW
            | NativeMethods.WS_EX_NOACTIVATE
            | NativeMethods.WS_EX_TRANSPARENT
            | NativeMethods.WS_EX_TOPMOST;
        NativeMethods.SetWindowLong(Hwnd, NativeMethods.GWL_EXSTYLE, ex);
    }

    private void PositionAboveTaskbar()
    {
        var primary = System.Windows.Forms.Screen.PrimaryScreen;
        if (primary == null) return;

        var src = PresentationSource.FromVisual(this);
        double scale = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

        double waLeftDip   = primary.WorkingArea.Left   / scale;
        double waTopDip    = primary.WorkingArea.Top    / scale;
        double waWidthDip  = primary.WorkingArea.Width  / scale;
        double waHeightDip = primary.WorkingArea.Height / scale;

        // Centered horizontally; resting position has flyout's bottom BottomMarginDip
        // above the taskbar.
        Left = waLeftDip + (waWidthDip - Width) / 2;
        _restTop = waTopDip + waHeightDip - Height - BottomMarginDip;
    }

    // ---- Public API ----

    public void ShowBrightness(double levelSigned)
    {
        SetMode(Mode.Brightness);
        IconText.Text = "\uE706";
        UpdateBrightnessBar(levelSigned);
        Flash();
    }

    public void ShowWarmth(int kelvin)
    {
        SetMode(Mode.Warmth);
        UpdateWarmthBar(kelvin);
        Flash();
    }

    public void ShowPaused(bool paused)
    {
        SetMode(Mode.Brightness);
        IconText.Text = paused ? "\uE769" : "\uE768";
        UpdateBrightnessBar(paused ? 0 : 100);
        Flash();
    }

    private void SetMode(Mode m)
    {
        _mode = m;
        bool brightness = m == Mode.Brightness;
        BrightnessBar.Visibility = brightness ? Visibility.Visible : Visibility.Collapsed;
        WarmthBar.Visibility     = brightness ? Visibility.Collapsed : Visibility.Visible;
        IconText.Visibility      = brightness ? Visibility.Visible : Visibility.Collapsed;
        CandleIcon.Visibility    = brightness ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- Bar drawing ----

    private void UpdateBrightnessBar(double levelSigned)
    {
        double total = Math.Max(0, BarRoot.ActualWidth);
        if (total <= 0)
        {
            Dispatcher.BeginInvoke(() => UpdateBrightnessBar(levelSigned), DispatcherPriority.Loaded);
            return;
        }

        double half = total / 2.0;
        double clamped = Math.Clamp(levelSigned, -100, 100);

        if (clamped >= 0)
        {
            FillRight.Width = clamped / 100.0 * half;
            FillLeft.Width  = 0;
        }
        else
        {
            FillLeft.Width  = -clamped / 100.0 * half;
            FillRight.Width = 0;
        }
    }

    private void UpdateWarmthBar(int kelvin)
    {
        double total = Math.Max(0, BarRoot.ActualWidth);
        if (total <= 0)
        {
            Dispatcher.BeginInvoke(() => UpdateWarmthBar(kelvin), DispatcherPriority.Loaded);
            return;
        }

        double f = Math.Clamp((kelvin - 1500) / 5000.0, 0, 1);
        WarmthFill.Width = f * total;
        WarmthEndStop.Color = BlendKelvin(kelvin);
    }

    private Color BlendKelvin(int kelvin)
    {
        var (r, g, b) = GammaRampApplier.KelvinToRgbMultipliers(kelvin);
        Color actual = Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        if (_palette == Light)
        {
            double f = Math.Clamp((kelvin - 1500) / 5000.0, 0, 1);
            return Lerp(_palette.WarmthStart, _palette.WarmthEnd, f);
        }
        return actual;
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        byte L(byte x, byte y) => (byte)(x + (y - x) * t);
        return Color.FromArgb(L(a.A, b.A), L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }

    // ---- Theme application ----

    private void ApplyTheme(Palette p)
    {
        _palette = p;

        // Pick the right Background tint for the current backdrop style.
        // With Acrylic / LiquidGlass we use a low-alpha tint so the live blur shows.
        // With None we use the nominal opaque colour.
        bool transparentBg = _useTransparency && _backdrop != BackdropStyle.None;
        Color bg = transparentBg ? p.BackgroundTransparent : p.Background;

        // LiquidGlass uses an even more transparent base so the highlight reads.
        if (transparentBg && _backdrop == BackdropStyle.LiquidGlass)
        {
            bg = Color.FromArgb(0x40, p.Background.R, p.Background.G, p.Background.B);
        }

        BackgroundBorder.Background  = new SolidColorBrush(bg);
        BackgroundBorder.BorderBrush = new SolidColorBrush(p.Border);
        TrackLeft.Background   = new SolidColorBrush(p.Track);
        TrackRight.Background  = new SolidColorBrush(p.Track);
        FillLeft.Background    = new SolidColorBrush(p.FillNegative);
        FillRight.Background   = new SolidColorBrush(CurrentAccent());
        MidMarker.Background   = new SolidColorBrush(p.MidMarker);
        IconText.Foreground    = new SolidColorBrush(p.Icon);
        CandleIcon.Fill        = new SolidColorBrush(p.Icon);
        WarmthTrack.Background = new SolidColorBrush(p.Track);
        WarmthStartStop.Color  = p.WarmthStart;

        // Show/hide the LiquidGlass specular highlight.
        GlassHighlight.Visibility = (transparentBg && _backdrop == BackdropStyle.LiquidGlass)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Apply the OS-level backdrop based on current settings.</summary>
    private void ApplyBackdrop()
    {
        if (Hwnd == IntPtr.Zero) return;

        bool wantBackdrop = _useTransparency && _backdrop != BackdropStyle.None;
        if (wantBackdrop)
        {
            Acrylic.EnableLiveAcrylic(Hwnd, _palette == Dark);
        }
        else
        {
            Acrylic.Disable(Hwnd);
        }
    }

    // ---- Animation ----

    private void Flash()
    {
        PositionAboveTaskbar();
        _hideTimer?.Stop();

        if (!IsVisible)
        {
            // Start position: a bit below rest, fully transparent.
            Top = _restTop + SlideDistance;
            Opacity = 0;
            Show();
        }

        // Animate Window.Top (entire window slides) and Opacity in parallel.
        var slideIn = new DoubleAnimation
        {
            To = _restTop,
            Duration = TimeSpan.FromMilliseconds(EntryMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        var fadeIn = new DoubleAnimation
        {
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(EntryMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(TopProperty, slideIn);
        BeginAnimation(OpacityProperty, fadeIn);

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ShowDurationMs) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer?.Stop();
            StartExitAnimation();
        };
        _hideTimer.Start();
    }

    private void StartExitAnimation()
    {
        var slideOut = new DoubleAnimation
        {
            To = _restTop + SlideDistance,
            Duration = TimeSpan.FromMilliseconds(ExitMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        var fadeOut = new DoubleAnimation
        {
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(ExitMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += (_, _) =>
        {
            if (Opacity <= 0.01) Hide();
        };
        BeginAnimation(TopProperty, slideOut);
        BeginAnimation(OpacityProperty, fadeOut);
    }
}
