using System;
using System.Runtime.InteropServices;
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
    /// <summary>v0.6.56: when true, the OSD never auto-hides. Used by
    /// the Settings window so the user can fine-tune OSD-affecting
    /// sliders without the OSD vanishing mid-tweak. Toggled via
    /// <see cref="SetPinned"/> and unpinned automatically when focus
    /// leaves the relevant controls.</summary>
    private bool _pinned;
    /// <summary>v0.6.77: true between StartExitAnimation and the
    /// Hide()-on-fade-complete callback. Lets Flash() detect that the
    /// OSD is mid-exit and snap it back to fully-visible instead of
    /// letting the fade-out complete + auto-hide.</summary>
    private bool _exitInProgress;
    private const int ShowDurationMs = 1300;

    // ---- Dynamic-Island entry/exit animation tunables ----
    // v0.6.32: entry and exit are mirrors — same total duration, slide and
    // morph run in parallel with one slightly leading the other.
    // v0.6.39: durations dropped to roughly match Windows' Fluent
    // "Normal" enter timing (~250 ms), with a little extra room for the
    // BackEase spring overshoot. Old 460 ms felt sluggish next to Windows
    // brightness/volume flyouts.
    private const int    EntryDurationMs = 320;
    private const int    MorphLeadMs     = 60;
    private const int    FadeInDurationMs = 200;
    /// <summary>Exit total duration. Mirror of <see cref="EntryDurationMs"/>.</summary>
    private const int    ExitDurationMs   = 280;
    /// <summary>
    /// Initial Y-offset for the entry slide, in dip. v0.6.39: this is no
    /// longer a constant — it scales with the user's distance-above-taskbar
    /// setting so the seed circle ALWAYS starts from the top of the
    /// taskbar regardless of how far above it the rest position sits.
    ///
    /// Computed as <c>pillHeight + taskbarGap</c>: at this slide offset the
    /// seed circle's top edge is exactly at the taskbar's top in window
    /// coordinates (= where VisibleClip's bottom is), so the seed is fully
    /// below the visible zone — invisible. Sliding to 0 raises the seed
    /// from "just below the taskbar" up to its rest position above the
    /// taskbar; the larger the gap, the longer the visible travel, but
    /// the duration stays fixed so a bigger gap just feels like a slightly
    /// longer arc rather than a slower animation.
    /// </summary>
    private double _slideFromBelowDip = 80;
    private const double EntryBackAmplitude = 0.4; // BackEase amplitude — overshoot strength
    /// <summary>
    /// Gap between the WPF window's bottom edge and the working area's bottom
    /// (top of the taskbar). The visible pill's bottom sits 10 dip above the
    /// window's bottom (shadow padding inside the bitmap), so the actual gap
    /// from pill bottom to taskbar top is BottomMarginDip + 10. Was 50 → 60 dip
    /// gap, now 0 → 10 dip gap. Keep ≥ 0 to avoid the window overlapping the
    /// taskbar (which can cause the taskbar to render in front of the pill).
    /// </summary>
    /// <summary>
    /// Y of pill bottom inside the window, in dip. The PillContainer is
    /// 300×66 at the top of the 300×160 WindowRoot; pill is at PillContainer
    /// y=10..56 (10 dip shadow above + 46 dip pill + 10 dip shadow below).
    /// </summary>
    /// <summary>v0.6.59: VisibleClip is offset from the window's top so
    /// the OSD's drop shadow has room to render above the pill without
    /// getting clipped by the window's top edge.
    /// v0.6.64: bumped from 20 to 60 dip so the user can crank the
    /// shadow blur/offset sliders up without the halo getting cropped
    /// by the window's top edge.</summary>
    private const double VisibleClipTopMarginDip = 60;
    /// <summary>Pill bottom in VisibleClip-local coordinates. Inside the
    /// clip the pill sits at clip-y 10..56, height 46.</summary>
    private const double PillBottomInClipDip = 56;
    /// <summary>Pill bottom in WINDOW coordinates. = clip top margin +
    /// pill bottom in clip. Used to anchor the window so the visible
    /// pill sits above the taskbar at the user-configured gap.</summary>
    private const double PillBottomYDip = VisibleClipTopMarginDip + PillBottomInClipDip;
    /// <summary>Default gap between pill bottom and taskbar top, in dip.
    /// v0.6.35: now also overridable per-user via
    /// <see cref="AppSettings.OsdGapAboveTaskbarDip"/>; this constant is the
    /// fallback for the brief window before settings are applied.</summary>
    private const double TaskbarGapDip  = 30;
    /// <summary>Live taskbar-gap value mirrored from settings, used by
    /// <see cref="PositionAboveTaskbar"/>. Updated in
    /// <see cref="UpdateVisualSettings"/>.</summary>
    private double _taskbarGapDip = TaskbarGapDip;

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
    /// <summary>v0.6.69: user-pickable brightness-icon (sun) colour.
    /// Overrides the luminance-based "auto" colour the icon used to
    /// take from the OSD's backdrop. Default = #FFFEC80F (amber).</summary>
    private Color _sunIconColor = Color.FromArgb(0xFF, 0xFE, 0xC8, 0x0F);
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

    // ---- Brightness fill brushes (v0.6.25) ----
    // Persistent SolidColorBrush instances so we can animate the Color property
    // smoothly when the indicator crosses the OS-min threshold (50). All four
    // brushes are kept around regardless of which bar style is active so a
    // mid-show toggle between Bar and SolidFill modes keeps animations alive.
    private SolidColorBrush? _brushFillLeft;
    private SolidColorBrush? _brushFillRight;
    private SolidColorBrush? _brushSolidNeg;
    private SolidColorBrush? _brushSolidPos;
    /// <summary>Tracks which side of the threshold the indicator was on last
    /// frame; null on first paint. Used to detect a crossing and trigger the
    /// 500 ms colour fade only on the cross — not on every mouse move.</summary>
    private bool? _wasBelowOsMin;
    /// <summary>Last target colour the bar was animating TOWARD. Compared
    /// against the current target to detect external changes (theme switch,
    /// accent recolour) — distinct from <c>_brushFillLeft.Color</c>, which is
    /// the live in-animation value and would always disagree with target
    /// during a fade.</summary>
    private Color _lastBrightnessTargetColor;
    private const double OsMinThreshold = 50.0;
    private const int    ColourFadeMs   = 500;
    /// <summary>Most recently shown brightness/warmth values, so a style flip can re-render in place.</summary>
    private double _lastBrightness;
    private int _lastWarmth = 6500;

    // Animated clip + slide for the Dynamic-Island-style entry. Created on first
    // SourceInitialized; animated on every Flash()/StartExitAnimation().
    private RectangleGeometry? _entryClip;
    /// <summary>v0.6.60: parallel slide so the shadow rides up alongside
    /// the pill instead of snapping to its rest position.
    /// v0.6.68: applied to ShadowImage (pre-rendered bitmap), which
    /// slides up + fades in during the entry animation. The bitmap
    /// itself doesn't morph; the visible pill morphs underneath it.</summary>
    private TranslateTransform? _shadowSlide;
    private TranslateTransform? _entrySlide;
    /// <summary>v0.6.69: ScaleTransform used by <see cref="ShowBrightnessAtBound"/>
    /// to produce a tiny rubber-band "pop" when the user tries to step
    /// brightness past the min or max. Resting ScaleX = 1.0; the pop
    /// briefly bumps it to ~1.05 and springs back. CenterX is set per
    /// pop to the opposite edge so the pill stretches toward the
    /// direction of pressure rather than ballooning symmetrically.</summary>
    private ScaleTransform? _popScale;

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
                                     OsdBarStyle barStyle, string brightnessHighColor,
                                     int osdGapAboveTaskbarDip)
    {
        _barStyle = barStyle;
        _brightnessHighColor = string.IsNullOrWhiteSpace(brightnessHighColor) ? "auto" : brightnessHighColor;
        _followWindowsAccent = followWindowsAccent;
        _customAccent = followWindowsAccent ? null : customAccent;
        _taskbarGapDip = Math.Clamp(osdGapAboveTaskbarDip, 0, 200);

        _useTransparency = transparencyMode switch
        {
            TransparencyMode.On  => true,
            TransparencyMode.Off => false,
            _                    => TransparencyPreference.IsEnabled(),
        };

        _backdrop = backdrop;
        _glass = glass.Clone();

        // v0.6.68: corner radius is baked into the pre-rendered shadow
        // bitmap, so we re-render here when the user changes corner
        // radius. Costs ~5-10 ms; OK during settings push.
        RebuildShadowBitmap();

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

        // v0.6.72: exclude the OSD pill from screen captures. Used to be
        // applied only inside LiveGlassController.TryEnableLive (because
        // the WGC capture needed it to avoid a feedback loop) — but the
        // user benefit is much broader: the pill never shows up in
        // screenshots, recordings, or Teams/Loom shares regardless of
        // whether live glass is on. Idempotent + cheap to call here.
        WindowDisplayAffinity.ExcludeFromCapture(Hwnd);

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
        // v0.6.35: clip + transform now live on ClipRoot (inside the 300×66
        // VisibleClip Border) instead of WindowRoot. The outer VisibleClip
        // masks out the bottom 94 dip of the window, so the seed circle's
        // slide-from-below animation visually emerges from behind the
        // bottom edge of its visible zone — faking the "behind taskbar"
        // effect without depending on actual z-order shenanigans.
        ClipRoot.Clip = _entryClip;

        _entrySlide = new TranslateTransform();
        // v0.6.69: bounds-hit pop. A ScaleTransform stacked with the
        // slide; ShowBrightnessAtBound briefly animates ScaleX with the
        // CenterX pinned to the opposite edge (so the pill stretches
        // toward the direction of pressure, then springs back). Resting
        // ScaleX = 1.0 = identity, so this is invisible until used.
        _popScale = new ScaleTransform { ScaleX = 1.0, ScaleY = 1.0 };
        var grp = new TransformGroup();
        grp.Children.Add(_popScale);
        grp.Children.Add(_entrySlide);
        ClipRoot.RenderTransform = grp;

        // v0.6.60: parallel slide so the shadow rides up with the pill.
        // v0.6.68: the shadow is now a pre-rendered bitmap on an Image
        // element. Slide rides ShadowImage; bitmap rebuild happens on
        // SetOsdShadow (settings change) or UpdateVisualSettings (corner
        // radius change). No more OpacityMask, no more cutout artifact.
        _shadowSlide = new TranslateTransform();
        ShadowImage.RenderTransform = _shadowSlide;
    }

    // ====================================================================
    // v0.6.68: pre-rendered shadow bitmap.
    //
    // We give up the live DropShadowEffect approach and instead rasterise
    // the shadow into a bitmap whenever a setting changes. This lets us
    // post-process the pixels — specifically, soft-fade the pill interior
    // to alpha 0 so the opaque source doesn't bleed through the
    // translucent glass, AND the inner edge is a smooth gradient instead
    // of a hard cutout line.
    // ====================================================================

    /// <summary>Last shadow params we rendered the bitmap with — including
    /// the visible pill rect so per-frame rect animation re-renders.
    /// Used to short-circuit RebuildShadowBitmap when nothing changed.</summary>
    private (int opacity, int blur, int offset, int angle, int spread,
             double cornerR, double rx, double ry, double rw, double rh) _lastShadowParams;

    /// <summary>Current shadow params, set by <see cref="SetOsdShadow"/>
    /// and read by <see cref="RebuildShadowBitmap"/>.</summary>
    private int _shadowOpacityPct = 55;
    private int _shadowBlurDip    = 22;
    private int _shadowOffsetDip  = 8;
    private int _shadowAngleDeg   = 270;
    private int _shadowSpreadDip;

    private const int  ShadowBitmapWidth        = 480;
    private const int  ShadowBitmapHeight       = 200;
    private const int  ShadowPillLeft           = 100;
    private const int  ShadowPillTop            = 70;
    /// <summary>v0.6.69: tight 1.5 dip fade band at the inner pill edge —
    /// just enough to anti-alias the boundary without producing a visible
    /// inner shadow. The previous 6 dip soft fade was producing a faint
    /// "rim darkness" through the translucent glass; with this narrower
    /// band the user sees only the outward halo, and the pill morph
    /// rebuilds the bitmap per frame so the inner edge always tracks
    /// the visible pill outline.</summary>
    private const double ShadowInnerFadeWidthDip = 1.5;

    /// <summary>v0.6.69: dependency property that drives the per-frame
    /// shadow rebuild during the entry/exit animation. Animating this
    /// rect (RectAnimation) re-renders the bitmap with that pill size
    /// as the source — so the halo's inner edge always tracks the
    /// visible pill outline as the pill morphs from seed circle to
    /// full pill. Default value is the full pill at rest.</summary>
    public static readonly DependencyProperty ShadowPillRectProperty =
        DependencyProperty.Register(
            nameof(ShadowPillRect), typeof(Rect), typeof(OsdWindow),
            new PropertyMetadata(
                new Rect(ShadowPillLeft, ShadowPillTop,
                         LiveGlassController.PillWidthDip,
                         LiveGlassController.PillHeightDip),
                OnShadowPillRectChanged));

    public Rect ShadowPillRect
    {
        get => (Rect)GetValue(ShadowPillRectProperty);
        set => SetValue(ShadowPillRectProperty, value);
    }

    private static void OnShadowPillRectChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is OsdWindow w) w.RebuildShadowBitmap();
    }

    /// <summary>v0.6.69: rest rect — the full pill outline in bitmap-local
    /// coords. Used as the To target of the entry RectAnimation and as
    /// the resting value the shadow snaps to outside animations.</summary>
    private static Rect ShadowFullVisibleRect() => new Rect(
        ShadowPillLeft, ShadowPillTop,
        LiveGlassController.PillWidthDip,
        LiveGlassController.PillHeightDip);

    /// <summary>v0.6.69: start rect — the seed circle, horizontally
    /// centred at the visible pill's centre. From here the
    /// RectAnimation morphs to <see cref="ShadowFullVisibleRect"/>.</summary>
    private static Rect ShadowSeedVisibleRect()
    {
        double d = LiveGlassController.PillHeightDip; // 46
        const double centerX = 240;                   // (480 / 2)
        return new Rect(centerX - d / 2.0, ShadowPillTop, d, d);
    }

    /// <summary>Rebuild <see cref="ShadowImage"/>.Source from the current
    /// shadow params + <see cref="_glass"/> corner radius + the current
    /// value of <see cref="ShadowPillRect"/>. Costs ~3–6 ms on the UI
    /// thread.
    /// <para>v0.6.69: now called per-frame during the entry/exit
    /// animation (driven by <see cref="ShadowPillRectProperty"/>'s
    /// change handler) so the shadow halo's inner edge tracks the
    /// visible pill outline as it morphs. The cache short-circuit
    /// keeps things efficient outside of animation — rebuilds only
    /// happen when one of the params (or the rect) actually changed.</para></summary>
    private void RebuildShadowBitmap()
    {
        if (ShadowImage == null) return;
        double cornerR = (LiveGlassController.PillHeightDip / 2.0) * (_glass.CornerRadius / 100.0);
        Rect visibleRect = ShadowPillRect;

        // Include the visible-pill rect in the cache key so a per-frame
        // rect animation actually re-renders, while same-settings calls
        // short-circuit.
        var key = (_shadowOpacityPct, _shadowBlurDip, _shadowOffsetDip,
                   _shadowAngleDeg, _shadowSpreadDip, cornerR,
                   visibleRect.X, visibleRect.Y, visibleRect.Width, visibleRect.Height);
        if (key.Equals(_lastShadowParams) && ShadowImage.Source != null) return;
        _lastShadowParams = key;

        if (_shadowOpacityPct <= 0)
        {
            ShadowImage.Source = null;
            return;
        }

        // 1. Build a Border containing the source pill, expanded by the
        //    spread amount (CSS box-shadow "spread" semantics). Border
        //    + measure/arrange is more reliable than DrawingVisual.Effect
        //    when rasterising via RenderTargetBitmap — DrawingVisual's
        //    Effect property is silently dropped on some WPF builds when
        //    the visual isn't attached to a live PresentationSource.
        //
        //    v0.6.69: source pill is now derived from the animated
        //    visibleRect rather than fixed constants, so the bitmap
        //    morphs with the pill during the entry/exit animation.
        int s = Math.Clamp(_shadowSpreadDip, 0, 60);
        double srcWidth  = visibleRect.Width  + 2 * s;
        double srcHeight = visibleRect.Height + 2 * s;
        double srcLeft   = visibleRect.X - s;
        double srcTop    = visibleRect.Y - s;
        double srcRadius = cornerR + s;

        var pillBorder = new System.Windows.Controls.Border
        {
            Width        = srcWidth,
            Height       = srcHeight,
            CornerRadius = new CornerRadius(srcRadius),
            Background   = System.Windows.Media.Brushes.Black,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment   = VerticalAlignment.Top,
            Margin       = new Thickness(srcLeft, srcTop, 0, 0),
            Effect       = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius  = Math.Clamp(_shadowBlurDip,   0, 80),
                ShadowDepth = Math.Clamp(_shadowOffsetDip, 0, 40),
                Direction   = ((_shadowAngleDeg % 360) + 360) % 360,
                Opacity     = Math.Clamp(_shadowOpacityPct, 0, 100) / 100.0,
                Color       = System.Windows.Media.Colors.Black,
            },
        };
        var container = new System.Windows.Controls.Grid
        {
            Width  = ShadowBitmapWidth,
            Height = ShadowBitmapHeight,
        };
        container.Children.Add(pillBorder);
        var arrangeBounds = new Rect(0, 0, ShadowBitmapWidth, ShadowBitmapHeight);
        container.Measure(arrangeBounds.Size);
        container.Arrange(arrangeBounds);
        container.UpdateLayout();

        // 2. Rasterise. Pbgra32 (premultiplied BGRA) is the only format
        //    RenderTargetBitmap supports for live composition.
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            ShadowBitmapWidth, ShadowBitmapHeight, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(container);

        // 3. Pixel pass — soft-fade alpha to 0 across a ShadowInnerFadeWidthDip
        //    band inside the visible pill outline. Pixels deep inside the
        //    pill (> fade width from outline) become fully transparent;
        //    pixels in the fade band are linearly scaled; pixels outside
        //    the pill outline are untouched (full halo).
        //
        //    IMPORTANT: the mask must use the EXPANDED pill geometry
        //    (srcLeft/srcTop/srcWidth/srcHeight/srcRadius), not the
        //    original PillWidthDip × PillHeightDip. The source border
        //    rendered in step 1 was expanded by `s` pixels in every
        //    direction; if we mask using the un-expanded pill rect, the
        //    ring of solid black between the original pill outline and
        //    the expanded source border never gets faded — and that's
        //    exactly what shows up as a hard black border that grows
        //    with the Spread slider. Using the expanded geometry here
        //    fades the entire source pill body, leaving only the
        //    DropShadowEffect-produced halo.
        int stride = ShadowBitmapWidth * 4;
        byte[] buf = new byte[stride * ShadowBitmapHeight];
        rtb.CopyPixels(buf, stride, 0);

        var maskRect = new Rect(srcLeft, srcTop, srcWidth, srcHeight);

        // Only scan the rows/cols covering the pill (plus a tiny margin) -
        // the rest of the bitmap is unaffected and skipping it saves CPU.
        int y0 = Math.Max(0, (int)maskRect.Y - 2);
        int y1 = Math.Min(ShadowBitmapHeight, (int)(maskRect.Y + maskRect.Height) + 2);
        int x0 = Math.Max(0, (int)maskRect.X - 2);
        int x1 = Math.Min(ShadowBitmapWidth, (int)(maskRect.X + maskRect.Width) + 2);

        for (int y = y0; y < y1; y++)
        {
            int row = y * stride;
            for (int x = x0; x < x1; x++)
            {
                double inset = InsideDistanceFromOutline(x + 0.5, y + 0.5, maskRect, srcRadius);
                if (inset <= 0) continue; // outside the pill outline — leave halo alone
                double fade;
                if (inset >= ShadowInnerFadeWidthDip) fade = 0.0;
                else fade = 1.0 - (inset / ShadowInnerFadeWidthDip);

                int idx = row + x * 4;
                // Pbgra32 is premultiplied, so we scale ALL four channels.
                buf[idx    ] = (byte)(buf[idx    ] * fade); // B
                buf[idx + 1] = (byte)(buf[idx + 1] * fade); // G
                buf[idx + 2] = (byte)(buf[idx + 2] * fade); // R
                buf[idx + 3] = (byte)(buf[idx + 3] * fade); // A
            }
        }

        var wb = new System.Windows.Media.Imaging.WriteableBitmap(
            ShadowBitmapWidth, ShadowBitmapHeight, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32, null);
        wb.WritePixels(
            new System.Windows.Int32Rect(0, 0, ShadowBitmapWidth, ShadowBitmapHeight),
            buf, stride, 0);
        wb.Freeze();
        ShadowImage.Source = wb;
    }

    /// <summary>Returns how far (px,py) is INSIDE the rounded rect's
    /// outline. Zero or negative if outside the outline, positive if
    /// inside (= distance from outline going inward). Used for the soft
    /// inner fade — pixels close to the outline get a smooth alpha
    /// ramp rather than a hard cutoff.</summary>
    private static double InsideDistanceFromOutline(double px, double py, Rect r, double radius)
    {
        // Quick reject — outside the axis-aligned bounding box.
        if (px < r.X || px > r.X + r.Width || py < r.Y || py > r.Y + r.Height)
            return 0;

        // Distance to each rectangular edge (positive == inside).
        double dL = px - r.X;
        double dR = r.X + r.Width  - px;
        double dT = py - r.Y;
        double dB = r.Y + r.Height - py;

        // In a corner zone? The two-axis check identifies the four
        // quarter-circle regions; outside those we're on a straight
        // edge and the nearest-edge distance is just the min of the
        // four.
        bool inCornerX = dL < radius || dR < radius;
        bool inCornerY = dT < radius || dB < radius;
        if (inCornerX && inCornerY)
        {
            double cx = (dL < radius) ? r.X + radius : r.X + r.Width  - radius;
            double cy = (dT < radius) ? r.Y + radius : r.Y + r.Height - radius;
            double dx = px - cx;
            double dy = py - cy;
            double fromCenter = Math.Sqrt(dx * dx + dy * dy);
            if (fromCenter > radius) return 0; // outside the rounded corner
            return radius - fromCenter;
        }

        return Math.Min(Math.Min(dL, dR), Math.Min(dT, dB));
    }

    /// <summary>
    /// Force the OSD to the top of the topmost z-order group so it stays
    /// above every app window (Chrome, fullscreen-but-not-exclusive, etc.).
    /// v0.6.35: no longer tries to slot below Shell_TrayWnd — the
    /// "behind taskbar" appearance is now faked entirely on the WPF side
    /// via the VisibleClip Border in OsdWindow.xaml. That made the
    /// taskbar-z-order trick unreliable on Win 11 setups where
    /// Shell_TrayWnd isn't topmost (slipping us below Chrome).
    ///
    /// Called every Show() because Windows can re-promote other topmost
    /// windows on activation; re-asserting HWND_TOPMOST keeps us at the
    /// top of the topmost group.
    /// </summary>
    private void PositionOsdZOrder()
    {
        if (Hwnd == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(
            Hwnd, NativeMethods.HWND_TOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE
            | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
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
    /// Apply a SetWindowRgn region that defines the OS-level outline of the
    /// window. v0.6.28: the region must encompass the FULL window rectangle
    /// (not just the rest pill) because the entry animation slides the seed
    /// circle through the bottom half of the window — content there has to be
    /// renderable. Visual rounding of the rest pill comes from
    /// <see cref="_entryClip"/> (a WPF RectangleGeometry on WindowRoot), not
    /// from the window region.
    /// </summary>
    private void ApplyPillRegion()
    {
        if (Hwnd == IntPtr.Zero) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        double scale = dpi.DpiScaleX;
        if (scale <= 0) scale = 1.0;

        // Full window rectangle — width/height as configured in XAML, scaled
        // by DPI. The ENTIRE window rectangle is part of the OS-level region
        // so the bottom-half slide-from-behind-taskbar works.
        int physWindowW = (int)Math.Round(this.Width  * scale);
        int physWindowH = (int)Math.Round(this.Height * scale);

        IntPtr rgn = NativeMethods.CreateRectRgn(0, 0, physWindowW, physWindowH);

        if (rgn != IntPtr.Zero)
        {
            // SetWindowRgn takes ownership of the region — do NOT DeleteObject.
            NativeMethods.SetWindowRgn(Hwnd, rgn, true);
        }
    }

    /// <summary>
    /// Anchor the OSD window so its visible-pill bottom sits
    /// <see cref="_taskbarGapDip"/> above the system taskbar's top edge,
    /// horizontally centred on the taskbar's monitor.
    ///
    /// v0.6.37: replaces the old WinForms Screen.WorkingArea read with the
    /// Windows-authoritative <c>ABM_GETTASKBARPOS</c> / <c>ABM_GETSTATE</c>
    /// shell-appbar query. WorkingArea was returning either DIPs or
    /// physical pixels depending on DPI awareness mode and Win 11 build,
    /// which made the gap visibly wrong on some setups even though the
    /// math looked right. The appbar API gives us the taskbar's real
    /// physical-pixel rect and its uEdge (top/bottom/left/right), and
    /// MonitorFromPoint plus GetMonitorInfo gives the monitor's physical
    /// rect for centring. We convert physical → DIP using the OSD's own
    /// per-monitor DPI scale at the end, so positioning is correct on any
    /// taskbar edge and any DPI configuration.
    ///
    /// Falls back to the old WorkingArea path if either call fails.
    /// </summary>
    private void PositionAboveTaskbar()
    {
        // v0.6.39: window + visible-clip + slide all derived from the
        // current gap setting, so the seed always emerges from the
        // taskbar's top edge regardless of how far above it the rest
        // position sits.
        ApplyGapDependentGeometry();

        var dpi = VisualTreeHelper.GetDpi(this);
        double scale = dpi.DpiScaleX;
        if (scale <= 0) scale = 1.0;

        if (!TryGetTaskbarAnchor(out NativeMethods.RECT taskbarRectPx,
                                   out NativeMethods.RECT monitorRectPx,
                                   out uint taskbarEdge,
                                   out bool autoHide))
        {
            FallbackPositionFromWorkingArea(scale);
            return;
        }

        // Pill-bottom anchor in PHYSICAL pixels — depends on which edge
        // the taskbar is on, plus the auto-hide state.
        int pillBottomPx;
        if (autoHide)
        {
            // Auto-hide taskbar: it's only visible while the cursor is at
            // the screen edge. Anchor the pill flush against the monitor
            // edge instead — the pill stays out of the slide-out tray's way.
            pillBottomPx = monitorRectPx.bottom - 1;
        }
        else
        {
            pillBottomPx = taskbarEdge switch
            {
                NativeMethods.ABE_BOTTOM => taskbarRectPx.top,
                // Top-edge or vertical taskbars: no taskbar at the screen's
                // bottom, so anchor against the monitor's bottom edge.
                NativeMethods.ABE_TOP    => monitorRectPx.bottom - 1,
                NativeMethods.ABE_LEFT   => monitorRectPx.bottom - 1,
                NativeMethods.ABE_RIGHT  => monitorRectPx.bottom - 1,
                _                        => taskbarRectPx.top,
            };
        }

        // User-configurable gap, plus the constant offset from the window's
        // top to the visible pill's bottom (PillBottomYDip).
        int gapPx       = (int)Math.Round(_taskbarGapDip * scale);
        int pillTargetPx = pillBottomPx - gapPx;
        int windowTopPx = pillTargetPx - (int)Math.Round(PillBottomYDip * scale);

        // Convert physical → DIP for WPF Top/Left.
        double windowTopDip = windowTopPx / scale;
        double monLeftDip   = monitorRectPx.left  / scale;
        double monRightDip  = monitorRectPx.right / scale;

        Left = monLeftDip + ((monRightDip - monLeftDip) - Width) / 2;
        _restTop = windowTopDip;
        Top = _restTop;
    }

    /// <summary>
    /// Pull the system taskbar's physical-pixel rect, its monitor's rect,
    /// the taskbar's screen edge, and whether it's set to auto-hide. All
    /// four come from authoritative Windows APIs (Shell appbar messages +
    /// MonitorFromPoint + GetMonitorInfo). Returns false if the calls fail
    /// — caller should fall back to WorkingArea-based positioning.
    /// </summary>
    private static bool TryGetTaskbarAnchor(
        out NativeMethods.RECT taskbarRectPx,
        out NativeMethods.RECT monitorRectPx,
        out uint taskbarEdge,
        out bool autoHide)
    {
        taskbarRectPx = default;
        monitorRectPx = default;
        taskbarEdge   = NativeMethods.ABE_BOTTOM;
        autoHide      = false;

        var data = new NativeMethods.APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.APPBARDATA>(),
        };
        if (NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETTASKBARPOS, ref data) == IntPtr.Zero)
            return false;
        taskbarRectPx = data.rc;
        taskbarEdge   = data.uEdge;

        // Auto-hide is a per-process app-bar state, not specific to one
        // taskbar instance — ABM_GETSTATE returns it as a bitfield.
        var stateData = new NativeMethods.APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.APPBARDATA>(),
        };
        long state = NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETSTATE, ref stateData).ToInt64();
        autoHide = (state & NativeMethods.ABS_AUTOHIDE) != 0;

        // Resolve the monitor that owns the taskbar — pick a point inside
        // the taskbar rect so we get the right monitor on multi-monitor
        // setups, then GetMonitorInfo for its physical rect.
        var pt = new NativeMethods.POINT
        {
            X = (taskbarRectPx.left + taskbarRectPx.right) / 2,
            Y = (taskbarRectPx.top  + taskbarRectPx.bottom) / 2,
        };
        IntPtr hMon = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        if (hMon == IntPtr.Zero) return false;

        var info = new NativeMethods.MONITORINFOEX
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>(),
        };
        if (!NativeMethods.GetMonitorInfo(hMon, ref info)) return false;
        monitorRectPx = info.rcMonitor;
        return true;
    }

    /// <summary>
    /// Resize the window + VisibleClip + slide-distance to all match the
    /// current <see cref="_taskbarGapDip"/>. The visible clip's bottom
    /// edge is positioned EXACTLY at the taskbar's top in window
    /// coordinates, so the seed circle's slide animation appears to
    /// emerge from behind the taskbar regardless of how high above the
    /// taskbar the OSD's rest position sits.
    ///
    /// Geometry, in window-local DIP:
    ///   • Pill rests at y = 10 .. <see cref="PillBottomYDip"/> (= 56), so
    ///     pill height = 46 dip.
    ///   • Taskbar top in window coords = <see cref="PillBottomYDip"/> +
    ///     <see cref="_taskbarGapDip"/> (the pill bottom is `gap` dip
    ///     above the taskbar).
    ///   • <see cref="VisibleClip"/>.Height clips everything below the
    ///     taskbar top, so set Height = PillBottomYDip + gap.
    ///   • Slide's "fully hidden" position needs the seed top at or below
    ///     VisibleClip's bottom: 10 + slide ≥ PillBottomYDip + gap →
    ///     slide ≥ pillHeight + gap = 46 + gap.
    ///   • Window.Height needs to host the lowest rendered pixel of the
    ///     animating pill: PillBottomYDip + slide = 56 + 46 + gap = 102 + gap.
    /// </summary>
    private void ApplyGapDependentGeometry()
    {
        const double pillHeight = 46;
        double gap = Math.Max(0, _taskbarGapDip);

        _slideFromBelowDip = pillHeight + gap;
        // VisibleClip.Height is its OWN internal extent: pill-bottom-in-
        // clip + gap. The window-coord pill bottom (PillBottomYDip) is
        // PillBottomInClipDip + the clip's top margin.
        VisibleClip.Height = PillBottomInClipDip + gap;
        // Window must hold the entire VisibleClip below its top margin.
        Height = PillBottomYDip + gap;

        // v0.6.70: VisibleClip has ClipToBounds=false in XAML so the
        // bounds-hit pop can stretch the pill horizontally without
        // getting cropped at x=300. We install a custom Clip here that
        // clips at y=VisibleClip.Height (preserving the "behind taskbar"
        // mask) but allows generous horizontal extension (±60 dip past
        // the 300 dip width). Has to be re-applied whenever the height
        // changes — which is right here, with the gap.
        const double horizontalSlack = 60;
        VisibleClip.Clip = new RectangleGeometry(new Rect(
            -horizontalSlack, 0,
            VisibleClip.Width + 2 * horizontalSlack,
            VisibleClip.Height));

        // The OS-level window region tracks the new height; the animation
        // slides through the full window rect.
        ApplyPillRegion();
    }

    /// <summary>Last-resort positioning if the appbar API or monitor query
    /// fails. Mirrors the v0.6.36 logic exactly.</summary>
    private void FallbackPositionFromWorkingArea(double scale)
    {
        var primary = System.Windows.Forms.Screen.PrimaryScreen;
        if (primary == null) return;

        double waLeftDip   = primary.WorkingArea.Left   / scale;
        double waTopDip    = primary.WorkingArea.Top    / scale;
        double waWidthDip  = primary.WorkingArea.Width  / scale;
        double waHeightDip = primary.WorkingArea.Height / scale;

        Left = waLeftDip + (waWidthDip - Width) / 2;
        _restTop = waTopDip + waHeightDip - PillBottomYDip - _taskbarGapDip;
        Top = _restTop;
    }

    // ---- Public API ----

    public void ShowBrightness(double levelSigned)
    {
        SetMode(Mode.Brightness);
        // v0.6.70: SetMode shows the filled-sun Path (SunIconFilled),
        // not the Fluent Icons glyph in IconText. The glyph is kept
        // only for ShowPaused's pause/play indicators.
        _lastBrightness = levelSigned;
        UpdateBrightnessBar(levelSigned);
        Flash();
    }

    /// <summary>v0.6.69: same as <see cref="ShowBrightness"/> but fires a
    /// tiny rubber-band "pop" because the user tried to step past the
    /// min or max bound. <paramref name="direction"/> is +1 for "up
    /// past max" (pill stretches to the right) and -1 for "down past
    /// min" (stretches to the left). The pop adds ~5 % temporary
    /// elongation toward the direction of pressure, then springs back \u2014
    /// matches Apple's "I heard you but can't go further" feedback on
    /// scroll bounds.</summary>
    public void ShowBrightnessAtBound(double levelSigned, int direction)
    {
        ShowBrightness(levelSigned);
        if (_popScale == null) return;

        // Pop centre is on the OPPOSITE edge so the pill grows toward
        // the direction of pressure. The pill within PillContainer-
        // local coordinates spans (PaddingDip, PaddingDip,
        // PillWidthDip, PillHeightDip) \u2014 so left edge x = PaddingDip,
        // right edge x = PaddingDip + PillWidthDip.
        double leftEdge  = LiveGlassController.PaddingDip;
        double rightEdge = LiveGlassController.PaddingDip + LiveGlassController.PillWidthDip;
        _popScale.CenterY = LiveGlassController.PaddingDip + LiveGlassController.PillHeightDip / 2.0;
        _popScale.CenterX = direction > 0 ? leftEdge : rightEdge;

        var anim = new DoubleAnimationUsingKeyFrames();
        // v0.6.70: gentler push (2.5 % instead of 5 %) so the inner
        // glass refraction barely stretches, and the rubber-band feel
        // stays subtle. Snap-back uses a soft QuadraticEase instead of
        // BackEase — the slight extra "overshoot" of BackEase was
        // reading as wobbliness rather than rubberband.
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.025,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70)),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        _popScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
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
        // v0.6.70: swap from the filled-sun Path to the IconText glyph
        // for pause/play, then restore on the next ShowBrightness via
        // SetMode resetting visibility to "sun shown, text hidden".
        SunIconFilled.Visibility = Visibility.Collapsed;
        IconText.Visibility      = Visibility.Visible;
        IconText.Text = paused ? "\uE769" : "\uE768";
        _lastBrightness = paused ? 0 : 100;
        UpdateBrightnessBar(_lastBrightness);
        Flash();
    }

    /// <summary>
    /// v0.6.48: invalidate the OSD's live-glass capture. Called by the
    /// host when Windows fires DisplaySettingsChanged (monitor connect /
    /// disconnect / resolution change). The WGC capture item is pinned
    /// to a specific HMONITOR, so a disconnected monitor leaves us
    /// rendering frozen pixels. Tearing down here forces the next
    /// Flash() to lazy-recreate against the current primary monitor.
    /// </summary>
    public void NotifyDisplaysChanged()
    {
        try
        {
            _liveGlass?.DisableLive();
            _liveGlass?.Dispose();
            _liveGlass = null;
        }
        catch (Exception ex) { Logger.Warn("Live-glass teardown on display change failed", ex); }
    }

    private void SetMode(Mode m)
    {
        _mode = m;
        bool brightness = m == Mode.Brightness;
        BrightnessBar.Visibility = brightness ? Visibility.Visible : Visibility.Collapsed;
        WarmthBar.Visibility     = brightness ? Visibility.Collapsed : Visibility.Visible;
        // v0.6.70: brightness mode shows the custom filled sun by default;
        // ShowPaused overrides afterwards to swap to the pause/play
        // glyph in IconText.
        SunIconFilled.Visibility = brightness ? Visibility.Visible : Visibility.Collapsed;
        IconText.Visibility      = Visibility.Collapsed;
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
    /// Brightness bar update — v0.6.24 model.
    ///
    /// The bar represents a 0..100 indicator that grows from the LEFT edge. The
    /// engine's signed -100..+100 level maps to displayValue 0..100 via
    /// (signed + 100) / 2.
    ///
    /// Layout: two side-by-side fills (FillLeft for 0..50, FillRight for 50..100)
    /// share a single colour. The colour switches between two states based on
    /// where the indicator currently sits:
    ///   • display ≥ 50 — both halves render in the accent colour. The Windows
    ///                    native brightness range (50..100) and the visible
    ///                    portion of the sub-OS-min half look identical; the
    ///                    bar reads as one continuous accent-coloured fill.
    ///   • display &lt; 50 — only FillLeft is visible (FillRight is empty), and
    ///                    it renders in the "low" / dim shade. The user gets a
    ///                    subtle hint that they've crossed below the Windows
    ///                    native minimum into Underlit's extended-dim range.
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

        // Colour both halves with the SAME colour at any given moment so the
        // visible fill reads as one continuous shape. The colour flips when
        // crossing the OS-min threshold; the flip animates over ColourFadeMs
        // so the user sees a gradual fade rather than a hard pop.
        Color accent = CurrentAccent();
        Color dim    = ResolveBrightnessLowColor(accent);
        bool below   = display < OsMinThreshold;
        Color target = below ? dim : accent;
        Color targetAlpha = WithAlpha(target, 0xC0);

        // Lazy-init the four persistent brushes and assign them as Background
        // exactly once. Subsequent paints animate the Color property in place
        // — much smoother than swapping in a fresh brush on every frame.
        if (_brushFillLeft == null)
        {
            _brushFillLeft  = new SolidColorBrush(target);
            _brushFillRight = new SolidColorBrush(target);
            _brushSolidNeg  = new SolidColorBrush(targetAlpha);
            _brushSolidPos  = new SolidColorBrush(targetAlpha);
            FillLeft.Background     = _brushFillLeft;
            FillRight.Background    = _brushFillRight;
            SolidFillNeg.Background = _brushSolidNeg;
            SolidFillPos.Background = _brushSolidPos;
            _wasBelowOsMin             = below;
            _lastBrightnessTargetColor = target;
        }
        else if (_wasBelowOsMin != below)
        {
            // Threshold cross — animate. ColorAnimation reads the live current
            // Color (whatever the previous animation finished/interrupted at)
            // when From is omitted, so this picks up cleanly even if the user
            // crosses back and forth quickly.
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
            var anim       = new ColorAnimation { To = target,      Duration = TimeSpan.FromMilliseconds(ColourFadeMs), EasingFunction = ease };
            var animAlpha  = new ColorAnimation { To = targetAlpha, Duration = TimeSpan.FromMilliseconds(ColourFadeMs), EasingFunction = ease };
            // The four brushes are guaranteed non-null in this branch
            // (see lazy-init above); silence CS8602 explicitly so the
            // CI's nullable-warnings-as-errors gate doesn't trip.
            _brushFillLeft!.BeginAnimation(SolidColorBrush.ColorProperty, anim);
            _brushFillRight!.BeginAnimation(SolidColorBrush.ColorProperty, anim);
            _brushSolidNeg!.BeginAnimation(SolidColorBrush.ColorProperty, animAlpha);
            _brushSolidPos!.BeginAnimation(SolidColorBrush.ColorProperty, animAlpha);
            _wasBelowOsMin             = below;
            _lastBrightnessTargetColor = target;
        }
        else if (_lastBrightnessTargetColor != target)
        {
            // No crossing this frame — but the accent itself just changed
            // (theme switch, user picked a new accent in Settings). Snap the
            // brushes without animation so the fill colour stays consistent.
            //
            // Critically: we compare _lastBrightnessTargetColor (our last
            // INTENDED target) to the current target, NOT the brush's live
            // .Color which is the in-animation value. The old code did the
            // latter, which mistakenly cancelled the fade on every drag-tick
            // because the live colour disagrees with target mid-fade.
            _brushFillLeft!.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _brushFillRight!.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _brushSolidNeg!.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _brushSolidPos!.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _brushFillLeft.Color  = target;
            _brushFillRight.Color = target;
            _brushSolidNeg.Color  = targetAlpha;
            _brushSolidPos.Color  = targetAlpha;
            _lastBrightnessTargetColor = target;
        }

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

        // v0.6.71: MidMarker is a "below-OS-min" indicator — hide it
        // above the threshold. Previously rendered at all brightness
        // levels; at full brightness during the bounds-pop animation
        // the scale transform briefly showed it as a tiny line bisecting
        // the bar. Hiding it above 50 removes the visual noise without
        // sacrificing the cue when the user actually goes below OS min.
        if (MidMarker != null)
        {
            MidMarker.Visibility = display < OsMinThreshold
                ? Visibility.Visible : Visibility.Collapsed;
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
            GlassBackdrop.Visibility   = Visibility.Collapsed;
            GlassBackdropBrush.ImageSource = null;
            GlassHighlights.Visibility = Visibility.Collapsed;
            GlassHighlightsBrush.ImageSource = null;
        }

        // Bar elements
        TrackLeft.Background   = new SolidColorBrush(p.Track);
        TrackRight.Background  = new SolidColorBrush(p.Track);
        // v0.6.19: brightness fill split at the 50% mark.
        //   FillLeft  = dim shade (signals "below Windows native min").
        //   FillRight = accent (Windows native brightness range).
        // Both anchored to the bar's left edge — see UpdateBrightnessBar.
        // FillLeft/FillRight Background is owned by the animated brushes that
        // UpdateBrightnessBar manages — see _brushFillLeft / _brushFillRight.
        // Don't replace those Background brushes here; the next UpdateBrightnessBar
        // tick picks up any accent change and snap-updates the brush in place.
        MidMarker.Background   = new SolidColorBrush(p.MidMarker);
        // v0.6.69: sun (brightness) icon honours the user-pickable
        // colour instead of the palette's auto colour. Candle (warmth)
        // icon still follows the palette/luminance auto colour so it
        // stays readable across light + dark backgrounds.
        IconText.Foreground    = new SolidColorBrush(_sunIconColor);
        CandleIcon.Fill        = new SolidColorBrush(p.Icon);
        WarmthTrack.Background = new SolidColorBrush(p.Track);
        WarmthStartStop.Color  = p.WarmthStart;

        // Solid-fill layer brushes — same colour scheme as the thin bar but
        // applied to the full-pill-width fills. Slight alpha reduction so the
        // live glass refraction (or Subtle blur) is still visible underneath.
        // Same dim/accent split as the thin bar, but at 75% alpha so the live
        // glass refraction underneath still shows through.
        // Same as FillLeft/FillRight above — Background is owned by the animated
        // brushes _brushSolidNeg / _brushSolidPos. UpdateBrightnessBar manages.
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
            _liveGlass = new LiveGlassController(this, GlassBackdropBrush, GlassHighlightsBrush);
        }
        GlassBackdrop.Visibility   = Visibility.Visible;
        GlassHighlights.Visibility = Visibility.Visible;

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

        // v0.6.69: sun icon uses the user-pickable colour. The candle
        // (warmth) icon still uses the luminance-based auto colour so
        // it stays legible on any glass backdrop.
        IconText.Foreground = new SolidColorBrush(_sunIconColor);
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
            _exitInProgress = false;
            // INITIAL SHOW: run the full Dynamic-Island entry animation.
            // (On re-press while visible we deliberately skip this so the OSD
            // doesn't shrink back to a circle and re-pop on every key tap.)

            if (_backdrop == BackdropStyle.LiquidGlass && _useTransparency)
                RefreshGlass();

            // Cancel any animations leftover from the previous hide cycle.
            _entryClip!.BeginAnimation(RectangleGeometry.RectProperty, null);
            _entrySlide!.BeginAnimation(TranslateTransform.YProperty, null);
            _shadowSlide!.BeginAnimation(TranslateTransform.YProperty, null);
            ShadowImage.BeginAnimation(OpacityProperty, null);
            // v0.6.77: ShadowPillRect no longer animates per-frame, but
            // we still clear any stale animation in case it's holding
            // an old value from a previous show.
            BeginAnimation(ShadowPillRectProperty, null);
            ShadowPillRect = ShadowFullVisibleRect();
            BeginAnimation(OpacityProperty, null);

            // Match the clip's corner radius to the user's chosen pill corner
            // radius so the shape morph hugs the actual pill edges rather than
            // over-rounding when the user has dialled it down.
            double pillR = (LiveGlassController.PillHeightDip / 2.0) * (_glass.CornerRadius / 100.0);
            _entryClip.RadiusX = pillR;
            _entryClip.RadiusY = pillR;

            // Seed the start state, then Show() and animate to rest.
            _entryClip.Rect = SeedCircleRect();
            _entrySlide.Y   = _slideFromBelowDip;
            _shadowSlide.Y  = _slideFromBelowDip;
            // v0.6.69: the shadow bitmap morphs with the pill via the
            // ShadowPillRect dependency property (RectAnimation rebuilds
            // the bitmap per frame). Seed it to the start rect now;
            // RebuildShadowBitmap runs on the property change.
            ShadowPillRect  = ShadowSeedVisibleRect();
            ShadowImage.Opacity = 0;
            Opacity         = 0;
            Show();
            // Place us at the right z-order layer (just below the taskbar if
            // the taskbar is topmost; otherwise force topmost). Has to be
            // after Show() because before that the window has no z-order to
            // manipulate.
            PositionOsdZOrder();

            var spring  = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = EntryBackAmplitude };
            var smooth  = new QuadraticEase { EasingMode = EasingMode.EaseOut };

            // Slide leads: starts at t=0, runs the full entry duration with a
            // BackEase spring overshoot near the end so the pill bounces into
            // place rather than locking on the rest position.
            var slideAnim = new DoubleAnimation
            {
                From = _slideFromBelowDip,
                To   = 0,
                Duration = TimeSpan.FromMilliseconds(EntryDurationMs),
                EasingFunction = spring,
            };

            // Morph trails by MorphLeadMs: the seed circle is most of the way
            // up before it starts stretching into the pill. Both finish at
            // the same t=EntryDurationMs so the user sees a single coordinated
            // motion settle, not two animations ending separately.
            var rectAnim = new RectAnimation
            {
                From = SeedCircleRect(),
                To   = FullPillRect(),
                BeginTime = TimeSpan.FromMilliseconds(MorphLeadMs),
                Duration = TimeSpan.FromMilliseconds(EntryDurationMs - MorphLeadMs),
                EasingFunction = smooth,
            };

            var fadeIn = new DoubleAnimation
            {
                From = 0,
                To   = 1.0,
                Duration = TimeSpan.FromMilliseconds(FadeInDurationMs),
                EasingFunction = smooth,
            };

            _entryClip.BeginAnimation(RectangleGeometry.RectProperty, rectAnim);
            _entrySlide.BeginAnimation(TranslateTransform.YProperty, slideAnim);
            // v0.6.77: dropped the per-frame ShadowPillRect rebuild.
            // Each rebuild rasterised the 480×200 shadow bitmap with a
            // DropShadowEffect at ~5-10 ms a pop on the UI thread — over
            // the 320 ms entry that was ~75 ms of cumulative blocking,
            // exactly the lag the user could feel before the OSD would
            // appear. Shadow now stays at the rest pill rect (rendered
            // once on SetOsdShadow) and just slides + fades in alongside
            // the pill morph. The shape mismatch during the brief morph
            // is barely perceptible because the halo is Gaussian-soft
            // and the eye is tracking the pill, not the halo's outline.
            var shadowSlideAnim = new DoubleAnimation
            {
                From = _slideFromBelowDip,
                To   = 0,
                Duration = TimeSpan.FromMilliseconds(EntryDurationMs),
                EasingFunction = spring,
            };
            var shadowFadeIn = new DoubleAnimation
            {
                From = 0,
                To   = 1.0,
                BeginTime = TimeSpan.FromMilliseconds(MorphLeadMs),
                Duration  = TimeSpan.FromMilliseconds(EntryDurationMs - MorphLeadMs),
                EasingFunction = smooth,
            };
            _shadowSlide.BeginAnimation(TranslateTransform.YProperty, shadowSlideAnim);
            ShadowImage.BeginAnimation(OpacityProperty, shadowFadeIn);
            BeginAnimation(OpacityProperty, fadeIn);
        }
        // else: already visible (mid-show re-press) — leave the current geometry
        // alone and just refresh the hide timer below. The new value is already
        // showing because UpdateBrightnessBar/UpdateWarmthBar ran before Flash().
        //
        // v0.6.77: if the OSD is mid-exit (fade-out animation still running
        // toward Opacity=0), the previous code path would let that fade
        // complete and auto-hide — so the next key press felt sluggish:
        // user saw the OSD fading out, pressed a key, OSD finished fading
        // out then a fresh entry animation had to start from scratch.
        // Now we detect that case, cancel every active exit animation,
        // and snap the OSD back to its fully-visible rest state. The
        // hide timer (started below regardless) gives the user the full
        // ShowDurationMs to look at the value before the next exit.
        else if (_exitInProgress)
        {
            _exitInProgress = false;
            BeginAnimation(OpacityProperty, null);
            Opacity = 1.0;
            ShadowImage.BeginAnimation(OpacityProperty, null);
            ShadowImage.Opacity = 1.0;
            _entryClip!.BeginAnimation(RectangleGeometry.RectProperty, null);
            _entryClip.Rect = FullPillRect();
            _entrySlide!.BeginAnimation(TranslateTransform.YProperty, null);
            _entrySlide.Y = 0;
            _shadowSlide!.BeginAnimation(TranslateTransform.YProperty, null);
            _shadowSlide.Y = 0;
        }

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
        // v0.6.56: don't arm the hide timer when pinned by the Settings
        // window's live-preview mode.
        if (!_pinned) _hideTimer.Start();
    }

    /// <summary>v0.6.56: lock the OSD visible (true) or release it back
    /// to its normal auto-hide behaviour (false). Settings window calls
    /// SetPinned(true) when the user starts interacting with an OSD-
    /// affecting slider, false when focus leaves all such controls.</summary>
    public void SetPinned(bool pinned)
    {
        _pinned = pinned;
        if (pinned)
        {
            _hideTimer?.Stop();
        }
        else
        {
            // Restart the hide countdown so the OSD goes away soon
            // after the user releases the slider.
            _hideTimer?.Stop();
            _hideTimer?.Start();
        }
    }

    /// <summary>v0.6.57: live-update the universal pill shadow's
    /// visual params from settings.
    /// <para>v0.6.68: the shadow is now a pre-rendered bitmap (see
    /// <see cref="RebuildShadowBitmap"/>) rather than a live
    /// DropShadowEffect. This method stashes the new params and
    /// triggers a bitmap rebuild — which is short-circuited if nothing
    /// actually changed, so calling it on every slider tick is safe.
    /// The rebuild costs ~5-10 ms on the UI thread.</para></summary>
    /// <summary>v0.6.69: live-update the sun icon's colour. Called from
    /// the host whenever settings change. If the OSD is currently
    /// showing brightness, the icon repaints immediately; otherwise the
    /// new colour takes effect on the next brightness show.</summary>
    public void SetSunIconColor(Color color)
    {
        _sunIconColor = color;
        var brush = new SolidColorBrush(color);
        if (IconText != null)
        {
            IconText.Foreground = brush;
        }
        // v0.6.70: the filled-sun Path uses the same colour as the
        // glyph would (one source of truth — the user's pick).
        if (SunIconFilled != null)
        {
            SunIconFilled.Fill = brush;
        }
    }

    /// <summary>v0.6.78: paint a matching black tint on top of the OSD
    /// pill's glass interior so the OSD reads at the same brightness as
    /// the dimmed desktop. The desktop dim is the per-monitor
    /// OverlayWindow's opacity; UnderlitHost forwards
    /// <see cref="UnderlitEngine.CurrentOverlayOpacity"/> here on every
    /// change. Capped at 0.92 mirroring OverlayWindow.SetDimOpacity so
    /// the OSD never becomes a fully-black pill (which would defeat the
    /// purpose of showing the brightness value).</summary>
    public void SetDimOverlayLevel(double opacity)
    {
        if (DimTint == null) return;
        DimTint.Opacity = Math.Clamp(opacity, 0.0, 0.92);
    }

    /// <summary>v0.6.71: set the right-side inset of the ContentRoot
    /// Grid inside the OSD pill. Smaller value = the bar (and warmth
    /// indicator) extends closer to the pill's right edge.
    /// Left margin stays at the icon's column reserve so the icon
    /// position is preserved.</summary>
    public void SetBarRightInset(int rightInsetDip)
    {
        int inset = Math.Clamp(rightInsetDip, 12, 120);
        if (ContentRoot == null) return;
        var m = ContentRoot.Margin;
        ContentRoot.Margin = new Thickness(m.Left, m.Top, inset, m.Bottom);
        // Bar fill widths are recalculated against the (possibly
        // changed) BarRoot.ActualWidth.
        if (_mode == Mode.Brightness) UpdateBrightnessBar(_lastBrightness);
        else                          UpdateWarmthBar(_lastWarmth);
    }

    public void SetOsdShadow(int opacity0to100, int blurRadiusDip, int offsetDip, int angleDeg, int spreadDip)
    {
        try
        {
            _shadowOpacityPct = Math.Clamp(opacity0to100, 0, 100);
            _shadowBlurDip    = Math.Clamp(blurRadiusDip, 0, 80);
            _shadowOffsetDip  = Math.Clamp(offsetDip,    0, 40);
            _shadowAngleDeg   = ((angleDeg % 360) + 360) % 360;
            _shadowSpreadDip  = Math.Clamp(spreadDip,    0, 60);

            // v0.6.69: settings push outside of animation snaps the
            // pill rect to its rest (full pill) value so the rebuild
            // below renders the correct shape. Mid-animation calls
            // will be immediately overridden by the next animation tick.
            BeginAnimation(ShadowPillRectProperty, null);
            ShadowPillRect = ShadowFullVisibleRect();
            RebuildShadowBitmap();

            // Hide the Image entirely at zero opacity to skip the render.
            if (ShadowImage != null)
            {
                ShadowImage.Visibility = opacity0to100 <= 0
                    ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        catch { /* never let a shadow tweak crash the OSD */ }
    }

    /// <summary>
    /// Reverse of the entry: pill shrinks back to the seed circle and slides
    /// down behind the taskbar while fading out. v0.6.32: total duration and
    /// timing structure mirror the entry. Morph leads (pill compresses to
    /// circle first), slide trails (the now-circular seed drops behind the
    /// taskbar). Both end at t=ExitDurationMs.
    /// </summary>
    private void StartExitAnimation()
    {
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseIn };

        // Morph leads: pill collapses to the seed circle in the first
        // (ExitDurationMs - MorphLeadMs) ms. By the time the slide finishes
        // its descent, the shape is already a circle.
        var rectAnim = new RectAnimation
        {
            From = FullPillRect(),
            To   = SeedCircleRect(),
            Duration = TimeSpan.FromMilliseconds(ExitDurationMs - MorphLeadMs),
            EasingFunction = ease,
        };

        // Slide trails: starts at t=0, ends at t=ExitDurationMs — runs the
        // whole exit alongside the morph. Morph finishing earlier means the
        // last sliver of the slide (last MorphLeadMs ms) is a pure circle
        // dropping behind the taskbar, which mirrors the entry's "circle
        // climbing up" leg.
        var slideAnim = new DoubleAnimation
        {
            From = 0,
            To   = _slideFromBelowDip,
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
                _exitInProgress = false; // exit reached its end naturally
                // Stop the WGC render ticker while the OSD is invisible — no point
                // burning frames nobody can see.
                _liveGlass?.StopLiveTicker();
            }
        };

        _entryClip!.BeginAnimation(RectangleGeometry.RectProperty, rectAnim);
        _entrySlide!.BeginAnimation(TranslateTransform.YProperty, slideAnim);
        // v0.6.77: dropped the per-frame ShadowPillRect collapse on exit
        // (same reason as the entry — every frame rebuild was burning
        // ~5-10 ms of UI thread). Shadow now just slides down + fades
        // out alongside the pill exit.
        var shadowSlideAnim = new DoubleAnimation
        {
            From = 0,
            To   = _slideFromBelowDip,
            Duration = TimeSpan.FromMilliseconds(ExitDurationMs),
            EasingFunction = ease,
        };
        var shadowFadeOut = new DoubleAnimation
        {
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(ExitDurationMs - MorphLeadMs),
            EasingFunction = ease,
        };
        _shadowSlide!.BeginAnimation(TranslateTransform.YProperty, shadowSlideAnim);
        ShadowImage.BeginAnimation(OpacityProperty, shadowFadeOut);
        BeginAnimation(OpacityProperty, fadeOut);
        _exitInProgress = true;
    }
}
