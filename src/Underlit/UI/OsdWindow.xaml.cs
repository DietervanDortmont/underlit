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
    // Visual pill bottom should sit ~60dip above the working area's bottom. The window
    // itself includes 10dip of shadow padding under the pill, so the window's OWN bottom
    // is 50dip above the working area bottom.
    private const double BottomMarginDip = 50;

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
    /// <summary>Most recently shown brightness/warmth values, so a style flip can re-render in place.</summary>
    private double _lastBrightness;
    private int _lastWarmth = 6500;

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
                                     OsdBarStyle barStyle)
    {
        _barStyle = barStyle;
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

    private void UpdateBrightnessBar(double levelSigned)
    {
        // The width source depends on the active style:
        //   • Bar       — the thin BarRoot, sitting in the column AFTER the icon.
        //                  brightness=0 anchors at the centre of THAT column.
        //   • SolidFill — the full pill width (SolidFillRoot is 280dp wide), with
        //                  brightness=0 anchored at the geometric centre of the pill.
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
        double posWidth = clamped >= 0 ?  clamped / 100.0 * half : 0;
        double negWidth = clamped <  0 ? -clamped / 100.0 * half : 0;

        if (solid)
        {
            SolidFillPos.Width = posWidth;
            SolidFillNeg.Width = negWidth;
        }
        else
        {
            FillRight.Width = posWidth;
            FillLeft.Width  = negWidth;
        }
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
        FillLeft.Background    = new SolidColorBrush(p.FillNegative);
        FillRight.Background   = new SolidColorBrush(CurrentAccent());
        MidMarker.Background   = new SolidColorBrush(p.MidMarker);
        IconText.Foreground    = new SolidColorBrush(p.Icon);
        CandleIcon.Fill        = new SolidColorBrush(p.Icon);
        WarmthTrack.Background = new SolidColorBrush(p.Track);
        WarmthStartStop.Color  = p.WarmthStart;

        // Solid-fill layer brushes — same colour scheme as the thin bar but
        // applied to the full-pill-width fills. Slight alpha reduction so the
        // live glass refraction (or Subtle blur) is still visible underneath.
        SolidFillNeg.Background       = new SolidColorBrush(WithAlpha(p.FillNegative, 0xC0));
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
            if (Opacity <= 0.01)
            {
                Hide();
                // Stop the WGC render ticker while the OSD is invisible — no point
                // burning ~30 fps of CPU on frames nobody can see.
                _liveGlass?.StopLiveTicker();
            }
        };
        BeginAnimation(OpacityProperty, fadeOut);
        BarSlideTransform.BeginAnimation(TranslateTransform.YProperty, slideOut);
    }
}
