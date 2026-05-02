using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Underlit.Display;
using Underlit.Hue;
using Underlit.Settings;
using Underlit.Sys;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using TextBox = System.Windows.Controls.TextBox;

namespace Underlit.UI;

public partial class SettingsWindow : Window
{
    public event Action<AppSettings>? Applied;
    private AppSettings _snapshot;
    private readonly IReadOnlyList<DisplayInfo> _displays;
    private readonly HardwareBrightness _hardware;

    public sealed class MonitorRow
    {
        public string DeviceName { get; set; } = "";
        public string StableId { get; set; } = "";
        public string Path { get; set; } = "";
        public double BrightnessOffset { get; set; }
        public int WarmthOffset { get; set; }
    }

    private readonly ObservableCollection<MonitorRow> _monRows = new();
    private readonly FrameworkElement[] _pages;
    private readonly Action<bool> _themeHandler;

    public SettingsWindow(AppSettings settings, IReadOnlyList<DisplayInfo> displays, HardwareBrightness hardware)
    {
        InitializeComponent();
        _snapshot = Clone(settings);
        _displays = displays;
        _hardware = hardware;
        LstMonitors.ItemsSource = _monRows;

        // Sidebar → page switching
        _pages = new FrameworkElement[] { PageGeneral, PageHotkeys, PageSchedule, PageLights, PageMonitors, PageExclusions };
        NavList.SelectionChanged += (_, _) => ShowSelectedPage();

        // Theming — initial, plus live follow on Windows theme change
        ApplyTheme(ThemeInfo.IsDarkMode());
        _themeHandler = isDark => Dispatcher.BeginInvoke(() => ApplyTheme(isDark));
        ThemeInfo.ThemeChanged += _themeHandler;
        Closed += (_, _) => ThemeInfo.ThemeChanged -= _themeHandler;

        // Populate the sidebar logo. Render our programmatic icon to a WPF ImageSource.
        AppLogo.Source = RenderLogoToImageSource(56);

        LoadFromSettings();
        UpdateAllValueChips();

        // Live-apply: push as you change, don't require save/close
        foreach (var cb in new[] { ChkStartWithWindows, ChkDisableNightLight, ChkHookNativeKeys, ChkScheduleEnabled, ChkFollowAccent, ChkGlassLiveCapture })
        {
            cb.Checked   += (_, _) => { PushSettings(); RefreshAccentSwatch(); };
            cb.Unchecked += (_, _) => { PushSettings(); RefreshAccentSwatch(); };
        }
        foreach (var sld in new[] { SldBrightnessStep, SldWarmthStep, SldRampDuration, SldNightWarmth,
                                     SldGlassLightAngle, SldGlassLightIntensity, SldGlassRefraction,
                                     SldGlassDepth, SldGlassDispersion, SldGlassFrost,
                                     SldGlassCornerRadius, SldGlassBevelWidth,
                                     SldGlassBevelDepth, SldGlassRimBrightness, SldGlassRimWidth,
                                     SldGlassRimSecondary, SldGlassTintStrength })
        {
            sld.ValueChanged += (_, _) => { PushSettings(); UpdateAllValueChips(); };
        }
        // Schedule/exclusion textboxes still commit on lost-focus.
        foreach (var tb in new TextBox[] { TxtBedtime, TxtWakeTime, TxtExclusions })
        {
            tb.LostFocus += (_, _) => PushSettings();
        }
        // HotkeyField raises ValueChanged whenever the user captures, clears, or
        // commits a new binding via the listen-to-bind UI. Bridge straight to PushSettings.
        foreach (var hk in new HotkeyField[] { TxtHkBrDown, TxtHkBrUp, TxtHkWrDown, TxtHkWrUp, TxtHkBoost, TxtHkToggle })
        {
            hk.ValueChanged += _ => PushSettings();
        }

        LstMonitors.SelectionChanged += OnMonitorSelected;
        SldMonBrOffset.ValueChanged += OnMonitorOffsetChanged;
        SldMonWrOffset.ValueChanged += OnMonitorOffsetChanged;

        BtnPickAccent.Click += OnPickAccent;
        BtnPickTint.Click   += OnPickTint;
        BtnPickHighColor.Click  += OnPickHighColor;
        BtnResetHighColor.Click += OnResetHighColor;

        // Profile management buttons.
        BtnAddProfile.Click    += OnAddProfile;
        BtnRemoveProfile.Click += OnRemoveProfile;
        BtnRenameProfile.Click += OnRenameProfile;
        CboProfile.SelectionChanged += OnProfileSelectionChanged;

        // Schedule graph redraws on any input that changes the curve. We hook
        // these in addition to the existing PushSettings hooks so the redraw is
        // immediate (PushSettings updates _snapshot, the graph re-samples it).
        ScheduleGraph.SizeChanged += (_, _) => RedrawScheduleGraph();
        ChkScheduleEnabled.Checked   += (_, _) => RedrawScheduleGraph();
        ChkScheduleEnabled.Unchecked += (_, _) => RedrawScheduleGraph();
        // Night-warmth slider drives BOTH deep-warmth anchors (BedtimeKelvin
        // + WakeupStartKelvin) in lockstep. The user can still drag those
        // anchors independently on the graph; the slider is the "set both"
        // shortcut. We also keep NightWarmthKelvin in sync for back-compat.
        SldNightWarmth.ValueChanged += (_, _) =>
        {
            int k = (int)SldNightWarmth.Value;
            var p = _snapshot.ActiveProfile();
            p.NightWarmthKelvin   = k;
            p.BedtimeKelvin       = k;
            p.WakeupStartKelvin   = k;
            _snapshot.EnsureScheduleCurveDerived();
            RedrawScheduleGraph();
        };
        foreach (var tb in new[] { TxtBedtime, TxtWakeTime })
            tb.LostFocus += (_, _) => RedrawScheduleGraph();

        // Graph drag — the four anchor points (BedtimeStart, BedtimeEnd,
        // WakeupStart, WakeupEnd) are click-and-drag editable. PreviewMouse*
        // beats any descendant ellipse capture and lets us coalesce drag state
        // in one place.
        ScheduleGraph.PreviewMouseLeftButtonDown += OnGraphMouseDown;
        ScheduleGraph.PreviewMouseMove           += OnGraphMouseMove;
        ScheduleGraph.PreviewMouseLeftButtonUp   += OnGraphMouseUp;
        CboTransparency.SelectionChanged += (_, _) =>
        {
            PushSettings();
            ApplyBackdropToWindow();
            ApplyTheme(ThemeInfo.IsDarkMode());
        };
        CboBackdrop.SelectionChanged += (_, _) =>
        {
            PushSettings();
            ApplyBackdropToWindow();
            ApplyTheme(ThemeInfo.IsDarkMode());
        };
        CboBarStyle.SelectionChanged += (_, _) => PushSettings();

        // Apply DWM backdrop to this window so the settings UI matches the OSD's mode.
        SourceInitialized += (_, _) => ApplyBackdropToWindow();

        // Lights / Hue page wiring.
        WireHuePage();
    }

    private void ApplyBackdropToWindow()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        bool useTransparency = _snapshot.TransparencyEffects switch
        {
            TransparencyMode.On  => true,
            TransparencyMode.Off => false,
            _                    => TransparencyPreference.IsEnabled(),
        };

        bool useBackdrop = useTransparency && _snapshot.OsdBackdrop != BackdropStyle.Solid;
        var kind = useBackdrop ? Acrylic.Backdrop.Acrylic : Acrylic.Backdrop.None;
        Acrylic.Apply(hwnd, kind, ThemeInfo.IsDarkMode());
    }

    private void OnPickAccent(object sender, RoutedEventArgs e)
    {
        // System.Windows.Forms.ColorDialog gives us a familiar Windows colour picker
        // without pulling in a heavyweight WPF dependency.
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
        };
        // Pre-seed with the current custom accent.
        if (TryParseHex(_snapshot.OsdAccentColor, out var current))
        {
            dlg.Color = System.Drawing.Color.FromArgb(current.A, current.R, current.G, current.B);
        }
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            var c = dlg.Color;
            _snapshot.OsdAccentColor = $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
            // If the user is picking a custom colour, switch off "Follow Windows".
            ChkFollowAccent.IsChecked = false;
            RefreshAccentSwatch();
            PushSettings();
        }
    }

    private void OnPickTint(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
        };
        if (TryParseHex(_snapshot.GlassTintColor, out var current))
        {
            dlg.Color = System.Drawing.Color.FromArgb(current.A, current.R, current.G, current.B);
        }
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            var c = dlg.Color;
            _snapshot.GlassTintColor = $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
            RefreshTintSwatch();
            PushSettings();
        }
    }

    private void RefreshTintSwatch()
    {
        Color color = TryParseHex(_snapshot.GlassTintColor, out var c)
            ? c
            : Color.FromRgb(0xFF, 0xFF, 0xFF);
        TintSwatch.Background = new SolidColorBrush(color);
    }

    private void OnPickHighColor(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
        };
        // Pre-seed with the currently-resolved colour (whatever auto would produce
        // if the user is on auto, otherwise their saved choice) so the picker opens
        // on something close to what they're seeing today.
        Color seed = ResolveHighColorForUI();
        dlg.Color = System.Drawing.Color.FromArgb(seed.A, seed.R, seed.G, seed.B);
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            var c = dlg.Color;
            _snapshot.OsdBrightnessHighColor = $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
            RefreshHighColorSwatch();
            PushSettings();
        }
    }

    private void OnResetHighColor(object sender, RoutedEventArgs e)
    {
        _snapshot.OsdBrightnessHighColor = "auto";
        RefreshHighColorSwatch();
        PushSettings();
    }

    /// <summary>The colour the swatch should display right now — either the user's
    /// saved choice or, when on "auto", the same RGB×0.55 darkening the OSD applies
    /// internally so the swatch matches what they actually see.</summary>
    private Color ResolveHighColorForUI()
    {
        bool isAuto = string.IsNullOrWhiteSpace(_snapshot.OsdBrightnessHighColor)
                   || _snapshot.OsdBrightnessHighColor.Equals("auto", StringComparison.OrdinalIgnoreCase);
        if (!isAuto && TryParseHex(_snapshot.OsdBrightnessHighColor, out var c))
            return c;

        // Auto mode — show the darkened accent that the OSD will actually paint.
        Color accent;
        if (ChkFollowAccent.IsChecked == true)
            accent = AccentColorReader.GetAccentColor();
        else
            TryParseHex(_snapshot.OsdAccentColor, out accent);
        return Color.FromArgb(
            accent.A,
            (byte)(accent.R * 0.55),
            (byte)(accent.G * 0.55),
            (byte)(accent.B * 0.55));
    }

    private void RefreshHighColorSwatch()
    {
        bool isAuto = string.IsNullOrWhiteSpace(_snapshot.OsdBrightnessHighColor)
                   || _snapshot.OsdBrightnessHighColor.Equals("auto", StringComparison.OrdinalIgnoreCase);
        HighColorSwatch.Background = new SolidColorBrush(ResolveHighColorForUI());
        LblHighColorMode.Text = isAuto
            ? "Auto — derived from your accent."
            : "Custom — click swatch to change, Auto to reset.";
    }

    private void RefreshAccentSwatch()
    {
        Color color;
        if (ChkFollowAccent.IsChecked == true)
        {
            color = AccentColorReader.GetAccentColor();
        }
        else if (TryParseHex(_snapshot.OsdAccentColor, out var custom))
        {
            color = custom;
        }
        else
        {
            color = AccentColorReader.GetAccentColor();
        }
        AccentSwatch.Background = new SolidColorBrush(color);
        // Disabled visual cue when we're following Windows.
        BtnPickAccent.Opacity = (ChkFollowAccent.IsChecked == true) ? 0.55 : 1.0;

        // The high-brightness swatch in "Auto" mode is derived from the accent,
        // so any accent change has to refresh it too.
        if (HighColorSwatch != null) RefreshHighColorSwatch();
    }

    private static bool TryParseHex(string? hex, out Color c)
    {
        c = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        try { c = (Color)ColorConverter.ConvertFromString(hex); return true; }
        catch { return false; }
    }

    // ---- Sidebar navigation ----

    private void ShowSelectedPage()
    {
        int idx = NavList.SelectedIndex < 0 ? 0 : NavList.SelectedIndex;
        for (int i = 0; i < _pages.Length; i++)
            _pages[i].Visibility = i == idx ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Theme ----

    private void ApplyTheme(bool isDark)
    {
        // The window's WindowBg/SidebarBg become semi-transparent when a DWM backdrop
        // is in play, so the live blur shows through. Cards stay more opaque to keep
        // text readable. In Solid mode, everything is opaque as before.
        bool transparency = _snapshot.TransparencyEffects switch
        {
            TransparencyMode.On  => true,
            TransparencyMode.Off => false,
            _                    => TransparencyPreference.IsEnabled(),
        };
        bool useBackdrop = transparency && _snapshot.OsdBackdrop != BackdropStyle.Solid;
        bool isGlass = useBackdrop && _snapshot.OsdBackdrop == BackdropStyle.LiquidGlass;

        var r = Resources;
        if (isGlass)
        {
            // Liquid Glass — theme-neutral, white-on-glass. Same in dark & light.
            r["App.WindowBg"]      = Brush(0x00000000);  // fully transparent
            r["App.SidebarBg"]     = Brush(0x14FFFFFF);  // faint white wash
            r["App.CardBg"]        = Brush(0x14FFFFFF);
            r["App.CardBorder"]    = Brush(0x33FFFFFF);
            r["App.Divider"]       = Brush(0x14FFFFFF);
            r["App.TextPrimary"]   = Brush(0xFFFFFFFF);
            r["App.TextSecondary"] = Brush(0xCCFFFFFF);
            r["App.HoverBg"]       = Brush(0x18FFFFFF);
            r["App.SelectedBg"]    = Brush(0x28FFFFFF);
            r["App.Accent"]        = Brush(0xFF60CDFF);
            r["App.TrackBg"]       = Brush(0x33FFFFFF);
            r["App.InputBg"]       = Brush(0x14FFFFFF);
            r["App.ToggleTrackOff"]= Brush(0x33FFFFFF);
        }
        else if (isDark)
        {
            r["App.WindowBg"]      = Brush(useBackdrop ? 0x66202020u : 0xFF202020u);
            r["App.SidebarBg"]     = Brush(useBackdrop ? 0x55181818u : 0xFF1A1A1Au);
            r["App.CardBg"]        = Brush(useBackdrop ? 0xCC2B2B2Bu : 0xFF2B2B2Bu);
            r["App.CardBorder"]    = Brush(0x1FFFFFFF);
            r["App.Divider"]       = Brush(0x14FFFFFF);
            r["App.TextPrimary"]   = Brush(0xFFFFFFFF);
            r["App.TextSecondary"] = Brush(0xB3FFFFFF);
            r["App.HoverBg"]       = Brush(0x14FFFFFF);
            r["App.SelectedBg"]    = Brush(0x24FFFFFF);
            r["App.Accent"]        = Brush(0xFF60CDFF);
            r["App.TrackBg"]       = Brush(0x33FFFFFF);
            r["App.InputBg"]       = Brush(useBackdrop ? 0x661E1E1Eu : 0xFF1E1E1Eu);
            r["App.ToggleTrackOff"]= Brush(0x33FFFFFF);
        }
        else
        {
            r["App.WindowBg"]      = Brush(useBackdrop ? 0x66F3F3F3u : 0xFFF3F3F3u);
            r["App.SidebarBg"]     = Brush(useBackdrop ? 0x55EDEDEDu : 0xFFEDEDEDu);
            r["App.CardBg"]        = Brush(useBackdrop ? 0xCCFFFFFFu : 0xFFFFFFFFu);
            r["App.CardBorder"]    = Brush(0x14000000);
            r["App.Divider"]       = Brush(0x0D000000);
            r["App.TextPrimary"]   = Brush(0xFF1F1F1F);
            r["App.TextSecondary"] = Brush(0x99000000);
            r["App.HoverBg"]       = Brush(0x0D000000);
            r["App.SelectedBg"]    = Brush(0x1A000000);
            r["App.Accent"]        = Brush(0xFF005FB8);
            r["App.TrackBg"]       = Brush(0x22000000);
            r["App.InputBg"]       = Brush(useBackdrop ? 0x66FFFFFFu : 0xFFFFFFFFu);
            r["App.ToggleTrackOff"]= Brush(0x22000000);
        }
    }

    private static SolidColorBrush Brush(uint argb)
    {
        byte a = (byte)((argb >> 24) & 0xFF);
        byte r = (byte)((argb >> 16) & 0xFF);
        byte g = (byte)((argb >> 8)  & 0xFF);
        byte b = (byte)(argb         & 0xFF);
        var br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        br.Freeze();
        return br;
    }

    // ---- Logo rendering ----

    /// <summary>
    /// Borrows the System.Drawing-generated logo from TrayIcon.CreateLogoIcon
    /// and converts to a WPF ImageSource for display in the sidebar.
    /// </summary>
    private static ImageSource RenderLogoToImageSource(int size)
    {
        using var icon = TrayIcon.CreateLogoIcon(size);
        using var bmp = icon.ToBitmap();
        using var ms = new System.IO.MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = ms;
        img.EndInit();
        img.Freeze();
        return img;
    }

    // ---- Live value labels next to sliders ----

    private void UpdateAllValueChips()
    {
        LblBrightnessStep.Text = ((int)SldBrightnessStep.Value).ToString();
        LblWarmthStep.Text     = ((int)SldWarmthStep.Value) + " K";
        LblRampDuration.Text   = ((int)SldRampDuration.Value) + " ms";
        LblNightWarmth.Text    = ((int)SldNightWarmth.Value) + " K";
        LblMonBr.Text          = ((int)SldMonBrOffset.Value >= 0 ? "+" : "") + (int)SldMonBrOffset.Value;
        LblMonWr.Text          = ((int)SldMonWrOffset.Value) + " K";

        // Live preview swatch for the night-warmth slider — same gamma → RGB
        // multipliers the engine applies, so the swatch is colour-accurate.
        if (NightWarmthPreview != null)
        {
            int k = (int)SldNightWarmth.Value;
            var (r, g, b) = Underlit.Display.GammaRampApplier.KelvinToRgbMultipliers(k);
            NightWarmthPreview.Background = new SolidColorBrush(Color.FromRgb(
                (byte)(r * 255), (byte)(g * 255), (byte)(b * 255)));
        }
    }

    // ---- Schedule graph + circadian recommendation ----

    /// <summary>
    /// Redraw the 24-hour warmth-curve preview using the values currently in the
    /// schedule UI fields (bedtime/wakeup textboxes + night-warmth slider).
    ///   X axis = time of day, 0..24h, left → right.
    ///   Y axis = kelvin, 1500..6500, bottom → top (so bedtime warmth dips low and
    ///            daytime neutral sits at the top).
    /// We sample <c>Scheduler.ComputeWarmth</c> every 10 minutes (144 samples) and
    /// draw the result as a Polyline. A subtle gradient stripe behind the line
    /// fades from cool (top, daytime) to warm (bottom, night) so the curve reads
    /// at a glance even without axis labels.
    /// </summary>
    /// <summary>Bottom margin reserved for time labels under the curve (dip).</summary>
    private const double GraphLabelHeight = 16;

    /// <summary>Hit radius (dip) for the draggable circles. Larger than the visual
    /// radius so the user has a generous click target.</summary>
    private const double GraphPointHitRadius = 12;
    /// <summary>Visual radius of the draggable circles (dip).</summary>
    private const double GraphPointVisualRadius = 6;

    /// <summary>The four anchor points the user can drag horizontally to retime
    /// the schedule curve. Each id corresponds to a TimeOfDay slot on the active
    /// profile that <see cref="OnGraphMouseMove"/> updates.</summary>
    private enum SchedulePointId { BedtimeStart, BedtimeEnd, WakeupStart, WakeupEnd }

    private SchedulePointId? _draggingSchedulePoint;

    private void RedrawScheduleGraph()
    {
        if (ScheduleGraph == null) return;
        double w = ScheduleGraph.ActualWidth;
        double h = ScheduleGraph.ActualHeight;
        if (w <= 1 || h <= 1) return;

        // Reserve the bottom strip for axis labels — the curve and points draw
        // into the area above this. We compute kelvin → y from this reduced
        // height so labels never overlap the curve.
        double curveH = Math.Max(1, h - GraphLabelHeight);

        ScheduleGraph.Children.Clear();

        // Background: vertical gradient from cool→warm to suggest "lower kelvin = warmer light".
        var bg = new System.Windows.Shapes.Rectangle
        {
            Width = w, Height = curveH,
            RadiusX = 6, RadiusY = 6,
            Fill = new LinearGradientBrush(
                Color.FromArgb(0x18, 0xA8, 0xC8, 0xFF),  // cool wash at top
                Color.FromArgb(0x28, 0xFF, 0xA0, 0x50),  // warm wash at bottom
                90),
        };
        Canvas.SetLeft(bg, 0); Canvas.SetTop(bg, 0);
        ScheduleGraph.Children.Add(bg);

        // Faint vertical hour markers at 6, 12, 18 — orientation cues.
        foreach (int hour in new[] { 6, 12, 18 })
        {
            double x = hour / 24.0 * w;
            var marker = new System.Windows.Shapes.Line
            {
                X1 = x, Y1 = 0, X2 = x, Y2 = curveH,
                Stroke = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 1,
            };
            ScheduleGraph.Children.Add(marker);
        }

        // Time labels along the bottom — every 6 hours, plus end caps.
        foreach (int hour in new[] { 0, 6, 12, 18, 24 })
        {
            double x = hour / 24.0 * w;
            string text = hour == 24 ? "24h"
                       : hour == 0  ? "0h"
                                    : $"{hour}h";
            var label = new TextBlock
            {
                Text = text,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
            };
            // Measure to centre the label under its tick.
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double labelW = label.DesiredSize.Width;
            // Clamp to graph bounds so the 0h label doesn't get clipped on the left.
            double labelX = Math.Max(0, Math.Min(w - labelW, x - labelW / 2));
            Canvas.SetLeft(label, labelX);
            Canvas.SetTop(label, curveH + 2);
            ScheduleGraph.Children.Add(label);
        }

        // Build a temporary AppSettings from the live UI values + active-profile
        // points so the graph tracks unsaved edits.
        var s = SnapshotForSchedule();

        var pts = new System.Windows.Media.PointCollection();
        const int samples = 144;
        for (int i = 0; i <= samples; i++)
        {
            double hour = i / (double)samples * 24.0;
            DateTime t = DateTime.Today.AddHours(hour);
            int k = Underlit.Core.Scheduler.ComputeWarmth(t, s);
            double x = hour / 24.0 * w;
            double y = curveH - ((k - 1500) / 5000.0) * curveH;
            pts.Add(new Point(x, y));
        }

        var line = new System.Windows.Shapes.Polyline
        {
            Points = pts,
            Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
        };
        ScheduleGraph.Children.Add(line);

        // Draggable anchor points — each pulls its kelvin from the active
        // profile so vertical drag really retargets warmth at that anchor.
        DrawSchedulePoint(SchedulePointId.WakeupStart, s.WakeupStart, s.WakeupStartKelvin,  w, curveH);
        DrawSchedulePoint(SchedulePointId.WakeupEnd,   s.WakeupEnd,   s.WakeupEndKelvin,    w, curveH);
        DrawSchedulePoint(SchedulePointId.BedtimeStart, s.BedtimeStart, s.BedtimeStartKelvin, w, curveH);
        DrawSchedulePoint(SchedulePointId.BedtimeEnd,   s.BedtimeEnd,   s.BedtimeEndKelvin,   w, curveH);

        // "Now" indicator — a vertical line at the current time so the user can
        // read off "what kelvin am I targeting right now" at a glance.
        DateTime now = DateTime.Now;
        double nowX = (now.Hour + now.Minute / 60.0) / 24.0 * w;
        var nowLine = new System.Windows.Shapes.Line
        {
            X1 = nowX, Y1 = 0, X2 = nowX, Y2 = curveH,
            Stroke = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 1.5,
            StrokeDashArray = new System.Windows.Media.DoubleCollection(new[] { 3.0, 3.0 }),
        };
        ScheduleGraph.Children.Add(nowLine);
    }

    /// <summary>Render one of the four draggable anchor markers at (timeOfDay, kelvin).
    /// Two layered ellipses: a transparent outer one that's the hit target, and a
    /// smaller visible one. Tags are used by the mouse-down handler to identify which
    /// point was grabbed.</summary>
    private void DrawSchedulePoint(SchedulePointId id, TimeOfDay t, int kelvin, double w, double curveH)
    {
        double x = t.AsHourFractional / 24.0 * w;
        double y = curveH - ((kelvin - 1500) / 5000.0) * curveH;

        // Larger transparent hit-target — improves drag affordance without
        // making the visible marker bulky. Cursor is "SizeAll" because each
        // anchor is freely draggable in both axes (X = retime, Y = retarget kelvin).
        var hit = new System.Windows.Shapes.Ellipse
        {
            Width = GraphPointHitRadius * 2,
            Height = GraphPointHitRadius * 2,
            Fill = System.Windows.Media.Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.SizeAll,
            Tag = id,
            ToolTip = SchedulePointTooltip(id, t, kelvin),
        };
        Canvas.SetLeft(hit, x - GraphPointHitRadius);
        Canvas.SetTop(hit, y - GraphPointHitRadius);
        ScheduleGraph.Children.Add(hit);

        // Visible marker — a filled circle with a thin accent ring.
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = GraphPointVisualRadius * 2,
            Height = GraphPointVisualRadius * 2,
            Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
            Stroke = (Brush?)TryFindResource("App.Accent") ?? new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF)),
            StrokeThickness = 2,
            IsHitTestVisible = false,   // hit target above handles input
        };
        Canvas.SetLeft(dot, x - GraphPointVisualRadius);
        Canvas.SetTop(dot, y - GraphPointVisualRadius);
        ScheduleGraph.Children.Add(dot);
    }

    private static string SchedulePointTooltip(SchedulePointId id, TimeOfDay t, int k) => id switch
    {
        SchedulePointId.BedtimeStart => $"Evening ramp starts at {t.Hour:D2}:{t.Minute:D2} ({k} K)",
        SchedulePointId.BedtimeEnd   => $"Bedtime — {k} K from {t.Hour:D2}:{t.Minute:D2}",
        SchedulePointId.WakeupStart  => $"Morning ramp starts at {t.Hour:D2}:{t.Minute:D2} ({k} K)",
        SchedulePointId.WakeupEnd    => $"Wake — {k} K by {t.Hour:D2}:{t.Minute:D2}",
        _ => string.Empty,
    };

    // ---- Schedule-graph mouse-drag handling ----

    private void OnGraphMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Find which anchor (if any) was clicked. We hit-test the canvas children
        // and look for a Tag that's a SchedulePointId.
        var pos = e.GetPosition(ScheduleGraph);
        var hit = ScheduleGraph.InputHitTest(pos);
        if (hit is FrameworkElement fe && fe.Tag is SchedulePointId id)
        {
            _draggingSchedulePoint = id;
            ScheduleGraph.CaptureMouse();
            UpdatePointFromMouse(id, pos.X, pos.Y);
            e.Handled = true;
        }
    }

    private void OnGraphMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_draggingSchedulePoint is not SchedulePointId id) return;
        var pos = e.GetPosition(ScheduleGraph);
        UpdatePointFromMouse(id, pos.X, pos.Y);
    }

    private void OnGraphMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_draggingSchedulePoint == null) return;
        _draggingSchedulePoint = null;
        ScheduleGraph.ReleaseMouseCapture();
        // Final commit — push to settings on release so the change persists.
        PushSettings();
        UpdateAllValueChips();
        WarmthPreviewEnded?.Invoke();
    }

    /// <summary>Map a mouse position to a (time-of-day, kelvin) pair and write
    /// both into the active profile's corresponding slot. Updates the bedtime
    /// / wake-time input fields if relevant, syncs the legacy ramp window,
    /// redraws the graph, and pings the live-preview event so the engine
    /// warmth follows the dragged point in real time.
    ///
    /// X axis maps to time-of-day (0..24h). Y axis maps to kelvin (1500..6500,
    /// inverted so the top of the graph is the cool end of the spectrum). The
    /// mouseY is measured against the curve area (excludes the bottom label
    /// strip) so the kelvin scale matches the visible curve exactly.</summary>
    private void UpdatePointFromMouse(SchedulePointId id, double mouseX, double mouseY)
    {
        double w = ScheduleGraph.ActualWidth;
        double h = ScheduleGraph.ActualHeight;
        if (w <= 0 || h <= 0) return;
        double curveH = Math.Max(1, h - GraphLabelHeight);

        double clampedX = Math.Clamp(mouseX, 0, w);
        double clampedY = Math.Clamp(mouseY, 0, curveH);
        double hour = clampedX / w * 24.0;
        TimeOfDay t = TimeOfDayFromHourFractional(hour);
        // Invert: top of curve area = max kelvin (6500), bottom = 1500.
        int kelvin = (int)Math.Round(1500 + (1.0 - clampedY / curveH) * 5000);
        kelvin = Math.Clamp(kelvin, 1500, 6500);

        var p = _snapshot.ActiveProfile();
        switch (id)
        {
            case SchedulePointId.BedtimeStart:
                p.BedtimeStart = t;
                p.BedtimeStartKelvin = kelvin;
                break;
            case SchedulePointId.BedtimeEnd:
                p.Bedtime = t;
                p.BedtimeKelvin = kelvin;
                TxtBedtime.Text = Fmt(t);
                break;
            case SchedulePointId.WakeupStart:
                p.WakeupStart = t;
                p.WakeupStartKelvin = kelvin;
                break;
            case SchedulePointId.WakeupEnd:
                p.WakeTime = t;
                p.WakeTimeKelvin = kelvin;
                TxtWakeTime.Text = Fmt(t);
                break;
        }

        // Keep the legacy single-kelvin field in sync with the deepest of the
        // two "deep warmth" anchors, so the NightWarmth slider in Settings
        // still reads back something sensible after a drag.
        p.NightWarmthKelvin = Math.Min(p.BedtimeKelvin, p.WakeupStartKelvin);

        _snapshot.EnsureScheduleCurveDerived();
        RedrawScheduleGraph();
        UpdateAllValueChips();

        // Live preview: ramp the screen to the kelvin the user just dragged
        // the anchor to. They feel the warmth they're configuring instead of
        // just seeing it on a graph.
        WarmthPreviewRequested?.Invoke(kelvin);
    }

    // ============================================================
    // Lights / Philips Hue (Settings → Lights page)
    // ============================================================

    private enum HueUiState { Disconnected, Pairing, Connected }

    /// <summary>Bindable row for the group-selection ItemsControl.</summary>
    private sealed class HueGroupRow : System.ComponentModel.INotifyPropertyChanged
    {
        public string Id { get; init; } = "";
        public string DisplayName { get; init; } = "";
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected != value) { _isSelected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); } }
        }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    private HueBridgeClient? _hueClient;
    private string? _huePairingIp;
    private DispatcherTimer? _huePairTimer;
    private int _huePairSecondsLeft;
    private System.Collections.ObjectModel.ObservableCollection<HueGroupRow>? _hueGroupRows;

    private void WireHuePage()
    {
        BtnHueDiscover.Click       += async (_, _) => await DiscoverHueBridgesAsync();
        BtnHueConnectManual.Click  += (_, _) => StartPairingFlow(TxtHueManualIp.Text.Trim());
        LstHueBridges.SelectionChanged += (_, _) =>
        {
            if (LstHueBridges.SelectedItem is HueDiscoveredBridge b) StartPairingFlow(b.Ip);
        };

        BtnHuePair.Click       += async (_, _) => await TryHuePairAsync();
        BtnHuePairCancel.Click += (_, _) => CancelHuePairing();

        BtnHueUnpair.Click         += (_, _) => UnpairHue();
        BtnHueRefreshGroups.Click  += async (_, _) => await LoadHueGroupsAsync();
        BtnHueTest.Click           += async (_, _) => await TestHueGroupsAsync();

        CboHueColorRange.SelectionChanged += (_, _) =>
        {
            int idx = Math.Max(0, CboHueColorRange.SelectedIndex);
            _snapshot.HueColorRange = (HueColorRangeMode)idx;
            PushSettings();
        };

        // Initial state.
        if (!string.IsNullOrEmpty(_snapshot.HueBridgeIp) && !string.IsNullOrEmpty(_snapshot.HueBridgeUsername))
        {
            _hueClient = new HueBridgeClient(_snapshot.HueBridgeIp!, _snapshot.HueBridgeUsername);
            CboHueColorRange.SelectedIndex = (int)_snapshot.HueColorRange;
            SetHueUiState(HueUiState.Connected);
            _ = LoadHueGroupsAsync();
        }
        else
        {
            SetHueUiState(HueUiState.Disconnected);
        }
    }

    private void SetHueUiState(HueUiState s)
    {
        HuePanelDisconnected.Visibility = s == HueUiState.Disconnected ? Visibility.Visible : Visibility.Collapsed;
        HuePanelPairing.Visibility      = s == HueUiState.Pairing      ? Visibility.Visible : Visibility.Collapsed;
        HuePanelConnected.Visibility    = s == HueUiState.Connected    ? Visibility.Visible : Visibility.Collapsed;

        if (s == HueUiState.Connected && !string.IsNullOrEmpty(_snapshot.HueBridgeIp))
            LblHueBridgeIp.Text = $"Connected to bridge at {_snapshot.HueBridgeIp}";
    }

    private async System.Threading.Tasks.Task DiscoverHueBridgesAsync()
    {
        BtnHueDiscover.IsEnabled = false;
        LblHueDiscoverStatus.Text = "Searching for bridges…";
        LblHueDiscoverStatus.Visibility = Visibility.Visible;
        HueDiscoveredCard.Visibility = Visibility.Collapsed;
        try
        {
            var bridges = await HueDiscovery.DiscoverViaCloudAsync();
            LstHueBridges.ItemsSource = bridges;
            if (bridges.Count == 0)
            {
                LblHueDiscoverStatus.Text =
                    "No bridges found. Make sure your bridge is powered on and on the same network, " +
                    "or enter its IP manually below.";
            }
            else
            {
                LblHueDiscoverStatus.Visibility = Visibility.Collapsed;
                HueDiscoveredCard.Visibility = Visibility.Visible;
            }
        }
        finally { BtnHueDiscover.IsEnabled = true; }
    }

    private void StartPairingFlow(string bridgeIp)
    {
        if (string.IsNullOrWhiteSpace(bridgeIp))
        {
            LblHueDiscoverStatus.Text = "Please enter a bridge IP first.";
            LblHueDiscoverStatus.Visibility = Visibility.Visible;
            return;
        }
        _huePairingIp = bridgeIp;
        _huePairSecondsLeft = 30;
        LblHuePairingStatus.Text = $"Bridge: {bridgeIp}  ·  press the link button now ({_huePairSecondsLeft}s)";
        SetHueUiState(HueUiState.Pairing);

        _huePairTimer?.Stop();
        _huePairTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _huePairTimer.Tick += (_, _) =>
        {
            _huePairSecondsLeft--;
            if (_huePairSecondsLeft <= 0)
            {
                _huePairSecondsLeft = 30;
                LblHuePairingStatus.Text = "Press the button again — the 30-second window has lapsed.";
            }
            else
            {
                LblHuePairingStatus.Text =
                    $"Bridge: {_huePairingIp}  ·  press the link button now ({_huePairSecondsLeft}s)";
            }
        };
        _huePairTimer.Start();
    }

    private void CancelHuePairing()
    {
        _huePairTimer?.Stop();
        _huePairTimer = null;
        _huePairingIp = null;
        SetHueUiState(HueUiState.Disconnected);
    }

    private async System.Threading.Tasks.Task TryHuePairAsync()
    {
        if (string.IsNullOrEmpty(_huePairingIp)) return;
        BtnHuePair.IsEnabled = false;
        LblHuePairingStatus.Text = "Pairing…";
        try
        {
            using var temp = new HueBridgeClient(_huePairingIp);
            string deviceType = $"Underlit#{Environment.MachineName}";
            if (deviceType.Length > 40) deviceType = deviceType[..40];

            var result = await temp.TryPairAsync(deviceType);
            if (!result.Success)
            {
                LblHuePairingStatus.Text = $"Couldn't pair: {result.Error}. " +
                    "Press the link button on the bridge and click Pair again within 30 seconds.";
                return;
            }

            _huePairTimer?.Stop();
            _snapshot.HueBridgeIp       = _huePairingIp;
            _snapshot.HueBridgeUsername = result.Username;
            _hueClient?.Dispose();
            _hueClient = new HueBridgeClient(_huePairingIp, result.Username);
            CboHueColorRange.SelectedIndex = (int)_snapshot.HueColorRange;
            PushSettings();
            SetHueUiState(HueUiState.Connected);
            await LoadHueGroupsAsync();
        }
        finally { BtnHuePair.IsEnabled = true; }
    }

    private async System.Threading.Tasks.Task LoadHueGroupsAsync()
    {
        if (_hueClient == null) return;
        BtnHueRefreshGroups.IsEnabled = false;
        try
        {
            List<HueGroup> groups;
            try { groups = await _hueClient.GetGroupsAsync(); }
            catch (Exception ex)
            {
                LblHueBridgeStatus.Text = $"Bridge unreachable: {ex.Message}";
                return;
            }
            LblHueBridgeStatus.Text = $"Reachable · {groups.Count} group{(groups.Count == 1 ? "" : "s")}";

            var prevSelected = new HashSet<string>(_snapshot.HueSelectedGroupIds, StringComparer.OrdinalIgnoreCase);
            _hueGroupRows = new System.Collections.ObjectModel.ObservableCollection<HueGroupRow>();
            foreach (var g in groups)
            {
                var row = new HueGroupRow
                {
                    Id          = g.Id,
                    DisplayName = g.ToString(),
                    IsSelected  = prevSelected.Contains(g.Id),
                };
                row.PropertyChanged += (_, _) =>
                {
                    _snapshot.HueSelectedGroupIds = _hueGroupRows!
                        .Where(r => r.IsSelected)
                        .Select(r => r.Id)
                        .ToList();
                    PushSettings();
                };
                _hueGroupRows.Add(row);
            }
            HueGroupsList.ItemsSource = _hueGroupRows;
        }
        finally { BtnHueRefreshGroups.IsEnabled = true; }
    }

    private async System.Threading.Tasks.Task TestHueGroupsAsync()
    {
        if (_hueClient == null || _hueGroupRows == null) return;
        var selected = _hueGroupRows.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0)
        {
            LblHueBridgeStatus.Text = "Select at least one group first.";
            return;
        }
        BtnHueTest.IsEnabled = false;
        try
        {
            foreach (var r in selected)
                await _hueClient.SetGroupStateAsync(r.Id, on: true, mireds: 250, brightness254: 254);
            await System.Threading.Tasks.Task.Delay(700);
            foreach (var r in selected)
                await _hueClient.SetGroupStateAsync(r.Id, on: null, mireds: 350, brightness254: 180);
        }
        finally { BtnHueTest.IsEnabled = true; }
    }

    private void UnpairHue()
    {
        _hueClient?.Dispose();
        _hueClient = null;
        _snapshot.HueBridgeIp = null;
        _snapshot.HueBridgeUsername = null;
        _snapshot.HueSelectedGroupIds = new();
        HueGroupsList.ItemsSource = null;
        _hueGroupRows = null;
        TxtHueManualIp.Text = "";
        PushSettings();
        SetHueUiState(HueUiState.Disconnected);
    }

    private static TimeOfDay TimeOfDayFromHourFractional(double hours)
    {
        while (hours < 0)  hours += 24;
        while (hours >= 24) hours -= 24;
        int hour = (int)Math.Floor(hours);
        int min  = (int)Math.Round((hours - hour) * 60);
        if (min >= 60) { hour = (hour + 1) % 24; min = 0; }
        return new TimeOfDay(hour, min);
    }

    /// <summary>Raised while the user is dragging a schedule anchor point. The
    /// host wires this to the engine's PreviewWarmth method so the screen
    /// warmth follows the drag in real time. The corresponding "ended" event
    /// fires on mouse-up so the engine can return to scheduled behaviour.</summary>
    public event Action<int>? WarmthPreviewRequested;
    public event Action? WarmthPreviewEnded;

    /// <summary>Build a transient AppSettings from the schedule UI fields so the
    /// graph reflects unsaved edits. Doesn't touch <c>_snapshot</c>.</summary>
    private AppSettings SnapshotForSchedule()
    {
        var s = new AppSettings
        {
            Bedtime  = ParseTime(TxtBedtime.Text,  _snapshot.Bedtime),
            WakeTime = ParseTime(TxtWakeTime.Text, _snapshot.WakeTime),
            NightWarmthKelvin = (int)SldNightWarmth.Value,
            ScheduleEnabled   = true,  // graph always shows the curve as if active
        };
        s.EnsureScheduleCurveDerived();  // populate the legacy 4-field window from Bedtime/WakeTime
        return s;
    }

    private static string ToHHMM(double hours)
    {
        int hour = (int)Math.Floor(hours);
        int min  = (int)Math.Round((hours - hour) * 60);
        if (min >= 60) { hour = (hour + 1) % 24; min = 0; }
        return $"{hour:D2}:{min:D2}";
    }

    // ---- Profile management ----

    private bool _suppressProfileSelectionEvents;

    /// <summary>Repopulate the profile dropdown from <c>_snapshot.WarmthProfiles</c>.
    /// Keeps the active selection on whichever profile name matches
    /// <c>_snapshot.ActiveProfileName</c>. Fires no SelectionChanged events
    /// while we're rebuilding (suppression flag).</summary>
    private void RefreshProfileDropdown()
    {
        _suppressProfileSelectionEvents = true;
        try
        {
            CboProfile.Items.Clear();
            foreach (var p in _snapshot.WarmthProfiles)
            {
                CboProfile.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Name });
            }
            // Restore active selection.
            for (int i = 0; i < CboProfile.Items.Count; i++)
            {
                if (((ComboBoxItem)CboProfile.Items[i]!).Tag is string n && n == _snapshot.ActiveProfileName)
                {
                    CboProfile.SelectedIndex = i;
                    break;
                }
            }
            // The − button is disabled for the built-in "Recommended" profile.
            var active = _snapshot.WarmthProfiles.FirstOrDefault(p => p.Name == _snapshot.ActiveProfileName);
            BtnRemoveProfile.IsEnabled = active != null && !active.IsBuiltIn;
        }
        finally
        {
            _suppressProfileSelectionEvents = false;
        }
    }

    /// <summary>Mirror the active profile's Bedtime/WakeTime/NightK into the UI
    /// fields, so when the user picks a different profile the inputs reflect it.</summary>
    private void LoadActiveProfileIntoFields()
    {
        var active = _snapshot.WarmthProfiles.FirstOrDefault(p => p.Name == _snapshot.ActiveProfileName);
        if (active == null) return;
        TxtBedtime.Text   = Fmt(active.Bedtime);
        TxtWakeTime.Text  = Fmt(active.WakeTime);
        // Slider position = the deeper of the two "deep warmth" anchors —
        // matches what the user actually sees on the graph.
        SldNightWarmth.Value = Math.Min(active.BedtimeKelvin, active.WakeupStartKelvin);
    }

    /// <summary>Push the current UI field values back into the active profile
    /// object, so user edits to Bedtime/WakeTime/NightK persist with that profile.</summary>
    private void SaveFieldsIntoActiveProfile()
    {
        var active = _snapshot.WarmthProfiles.FirstOrDefault(p => p.Name == _snapshot.ActiveProfileName);
        if (active == null) return;
        active.Bedtime  = ParseTime(TxtBedtime.Text,  active.Bedtime);
        active.WakeTime = ParseTime(TxtWakeTime.Text, active.WakeTime);
        active.NightWarmthKelvin = (int)SldNightWarmth.Value;
    }

    private void OnProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProfileSelectionEvents) return;
        if (CboProfile.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is not string newName) return;
        // Save current field values into the OLD active profile before switching.
        SaveFieldsIntoActiveProfile();
        _snapshot.ActiveProfileName = newName;
        LoadActiveProfileIntoFields();
        UpdateAllValueChips();
        RefreshProfileDropdown();   // refresh − button enable state
        PushSettings();
        RedrawScheduleGraph();
    }

    private void OnAddProfile(object sender, RoutedEventArgs e)
    {
        // Clone the current active profile so the user starts from familiar values.
        var src = _snapshot.WarmthProfiles.FirstOrDefault(p => p.Name == _snapshot.ActiveProfileName)
                  ?? _snapshot.WarmthProfiles[0];
        // Find a non-colliding name.
        string baseName = "My profile";
        string name = baseName;
        for (int i = 2; _snapshot.WarmthProfiles.Any(p => p.Name == name); i++)
            name = $"{baseName} {i}";

        SaveFieldsIntoActiveProfile();   // freeze in any pending edits to the previous profile
        _snapshot.WarmthProfiles.Add(new WarmthProfile
        {
            Name = name,
            IsBuiltIn = false,
            Bedtime = src.Bedtime,
            WakeTime = src.WakeTime,
            NightWarmthKelvin = src.NightWarmthKelvin,
        });
        _snapshot.ActiveProfileName = name;
        RefreshProfileDropdown();
        LoadActiveProfileIntoFields();
        UpdateAllValueChips();
        PushSettings();
        RedrawScheduleGraph();

        // Immediately drop the user into rename mode so they can name the profile.
        OnRenameProfile(this, new RoutedEventArgs());
    }

    private void OnRemoveProfile(object sender, RoutedEventArgs e)
    {
        var active = _snapshot.WarmthProfiles.FirstOrDefault(p => p.Name == _snapshot.ActiveProfileName);
        if (active == null || active.IsBuiltIn) return;   // can't delete the recommended preset

        _snapshot.WarmthProfiles.Remove(active);
        _snapshot.ActiveProfileName = _snapshot.WarmthProfiles[0].Name;
        RefreshProfileDropdown();
        LoadActiveProfileIntoFields();
        UpdateAllValueChips();
        PushSettings();
        RedrawScheduleGraph();
    }

    private void OnRenameProfile(object sender, RoutedEventArgs e)
    {
        var active = _snapshot.WarmthProfiles.FirstOrDefault(p => p.Name == _snapshot.ActiveProfileName);
        if (active == null) return;

        // Lightweight in-place rename — a small input prompt window.
        var dlg = new ProfileRenameDialog(active.Name, _snapshot.WarmthProfiles
            .Where(p => p != active).Select(p => p.Name))
        {
            Owner = this,
        };
        if (dlg.ShowDialog() != true) return;
        var newName = dlg.NewName.Trim();
        if (string.IsNullOrEmpty(newName) || newName == active.Name) return;

        active.Name = newName;
        _snapshot.ActiveProfileName = newName;
        RefreshProfileDropdown();
        PushSettings();
    }

    // ---- Settings load/save ----

    private static AppSettings Clone(AppSettings s)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(s);
        return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
    }

    private void LoadFromSettings()
    {
        ChkStartWithWindows.IsChecked  = _snapshot.StartWithWindows;
        ChkDisableNightLight.IsChecked = _snapshot.DisableWindowsNightLight;
        ChkHookNativeKeys.IsChecked    = _snapshot.HookNativeBrightnessKeys;
        ChkScheduleEnabled.IsChecked   = _snapshot.ScheduleEnabled;
        ChkFollowAccent.IsChecked      = _snapshot.FollowWindowsAccent;
        ChkGlassLiveCapture.IsChecked  = _snapshot.GlassLiveCapture;
        CboTransparency.SelectedIndex  = (int)_snapshot.TransparencyEffects;
        CboBackdrop.SelectedIndex      = (int)_snapshot.OsdBackdrop;
        CboBarStyle.SelectedIndex      = (int)_snapshot.OsdBarStyle;
        RefreshAccentSwatch();

        SldBrightnessStep.Value = _snapshot.BrightnessStep;
        SldWarmthStep.Value     = _snapshot.WarmthStep;
        SldRampDuration.Value   = _snapshot.RampDurationMs;
        SldNightWarmth.Value    = _snapshot.NightWarmthKelvin;

        SldGlassLightAngle.Value     = _snapshot.GlassLightAngleDeg;
        SldGlassLightIntensity.Value = _snapshot.GlassLightIntensity;
        SldGlassRefraction.Value     = _snapshot.GlassRefraction;
        SldGlassDepth.Value          = _snapshot.GlassDepth;
        SldGlassDispersion.Value     = _snapshot.GlassDispersion;
        SldGlassFrost.Value          = _snapshot.GlassFrost;
        SldGlassCornerRadius.Value   = _snapshot.GlassCornerRadius;
        SldGlassBevelWidth.Value     = _snapshot.GlassBevelWidth;
        SldGlassBevelDepth.Value     = _snapshot.GlassBevelDepth;
        SldGlassRimBrightness.Value  = _snapshot.GlassRimBrightness;
        SldGlassRimWidth.Value       = _snapshot.GlassRimWidth;
        SldGlassRimSecondary.Value   = _snapshot.GlassRimSecondary;
        SldGlassTintStrength.Value   = _snapshot.GlassTintStrength;
        RefreshTintSwatch();
        RefreshHighColorSwatch();
        // Schedule graph: redraw with the just-loaded values. The Canvas may not
        // be measured yet (hidden page), so SizeChanged will also fire it.
        Dispatcher.BeginInvoke((Action)RedrawScheduleGraph, DispatcherPriority.Loaded);

        TxtHkBrDown.Value = _snapshot.HotkeyBrightnessDown ?? string.Empty;
        TxtHkBrUp.Value   = _snapshot.HotkeyBrightnessUp   ?? string.Empty;
        TxtHkWrDown.Value = _snapshot.HotkeyWarmthDown     ?? string.Empty;
        TxtHkWrUp.Value   = _snapshot.HotkeyWarmthUp       ?? string.Empty;
        TxtHkBoost.Value  = _snapshot.HotkeyBoost          ?? string.Empty;
        TxtHkToggle.Value = _snapshot.HotkeyToggle         ?? string.Empty;

        // Bedtime + wake time are now per-profile. Make sure the profile list is
        // initialised, then populate the dropdown and the active profile's fields.
        _snapshot.EnsureProfilesInitialized();
        RefreshProfileDropdown();
        LoadActiveProfileIntoFields();
        TxtBedtime.Text  = Fmt(_snapshot.Bedtime);
        TxtWakeTime.Text = Fmt(_snapshot.WakeTime);

        TxtExclusions.Text = string.Join(Environment.NewLine, _snapshot.ExcludedProcessNames);

        _monRows.Clear();
        foreach (var d in _displays)
        {
            var pm = _snapshot.PerMonitor.TryGetValue(d.StableId, out var p) ? p : new PerMonitor();
            _monRows.Add(new MonitorRow
            {
                DeviceName = d.DeviceName,
                StableId = d.StableId,
                Path = _hardware.PathFor(d).ToString(),
                BrightnessOffset = pm.BrightnessOffset,
                WarmthOffset = pm.WarmthOffsetKelvin
            });
        }
    }

    private void OnMonitorSelected(object sender, SelectionChangedEventArgs e)
    {
        if (LstMonitors.SelectedItem is MonitorRow row)
        {
            SldMonBrOffset.Value = row.BrightnessOffset;
            SldMonWrOffset.Value = row.WarmthOffset;
            UpdateAllValueChips();
        }
    }

    private void OnMonitorOffsetChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LstMonitors.SelectedItem is MonitorRow row)
        {
            row.BrightnessOffset = SldMonBrOffset.Value;
            row.WarmthOffset = (int)SldMonWrOffset.Value;
            LstMonitors.Items.Refresh();
            UpdateAllValueChips();
            PushSettings();
        }
    }

    private static string Fmt(TimeOfDay t) => $"{t.Hour:D2}:{t.Minute:D2}";

    private static TimeOfDay ParseTime(string? s, TimeOfDay fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        var parts = s.Split(':');
        if (parts.Length != 2) return fallback;
        if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return fallback;
        return new TimeOfDay(Math.Clamp(h, 0, 23), Math.Clamp(m, 0, 59));
    }

    private void PushSettings()
    {
        _snapshot.StartWithWindows          = ChkStartWithWindows.IsChecked == true;
        _snapshot.DisableWindowsNightLight  = ChkDisableNightLight.IsChecked == true;
        _snapshot.HookNativeBrightnessKeys  = ChkHookNativeKeys.IsChecked == true;
        _snapshot.ScheduleEnabled           = ChkScheduleEnabled.IsChecked == true;
        _snapshot.FollowWindowsAccent       = ChkFollowAccent.IsChecked == true;
        _snapshot.GlassLiveCapture          = ChkGlassLiveCapture.IsChecked == true;
        _snapshot.TransparencyEffects       = (TransparencyMode)Math.Max(0, CboTransparency.SelectedIndex);
        _snapshot.OsdBackdrop               = (BackdropStyle)Math.Max(0, CboBackdrop.SelectedIndex);
        _snapshot.OsdBarStyle               = (OsdBarStyle)Math.Max(0, CboBarStyle.SelectedIndex);

        _snapshot.BrightnessStep  = SldBrightnessStep.Value;
        _snapshot.WarmthStep      = (int)SldWarmthStep.Value;
        _snapshot.RampDurationMs  = (int)SldRampDuration.Value;
        _snapshot.SmoothRamping   = _snapshot.RampDurationMs > 10;
        _snapshot.NightWarmthKelvin = (int)SldNightWarmth.Value;

        _snapshot.GlassLightAngleDeg  = SldGlassLightAngle.Value;
        _snapshot.GlassLightIntensity = SldGlassLightIntensity.Value;
        _snapshot.GlassRefraction     = SldGlassRefraction.Value;
        _snapshot.GlassDepth          = SldGlassDepth.Value;
        _snapshot.GlassDispersion     = SldGlassDispersion.Value;
        _snapshot.GlassFrost          = SldGlassFrost.Value;
        _snapshot.GlassCornerRadius   = SldGlassCornerRadius.Value;
        _snapshot.GlassBevelWidth     = SldGlassBevelWidth.Value;
        _snapshot.GlassBevelDepth     = SldGlassBevelDepth.Value;
        _snapshot.GlassRimBrightness  = SldGlassRimBrightness.Value;
        _snapshot.GlassRimWidth       = SldGlassRimWidth.Value;
        _snapshot.GlassRimSecondary   = SldGlassRimSecondary.Value;
        _snapshot.GlassTintStrength   = SldGlassTintStrength.Value;

        _snapshot.HotkeyBrightnessDown = TxtHkBrDown.Value.Trim();
        _snapshot.HotkeyBrightnessUp   = TxtHkBrUp.Value.Trim();
        _snapshot.HotkeyWarmthDown     = TxtHkWrDown.Value.Trim();
        _snapshot.HotkeyWarmthUp       = TxtHkWrUp.Value.Trim();
        _snapshot.HotkeyBoost          = TxtHkBoost.Value.Trim();
        _snapshot.HotkeyToggle         = TxtHkToggle.Value.Trim();

        // Bedtime/wake go to the active profile, then we mirror them to the
        // top-level Bedtime/WakeTime fields and re-derive the legacy ramp window
        // that Scheduler.ComputeWarmth still reads.
        _snapshot.Bedtime  = ParseTime(TxtBedtime.Text,  _snapshot.Bedtime);
        _snapshot.WakeTime = ParseTime(TxtWakeTime.Text, _snapshot.WakeTime);
        SaveFieldsIntoActiveProfile();
        _snapshot.EnsureScheduleCurveDerived();

        _snapshot.ExcludedProcessNames = TxtExclusions.Text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Replace(".exe", "", StringComparison.OrdinalIgnoreCase))
            .ToList();

        _snapshot.PerMonitor.Clear();
        foreach (var r in _monRows)
        {
            if (r.BrightnessOffset != 0 || r.WarmthOffset != 0)
            {
                _snapshot.PerMonitor[r.StableId] = new PerMonitor
                {
                    BrightnessOffset = r.BrightnessOffset,
                    WarmthOffsetKelvin = r.WarmthOffset
                };
            }
        }

        Applied?.Invoke(_snapshot);
    }
}
