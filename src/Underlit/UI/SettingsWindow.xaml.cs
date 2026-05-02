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
        _pages = new FrameworkElement[] { PageGeneral, PageHotkeys, PageSchedule, PageMonitors, PageExclusions };
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
        foreach (var tb in new TextBox[] { TxtBedStart, TxtBedEnd, TxtWakeStart, TxtWakeEnd, TxtExclusions })
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
        BtnApplyCircadian.Click += OnApplyCircadian;

        // Schedule graph redraws on any input that changes the curve. We hook
        // these in addition to the existing PushSettings hooks so the redraw is
        // immediate (PushSettings updates _snapshot, the graph re-samples it).
        ScheduleGraph.SizeChanged += (_, _) => RedrawScheduleGraph();
        ChkScheduleEnabled.Checked   += (_, _) => RedrawScheduleGraph();
        ChkScheduleEnabled.Unchecked += (_, _) => RedrawScheduleGraph();
        SldNightWarmth.ValueChanged += (_, _) => RedrawScheduleGraph();
        foreach (var tb in new[] { TxtBedStart, TxtBedEnd, TxtWakeStart, TxtWakeEnd })
            tb.LostFocus += (_, _) => RedrawScheduleGraph();
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
    private void RedrawScheduleGraph()
    {
        if (ScheduleGraph == null) return;
        double w = ScheduleGraph.ActualWidth;
        double h = ScheduleGraph.ActualHeight;
        if (w <= 1 || h <= 1) return;

        ScheduleGraph.Children.Clear();

        // Background: vertical gradient from cool→warm to suggest "lower kelvin = warmer light".
        var bg = new System.Windows.Shapes.Rectangle
        {
            Width = w, Height = h,
            RadiusX = 6, RadiusY = 6,
            Fill = new LinearGradientBrush(
                Color.FromArgb(0x18, 0xA8, 0xC8, 0xFF),  // cool wash at top
                Color.FromArgb(0x28, 0xFF, 0xA0, 0x50),  // warm wash at bottom
                90),
        };
        Canvas.SetLeft(bg, 0); Canvas.SetTop(bg, 0);
        ScheduleGraph.Children.Add(bg);

        // Hour markers — faint vertical lines at 6, 12, 18.
        foreach (int hour in new[] { 6, 12, 18 })
        {
            double x = hour / 24.0 * w;
            var marker = new System.Windows.Shapes.Line
            {
                X1 = x, Y1 = 0, X2 = x, Y2 = h,
                Stroke = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 1,
            };
            ScheduleGraph.Children.Add(marker);
        }

        // Build a temporary AppSettings from the live UI values so the graph
        // tracks edits without first persisting them through PushSettings.
        var s = SnapshotForSchedule();

        var pts = new System.Windows.Media.PointCollection();
        const int samples = 144;
        for (int i = 0; i <= samples; i++)
        {
            double hour = i / (double)samples * 24.0;
            DateTime t = DateTime.Today.AddHours(hour);
            int k = Underlit.Core.Scheduler.ComputeWarmth(t, s);
            double x = hour / 24.0 * w;
            double y = h - ((k - 1500) / 5000.0) * h;
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

        // "Now" indicator — a vertical line at the current time so the user can
        // read off "what kelvin am I targeting right now" at a glance.
        DateTime now = DateTime.Now;
        double nowX = (now.Hour + now.Minute / 60.0) / 24.0 * w;
        var nowLine = new System.Windows.Shapes.Line
        {
            X1 = nowX, Y1 = 0, X2 = nowX, Y2 = h,
            Stroke = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 1.5,
            StrokeDashArray = new System.Windows.Media.DoubleCollection(new[] { 3.0, 3.0 }),
        };
        ScheduleGraph.Children.Add(nowLine);
    }

    /// <summary>Build a transient AppSettings from the schedule UI fields so the
    /// graph reflects unsaved edits. Doesn't touch <c>_snapshot</c>.</summary>
    private AppSettings SnapshotForSchedule() => new()
    {
        BedtimeStart = ParseTime(TxtBedStart.Text, _snapshot.BedtimeStart),
        BedtimeEnd   = ParseTime(TxtBedEnd.Text,   _snapshot.BedtimeEnd),
        WakeupStart  = ParseTime(TxtWakeStart.Text, _snapshot.WakeupStart),
        WakeupEnd    = ParseTime(TxtWakeEnd.Text,   _snapshot.WakeupEnd),
        NightWarmthKelvin = (int)SldNightWarmth.Value,
        ScheduleEnabled   = true,  // graph always shows the curve as if active
    };

    /// <summary>
    /// Apply a circadian-physiology default schedule derived from the user's wake time:
    ///   • WakeupEnd     — kept (the user's wake time, the anchor).
    ///   • WakeupStart   — wake − 45 min (start neutralising warmth before fully awake).
    ///   • BedtimeEnd    — wake + 14h (deep-warmth phase aligns with melatonin onset
    ///                     for a typical 8-hour sleep).
    ///   • BedtimeStart  — bedEnd − 2.5h (gentle 2.5h ramp into deep warmth).
    ///   • NightK        — 2700 K (a calmer warm than the previous 3400 default; in
    ///                     line with circadian-friendly evening light recommendations).
    /// All times wrap around midnight via mod 24. Doesn't touch the user's actual
    /// wake time — we read whatever's currently in TxtWakeEnd.
    /// </summary>
    private void OnApplyCircadian(object sender, RoutedEventArgs e)
    {
        var wake = ParseTime(TxtWakeEnd.Text, _snapshot.WakeupEnd);
        double wakeH = wake.AsHourFractional;
        double wakeStartH = (wakeH - 0.75 + 24) % 24;
        double bedEndH    = (wakeH + 14)        % 24;
        double bedStartH  = (bedEndH - 2.5 + 24) % 24;

        TxtWakeStart.Text = ToHHMM(wakeStartH);
        TxtBedEnd.Text    = ToHHMM(bedEndH);
        TxtBedStart.Text  = ToHHMM(bedStartH);
        SldNightWarmth.Value = 2700;

        PushSettings();
        UpdateAllValueChips();
        RedrawScheduleGraph();
    }

    private static string ToHHMM(double hours)
    {
        int hour = (int)Math.Floor(hours);
        int min  = (int)Math.Round((hours - hour) * 60);
        if (min >= 60) { hour = (hour + 1) % 24; min = 0; }
        return $"{hour:D2}:{min:D2}";
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

        TxtBedStart.Text  = Fmt(_snapshot.BedtimeStart);
        TxtBedEnd.Text    = Fmt(_snapshot.BedtimeEnd);
        TxtWakeStart.Text = Fmt(_snapshot.WakeupStart);
        TxtWakeEnd.Text   = Fmt(_snapshot.WakeupEnd);

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

        _snapshot.BedtimeStart = ParseTime(TxtBedStart.Text, _snapshot.BedtimeStart);
        _snapshot.BedtimeEnd   = ParseTime(TxtBedEnd.Text, _snapshot.BedtimeEnd);
        _snapshot.WakeupStart  = ParseTime(TxtWakeStart.Text, _snapshot.WakeupStart);
        _snapshot.WakeupEnd    = ParseTime(TxtWakeEnd.Text, _snapshot.WakeupEnd);

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
