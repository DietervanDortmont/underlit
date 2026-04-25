using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        foreach (var cb in new[] { ChkStartWithWindows, ChkDisableNightLight, ChkHookNativeKeys, ChkScheduleEnabled, ChkFollowAccent })
        {
            cb.Checked   += (_, _) => { PushSettings(); RefreshAccentSwatch(); };
            cb.Unchecked += (_, _) => { PushSettings(); RefreshAccentSwatch(); };
        }
        foreach (var sld in new[] { SldBrightnessStep, SldWarmthStep, SldRampDuration, SldNightWarmth })
        {
            sld.ValueChanged += (_, _) => { PushSettings(); UpdateAllValueChips(); };
        }
        foreach (var tb in new TextBox[] { TxtHkBrDown, TxtHkBrUp, TxtHkWrDown, TxtHkWrUp, TxtHkBoost, TxtHkToggle,
                                           TxtBedStart, TxtBedEnd, TxtWakeStart, TxtWakeEnd, TxtExclusions })
        {
            tb.LostFocus += (_, _) => PushSettings();
        }

        LstMonitors.SelectionChanged += OnMonitorSelected;
        SldMonBrOffset.ValueChanged += OnMonitorOffsetChanged;
        SldMonWrOffset.ValueChanged += OnMonitorOffsetChanged;

        BtnPickAccent.Click += OnPickAccent;
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
        CboTransparency.SelectedIndex  = (int)_snapshot.TransparencyEffects;
        CboBackdrop.SelectedIndex      = (int)_snapshot.OsdBackdrop;
        RefreshAccentSwatch();

        SldBrightnessStep.Value = _snapshot.BrightnessStep;
        SldWarmthStep.Value     = _snapshot.WarmthStep;
        SldRampDuration.Value   = _snapshot.RampDurationMs;
        SldNightWarmth.Value    = _snapshot.NightWarmthKelvin;

        TxtHkBrDown.Text = _snapshot.HotkeyBrightnessDown;
        TxtHkBrUp.Text   = _snapshot.HotkeyBrightnessUp;
        TxtHkWrDown.Text = _snapshot.HotkeyWarmthDown;
        TxtHkWrUp.Text   = _snapshot.HotkeyWarmthUp;
        TxtHkBoost.Text  = _snapshot.HotkeyBoost;
        TxtHkToggle.Text = _snapshot.HotkeyToggle;

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
        _snapshot.TransparencyEffects       = (TransparencyMode)Math.Max(0, CboTransparency.SelectedIndex);
        _snapshot.OsdBackdrop               = (BackdropStyle)Math.Max(0, CboBackdrop.SelectedIndex);

        _snapshot.BrightnessStep  = SldBrightnessStep.Value;
        _snapshot.WarmthStep      = (int)SldWarmthStep.Value;
        _snapshot.RampDurationMs  = (int)SldRampDuration.Value;
        _snapshot.SmoothRamping   = _snapshot.RampDurationMs > 10;
        _snapshot.NightWarmthKelvin = (int)SldNightWarmth.Value;

        _snapshot.HotkeyBrightnessDown = TxtHkBrDown.Text.Trim();
        _snapshot.HotkeyBrightnessUp   = TxtHkBrUp.Text.Trim();
        _snapshot.HotkeyWarmthDown     = TxtHkWrDown.Text.Trim();
        _snapshot.HotkeyWarmthUp       = TxtHkWrUp.Text.Trim();
        _snapshot.HotkeyBoost          = TxtHkBoost.Text.Trim();
        _snapshot.HotkeyToggle         = TxtHkToggle.Text.Trim();

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
