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
    private const double PillBottomYDip = 56;
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
        // v0.6.35: clip + transform now live on ClipRoot (inside the 300×66
        // VisibleClip Border) instead of WindowRoot. The outer VisibleClip
        // masks out the bottom 94 dip of the window, so the seed circle's
        // slide-from-below animation visually emerges from behind the
        // bottom edge of its visible zone — faking the "behind taskbar"
        // effect without depending on actual z-order shenanigans.
        ClipRoot.Clip = _entryClip;

        _entrySlide = new TranslateTransform();
        ClipRoot.RenderTransform = _entrySlide;
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
        const double pillHeight = 46;  // PillBottomYDip - paddingTop = 56 - 10
        double gap = Math.Max(0, _taskbarGapDip);

        _slideFromBelowDip = pillHeight + gap;
        VisibleClip.Height = PillBottomYDip + gap;
        // Window's drawable region needs to be exactly as tall as
        // VisibleClip — anything past that is clipped anyway (and would
        // overlap the taskbar/screen edge if it weren't).
        Height = PillBottomYDip + gap;

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
        IconText.Foreground    = new SolidColorBrush(p.Icon);
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
            _entrySlide.Y   = _slideFromBelowDip;
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
