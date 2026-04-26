using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Underlit.Display;
using Underlit.Settings;
using Underlit.Sys;
using Color = System.Windows.Media.Color;

namespace Underlit.UI;

/// <summary>
/// OSD flyout for brightness / warmth.
///
/// Backdrop modes (chosen via settings):
///   • Solid       — opaque tinted Border. Theme-aware (dark/light).
///   • Subtle      — DWM transient acrylic. Live blur on Win11 22H2+. Theme-aware tint.
///   • LiquidGlass — REAL glass. We BitBlt the screen pixels currently behind the OSD's
///                   location, run a 3-pass box blur + edge refraction shader on them
///                   in C#, and use the result as our actual backdrop. On top of that
///                   we layer the existing specular / sheen / edge-ring overlays for
///                   the "wet rim" highlight Apple's Liquid Glass shows. Theme-NEUTRAL.
///
/// Architecture notes:
///   • The window is exactly the visible flyout's footprint (280×48). DWM applies the
///     backdrop only to that area — no "huge frame" of blur around it.
///   • AllowsTransparency=False so DWM compositing works; rounded corners come from
///     DWMWA_WINDOW_CORNER_PREFERENCE; soft shadow from DWM itself.
///   • Slide animation is internal (TranslateTransform on a content layer) — moving
///     Window.Top would cause DWM to re-render the backdrop on every frame which the
///     user reported as laggy. Instead we Show/Hide and fade Opacity with a tiny
///     internal slide of the bar contents, while the window itself stays put.
/// </summary>
public partial class OsdWindow : Window
{
    public IntPtr Hwnd { get; private set; }
    public HwndSource? Source { get; private set; }

    private enum Mode { Brightness, Warmth }
    private Mode _mode = Mode.Brightness;

    private DispatcherTimer? _hideTimer;
    private const int ShowDurationMs = 1300;
    private const int EntryMs = 220;
    private const int ExitMs  = 170;
    private const double SlideDistance = 8;       // small internal bar slide on entry/exit
    private const double BottomMarginDip = 60;    // distance from taskbar to flyout bottom

    // ---- Theme palettes ----
    private sealed record ThemeTints(
        Color SolidBg,
        Color AcrylicTint,
        Color Border,
        Color Track,
        Color FillNegative,
        Color MidMarker,
        Color Icon,
        Color WarmthStart,
        Color WarmthEnd);

    private static readonly ThemeTints Dark = new(
        SolidBg:      Color.FromArgb(0xF5, 0x20, 0x20, 0x20),
        AcrylicTint:  Color.FromArgb(0x66, 0x20, 0x20, 0x20),
        Border:       Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF),
        Track:        Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF),
        FillNegative: Color.FromRgb(0xE5, 0xB6, 0x75),
        MidMarker:    Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF),
        Icon:         Color.FromRgb(0xFF, 0xFF, 0xFF),
        WarmthStart:  Color.FromRgb(0xFF, 0x5A, 0x00),
        WarmthEnd:    Color.FromRgb(0xFF, 0xF9, 0xFB)
    );

    private static readonly ThemeTints Light = new(
        SolidBg:      Color.FromArgb(0xF5, 0xF3, 0xF3, 0xF3),
        AcrylicTint:  Color.FromArgb(0x66, 0xF3, 0xF3, 0xF3),
        Border:       Color.FromArgb(0x1F, 0x00, 0x00, 0x00),
        Track:        Color.FromArgb(0x1A, 0x00, 0x00, 0x00),
        FillNegative: Color.FromRgb(0xB0, 0x6B, 0x17),
        MidMarker:    Color.FromArgb(0x66, 0x00, 0x00, 0x00),
        Icon:         Color.FromRgb(0x1F, 0x1F, 0x1F),
        WarmthStart:  Color.FromRgb(0xFF, 0x5A, 0x00),
        WarmthEnd:    Color.FromRgb(0x6B, 0xB0, 0xE0)
    );

    /// <summary>
    /// Liquid Glass values are theme-neutral. The tint is a faint white-ish wash so
    /// the live blur dominates; text/icons stay light because that's how iOS glass
    /// always looks (white symbols on a translucent pane).
    /// </summary>
    private static readonly ThemeTints Glass = new(
        SolidBg:      Color.FromArgb(0xC0, 0x80, 0x80, 0x88),    // unused in Glass mode
        AcrylicTint:  Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF),    // very faint white wash
        Border:       Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF),
        Track:        Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF),
        FillNegative: Color.FromRgb(0xFF, 0xC9, 0x88),
        MidMarker:    Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF),
        Icon:         Color.FromRgb(0xFF, 0xFF, 0xFF),
        WarmthStart:  Color.FromRgb(0xFF, 0x5A, 0x00),
        WarmthEnd:    Color.FromRgb(0xFF, 0xF9, 0xFB)
    );

    private bool _darkMode = true;
    private ThemeTints _palette = Dark;

    // ---- Visual settings (set by host) ----
    private bool _followWindowsAccent = true;
    private Color? _customAccent;
    private bool _useTransparency = true;
    private BackdropStyle _backdrop = BackdropStyle.Subtle;

    // ---- Liquid Glass live engine ----
    // Created lazily after the window is first shown (we need an HWND to set
    // exclude-from-capture). Active only while OSD is visible in LiquidGlass mode.
    private LiveGlassController? _liveGlass;

    private double _restTop;

    private readonly Action<bool> _themeHandler;
    private readonly Action<Color> _accentHandler;
    private readonly Action<bool> _transparencyHandler;

    public OsdWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) =>
        {
            _darkMode = ThemeInfo.IsDarkMode();
            ApplyAll();
        };

        _themeHandler = isDark => Dispatcher.BeginInvoke(() =>
        {
            _darkMode = isDark;
            ApplyAll();
        });
        _accentHandler = _ => Dispatcher.BeginInvoke(() =>
        {
            if (_followWindowsAccent) ApplyVisuals();
        });
        _transparencyHandler = _ => Dispatcher.BeginInvoke(ApplyAll);

        ThemeInfo.ThemeChanged += _themeHandler;
        AccentColorReader.AccentChanged += _accentHandler;
        TransparencyPreference.Changed += _transparencyHandler;
        Closed += (_, _) =>
        {
            ThemeInfo.ThemeChanged -= _themeHandler;
            AccentColorReader.AccentChanged -= _accentHandler;
            TransparencyPreference.Changed -= _transparencyHandler;
            _liveGlass?.Dispose();
            _liveGlass = null;
        };
    }

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

        if (IsLoaded) ApplyAll();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        Hwnd = new WindowInteropHelper(this).Handle;
        Source = HwndSource.FromHwnd(Hwnd);

        // Note: we deliberately do NOT set WS_EX_TRANSPARENT here. That style suppresses
        // hit-testing AND can interfere with DWM compositing for the backdrop. We use
        // IsHitTestVisible="False" on the WPF side to make the OSD click-through.
        int ex = NativeMethods.GetWindowLong(Hwnd, NativeMethods.GWL_EXSTYLE);
        ex |= NativeMethods.WS_EX_TOOLWINDOW
            | NativeMethods.WS_EX_NOACTIVATE
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

        Left = waLeftDip + (waWidthDip - Width) / 2;
        _restTop = waTopDip + waHeightDip - Height - BottomMarginDip;
        Top = _restTop;
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

    private Color CurrentAccent()
    {
        Color baseAccent = _customAccent ?? AccentColorReader.GetAccentColor();
        // Lighten in dark mode (Windows' "Light 2" variant). In light, leave as-is.
        if (_darkMode || _backdrop == BackdropStyle.LiquidGlass)
            return Lerp(baseAccent, Color.FromRgb(255, 255, 255), 0.30);
        return baseAccent;
    }

    // ---- Theme + backdrop application ----

    /// <summary>Re-apply EVERYTHING — palette, brushes, DWM backdrop. Idempotent.</summary>
    private void ApplyAll()
    {
        _palette = _backdrop switch
        {
            BackdropStyle.LiquidGlass => Glass,
            _                          => _darkMode ? Dark : Light,
        };
        ApplyVisuals();
        ApplyBackdrop();
    }

    /// <summary>Updates brushes and visual layers. No DWM API calls.</summary>
    private void ApplyVisuals()
    {
        var p = _palette;

        bool useBlur = _useTransparency && _backdrop != BackdropStyle.Solid;
        bool isGlass = _backdrop == BackdropStyle.LiquidGlass;

        // Tint layer colour:
        Color tint = _backdrop switch
        {
            BackdropStyle.Solid       => p.SolidBg,
            BackdropStyle.Subtle      => useBlur ? p.AcrylicTint : p.SolidBg,
            BackdropStyle.LiquidGlass => p.AcrylicTint,    // already glass-light
            _                         => p.SolidBg,
        };
        TintLayer.Background = new SolidColorBrush(tint);

        // Edge ring: thin and theme-coloured in Solid/Subtle; thicker/brighter
        // in Liquid Glass to give a defined "rim of light" like a real glass edge.
        if (isGlass)
        {
            EdgeRing.BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
            EdgeRing.BorderThickness = new Thickness(1);
        }
        else
        {
            EdgeRing.BorderBrush = new SolidColorBrush(p.Border);
            EdgeRing.BorderThickness = new Thickness(1);
        }

        // Glass overlays show only in LiquidGlass mode.
        GlassSpecular.Visibility       = isGlass ? Visibility.Visible : Visibility.Collapsed;
        GlassSideRefraction.Visibility = isGlass ? Visibility.Visible : Visibility.Collapsed;
        GlassBottomSheen.Visibility    = isGlass ? Visibility.Visible : Visibility.Collapsed;

        // Captured-and-blurred backdrop is only used in Liquid Glass mode. RefreshNow()
        // re-asserts brush.ImageSource on every call, so it's safe to null it here on a
        // mode flip — the next Flash() will repaint cleanly.
        if (!isGlass)
        {
            GlassBackdrop.Visibility = Visibility.Collapsed;
            GlassBackdropBrush.ImageSource = null;
        }

        // Bar elements
        TrackLeft.Background   = new SolidColorBrush(p.Track);
        TrackRight.Background  = new SolidColorBrush(p.Track);
        FillLeft.Background    = new SolidColorBrush(p.FillNegative);
        FillRight.Background   = new SolidColorBrush(CurrentAccent());
        MidMarker.Background   = new SolidColorBrush(p.MidMarker);
        IconText.Foreground    = new SolidColorBrush(p.Icon);
        CandleIcon.Fill        = new SolidColorBrush(p.Icon);
        WarmthTrack.Background = new SolidColorBrush(p.Track);
        WarmthStartStop.Color  = p.WarmthStart;
    }

    /// <summary>Applies (or removes) the DWM backdrop.</summary>
    private void ApplyBackdrop()
    {
        if (Hwnd == IntPtr.Zero) return;
        // For Solid mode: no backdrop. For Subtle/LiquidGlass with transparency on: acrylic.
        bool useBackdrop = _useTransparency && _backdrop != BackdropStyle.Solid;
        var kind = useBackdrop ? Acrylic.Backdrop.Acrylic : Acrylic.Backdrop.None;
        // For LiquidGlass, dark-immersive isn't really right (glass is theme-neutral),
        // but DWM treats this attribute as just "dark borders" — pass _darkMode anyway.
        Acrylic.Apply(Hwnd, kind, _darkMode);
    }

    // ---- Liquid Glass capture ----

    /// <summary>
    /// Capture the screen pixels behind us NOW, run them through GlassRenderer, and
    /// paint into GlassBackdropBrush. Call before Show() so the OSD window isn't in
    /// the capture (we don't have a flicker-free "exclude from capture" mechanism on
    /// Windows yet — see LiveGlassController docstring).
    /// </summary>
    private void RefreshGlass()
    {
        if (_liveGlass == null)
            _liveGlass = new LiveGlassController(this, GlassBackdropBrush);
        GlassBackdrop.Visibility = Visibility.Visible;
        _liveGlass.RefreshNow();
    }

    // ---- Animation ----

    private void Flash()
    {
        PositionAboveTaskbar();
        _hideTimer?.Stop();

        if (!IsVisible)
        {
            // Capture the screen pixels behind our about-to-show position BEFORE Show()
            // so we don't include ourselves in the BitBlt. The capture is frozen during
            // the 1.3s display, but each new press starts fresh — same model Apple uses
            // for transient flyouts on iOS lock-screen Control Center.
            if (_backdrop == BackdropStyle.LiquidGlass && _useTransparency)
                RefreshGlass();

            // Initial states for the in-animation
            Opacity = 0;
            BarSlideTransform.Y = SlideDistance;
            Show();
        }

        // Fade in. Slight bar slide for a touch of motion. Window itself stays put —
        // moving Window.Top causes DWM to re-render acrylic each frame, which the user
        // reported as laggy.
        var fadeIn = new DoubleAnimation
        {
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(EntryMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        var slideIn = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(EntryMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(OpacityProperty, fadeIn);
        BarSlideTransform.BeginAnimation(TranslateTransform.YProperty, slideIn);

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
        var fadeOut = new DoubleAnimation
        {
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(ExitMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        var slideOut = new DoubleAnimation
        {
            To = SlideDistance,
            Duration = TimeSpan.FromMilliseconds(ExitMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += (_, _) =>
        {
            if (Opacity <= 0.01) Hide();
        };
        BeginAnimation(OpacityProperty, fadeOut);
        BarSlideTransform.BeginAnimation(TranslateTransform.YProperty, slideOut);
    }
}
