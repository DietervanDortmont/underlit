using System;
using System.Windows;
using System.Windows.Input;
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

    // ---- Dynamic-Island entry animation tunables ----
    // The pill morphs from a circle (height×height, fully rounded) into the full
    // pill shape via animating WindowRoot.Clip's RectangleGeometry — this gives a
    // TRUE shape morph with the corners staying perfectly rounded throughout
    // (a ScaleTransform would distort the corner radii in screen space).
    // Combined with a Y-translate slide and a back-ease (overshoot) we get the
    // spring/bounce feel.
    private const int    EntryDurationMs = 460;
    private const int    FadeInDurationMs = 240;
    private const int    ExitDurationMs   = 220;
    /// <summary>
    /// Initial Y-offset for the entry slide, in dip. The seed circle is 46 dip
    /// tall, sits at local y=10–56, and the window is 66 dip tall. With slide=44
    /// the circle is rendered at y=54–100 → only the top 12 dip is visible at
    /// frame 0, sitting right above the taskbar (BottomMarginDip is 0). That's
    /// the "circle peeking up from behind the taskbar" moment. Animation then
    /// pulls slide to 0, with a BackEase overshoot of about 18 dip past rest
    /// (44 × 0.4) for the bouncy spring.
    ///
    /// Push higher (max ≈ 56) to bury the circle deeper at frame 0; lower to
    /// reveal more of it. Beyond ~50 nothing is visible at the start, so the
    /// "rise" effect disappears.
    /// </summary>
    private const double SlideFromBelowDip = 44;
    private const double EntryBackAmplitude = 0.4; // BackEase amplitude — overshoot strength
    /// <summary>
    /// Gap between the WPF window's bottom edge and the working area's bottom
    /// (top of the taskbar). The visible pill's bottom sits 10 dip above the
    /// window's bottom (shadow padding inside the bitmap), so the actual gap
    /// from pill bottom to taskbar top is BottomMarginDip + 10. Was 50 → 60 dip
    /// gap, now 0 → 10 dip gap. Keep ≥ 0 to avoid the window overlapping the
    /// taskbar (which can cause the taskbar to render in front of the pill).
    /// </summary>
    private const double BottomMarginDip = 0;

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
    private GlassParams _glass = new();
    /// <summary>
    /// True = WGC live capture (real-time refraction + brief Win11 yellow border).
    /// False = one-shot BitBlt per Show() (frozen for 1.3 s, no yellow border).
    /// </summary>
    private bool _glassLiveCapture = true;
    /// <summary>Which bar style the OSD draws — Bar (thin slider) vs SolidFill (tall pill fill).</summary>
    private OsdBarStyle _barStyle = OsdBarStyle.Bar;
    /// <summary>"auto" or hex string — colour for the dim sub-OS-min half of the brightness bar.</summary>
    private string _brightnessHighColor = "auto";

    // ---- Mouse drag interaction (v0.6.19) ----
    /// <summary>Raised while the user is dragging the OSD's brightness slider with the
    /// mouse. The argument is a signed level in the engine's -100..+100 space.</summary>
    public event Action<double>? BrightnessSetRequested;
    private bool _dragging;
    /// <summary>Most recently shown brightness/warmth values, so a style flip can re-render in place.</summary>
    private double _lastBrightness;
    private int _lastWarmth = 6500;

    // Animated clip + slide for the Dynamic-Island-style entry. Created on first
    // SourceInitialized; animated on every Flash()/StartExitAnimation().
    private RectangleGeometry? _entryClip;
    private TranslateTransform? _entrySlide;

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
            // Apply the SetWindowRgn pill region NOW, after the window has been laid out
            // and DPI is settled. Doing this in OnSourceInitialized was unreliable because
            // CompositionTarget.TransformToDevice could still report 1.0× before the
            // window was placed on its actual monitor.
            ApplyPillRegion();
        };
        DpiChanged += (_, _) => ApplyPillRegion();

        // Mouse-drag brightness adjustment. Bind to Preview* so we beat any
        // descendant — there are no descendants that want the events anyway,
        // but Preview gives us the deterministic root-tunneled handler.
        WindowRoot.PreviewMouseLeftButtonDown += OnPillMouseDown;
        WindowRoot.PreviewMouseMove           += OnPillMouseMove;
        WindowRoot.PreviewMouseLeftButtonUp   += OnPillMouseUp;

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
                                     TransparencyMode transparencyMode, BackdropStyle backdrop,
                                     GlassParams glass, bool glassLiveCapture,
                                     OsdBarStyle barStyle, string brightnessHighColor)
    {
        _barStyle = barStyle;
        _brightnessHighColor = string.IsNullOrWhiteSpace(brightnessHighColor) ? "auto" : brightnessHighColor;
        _followWindowsAccent = followWindowsAccent;
        _customAccent = followWindowsAccent ? null : customAccent;

        _useTransparency = transparencyMode switch
        {
            TransparencyMode.On  => true,
            TransparencyMode.Off => false,
            _                    => TransparencyPreference.IsEnabled(),
        };

        _backdrop = backdrop;
        _glass = glass.Clone();

        // Mode flip handling: if the user just turned live OFF, tear down WGC right
        // away so no yellow border can be triggered on the next Show(). If they turned
        // it ON, the next RefreshGlass() will lazily re-init WGC.
        bool prevLive = _glassLiveCapture;
        _glassLiveCapture = glassLiveCapture;
        if (prevLive && !glassLiveCapture)
        {
            // DisableLive() handles both ticker shutdown AND WGC dispose.
            _liveGlass?.DisableLive();
        }

        if (IsLoaded)
        {
            ApplyAll();
            ApplyPillRegion();   // corner radius may have changed
        }
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

        // CRITICAL: kill DWM's automatic rounded-corner + window-shadow + border
        // treatment. Win11 22H2+ paints these even on layered AllowsTransparency=true
        // windows by default; we don't want any of it.
        Acrylic.DisableSystemRounding(Hwnd);

        // Set up the animated clip + slide that drive the Dynamic-Island-style
        // entry/exit. We seed the clip with the FULL pill rect so the OSD looks
        // normal during the initial Show()/Hide() pair UnderlitHost does at
        // startup, then Flash() overwrites it with the seed circle on each show.
        _entryClip = new RectangleGeometry
        {
            RadiusX = LiveGlassController.PillHeightDip / 2.0,
            RadiusY = LiveGlassController.PillHeightDip / 2.0,
            Rect = FullPillRect(),
        };
        WindowRoot.Clip = _entryClip;

        _entrySlide = new TranslateTransform();
        WindowRoot.RenderTransform = _entrySlide;
    }

    /// <summary>The Rect that, used with the WindowRoot clip, exposes the entire visible
    /// pill — i.e. the OSD at rest. Matches the SetWindowRgn the layout uses.</summary>
    private static Rect FullPillRect() => new Rect(
        LiveGlassController.PaddingDip,
        LiveGlassController.PaddingDip,
        LiveGlassController.PillWidthDip,
        LiveGlassController.PillHeightDip);

    /// <summary>The Rect that makes the clip a centred CIRCLE (height×height with R=H/2).
    /// This is the "seed" the entry animation grows out of.</summary>
    private static Rect SeedCircleRect()
    {
        double d = LiveGlassController.PillHeightDip;
        return new Rect(
            (LiveGlassController.FullWidthDip - d) / 2.0,
            LiveGlassController.PaddingDip,
            d, d);
    }

    /// <summary>
    /// Apply a SetWindowRgn region matching exactly the renderer's drawn shape, so
    /// the OS clips any DWM shadow / border to the same outline. The corner radius
    /// is driven by GlassParams.CornerRadius so the slider in Settings affects both
    /// the visible bitmap AND the OS clip simultaneously.
    /// </summary>
    private void ApplyPillRegion()
    {
        if (Hwnd == IntPtr.Zero) return;

        // VisualTreeHelper.GetDpi queries the OS for the actual DPI of the window's
        // current monitor. More reliable than CompositionTarget.TransformToDevice
        // (which can read 1.0 before the window is placed).
        var dpi = VisualTreeHelper.GetDpi(this);
        double scale = dpi.DpiScaleX;
        if (scale <= 0) scale = 1.0;

        // Use the EXACT same math the renderer uses to compute its pill — otherwise
        // at fractional DPI we get 1-pixel mismatches between OS clip and rendered
        // content. Renderer does: pillW = fullW − 2·padX. Match that here.
        int physFullW = (int)Math.Round(LiveGlassController.FullWidthDip  * scale);
        int physFullH = (int)Math.Round(LiveGlassController.FullHeightDip * scale);
        int physPadX  = (int)Math.Round(LiveGlassController.PaddingDip    * scale);
        int physPadY  = (int)Math.Round(LiveGlassController.PaddingDip    * scale);
        int physPillW = physFullW - 2 * physPadX;
        int physPillH = physFullH - 2 * physPadY;
        int rPx = _glass.CornerRadiusPx(physPillH);

        // CreateRoundRectRgn corners are an ellipse of width=cx, height=cy. For corner
        // radius rPx we want a circle of diameter 2*rPx.
        IntPtr rgn = NativeMethods.CreateRoundRectRgn(
            physPadX,
            physPadY,
            physPadX + physPillW,
            physPadY + physPillH,
            rPx * 2,
            rPx * 2);

        if (rgn != IntPtr.Zero)
        {
            // SetWindowRgn takes ownership of the region — do NOT DeleteObject.
            NativeMethods.SetWindowRgn(Hwnd, rgn, true);
        }
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
        _lastBrightness = levelSigned;
        UpdateBrightnessBar(levelSigned);
        Flash();
    }

    public void ShowWarmth(int kelvin)
    {
        SetMode(Mode.Warmth);
        _lastWarmth = kelvin;
        UpdateWarmthBar(kelvin);
        Flash();
    }

    public void ShowPaused(bool paused)
    {
        SetMode(Mode.Brightness);
        IconText.Text = paused ? "\uE769" : "\uE768";
        _lastBrightness = paused ? 0 : 100;
        UpdateBrightnessBar(_lastBrightness);
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

        // Mirror brightness/warmth visibility onto the solid-fill layer too —
        // it lives outside BrightnessBar/WarmthBar so we have to switch its
        // children explicitly. No-op when the style is Bar (whole layer hidden).
        if (_barStyle == OsdBarStyle.SolidFill)
        {
            SolidFillBrightness.Visibility = brightness ? Visibility.Visible : Visibility.Collapsed;
            SolidFillWarmthBar.Visibility  = brightness ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    // ---- Bar drawing ----

    /// <summary>
    /// Brightness bar update — v0.6.19 model.
    ///
    /// The bar represents a 0..100 indicator that grows from the LEFT edge as the
    /// engine's signed -100..+100 level goes from -100 (extended-dim max) to +100
    /// (hardware max). Mapping: displayValue = (signedLevel + 100) / 2.
    ///
    /// The fill is split into two coloured halves whose widths are independent:
    ///   • FillLeft  — covers 0..50 of the bar. Anchored at the LEFT edge.
    ///                 Width = min(displayValue, 50) / 50 × halfBar.
    ///                 Colour = "low" / dim shade (signals "below Windows native min").
    ///   • FillRight — covers 50..100 of the bar. Anchored at the LEFT edge of the
    ///                 right column.
    ///                 Width = max(displayValue - 50, 0) / 50 × halfBar.
    ///                 Colour = accent (Windows native brightness range).
    /// At exactly 50, FillLeft is full and FillRight is empty — the user sees the
    /// dim shade fill exactly half the bar with a visible (hard) edge at the
    /// transition. As they brighten further, the accent extends rightward.
    /// </summary>
    private void UpdateBrightnessBar(double levelSigned)
    {
        bool solid = _barStyle == OsdBarStyle.SolidFill;
        double total = solid ? Math.Max(0, SolidFillRoot.ActualWidth)
                             : Math.Max(0, BarRoot.ActualWidth);
        if (total <= 0)
        {
            Dispatcher.BeginInvoke(() => UpdateBrightnessBar(levelSigned), DispatcherPriority.Loaded);
            return;
        }

        double half = total / 2.0;
        double clamped = Math.Clamp(levelSigned, -100, 100);
        double display = (clamped + 100.0) / 2.0;            // map -100..+100 → 0..100
        double leftWidth  = Math.Min(display, 50.0) / 50.0 * half;
        double rightWidth = Math.Max(0, display - 50.0) / 50.0 * half;

        if (solid)
        {
            SolidFillNeg.Width = leftWidth;
            SolidFillPos.Width = rightWidth;
        }
        else
        {
            FillLeft.Width  = leftWidth;
            FillRight.Width = rightWidth;
        }
    }

    /// <summary>Resolve the user's "low brightness" colour preference into a Color.
    /// Auto-mode derives from the supplied accent so the gradient still tracks Windows.</summary>
    private Color ResolveBrightnessLowColor(Color accent)
    {
        if (string.IsNullOrWhiteSpace(_brightnessHighColor)
            || _brightnessHighColor.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return AutoDarken(accent);

        if (TryParseHexColor(_brightnessHighColor, out var c)) return c;
        return AutoDarken(accent);
    }

    private static Color AutoDarken(Color c) => Color.FromArgb(
        c.A,
        (byte)(c.R * 0.55),
        (byte)(c.G * 0.55),
        (byte)(c.B * 0.55));

    private static Color LerpColor(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t),
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    private static bool TryParseHexColor(string s, out Color c)
    {
        c = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (s.StartsWith("#")) s = s[1..];
        try
        {
            if (s.Length == 6)
            {
                c = Color.FromRgb(
                    Convert.ToByte(s[..2], 16),
                    Convert.ToByte(s[2..4], 16),
                    Convert.ToByte(s[4..6], 16));
                return true;
            }
            if (s.Length == 8)
            {
                c = Color.FromArgb(
                    Convert.ToByte(s[..2], 16),
                    Convert.ToByte(s[2..4], 16),
                    Convert.ToByte(s[4..6], 16),
                    Convert.ToByte(s[6..8], 16));
                return true;
            }
        }
        catch { }
        return false;
    }

    private void UpdateWarmthBar(int kelvin)
    {
        bool solid = _barStyle == OsdBarStyle.SolidFill;
        double total = solid ? Math.Max(0, SolidFillRoot.ActualWidth)
                             : Math.Max(0, BarRoot.ActualWidth);
        if (total <= 0)
        {
            Dispatcher.BeginInvoke(() => UpdateWarmthBar(kelvin), DispatcherPriority.Loaded);
            return;
        }
        double f = Math.Clamp((kelvin - 1500) / 5000.0, 0, 1);
        var endColor = BlendKelvin(kelvin);

        if (solid)
        {
            SolidFillWarmthBar.Width    = f * total;
            SolidFillWarmthEndStop.Color = endColor;
        }
        else
        {
            WarmthFill.Width    = f * total;
            WarmthEndStop.Color = endColor;
        }
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
        // In LiquidGlass mode the captured image is the body — we don't tint over it,
        // the renderer already handled everything (vibrancy, saturation, frost).
        Color tint = _backdrop switch
        {
            BackdropStyle.Solid       => p.SolidBg,
            BackdropStyle.Subtle      => useBlur ? p.AcrylicTint : p.SolidBg,
            BackdropStyle.LiquidGlass => Color.FromArgb(0, 0, 0, 0),    // transparent
            _                         => p.SolidBg,
        };
        TintLayer.Background = new SolidColorBrush(tint);

        // Edge ring: in Solid/Subtle modes only — gives a thin theme-coloured outline.
        // In Liquid Glass mode the renderer's Fresnel does the rim properly so we
        // suppress this one to avoid stacking two rim treatments.
        if (isGlass)
        {
            EdgeRing.BorderThickness = new Thickness(0);
        }
        else
        {
            EdgeRing.BorderBrush = new SolidColorBrush(p.Border);
            EdgeRing.BorderThickness = new Thickness(1);
        }

        // Old gradient overlays — DEPRECATED in v0.2.4. The renderer now bakes the
        // specular highlight, side refraction, and bottom sheen into the captured
        // image using a proper normal map and directional Phong+Fresnel lighting.
        // We keep the elements so existing layout stays put, but always Collapsed.
        GlassSpecular.Visibility       = Visibility.Collapsed;
        GlassSideRefraction.Visibility = Visibility.Collapsed;
        GlassBottomSheen.Visibility    = Visibility.Collapsed;

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
        // v0.6.19: brightness fill split at the 50% mark.
        //   FillLeft  = dim shade (signals "below Windows native min").
        //   FillRight = accent (Windows native brightness range).
        // Both anchored to the bar's left edge — see UpdateBrightnessBar.
        FillLeft.Background    = new SolidColorBrush(ResolveBrightnessLowColor(CurrentAccent()));
        FillRight.Background   = new SolidColorBrush(CurrentAccent());
        MidMarker.Background   = new SolidColorBrush(p.MidMarker);
        IconText.Foreground    = new SolidColorBrush(p.Icon);
        CandleIcon.Fill        = new SolidColorBrush(p.Icon);
        WarmthTrack.Background = new SolidColorBrush(p.Track);
        WarmthStartStop.Color  = p.WarmthStart;

        // Solid-fill layer brushes — same colour scheme as the thin bar but
        // applied to the full-pill-width fills. Slight alpha reduction so the
        // live glass refraction (or Subtle blur) is still visible underneath.
        // Same dim/accent split as the thin bar, but at 75% alpha so the live
        // glass refraction underneath still shows through.
        SolidFillNeg.Background       = new SolidColorBrush(WithAlpha(ResolveBrightnessLowColor(CurrentAccent()), 0xC0));
        SolidFillPos.Background       = new SolidColorBrush(WithAlpha(CurrentAccent(), 0xC0));
        SolidFillWarmthStartStop.Color = p.WarmthStart;
        // Warmth end stop is set per-frame from BlendKelvin in UpdateWarmthBar.

        ApplyBarStyle();
    }

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    /// <summary>
    /// Switches between two visually distinct indicator widgets:
    ///
    ///   • Bar (default) — the classic thin slider. Lives in BarRoot, which sits
    ///     in the column AFTER the icon. Has a track, a partial fill, and a
    ///     centre mid-marker. Brightness 0 anchors to the BAR's centre (NOT the
    ///     pill's centre — there's an icon to the left).
    ///
    ///   • SolidFill   — full-pill-width fills in SolidFillRoot, sitting BEHIND
    ///     the icon so the icon overlays on top of the colour. Fills are dead-
    ///     rectangular (the pill's SetWindowRgn clip rounds the outer edges for
    ///     us). Brightness 0 anchors to the pill's geometric centre, so a
    ///     negative reading flows leftward across the icon area — like Apple's
    ///     brightness OSD.
    ///
    /// We toggle Visibility on BarRoot and SolidFillRoot here, plus the inner
    /// brightness/warmth children of SolidFillRoot, then re-run the bar-update
    /// methods so widths recompute against the new container's ActualWidth.
    /// </summary>
    private void ApplyBarStyle()
    {
        bool solid = _barStyle == OsdBarStyle.SolidFill;

        // Thin slider widgets (BarRoot's children) — visible only in Bar mode.
        // We hide BarRoot wholesale so it doesn't claim any layout space; in
        // Solid-fill mode the entire indicator lives in SolidFillRoot.
        BarRoot.Visibility = solid ? Visibility.Collapsed : Visibility.Visible;

        // Full-pill-width solid-fill layer — visible only in Solid-fill mode.
        SolidFillRoot.Visibility = solid ? Visibility.Visible : Visibility.Collapsed;

        // Inside SolidFillRoot, route brightness vs warmth visibility based on
        // the current mode. SetMode() also handles this — we mirror its choice
        // here for the case where the style flips while OSD is mid-show.
        if (solid)
        {
            bool brightness = _mode == Mode.Brightness;
            SolidFillBrightness.Visibility = brightness ? Visibility.Visible : Visibility.Collapsed;
            SolidFillWarmthBar.Visibility  = brightness ? Visibility.Collapsed : Visibility.Visible;
        }

        // Re-trigger the width update so the fill width recalculates against the
        // new bar geometry without waiting for the next hotkey press.
        if (_mode == Mode.Brightness) UpdateBrightnessBar(_lastBrightness);
        else                          UpdateWarmthBar(_lastWarmth);
    }

    /// <summary>
    /// Intentionally a no-op in v0.3.3+. The OSD window is AllowsTransparency=true,
    /// which means DWM's modern backdrop API doesn't apply anyway. Calling
    /// Acrylic.Apply here was actively harmful — it called DwmExtendFrameIntoClientArea
    /// and reset DWMWA_WINDOW_CORNER_PREFERENCE to ROUND, painting a 300×66 rounded
    /// rectangle outline behind the pill (the "weird frame" the user kept flagging).
    /// We disable system rounding once in OnSourceInitialized and never touch DWM
    /// attributes for this window again.
    /// </summary>
    private void ApplyBackdrop()
    {
        // No DWM calls. The renderer is the entire visual.
    }

    // ---- Liquid Glass capture ----

    /// <summary>
    /// Bring the glass to its current state. Two paths:
    ///   • LIVE  — kick the WGC ticker so the bitmap updates every ~33 ms while OSD
    ///             is visible (yellow border appears on Win11 22H2/23H2).
    ///   • STATIC — one BitBlt of whatever is behind the about-to-show OSD, frozen
    ///             for the 1.3 s the OSD is up. No border, ever.
    /// Routing is driven by _glassLiveCapture (settable via Settings).
    /// </summary>
    private void RefreshGlass()
    {
        if (_liveGlass == null)
        {
            _liveGlass = new LiveGlassController(this, GlassBackdropBrush);
        }
        GlassBackdrop.Visibility = Visibility.Visible;

        if (_glassLiveCapture)
        {
            // Lazy-init WGC the first time live mode is needed. If WGC fails
            // (older Windows / permissions), TryEnableLive returns false and we
            // fall through to the static refresh — better some glass than none.
            _liveGlass.TryEnableLive(Hwnd);
            _liveGlass.StartLiveTicker(_glass);

            // Belt-and-braces: also do a one-shot static render on first show so
            // the FIRST frame isn't blank while WGC's first FrameArrived flies in.
            _liveGlass.RefreshNow(_glass);
        }
        else
        {
            // STATIC mode — must run BEFORE Show() (which Flash() arranges) so the
            // BitBlt doesn't include the OSD itself.
            _liveGlass.RefreshNow(_glass);
        }

        ApplyAdaptiveIconColor(_liveGlass.LastPillLuminance);
    }

    /// <summary>
    /// "Frosted-glass icon" treatment for LiquidGlass mode (v0.5.1). Instead of a
    /// solid white-or-black icon, we use a TRANSLUCENT tint so the refracted glass
    /// shows through the icon shape — Apple's vibrancy effect. The icon becomes
    /// "the glass body slightly brighter where the icon shape is", not a flat
    /// silhouette overlay.
    ///
    /// We still adapt to backdrop luminance so the icon contrasts on any wallpaper:
    /// bright bg → translucent dark icon, dark bg → translucent bright icon.
    /// </summary>
    private void ApplyAdaptiveIconColor(float lum)
    {
        if (_backdrop != BackdropStyle.LiquidGlass) return;

        double t;
        if (lum <= 0.45f) t = 0.0;
        else if (lum >= 0.65f) t = 1.0;
        else t = (lum - 0.45f) / 0.20f;

        // Channel: 255 (white) on dark, ~30 (near-black) on light.
        byte channel = (byte)Math.Round(255 * (1.0 - t * 0.88));
        // Alpha: ~0xB0 (~70%) so the underlying refracted glass shows through —
        // gives the "icon = brighter region of frosted glass" look from the user's
        // reference shot, instead of a solid silhouette painted on top.
        const byte iconAlpha = 0xC0;   // 75% — frosted-glass translucency
        var iconColor = Color.FromArgb(iconAlpha, channel, channel, channel);

        IconText.Foreground = new SolidColorBrush(iconColor);
        CandleIcon.Fill     = new SolidColorBrush(iconColor);
        MidMarker.Background = new SolidColorBrush(
            Color.FromArgb(0xA8, channel, channel, channel));
    }

    // ---- Mouse drag (brightness slider) ----

    /// <summary>
    /// Mouse-down on the OSD's pill area starts a brightness drag. We use the
    /// pill rectangle (10..290 in WindowRoot dip space — i.e. the bar's full
    /// horizontal extent in SolidFill mode, or the BarRoot column in Bar mode)
    /// to map mouse X → 0..100 displayValue → engine signed level.
    /// </summary>
    private void OnPillMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_mode != Mode.Brightness) return;   // mouse drag only for brightness
        if (!IsBrightnessDragHit(e, out double signed)) return;
        _dragging = true;
        WindowRoot.CaptureMouse();
        ApplyDragLevel(signed);
    }

    private void OnPillMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        if (!IsBrightnessDragHit(e, out double signed)) return;
        ApplyDragLevel(signed);
    }

    /// <summary>Called on every mouse-drag tick: notify host (which writes to the
    /// engine) AND update the local bar visual immediately so the slider tracks
    /// the cursor with no perceptible lag — the engine's gamma/hardware ramp
    /// happens behind the scenes asynchronously.</summary>
    private void ApplyDragLevel(double signed)
    {
        _lastBrightness = signed;
        UpdateBrightnessBar(signed);
        BrightnessSetRequested?.Invoke(signed);
        KeepAliveOsd();
    }

    private void OnPillMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        WindowRoot.ReleaseMouseCapture();
        KeepAliveOsd();
    }

    /// <summary>
    /// Compute the engine-signed brightness from the current mouse position.
    /// Returns false if the mouse is outside the bar's horizontal extent.
    /// In Bar mode the source rect is BarRoot's column; in SolidFill mode it's
    /// SolidFillRoot (the full pill width). Either way the mapping is:
    ///   displayValue = mouseX / barWidth × 100  (0..100)
    ///   signedLevel  = displayValue × 2 − 100   (-100..+100, engine space)
    /// </summary>
    private bool IsBrightnessDragHit(MouseEventArgs e, out double signedLevel)
    {
        signedLevel = 0;
        FrameworkElement? bar = _barStyle == OsdBarStyle.SolidFill
            ? (FrameworkElement)SolidFillRoot
            : BarRoot;
        if (bar == null || bar.ActualWidth <= 0) return false;
        var p = e.GetPosition(bar);
        if (p.X < -8 || p.X > bar.ActualWidth + 8) return false;  // small slack
        double display = Math.Clamp(p.X / bar.ActualWidth, 0, 1) * 100.0;
        signedLevel = display * 2.0 - 100.0;
        return true;
    }

    /// <summary>Restart the auto-hide timer so the OSD stays visible while the user
    /// is interacting. The 1.3 s grace period restarts on every mouse event.</summary>
    private void KeepAliveOsd()
    {
        if (_hideTimer == null) return;
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    // ---- Animation ----

    /// <summary>
    /// Dynamic-Island-style entry animation:
    ///   • The WindowRoot clip morphs from a CIRCLE (height×height, R=H/2) sitting
    ///     in the centre of the window to the FULL pill rect. Because the
    ///     RectangleGeometry has fixed RadiusX/Y = H/2, the corners stay perfectly
    ///     rounded the whole way through — a true shape morph.
    ///   • The pill simultaneously slides up from <see cref="SlideFromBelowDip"/>
    ///     pixels below its rest position, suggesting it's coming up from behind
    ///     the taskbar.
    ///   • Both animations use BackEase EaseOut for the spring/overshoot bounce.
    ///   • A short opacity fade-in runs in parallel so the visible content
    ///     doesn't pop in instantly.
    ///
    /// Using Clip + a render-transform Y means the OSD Window itself doesn't move
    /// (the laggy DWM-rerender path the codebase used to fight). Everything
    /// happens inside the existing 300×66 window, with the clip handling the
    /// visible "shape" and the transform handling the slide.
    /// </summary>
    private void Flash()
    {
        PositionAboveTaskbar();
        _hideTimer?.Stop();

        if (!IsVisible)
        {
            // INITIAL SHOW: run the full Dynamic-Island entry animation.
            // (On re-press while visible we deliberately skip this so the OSD
            // doesn't shrink back to a circle and re-pop on every key tap.)

            if (_backdrop == BackdropStyle.LiquidGlass && _useTransparency)
                RefreshGlass();

            // Cancel any animations leftover from the previous hide cycle.
            _entryClip!.BeginAnimation(RectangleGeometry.RectProperty, null);
            _entrySlide!.BeginAnimation(TranslateTransform.YProperty, null);
            BeginAnimation(OpacityProperty, null);

            // Match the clip's corner radius to the user's chosen pill corner
            // radius so the shape morph hugs the actual pill edges rather than
            // over-rounding when the user has dialled it down.
            double pillR = (LiveGlassController.PillHeightDip / 2.0) * (_glass.CornerRadius / 100.0);
            _entryClip.RadiusX = pillR;
            _entryClip.RadiusY = pillR;

            // Seed the start state, then Show() and animate to rest.
            _entryClip.Rect = SeedCircleRect();
            _entrySlide.Y   = SlideFromBelowDip;
            Opacity         = 0;
            Show();

            var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = EntryBackAmplitude };

            var rectAnim = new RectAnimation
            {
                From = SeedCircleRect(),
                To   = FullPillRect(),
                Duration = TimeSpan.FromMilliseconds(EntryDurationMs),
                EasingFunction = spring,
            };
            var slideAnim = new DoubleAnimation
            {
                From = SlideFromBelowDip,
                To   = 0,
                Duration = TimeSpan.FromMilliseconds(EntryDurationMs),
                EasingFunction = spring,
            };
            var fadeIn = new DoubleAnimation
            {
                From = 0,
                To   = 1.0,
                Duration = TimeSpan.FromMilliseconds(FadeInDurationMs),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };

            _entryClip.BeginAnimation(RectangleGeometry.RectProperty, rectAnim);
            _entrySlide.BeginAnimation(TranslateTransform.YProperty, slideAnim);
            BeginAnimation(OpacityProperty, fadeIn);
        }
        // else: already visible (mid-show re-press) — leave the current geometry
        // alone and just refresh the hide timer below. The new value is already
        // showing because UpdateBrightnessBar/UpdateWarmthBar ran before Flash().

        // DispatcherTimer's parameterless ctor uses Background priority, which is
        // BELOW Render — meaning a 60 fps live-glass render loop at Render priority
        // would starve this timer and the OSD would never auto-hide. Pinning it to
        // Normal (above Render and DataBind) guarantees the tick fires on time.
        _hideTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(ShowDurationMs)
        };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer?.Stop();
            StartExitAnimation();
        };
        _hideTimer.Start();
    }

    /// <summary>
    /// Reverse of the entry: pill shrinks back to the seed circle and slides
    /// down behind the taskbar while fading out. We use QuadraticEase EaseIn for
    /// the exit (no overshoot — undershoot before disappearing would look weird).
    /// </summary>
    private void StartExitAnimation()
    {
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseIn };

        var rectAnim = new RectAnimation
        {
            To = SeedCircleRect(),
            Duration = TimeSpan.FromMilliseconds(ExitDurationMs),
            EasingFunction = ease,
        };
        var slideAnim = new DoubleAnimation
        {
            To = SlideFromBelowDip,
            Duration = TimeSpan.FromMilliseconds(ExitDurationMs),
            EasingFunction = ease,
        };
        var fadeOut = new DoubleAnimation
        {
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(ExitDurationMs),
            EasingFunction = ease,
        };
        fadeOut.Completed += (_, _) =>
        {
            if (Opacity <= 0.01)
            {
                Hide();
                // Stop the WGC render ticker while the OSD is invisible — no point
                // burning frames nobody can see.
                _liveGlass?.StopLiveTicker();
            }
        };

        _entryClip!.BeginAnimation(RectangleGeometry.RectProperty, rectAnim);
        _entrySlide!.BeginAnimation(TranslateTransform.YProperty, slideAnim);
        BeginAnimation(OpacityProperty, fadeOut);
    }
}
