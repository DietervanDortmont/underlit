using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;              // TranslateTransform, StreamGeometry, SolidColorBrush
using System.Windows.Media.Effects;       // DropShadowEffect, RenderingBias
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Underlit.Display;
using Underlit.Sys;
using Point = System.Windows.Point;       // disambiguate vs System.Drawing.Point
using Color = System.Windows.Media.Color; // disambiguate vs System.Drawing.Color

namespace Underlit.UI;

/// <summary>
/// Transient OSD matching Windows 11's brightness flyout in size/position/animation.
///
/// Differences from the native flyout worth naming:
///   • The rounded corners are a COMPOUND BEZIER squircle (two cubics per corner,
///     meeting at 45° with a shared tangent direction) — continuous curvature, iOS-ish.
///   • The brightness bar is BIPOLAR. A tall mid-marker divides it. Right-of-center
///     fills with the "positive" colour (Windows' native brightness range). Left-of-center
///     fills with a distinct amber for Underlit's extended sub-zero range, so you can see
///     at a glance whether you're in or past Windows' own range.
///   • The warmth bar has a grey track; the filled portion is a gradient from the 1500 K
///     colour up to the CURRENT Kelvin colour. So as you warm up/cool down, the colour
///     AT the bar's leading edge matches the actual on-screen warmth.
///   • The whole flyout repaints when Windows' dark/light theme toggles.
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
    /// <summary>
    /// DIPs the flyout slides on entry/exit. The visible Path sits at window-Y 63..107
    /// (Flyout top at 20, +vertical centering 43 inside the 130-tall Flyout Grid).
    /// With slide 96 the Path's bottom reaches window-Y 203 but the window ends at 180,
    /// so ~23 DIPs of the flyout clip against the window edge — which visually reads
    /// as "sliding behind the taskbar" (window bottom is pinned to the taskbar top).
    /// </summary>
    private const double SlideDistance = 96;


    // ---- Theme palettes (colors picked to match Windows 11's native flyout) ----
    private sealed record Palette(
        Color Background, Color Border,
        Color Track,
        Color FillPositive, Color FillNegative,
        Color MidMarker, Color Icon,
        Color WarmthStart, Color WarmthEnd,
        double ShadowOpacity);

    private static readonly Palette Dark = new(
        // Windows 11 dark flyout: ~#202020 with subtle translucency (Mica-like).
        Background:   Color.FromArgb(0xF2, 0x20, 0x20, 0x20),
        Border:       Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF),
        Track:        Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF),
        // Windows dark-mode accent uses the "Light 2" variant of the system accent —
        // default is a cyan-blue ~#60CDFF. Looks brighter/cooler than light-mode accent.
        FillPositive: Color.FromRgb(0x60, 0xCD, 0xFF),
        // Warm amber for the sub-zero extended-dim range — distinct from the accent above.
        FillNegative: Color.FromRgb(0xE5, 0xB6, 0x75),
        MidMarker:    Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF),
        Icon:         Color.FromRgb(0xFF, 0xFF, 0xFF),
        WarmthStart:  Color.FromRgb(0xFF, 0x5A, 0x00),   // 1500 K
        WarmthEnd:    Color.FromRgb(0xFF, 0xF9, 0xFB),   // 6500 K near-white
        ShadowOpacity: 0.60
    );

    private static readonly Palette Light = new(
        // Windows 11 light flyout: ~#F3F3F3 off-white (Mica-like).
        Background:   Color.FromArgb(0xF2, 0xF3, 0xF3, 0xF3),
        Border:       Color.FromArgb(0x1F, 0x00, 0x00, 0x00),
        Track:        Color.FromArgb(0x1A, 0x00, 0x00, 0x00),
        // Windows light-mode accent for fills is the darker variant (~#005FB8 for default blue).
        FillPositive: Color.FromRgb(0x00, 0x5F, 0xB8),
        FillNegative: Color.FromRgb(0xB0, 0x6B, 0x17),
        MidMarker:    Color.FromArgb(0x66, 0x00, 0x00, 0x00),
        Icon:         Color.FromRgb(0x1F, 0x1F, 0x1F),
        WarmthStart:  Color.FromRgb(0xFF, 0x5A, 0x00),
        // Cool blue at the top of the spectrum so the gradient is visible on a light background.
        WarmthEnd:    Color.FromRgb(0x6B, 0xB0, 0xE0),
        ShadowOpacity: 0.30
    );

    private Palette _palette = Dark;
    private DropShadowEffect? _shadow;

    private readonly Action<bool> _themeHandler;

    public OsdWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => ApplyTheme(ThemeInfo.IsDarkMode() ? Dark : Light);

        _themeHandler = isDark => Dispatcher.BeginInvoke(() => ApplyTheme(isDark ? Dark : Light));
        ThemeInfo.ThemeChanged += _themeHandler;
        Closed += (_, _) => ThemeInfo.ThemeChanged -= _themeHandler;
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

        Left = waLeftDip + (waWidthDip  - Width)  / 2;
        Top  = waTopDip  +  waHeightDip - Height;
    }

    // ---- Public API ----

    public void ShowBrightness(double levelSigned)
    {
        SetMode(Mode.Brightness);
        IconText.Text = "\uE706"; // sun
        UpdateBrightnessBar(levelSigned);
        Flash();
    }

    public void ShowWarmth(int kelvin)
    {
        SetMode(Mode.Warmth);
        // Candle icon replaces the Segoe glyph.
        UpdateWarmthBar(kelvin);
        Flash();
    }

    public void ShowPaused(bool paused)
    {
        SetMode(Mode.Brightness);
        IconText.Text = paused ? "\uE769" : "\uE768"; // pause / play
        UpdateBrightnessBar(paused ? 0 : 100);
        Flash();
    }

    // ---- Mode switching ----

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

        // Gradient start is fixed at 1500 K (palette.WarmthStart).
        // Gradient end follows the current Kelvin — but clamped to the palette's theme-appropriate
        // "cool" color so the top of the spectrum stays visible in light mode.
        Color endColor = BlendKelvin(kelvin);
        WarmthEndStop.Color = endColor;
    }

    /// <summary>
    /// Returns the visible colour to use at a given Kelvin in the current theme.
    /// In dark mode we use the actual blackbody colour (near-white at 6500 K).
    /// In light mode we blend toward the palette's cool end to keep contrast.
    /// </summary>
    private Color BlendKelvin(int kelvin)
    {
        var (r, g, b) = GammaRampApplier.KelvinToRgbMultipliers(kelvin);
        Color actual = Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));

        if (_palette == Light)
        {
            // Interpolate between WarmthStart at 1500 K and WarmthEnd at 6500 K.
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
        BackgroundBorder.Background  = new SolidColorBrush(p.Background);
        BackgroundBorder.BorderBrush = new SolidColorBrush(p.Border);
        TrackLeft.Background   = new SolidColorBrush(p.Track);
        TrackRight.Background  = new SolidColorBrush(p.Track);
        FillLeft.Background    = new SolidColorBrush(p.FillNegative);
        FillRight.Background   = new SolidColorBrush(p.FillPositive);
        MidMarker.Background   = new SolidColorBrush(p.MidMarker);
        IconText.Foreground    = new SolidColorBrush(p.Icon);
        CandleIcon.Fill        = new SolidColorBrush(p.Icon);
        WarmthTrack.Background = new SolidColorBrush(p.Track);
        WarmthStartStop.Color  = p.WarmthStart;
        // WarmthEndStop.Color is set dynamically in UpdateWarmthBar.

        // Drop shadow: soft long-blur, more opaque than before so it's clearly visible
        // (Windows' native flyout shadow is fairly prominent). Theme-tuned opacity.
        if (_shadow == null)
        {
            _shadow = new DropShadowEffect
            {
                BlurRadius = 36,
                ShadowDepth = 5,
                Direction = 270,
                Color = Colors.Black,
                RenderingBias = RenderingBias.Performance
            };
            BackgroundBorder.Effect = _shadow;
        }
        _shadow.Opacity = p.ShadowOpacity;
    }

    // ---- Animation ----

    private void Flash()
    {
        PositionAboveTaskbar();
        _hideTimer?.Stop();

        if (!IsVisible) Show();

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
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, slideIn);

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
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, slideOut);
    }
}
