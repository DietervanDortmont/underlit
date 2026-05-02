using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    /// <summary>
    /// One running app's row in the Exclusions page. Bound to a
    /// CheckBox via IsExcluded — toggling adds/removes the process
    /// name from <see cref="AppSettings.ExcludedProcessNames"/>.
    /// </summary>
    public sealed class RunningAppRow : System.ComponentModel.INotifyPropertyChanged
    {
        public string ProcessName { get; init; } = "";
        public string DisplayName { get; init; } = "";

        private bool _isExcluded;
        public bool IsExcluded
        {
            get => _isExcluded;
            set
            {
                if (_isExcluded == value) return;
                _isExcluded = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsExcluded)));
                ExclusionToggled?.Invoke(this);
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public event Action<RunningAppRow>? ExclusionToggled;
    }

    private readonly ObservableCollection<MonitorRow> _monRows = new();
    private readonly ObservableCollection<RunningAppRow> _runningAppRows = new();
    private readonly FrameworkElement[] _pages;
    private readonly Action<bool> _themeHandler;

    public SettingsWindow(AppSettings settings, IReadOnlyList<DisplayInfo> displays, HardwareBrightness hardware)
    {
        InitializeComponent();
        _snapshot = Clone(settings);
        _displays = displays;
        _hardware = hardware;
        LstMonitors.ItemsSource = _monRows;
        LstRunningApps.ItemsSource = _runningAppRows;
        BtnRefreshRunningApps.Click += (_, _) => RefreshRunningApps();
        // v0.6.49: + button next to the Custom exclusions textbox opens
        // an OpenFileDialog filtered to .exe files. We append the chosen
        // executable's base name (no extension) to the textbox on a new
        // line, dedupe on the way in, then push so the change is live.
        BtnExclusionsBrowse.Click += (_, _) => OnBrowseExclusionExe();

        // Sidebar → page switching
        // Order MUST match the sidebar ListBoxItems in SettingsWindow.xaml.
        // v0.6.48: Schedule, Hotkeys, Lights, Monitors, Exclusions, General.
        _pages = new FrameworkElement[] { PageSchedule, PageHotkeys, PageLights, PageMonitors, PageExclusions, PageGeneral };
        NavList.SelectionChanged += (_, _) => ShowSelectedPage();

        // Theming — initial, plus live follow on Windows theme change
        ApplyTheme(ThemeInfo.IsDarkMode());
        _themeHandler = isDark => Dispatcher.BeginInvoke(() => ApplyTheme(isDark));
        ThemeInfo.ThemeChanged += _themeHandler;
        Closed += (_, _) => ThemeInfo.ThemeChanged -= _themeHandler;

        // v0.6.48: persist Settings window geometry across sessions.
        // Restore from settings on construction; save on Closed.
        RestoreWindowGeometry();
        Closed += (_, _) => SaveWindowGeometry();

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
        foreach (var sld in new[] { SldBrightnessStep, SldWarmthStep, SldRampDuration, SldNightWarmth, SldOsdGap })
        {
            sld.ValueChanged += (_, _) => { PushSettings(); UpdateAllValueChips(); };
        }
        // Glass sliders also fire GlassPreviewRequested so the host can keep
        // the OSD on screen while the user tweaks them — otherwise they'd be
        // tweaking blind, only seeing the result on the next hotkey press.
        foreach (var sld in new[] { SldGlassLightAngle, SldGlassLightIntensity, SldGlassRefraction,
                                     SldGlassDepth, SldGlassDispersion, SldGlassFrost,
                                     SldGlassCornerRadius, SldGlassBevelWidth,
                                     SldGlassBevelDepth, SldGlassRimBrightness, SldGlassRimWidth,
                                     SldGlassRimSecondary, SldGlassTintStrength })
        {
            sld.ValueChanged += (_, _) =>
            {
                PushSettings();
                UpdateAllValueChips();
                GlassPreviewRequested?.Invoke();
            };
        }
        // Schedule/exclusion textboxes still commit on lost-focus.
        foreach (var tb in new TextBox[] { TxtBedtimeStart, TxtBedtime, TxtWakeupStart, TxtWakeTime, TxtExclusions })
        {
            tb.LostFocus += (_, _) => PushSettings();
        }
        // HotkeyField raises ValueChanged whenever the user captures, clears, or
        // commits a new binding via the listen-to-bind UI. Bridge straight to PushSettings.
        foreach (var hk in new HotkeyField[] {
            TxtHkBrDown, TxtHkBrUp, TxtHkWrDown, TxtHkWrUp, TxtHkBoost, TxtHkToggle,
            TxtHkHueBrDown, TxtHkHueBrUp, TxtHkHueWrDown, TxtHkHueWrUp })
        {
            hk.ValueChanged += _ => PushSettings();
        }

        // Hue brightness/warmth-offset sliders push settings live (so the
        // HueController in UnderlitHost picks up the new values immediately).
        SldHueBrightness.ValueChanged   += (_, _) =>
        {
            LblHueBrightness.Text = ((int)SldHueBrightness.Value) + "%";
            if (_suppressHueSync) return;
            PushSettings();
        };
        SldHueWarmthOffset.ValueChanged += (_, _) =>
        {
            LblHueWarmthOffset.Text = FormatKelvinOffset((int)SldHueWarmthOffset.Value);
            if (_suppressHueSync) return;
            PushSettings();
        };
        ChkHueIgnoreBoost.Checked   += (_, _) => PushSettings();
        ChkHueIgnoreBoost.Unchecked += (_, _) => PushSettings();

        LstMonitors.SelectionChanged += OnMonitorSelected;
        SldMonBrOffset.ValueChanged += OnMonitorOffsetChanged;
        SldMonWrOffset.ValueChanged += OnMonitorOffsetChanged;
        BtnResetMonitors.Click += OnResetAllMonitorOffsets;

        // v0.6.45: chip drag + double-click. Each chip carries its
        // associated slider via a Tag set in WireValueChipDragAndEdit
        // so the handler knows which slider to nudge.
        WireValueChipDragAndEdit(LblMonBr, SldMonBrOffset, scalePerPixel: 0.20);
        WireValueChipDragAndEdit(LblMonWr, SldMonWrOffset, scalePerPixel: 8.0);

        BtnPickAccent.Click += OnPickAccent;
        BtnPickTint.Click   += OnPickTint;
        BtnPickHighColor.Click  += OnPickHighColor;
        BtnResetHighColor.Click += OnResetHighColor;

        // Profile management buttons.
        BtnAddProfile.Click    += OnAddProfile;
        BtnRemoveProfile.Click += OnRemoveProfile;
        BtnRenameProfile.Click += OnRenameProfile;
        CboProfile.SelectionChanged += OnProfileSelectionChanged;

        // v0.6.42: inline rename on TxtProfileRename (Enter commits, Escape
        // cancels, LostFocus commits). Replaces the modal dialog.
        TxtProfileRename.KeyDown   += OnProfileRenameKeyDown;
        TxtProfileRename.LostFocus += OnProfileRenameLostFocus;

        // Schedule graph redraws on any input that changes the curve. We hook
        // these in addition to the existing PushSettings hooks so the redraw is
        // immediate (PushSettings updates _snapshot, the graph re-samples it).
        ScheduleGraph.SizeChanged += (_, _) => RedrawScheduleGraph();
        ChkScheduleEnabled.Checked   += (_, _) => RedrawScheduleGraph();
        ChkScheduleEnabled.Unchecked += (_, _) => RedrawScheduleGraph();
        // v0.6.34: night-warmth slider only writes to the per-anchor kelvins
        // when the USER is actively interacting with it (mouse-down or
        // keyboard focus). Programmatic value changes — from a graph drag,
        // from LoadActiveProfileIntoFields, from a profile switch — must NOT
        // touch the per-anchor kelvins, otherwise the user's independent
        // per-anchor edits get auto-linked behind their back.
        //
        // The previous boolean-flag suppression had subtle holes: any code
        // path that forgot to wrap a programmatic Value assignment in the
        // suppression scope would silently re-link the bottom anchors. The
        // new gate uses Mouse.Captured / IsKeyboardFocused, which only ever
        // resolves to true during real user input, never during code-driven
        // updates.
        SldNightWarmth.PreviewMouseLeftButtonDown += (_, _) => _nightSliderUserActive = true;
        SldNightWarmth.PreviewMouseLeftButtonUp   += (_, _) => _nightSliderUserActive = false;
        SldNightWarmth.LostMouseCapture           += (_, _) => _nightSliderUserActive = false;
        SldNightWarmth.ValueChanged += (_, _) =>
        {
            // Always update the legacy NightWarmthKelvin so the value
            // chip + tint preview track the slider. The per-anchor write
            // below is gated on real user interaction.
            int k = (int)SldNightWarmth.Value;
            _snapshot.NightWarmthKelvin = k;
            UpdateAllValueChips();

            bool userIsDriving = _nightSliderUserActive
                              || SldNightWarmth.IsKeyboardFocusWithin;
            if (!userIsDriving) return;

            var p = _snapshot.ActiveProfile();
            // v0.6.36: the built-in "Recommended" profile is locked on
            // kelvin. Slider drags update only the legacy display field,
            // not the per-anchor kelvins, so the curated curve shape is
            // preserved. Cloning to a custom profile (+) re-enables the
            // full slider behaviour.
            if (p.IsBuiltIn)
            {
                p.NightWarmthKelvin = k;
                _snapshot.EnsureScheduleCurveDerived();
                RedrawScheduleGraph();
                return;
            }
            p.NightWarmthKelvin   = k;
            p.BedtimeKelvin       = k;
            p.WakeupStartKelvin   = k;
            _snapshot.EnsureScheduleCurveDerived();
            RedrawScheduleGraph();
        };
        foreach (var tb in new[] { TxtBedtimeStart, TxtBedtime, TxtWakeupStart, TxtWakeTime })
            tb.LostFocus += (_, _) => RedrawScheduleGraph();

        // v0.6.45: when the user edits Bedtime or WakeTime on the
        // Recommended profile, the derived ramp-onset times need to
        // refresh in their (read-only) text boxes too.
        TxtBedtime.LostFocus  += (_, _) => RefreshDerivedTimesIfBuiltIn();
        TxtWakeTime.LostFocus += (_, _) => RefreshDerivedTimesIfBuiltIn();

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

        // v0.6.48: About footer wiring. Version label, GitHub icon
        // (opens releases page), log folder icon, "Check for updates"
        // button (polls GitHub Releases API).
        var version = System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString(3) ?? "?";
        LblAppVersion.Text = $"Underlit {version}";
        BtnAboutGithub.Click       += (_, _) => OpenUrl(GitHubReleasesUrl);
        BtnAboutLog.Click          += (_, _) => OpenLogFolder();
        BtnAboutCheckUpdate.Click  += async (_, _) => await CheckForUpdatesAsync();
        BtnRestoreDefaults.Click   += (_, _) => RestoreAllDefaults();

        // v0.6.40: every inline RowHint TextBlock turns into a hover
        // tooltip on the closest interactive control in its row, so the
        // settings UI looks like a flat list of controls rather than a
        // wall of small explanatory text. Done at Loaded so the visual
        // tree is fully populated.
        Loaded += (_, _) => MigrateRowHintsToTooltips();
    }

    /// <summary>
    /// Walk the visual tree and convert every <c>TextBlock</c> using the
    /// <c>RowHint</c> style into a tooltip on the nearest interactive
    /// control in the same row. Em-dashes and en-dashes in the hint text
    /// are normalised to ASCII so the tooltip reads clean.
    /// </summary>
    private void MigrateRowHintsToTooltips()
    {
        var hintStyle = TryFindResource("RowHint") as Style;
        if (hintStyle == null) return;

        foreach (var hint in EnumerateVisualDescendants(this).OfType<TextBlock>())
        {
            if (!ReferenceEquals(hint.Style, hintStyle)) continue;
            string text = NormaliseTooltipText(hint.Text);
            if (string.IsNullOrWhiteSpace(text)) continue;

            // Find the row Grid (the closest ancestor with the Row style).
            var row = FindAncestor<Grid>(hint, g => g.Style == TryFindResource("Row") as Style);
            DependencyObject? scope = row;
            scope ??= FindAncestor<StackPanel>(hint, _ => true);
            scope ??= VisualTreeHelper.GetParent(hint);
            if (scope == null) continue;

            var target = EnumerateVisualDescendants(scope).FirstOrDefault(IsInteractiveControl);
            if (target is FrameworkElement fe && fe.ToolTip == null)
            {
                fe.ToolTip = text;
                ToolTipService.SetInitialShowDelay(fe, 350);
                ToolTipService.SetBetweenShowDelay(fe, 250);
                ToolTipService.SetShowDuration(fe, 12_000);
            }
        }
    }

    private static string NormaliseTooltipText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        return raw
            .Replace("—", ", ")  // em dash
            .Replace("–", "-")   // en dash
            .Replace("  ", " ")
            .Trim();
    }

    private static bool IsInteractiveControl(DependencyObject d) =>
        d is CheckBox || d is Slider || d is ComboBox || d is TextBox || d is Button
        || d is HotkeyField || d is ListBox;

    private static IEnumerable<DependencyObject> EnumerateVisualDescendants(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            yield return c;
            foreach (var x in EnumerateVisualDescendants(c)) yield return x;
        }
    }

    private static T? FindAncestor<T>(DependencyObject d, Func<T, bool> predicate) where T : DependencyObject
    {
        var p = VisualTreeHelper.GetParent(d);
        while (p != null)
        {
            if (p is T t && predicate(t)) return t;
            p = VisualTreeHelper.GetParent(p);
        }
        return null;
    }

    private void ApplyBackdropToWindow()
    {
        // v0.6.40: settings window is now a flat themed surface (Apple-style),
        // no DWM acrylic. The OSD's backdrop preference still drives the
        // pill itself, but it should no longer reach into the settings UI.
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        Acrylic.Apply(hwnd, Acrylic.Backdrop.None, ThemeInfo.IsDarkMode());
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
            ? "Auto: derived from your accent."
            : "Custom: click the swatch to change, Auto to reset.";
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

        // Lazy-populate the running-apps list whenever the Exclusions
        // page is shown so it reflects whatever is open right now.
        if (idx >= 0 && idx < _pages.Length && ReferenceEquals(_pages[idx], PageExclusions))
        {
            RefreshRunningApps();
        }
    }

    // ---- Theme ----

    /// <summary>
    /// Apply the settings window's colour palette. v0.6.40: theme follows
    /// the Windows app-mode setting (dark vs light) only — the OSD's
    /// Liquid Glass / Subtle / Solid backdrop preference no longer changes
    /// the settings UI, so the settings window has its own consistent
    /// look that doesn't mutate when the user switches OSD style.
    /// </summary>
    private void ApplyTheme(bool isDark)
    {
        var r = Resources;
        if (isDark)
        {
            r["App.WindowBg"]      = Brush(0xFF1C1C1E);   // iOS-like deep neutral
            r["App.SidebarBg"]     = Brush(0xFF161618);
            r["App.CardBg"]        = Brush(0xFF242427);
            r["App.CardBorder"]    = Brush(0x14FFFFFF);
            r["App.Divider"]       = Brush(0x0FFFFFFF);
            r["App.TextPrimary"]   = Brush(0xFFFFFFFF);
            r["App.TextSecondary"] = Brush(0x99FFFFFF);
            r["App.HoverBg"]       = Brush(0x0FFFFFFF);
            r["App.SelectedBg"]    = Brush(0x1FFFFFFF);
            r["App.Accent"]        = Brush(0xFF60CDFF);
            r["App.TrackBg"]       = Brush(0x26FFFFFF);
            r["App.InputBg"]       = Brush(0xFF2C2C2F);
            r["App.ToggleTrackOff"]= Brush(0x33FFFFFF);
            // Buttons stand out by being lighter than the dark surface.
            r["App.ButtonBg"]      = Brush(0xFFE5E5EA);
            r["App.ButtonFg"]      = Brush(0xFF1A1A1C);
        }
        else
        {
            r["App.WindowBg"]      = Brush(0xFFF7F7F8);   // iOS-like off-white
            r["App.SidebarBg"]     = Brush(0xFFEEEEF1);
            r["App.CardBg"]        = Brush(0xFFFFFFFF);
            r["App.CardBorder"]    = Brush(0x12000000);
            r["App.Divider"]       = Brush(0x08000000);
            r["App.TextPrimary"]   = Brush(0xFF1A1A1C);
            r["App.TextSecondary"] = Brush(0x80000000);
            r["App.HoverBg"]       = Brush(0x08000000);
            r["App.SelectedBg"]    = Brush(0x14000000);
            r["App.Accent"]        = Brush(0xFF005FB8);
            r["App.TrackBg"]       = Brush(0x1A000000);
            r["App.InputBg"]       = Brush(0xFFFFFFFF);
            r["App.ToggleTrackOff"]= Brush(0x29000000);
            // Buttons stand out by being darker than the light surface.
            r["App.ButtonBg"]      = Brush(0xFF1A1A1C);
            r["App.ButtonFg"]      = Brush(0xFFFFFFFF);
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
        LblOsdGap.Text         = ((int)SldOsdGap.Value) + " dip";
        // Always integer + signed format so a +3 monitor offset and a 0
        // baseline read identically; matches the GridView's StringFormat.
        int monBr = (int)Math.Round(SldMonBrOffset.Value);
        int monWr = (int)Math.Round(SldMonWrOffset.Value);
        LblMonBr.Text = monBr.ToString("+0;-0;0");
        LblMonWr.Text = monWr.ToString("+0;-0;0") + " K";

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
    /// <summary>Left margin reserved for kelvin labels on the Y axis (dip).
    /// v0.6.30: introduced so the curve has a labelled vertical axis. All
    /// X-coordinate calculations on the graph offset by this much from the
    /// left edge so the curve doesn't draw under the labels.</summary>
    private const double GraphYAxisWidth = 36;
    /// <summary>Top breathing-room margin (dip). v0.6.34: dots and the curve
    /// at 6500 K (the maximum) used to draw flush against the top edge of the
    /// card, so they read as cramped. Reserving a small strip lets the
    /// 6500 K anchor circles sit fully inside the gradient backdrop with a
    /// visible halo above them.</summary>
    private const double GraphTopPadding = 10;

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

        // Carve out reserved strips:
        //   • Bottom GraphLabelHeight dip → X-axis time labels.
        //   • Left   GraphYAxisWidth dip → Y-axis kelvin labels.
        //   • Top    GraphTopPadding dip → halo above the 6500 K row so dots
        //                                  there don't draw flush to the card edge.
        // The curve area is what's left in the middle.
        double curveTop  = GraphTopPadding;
        double curveH    = Math.Max(1, h - GraphLabelHeight - GraphTopPadding);
        double curveLeft = GraphYAxisWidth;
        double curveW    = Math.Max(1, w - curveLeft);

        ScheduleGraph.Children.Clear();

        // Background: vertical gradient from cool→warm to suggest "lower kelvin = warmer light".
        var bg = new System.Windows.Shapes.Rectangle
        {
            Width = curveW, Height = curveH,
            RadiusX = 6, RadiusY = 6,
            Fill = new LinearGradientBrush(
                Color.FromArgb(0x18, 0xA8, 0xC8, 0xFF),  // cool wash at top
                Color.FromArgb(0x28, 0xFF, 0xA0, 0x50),  // warm wash at bottom
                90),
        };
        Canvas.SetLeft(bg, curveLeft); Canvas.SetTop(bg, curveTop);
        ScheduleGraph.Children.Add(bg);

        // Faint vertical hour markers at 6, 12, 18 — orientation cues.
        foreach (int hour in new[] { 6, 12, 18 })
        {
            double x = curveLeft + hour / 24.0 * curveW;
            var marker = new System.Windows.Shapes.Line
            {
                X1 = x, Y1 = curveTop, X2 = x, Y2 = curveTop + curveH,
                Stroke = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 1,
            };
            ScheduleGraph.Children.Add(marker);
        }

        // ---- X-axis time labels (below curve) ----
        // v0.6.42: pull a theme-aware foreground from window resources so
        // axis labels are visible in BOTH light and dark mode. The old
        // hard-coded white was invisible on the new light-theme card.
        var axisBrush = (Brush?)TryFindResource("App.TextSecondary")
                        ?? new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF));

        foreach (int hour in new[] { 0, 6, 12, 18, 24 })
        {
            double x = curveLeft + hour / 24.0 * curveW;
            var label = new TextBlock
            {
                Text = FmtAxisHour(hour),
                FontSize = 10,
                Foreground = axisBrush,
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double labelW = label.DesiredSize.Width;
            // Clamp to the graph's full width so the leftmost/rightmost labels don't clip.
            double labelX = Math.Max(0, Math.Min(w - labelW, x - labelW / 2));
            Canvas.SetLeft(label, labelX);
            Canvas.SetTop(label, curveTop + curveH + 2);
            ScheduleGraph.Children.Add(label);
        }

        // ---- Y-axis kelvin labels (left of curve) ----
        foreach (int kelvin in new[] { 6500, 5250, 4000, 2750, 1500 })
        {
            double y = curveTop + curveH - ((kelvin - 1500) / 5000.0) * curveH;
            var label = new TextBlock
            {
                Text = $"{kelvin} K",
                FontSize = 10,
                Foreground = axisBrush,
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double labelW = label.DesiredSize.Width;
            double labelH = label.DesiredSize.Height;
            // Right-align inside the reserved left strip, with a 4 dip gutter.
            double labelX = Math.Max(0, curveLeft - labelW - 4);
            // Clamp vertically so the top/bottom labels don't get cut off.
            double labelY = Math.Max(0, Math.Min(h - labelH, y - labelH / 2));
            Canvas.SetLeft(label, labelX);
            Canvas.SetTop(label, labelY);
            ScheduleGraph.Children.Add(label);
        }

        // Build the schedule snapshot — pulls live profile + text-field values.
        var s = SnapshotForSchedule();

        var pts = new System.Windows.Media.PointCollection();
        const int samples = 144;
        for (int i = 0; i <= samples; i++)
        {
            double hour = i / (double)samples * 24.0;
            DateTime t = DateTime.Today.AddHours(hour);
            int k = Underlit.Core.Scheduler.ComputeWarmth(t, s);
            double x = curveLeft + hour / 24.0 * curveW;
            double y = curveTop + curveH - ((k - 1500) / 5000.0) * curveH;
            pts.Add(new Point(x, y));
        }

        // v0.6.45: dark-orange curve. Reads as "warmth" against the cool-
        // to-warm gradient backdrop and is visible in both light and dark
        // mode without depending on theme primary text colour.
        var curveBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0x5A, 0x10));
        curveBrush.Freeze();
        var line = new System.Windows.Shapes.Polyline
        {
            Points = pts,
            Stroke = curveBrush,
            StrokeThickness = 2.6,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap   = PenLineCap.Round,
        };
        ScheduleGraph.Children.Add(line);

        // Draggable anchor points — each pulls its kelvin from the active
        // profile so vertical drag really retargets warmth at that anchor.
        DrawSchedulePoint(SchedulePointId.WakeupStart, s.WakeupStart, s.WakeupStartKelvin,  curveLeft, curveTop, curveW, curveH);
        DrawSchedulePoint(SchedulePointId.WakeupEnd,   s.WakeupEnd,   s.WakeupEndKelvin,    curveLeft, curveTop, curveW, curveH);
        DrawSchedulePoint(SchedulePointId.BedtimeStart, s.BedtimeStart, s.BedtimeStartKelvin, curveLeft, curveTop, curveW, curveH);
        DrawSchedulePoint(SchedulePointId.BedtimeEnd,   s.BedtimeEnd,   s.BedtimeEndKelvin,   curveLeft, curveTop, curveW, curveH);

        // "Now" indicator — a vertical accent line at the current time so
        // the user can read off "what kelvin am I targeting right now"
        // at a glance. v0.6.45: bumped from a faint white dotted line to
        // the app accent colour, thicker, and with a label so it reads
        // clearly in both light and dark mode.
        DateTime now = DateTime.Now;
        double nowX = curveLeft + (now.Hour + now.Minute / 60.0) / 24.0 * curveW;
        var nowAccent = (Brush?)TryFindResource("App.Accent")
                        ?? new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF));
        var nowLine = new System.Windows.Shapes.Line
        {
            X1 = nowX, Y1 = curveTop, X2 = nowX, Y2 = curveTop + curveH,
            Stroke = nowAccent,
            StrokeThickness = 2.0,
            StrokeDashArray = new System.Windows.Media.DoubleCollection(new[] { 4.0, 3.0 }),
        };
        ScheduleGraph.Children.Add(nowLine);

        // v0.6.48: include the current schedule kelvin in the now-line
        // label so the user can read off "what colour temperature am I
        // targeting right now" at a glance.
        int nowKelvin = Underlit.Core.Scheduler.ComputeWarmth(now, s);
        var nowLabel = new TextBlock
        {
            Text = $"now · {nowKelvin} K",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = nowAccent,
        };
        nowLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(nowLabel, Math.Max(0, Math.Min(w - nowLabel.DesiredSize.Width, nowX + 4)));
        Canvas.SetTop(nowLabel, Math.Max(0, curveTop - 1));
        ScheduleGraph.Children.Add(nowLabel);
    }

    /// <summary>Render one of the four draggable anchor markers at (timeOfDay, kelvin).
    /// Two layered ellipses: a transparent outer one that's the hit target, and a
    /// smaller visible one. Tags are used by the mouse-down handler to identify which
    /// point was grabbed. v0.6.30: takes the curve area's left offset and width
    /// so dots draw aligned with the (now Y-axis-labelled) curve, not the raw
    /// canvas left edge.</summary>
    private void DrawSchedulePoint(SchedulePointId id, TimeOfDay t, int kelvin, double curveLeft, double curveTop, double curveW, double curveH)
    {
        double x = curveLeft + t.AsHourFractional / 24.0 * curveW;
        double y = curveTop + curveH - ((kelvin - 1500) / 5000.0) * curveH;

        // v0.6.36: built-in profiles lock kelvin → cursor reads "SizeWE"
        // (horizontal-only) so the user knows the dot is re-time-able but
        // its colour is fixed. Custom profiles get "SizeAll" (both axes).
        bool locked = _snapshot.ActiveProfile().IsBuiltIn;
        var cursor = locked
            ? System.Windows.Input.Cursors.SizeWE
            : System.Windows.Input.Cursors.SizeAll;

        // Larger transparent hit-target — improves drag affordance without
        // making the visible marker bulky.
        var hit = new System.Windows.Shapes.Ellipse
        {
            Width = GraphPointHitRadius * 2,
            Height = GraphPointHitRadius * 2,
            Fill = System.Windows.Media.Brushes.Transparent,
            Cursor = cursor,
            Tag = id,
            ToolTip = SchedulePointTooltip(id, t, kelvin, locked),
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

    private static string SchedulePointTooltip(SchedulePointId id, TimeOfDay t, int k, bool locked = false)
    {
        string body = id switch
        {
            SchedulePointId.BedtimeStart => $"Evening ramp starts at {Fmt(t)} ({k} K)",
            SchedulePointId.BedtimeEnd   => $"Bedtime: {k} K from {Fmt(t)}",
            SchedulePointId.WakeupStart  => $"Morning ramp starts at {Fmt(t)} ({k} K)",
            SchedulePointId.WakeupEnd    => $"Wake: {k} K by {Fmt(t)}",
            _ => string.Empty,
        };
        if (locked && !string.IsNullOrEmpty(body))
            body += "\n(Recommended profile is locked on kelvin. Clone via + to edit colour temperatures.)";
        return body;
    }

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
            // v0.6.34: deliberately do NOT call UpdatePointFromMouse here.
            // The previous version applied the click position immediately,
            // which fired a WarmthPreviewRequested with the dot's existing
            // kelvin → the engine ramped from whatever the screen was
            // showing to (e.g.) 6500 K on a click of the BedtimeStart
            // anchor. That mid-evening "screen flashes neutral, then ramps
            // back as you drag" sequence was the flicker the user reported.
            // OnGraphMouseMove handles the first real update once the user
            // actually moves; clicks-without-drag are now a true no-op.
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
        // Final redraw with the now-committed values + the slider in its
        // updated position. Without this, the graph could be stale if no
        // mouse-move fired between the last UpdatePointFromMouse and the
        // mouse-up (rare but possible on a single-click).
        RedrawScheduleGraph();
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
        double curveTop  = GraphTopPadding;
        double curveH    = Math.Max(1, h - GraphLabelHeight - GraphTopPadding);
        double curveLeft = GraphYAxisWidth;
        double curveW    = Math.Max(1, w - curveLeft);

        // Subtract the Y-axis label strip + the top breathing-room strip
        // before mapping into curve-space, otherwise a click at canvas
        // (0,0) would resolve to (00:00, 6500K) but the curve at that
        // anchor actually draws at (curveLeft, curveTop).
        double clampedX = Math.Clamp(mouseX - curveLeft, 0, curveW);
        double clampedY = Math.Clamp(mouseY - curveTop, 0, curveH);
        double hour = clampedX / curveW * 24.0;
        TimeOfDay t = TimeOfDayFromHourFractional(hour);
        // Invert: top of curve area = max kelvin (6500), bottom = 1500.
        int kelvin = (int)Math.Round(1500 + (1.0 - clampedY / curveH) * 5000);
        kelvin = Math.Clamp(kelvin, 1500, 6500);

        var p = _snapshot.ActiveProfile();
        // v0.6.45: built-in "Recommended" profile is fully science-locked.
        // Only Bedtime, WakeTime, and the night-warmth slider are user-
        // editable. Drag on BedtimeStart / WakeupStart anchors is a no-op
        // (their times are derived from Bedtime/WakeTime ± science offsets
        // in SaveFieldsIntoActiveProfile). Drag on BedtimeEnd / WakeupEnd
        // updates the time only — kelvins stay at their curated values.
        bool locked = p.IsBuiltIn;
        switch (id)
        {
            case SchedulePointId.BedtimeStart:
                if (!locked)
                {
                    p.BedtimeStart       = t;
                    p.BedtimeStartKelvin = kelvin;
                    TxtBedtimeStart.Text = Fmt(t);
                }
                break;
            case SchedulePointId.BedtimeEnd:
                p.Bedtime       = t;
                if (!locked)
                {
                    // v0.6.38: deep-night anchors are kept equal at all
                    // times — dragging Bedtime's vertical position pulls
                    // WakeupStart along with it. The night plateau between
                    // them is by definition a single warmth, so allowing
                    // them to drift would just produce a slightly sloped
                    // plateau the user has to manually re-flatten.
                    p.BedtimeKelvin     = kelvin;
                    p.WakeupStartKelvin = kelvin;
                }
                TxtBedtime.Text = Fmt(t);
                break;
            case SchedulePointId.WakeupStart:
                if (!locked)
                {
                    p.WakeupStart       = t;
                    p.WakeupStartKelvin = kelvin;
                    p.BedtimeKelvin     = kelvin;
                    TxtWakeupStart.Text = Fmt(t);
                }
                break;
            case SchedulePointId.WakeupEnd:
                p.WakeTime       = t;
                if (!locked) p.WakeTimeKelvin = kelvin;
                TxtWakeTime.Text = Fmt(t);
                break;
        }

        // Keep the legacy single-kelvin field in sync with the deepest of the
        // two "deep warmth" anchors, so the NightWarmth slider in Settings
        // still reads back something sensible after a drag.
        p.NightWarmthKelvin = Math.Min(p.BedtimeKelvin, p.WakeupStartKelvin);

        // Mirror the post-drag deepest kelvin into the slider's visual
        // position so the slider stays in sync with whichever anchor is
        // deeper. v0.6.34: no need to suppress — the slider's ValueChanged
        // handler now gates the per-anchor write on
        // _nightSliderUserActive / IsKeyboardFocusWithin, both of which
        // are false during this programmatic assignment.
        SldNightWarmth.Value = p.NightWarmthKelvin;

        _snapshot.EnsureScheduleCurveDerived();
        RedrawScheduleGraph();
        UpdateAllValueChips();

        // Live preview: ramp the screen to the kelvin actually associated
        // with the anchor after the (possibly skipped) write — i.e. the
        // dragged value on a custom profile, the locked default on a
        // built-in. Without this, dragging vertically on Recommended would
        // make the screen track the mouse even though the dot snaps back.
        int previewKelvin = locked
            ? AnchorKelvin(p, id)
            : kelvin;
        WarmthPreviewRequested?.Invoke(previewKelvin);
    }

    /// <summary>Read the current kelvin stored on a profile for the given
    /// schedule anchor. Used by <see cref="UpdatePointFromMouse"/> to
    /// preview the LOCKED value on built-in profiles instead of the
    /// transient mouse Y, which would otherwise let the user "feel" a
    /// warmth they can't actually commit.</summary>
    private static int AnchorKelvin(WarmthProfile p, SchedulePointId id) => id switch
    {
        SchedulePointId.BedtimeStart => p.BedtimeStartKelvin,
        SchedulePointId.BedtimeEnd   => p.BedtimeKelvin,
        SchedulePointId.WakeupStart  => p.WakeupStartKelvin,
        SchedulePointId.WakeupEnd    => p.WakeTimeKelvin,
        _ => 6500,
    };

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
            UpdateHueGradientPanelVisibility();
            PushSettings();
        };

        // v0.6.49: multi-stop gradient editor. Click on a marker to
        // select it; drag a marker to move it; double-click on the bar
        // to add a new stop; Edit colour / Remove act on the selected
        // stop. The bar redraws itself on size change so it stays in
        // sync with the markers when the window resizes.
        HueGradientBar.SizeChanged              += (_, _) => RedrawHueGradient();
        HueGradientMarkers.SizeChanged          += (_, _) => RedrawHueGradient();
        HueGradientMarkers.MouseLeftButtonDown  += OnHueGradientMarkersMouseDown;
        HueGradientMarkers.MouseMove            += OnHueGradientMarkersMouseMove;
        HueGradientMarkers.MouseLeftButtonUp    += OnHueGradientMarkersMouseUp;
        HueGradientMarkers.LostMouseCapture     += (_, _) => _hueGradientDragIndex = -1;
        HueGradientBar.MouseLeftButtonDown      += OnHueGradientBarMouseDown;
        BtnHueGradientEditStop.Click            += (_, _) => EditSelectedHueGradientStop();
        BtnHueGradientRemoveStop.Click          += (_, _) => RemoveSelectedHueGradientStop();

        // Initial state.
        if (!string.IsNullOrEmpty(_snapshot.HueBridgeIp) && !string.IsNullOrEmpty(_snapshot.HueBridgeUsername))
        {
            _hueClient = new HueBridgeClient(_snapshot.HueBridgeIp!, _snapshot.HueBridgeUsername);
            CboHueColorRange.SelectedIndex = (int)_snapshot.HueColorRange;
            UpdateHueGradientPanelVisibility();
            SetHueUiState(HueUiState.Connected);
            _ = LoadHueGroupsAsync();
        }
        else
        {
            UpdateHueGradientPanelVisibility();
            SetHueUiState(HueUiState.Disconnected);
        }
    }

    // ---- Multi-stop Hue gradient editor (v0.6.49) -----------------------

    /// <summary>
    /// Index into <see cref="AppSettings.HueGradientStops"/> of the stop
    /// currently selected in the editor. -1 = no selection. Selecting a
    /// stop drives the swatch + label and enables the Edit/Remove pills.
    /// </summary>
    private int _hueGradientSelectedIndex = -1;
    /// <summary>Index of the marker currently being dragged, or -1.</summary>
    private int _hueGradientDragIndex = -1;
    /// <summary>Track distinguishing "click without drag" from real drags
    /// so a click selects without instantly snapping the kelvin to the
    /// click x. We start a drag only after the mouse has moved more than
    /// 3 px from the original press point.</summary>
    private System.Windows.Point _hueGradientDragStart;
    private bool _hueGradientDragMoved;

    /// <summary>Visible kelvin range covered by the gradient bar; same
    /// as the rest of the schedule UI.</summary>
    private const int HueGradientMinK = 1500;
    private const int HueGradientMaxK = 6500;

    /// <summary>v0.6.49: show the gradient editor only when Custom
    /// gradient mode is selected, then redraw the bar + markers from
    /// the user's stored stops. Selection is reset every time so a
    /// stale index from a previous snapshot can't point past the end
    /// of the new stops list (e.g. after Restore defaults).</summary>
    private void UpdateHueGradientPanelVisibility()
    {
        bool show = _snapshot.HueColorRange == HueColorRangeMode.WhiteToRed;
        HueGradientPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        HueGradientRow.Visibility   = show ? Visibility.Visible : Visibility.Collapsed;
        _hueGradientSelectedIndex = -1;
        if (show) RedrawHueGradient();
    }

    private static Brush HexToBrush(string? hex, Color fallback)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(hex);
                return new SolidColorBrush(c);
            }
            catch { /* fall through */ }
        }
        return new SolidColorBrush(fallback);
    }

    private static Color HexToColor(string? hex, Color fallback)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            try { return (Color)ColorConverter.ConvertFromString(hex); }
            catch { /* fall through */ }
        }
        return fallback;
    }

    /// <summary>Map a kelvin value (1500..6500) to an x-coordinate inside
    /// the gradient bar's local space.</summary>
    private double KelvinToX(int kelvin, double width)
    {
        double t = (Math.Clamp(kelvin, HueGradientMinK, HueGradientMaxK) - HueGradientMinK)
                 / (double)(HueGradientMaxK - HueGradientMinK);
        return t * width;
    }

    private int XToKelvin(double x, double width)
    {
        if (width <= 0) return HueGradientMinK;
        double t = Math.Clamp(x / width, 0.0, 1.0);
        return (int)Math.Round(HueGradientMinK + t * (HueGradientMaxK - HueGradientMinK));
    }

    /// <summary>
    /// Rebuild the gradient bar's brush and the marker triangles from
    /// the snapshot's stop list. Called after every edit, drag, add, or
    /// remove, plus on size changes.
    /// </summary>
    private void RedrawHueGradient()
    {
        if (_snapshot is null) return;

        // Always sort by kelvin so the brush gradient is monotonic, but
        // keep the user's original list ordering in the snapshot — the
        // editor remembers selection by index into the unsorted list, so
        // we map between the two via the kelvin value.
        var stops = _snapshot.HueGradientStops;
        if (stops.Count < 2) return;

        // 1) Build the LinearGradientBrush.
        var brush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0.5),
            EndPoint   = new System.Windows.Point(1, 0.5),
        };
        foreach (var s in stops.OrderBy(s => s.Kelvin))
        {
            double t = (Math.Clamp(s.Kelvin, HueGradientMinK, HueGradientMaxK) - HueGradientMinK)
                     / (double)(HueGradientMaxK - HueGradientMinK);
            brush.GradientStops.Add(new GradientStop(
                HexToColor(s.Color, Color.FromRgb(0xFF, 0xFF, 0xFF)),
                t));
        }
        HueGradientBar.Fill = brush;

        // 2) Redraw the markers in the canvas. Each marker is a small
        //    rounded rectangle filled with the stop's colour, with a
        //    triangle "tail" pointing up at the bar.
        HueGradientMarkers.Children.Clear();
        double width = HueGradientMarkers.ActualWidth;
        if (width <= 0) width = HueGradientBar.ActualWidth;
        if (width <= 0) return;

        for (int i = 0; i < stops.Count; i++)
        {
            var stop = stops[i];
            double x = KelvinToX(stop.Kelvin, width);

            var marker = BuildHueGradientMarker(stop, isSelected: i == _hueGradientSelectedIndex);
            marker.Tag = i;
            // Centre the 16-px marker on the kelvin's x.
            Canvas.SetLeft(marker, x - 8);
            Canvas.SetTop(marker, 0);
            HueGradientMarkers.Children.Add(marker);
        }

        // 3) Sync the action row (swatch + label).
        if (_hueGradientSelectedIndex >= 0
            && _hueGradientSelectedIndex < stops.Count)
        {
            var sel = stops[_hueGradientSelectedIndex];
            HueGradientSelectedSwatch.Background =
                HexToBrush(sel.Color, Color.FromRgb(0xFF, 0xFF, 0xFF));
            LblHueGradientSelected.Text = $"Stop at {sel.Kelvin} K";
            BtnHueGradientEditStop.IsEnabled   = true;
            BtnHueGradientRemoveStop.IsEnabled = stops.Count > 2;
        }
        else
        {
            HueGradientSelectedSwatch.Background =
                new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80));
            LblHueGradientSelected.Text = "Click a marker to select a stop";
            BtnHueGradientEditStop.IsEnabled   = false;
            BtnHueGradientRemoveStop.IsEnabled = false;
        }
    }

    /// <summary>Build one marker visual: a small upward-pointing chevron
    /// + colour-filled square, themed with a selection outline.</summary>
    private FrameworkElement BuildHueGradientMarker(HueGradientStop stop, bool isSelected)
    {
        var fill   = HexToBrush(stop.Color, Color.FromRgb(0xFF, 0xFF, 0xFF));
        var stroke = isSelected
            ? ((Brush?)TryFindResource("App.Accent")
                ?? new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF)))
            : ((Brush?)TryFindResource("App.TextSecondary")
                ?? new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)));

        var grid = new Grid
        {
            Width = 16,
            Height = 22,
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        // The triangular pointer at the top — points up at the bar.
        var triangle = new System.Windows.Shapes.Polygon
        {
            Points = new PointCollection
            {
                new System.Windows.Point(8, 0),
                new System.Windows.Point(2, 6),
                new System.Windows.Point(14, 6),
            },
            Fill            = stroke,
            Stretch         = Stretch.None,
            VerticalAlignment   = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        grid.Children.Add(triangle);

        // The colour swatch body.
        var body = new Border
        {
            Width = 14, Height = 14,
            CornerRadius = new CornerRadius(2),
            Background = fill,
            BorderBrush = stroke,
            BorderThickness = new Thickness(isSelected ? 2 : 1),
            VerticalAlignment   = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        grid.Children.Add(body);

        return grid;
    }

    /// <summary>Hit-test the marker canvas at point p and return the
    /// stops index under it, or -1 if none.</summary>
    private int HitTestHueGradientMarker(System.Windows.Point p)
    {
        // Walk in reverse so the topmost (last-added) marker wins on
        // overlap.
        for (int i = HueGradientMarkers.Children.Count - 1; i >= 0; i--)
        {
            if (HueGradientMarkers.Children[i] is FrameworkElement fe
                && fe.Tag is int idx)
            {
                double left = Canvas.GetLeft(fe);
                if (double.IsNaN(left)) continue;
                if (p.X >= left && p.X <= left + fe.Width
                    && p.Y >= 0 && p.Y <= fe.Height)
                {
                    return idx;
                }
            }
        }
        return -1;
    }

    private void OnHueGradientMarkersMouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(HueGradientMarkers);
        int idx = HitTestHueGradientMarker(p);
        if (idx < 0) return;

        _hueGradientSelectedIndex = idx;
        _hueGradientDragIndex     = idx;
        _hueGradientDragStart     = p;
        _hueGradientDragMoved     = false;
        HueGradientMarkers.CaptureMouse();
        e.Handled = true;
        RedrawHueGradient();
    }

    private void OnHueGradientMarkersMouseMove(object sender, MouseEventArgs e)
    {
        if (_hueGradientDragIndex < 0) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var p = e.GetPosition(HueGradientMarkers);
        if (!_hueGradientDragMoved)
        {
            // Suppress micro-jitter from a click — only enter drag
            // mode after a real movement.
            if (Math.Abs(p.X - _hueGradientDragStart.X) < 3) return;
            _hueGradientDragMoved = true;
        }

        var stops = _snapshot.HueGradientStops;
        if (_hueGradientDragIndex >= stops.Count) return;

        double width = HueGradientMarkers.ActualWidth;
        if (width <= 0) width = HueGradientBar.ActualWidth;

        int kelvin = XToKelvin(p.X, width);
        if (stops[_hueGradientDragIndex].Kelvin == kelvin) return;
        stops[_hueGradientDragIndex].Kelvin = kelvin;
        RedrawHueGradient();
        // Live push so the user's bulbs follow the drag.
        PushSettings();
    }

    private void OnHueGradientMarkersMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (HueGradientMarkers.IsMouseCaptured) HueGradientMarkers.ReleaseMouseCapture();
        _hueGradientDragIndex = -1;
    }

    /// <summary>Double-click on the bar adds a new stop at the click x
    /// with the colour the gradient is currently showing at that
    /// position. Single click on the bar deselects.</summary>
    private void OnHueGradientBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            var p = e.GetPosition(HueGradientBar);
            double width = HueGradientBar.ActualWidth;
            int k = XToKelvin(p.X, width);

            // Sample the existing brush at the same x to seed the new
            // stop's colour. We do this in xy → not directly meaningful
            // here, so just lerp between adjacent stop colours in sRGB.
            string newHex = SampleGradientHex(k);

            var stops = _snapshot.HueGradientStops;
            stops.Add(new HueGradientStop { Kelvin = k, Color = newHex });
            _hueGradientSelectedIndex = stops.Count - 1;
            RedrawHueGradient();
            PushSettings();
            e.Handled = true;
        }
        else
        {
            // Plain click anywhere on the bar deselects (so the action
            // row stops nagging the user about a stop that's no longer
            // logically active).
            _hueGradientSelectedIndex = -1;
            RedrawHueGradient();
        }
    }

    /// <summary>sRGB lerp between the bracketing stops at the given
    /// kelvin. Used as the seed colour when the user adds a new stop
    /// via double-click.</summary>
    private string SampleGradientHex(int kelvin)
    {
        var stops = _snapshot.HueGradientStops.OrderBy(s => s.Kelvin).ToList();
        if (stops.Count == 0) return "#FFFFFFFF";
        if (kelvin <= stops[0].Kelvin) return stops[0].Color;
        if (kelvin >= stops[^1].Kelvin) return stops[^1].Color;
        for (int i = 0; i < stops.Count - 1; i++)
        {
            var a = stops[i];
            var b = stops[i + 1];
            if (kelvin >= a.Kelvin && kelvin <= b.Kelvin)
            {
                int span = b.Kelvin - a.Kelvin;
                double t = span <= 0 ? 0.0 : (kelvin - a.Kelvin) / (double)span;
                var ca = HexToColor(a.Color, Color.FromRgb(0xFF, 0xFF, 0xFF));
                var cb = HexToColor(b.Color, Color.FromRgb(0xB0, 0x00, 0x10));
                byte r = (byte)Math.Round(ca.R + (cb.R - ca.R) * t);
                byte g = (byte)Math.Round(ca.G + (cb.G - ca.G) * t);
                byte bC = (byte)Math.Round(ca.B + (cb.B - ca.B) * t);
                return $"#FF{r:X2}{g:X2}{bC:X2}";
            }
        }
        return stops[^1].Color;
    }

    /// <summary>Open the system colour picker for the currently-selected
    /// stop and persist the change.</summary>
    private void EditSelectedHueGradientStop()
    {
        var stops = _snapshot.HueGradientStops;
        int idx = _hueGradientSelectedIndex;
        if (idx < 0 || idx >= stops.Count) return;

        var seed = HexToColor(stops[idx].Color, Color.FromRgb(0xFF, 0xFF, 0xFF));
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            Color = System.Drawing.Color.FromArgb(seed.R, seed.G, seed.B),
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        string hex = $"#FF{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
        stops[idx].Color = hex;

        // Mirror the change into the legacy two-endpoint fields so older
        // settings.json readers, hotkey paths, and rollback codepaths
        // still get a reasonable value when the user has a complex
        // gradient. We pick the lowest-kelvin stop's colour as Warm and
        // the highest-kelvin stop's colour as Cool.
        var sorted = stops.OrderBy(s => s.Kelvin).ToList();
        _snapshot.HueGradientWarmColor = sorted[0].Color;
        _snapshot.HueGradientCoolColor = sorted[^1].Color;

        RedrawHueGradient();
        PushSettings();
    }

    /// <summary>Remove the selected stop. Refuses if only two stops
    /// remain — the gradient needs both endpoints to interpolate.</summary>
    private void RemoveSelectedHueGradientStop()
    {
        var stops = _snapshot.HueGradientStops;
        int idx = _hueGradientSelectedIndex;
        if (idx < 0 || idx >= stops.Count) return;
        if (stops.Count <= 2) return;

        stops.RemoveAt(idx);
        _hueGradientSelectedIndex = -1;

        // Keep the legacy endpoint fields in sync — see EditSelectedHueGradientStop.
        var sorted = stops.OrderBy(s => s.Kelvin).ToList();
        _snapshot.HueGradientWarmColor = sorted[0].Color;
        _snapshot.HueGradientCoolColor = sorted[^1].Color;

        RedrawHueGradient();
        PushSettings();
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
                LblHuePairingStatus.Text = "Press the button again. The 30 second window has lapsed.";
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

    /// <summary>Raised whenever the user nudges a Liquid-Glass tuning slider
    /// (refraction, frost, rim, etc.). The host responds by re-showing the
    /// OSD with the freshly-pushed glass parameters so the user can see the
    /// effect in real time, instead of waiting for the next hotkey press.</summary>
    public event Action? GlassPreviewRequested;

    /// <summary>Build a transient AppSettings from the schedule UI fields so the
    /// graph reflects unsaved edits. Doesn't touch <c>_snapshot</c>.</summary>
    /// <summary>
    /// Build the AppSettings the schedule graph samples from. v0.6.30: returns
    /// <see cref="_snapshot"/> itself with the four time text-fields (which may
    /// be edited but not yet committed) overlaid into the active profile, then
    /// re-derives the legacy curve fields. This keeps every per-anchor kelvin
    /// the user has set via drag (BedtimeStartKelvin / WakeupStartKelvin / etc.)
    /// — the previous "build a fresh AppSettings with only Bedtime + WakeTime"
    /// approach threw all that away and made the ramp-start dots redraw at
    /// derived positions instead of the user's drag positions.
    /// </summary>
    private AppSettings SnapshotForSchedule()
    {
        var p = _snapshot.ActiveProfile();
        // Pull the latest text-field values into the active profile so unsaved
        // edits are reflected. The profile is the authoritative source for
        // anchor kelvins (per-anchor drag writes there); times can move via
        // either text edit or drag, so refresh from the boxes.
        p.BedtimeStart = ParseTime(TxtBedtimeStart.Text, p.BedtimeStart);
        p.Bedtime      = ParseTime(TxtBedtime.Text,      p.Bedtime);
        p.WakeupStart  = ParseTime(TxtWakeupStart.Text,  p.WakeupStart);
        p.WakeTime     = ParseTime(TxtWakeTime.Text,     p.WakeTime);
        // Don't mutate ScheduleEnabled here — Scheduler.ComputeWarmth doesn't
        // gate on it, and stomping the user's setting on every redraw would
        // silently re-enable schedule mode behind their back.
        _snapshot.EnsureScheduleCurveDerived();      // mirror profile → top-level fields
        return _snapshot;
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
    /// <summary>v0.6.34: true while the user is actively pressing the mouse
    /// on the night-warmth slider. Used by the slider's ValueChanged handler
    /// to decide whether to write the new value into BedtimeKelvin /
    /// WakeupStartKelvin (= link the bottom anchors to the slider). Only true
    /// during real user interaction, never during programmatic Value writes,
    /// so per-anchor edits made via graph drag survive any subsequent
    /// programmatic slider updates.</summary>
    private bool _nightSliderUserActive;

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

    /// <summary>Mirror the active profile's four anchor times + the deepest
    /// kelvin into the UI fields, so when the user picks a different profile
    /// (or returns from a graph drag) the inputs reflect it.</summary>
    private void LoadActiveProfileIntoFields()
    {
        var active = _snapshot.WarmthProfiles.FirstOrDefault(p => p.Name == _snapshot.ActiveProfileName);
        if (active == null) return;
        TxtBedtimeStart.Text = Fmt(active.BedtimeStart);
        TxtBedtime.Text      = Fmt(active.Bedtime);
        TxtWakeupStart.Text  = Fmt(active.WakeupStart);
        TxtWakeTime.Text     = Fmt(active.WakeTime);

        // v0.6.45: built-in Recommended is science-locked on the ramp
        // onsets — disable the text fields so the user can't try to
        // edit them. The fields stay visible (greyed) so the user sees
        // the derived values for reference.
        TxtBedtimeStart.IsEnabled = !active.IsBuiltIn;
        TxtWakeupStart.IsEnabled  = !active.IsBuiltIn;

        // Programmatic slider update — the handler gates the per-anchor
        // write on _nightSliderUserActive / IsKeyboardFocusWithin, both of
        // which are false here, so nothing gets stomped.
        SldNightWarmth.Value = Math.Min(active.BedtimeKelvin, active.WakeupStartKelvin);
    }

    /// <summary>Push the four time-of-day input fields into the active profile.
    /// NightWarmthKelvin is intentionally NOT written here — that field is
    /// owned by the SldNightWarmth ValueChanged handler (when the user moves
    /// the slider) and by UpdatePointFromMouse (when the user drags a deep
    /// anchor on the graph). Writing the slider's value here was stomping
    /// freshly-dragged per-anchor kelvins every time PushSettings ran, which
    /// is the bug the user hit in v0.6.28.</summary>
    private void SaveFieldsIntoActiveProfile()
    {
        var active = _snapshot.WarmthProfiles.FirstOrDefault(p => p.Name == _snapshot.ActiveProfileName);
        if (active == null) return;
        active.Bedtime  = ParseTime(TxtBedtime.Text,  active.Bedtime);
        active.WakeTime = ParseTime(TxtWakeTime.Text, active.WakeTime);

        if (active.IsBuiltIn)
        {
            // v0.6.45: Recommended profile is science-locked on the ramp
            // onsets. Bedtime/WakeTime drive the curve shape; the user's
            // typed onset values (if they typed any) are ignored here so
            // the curated 2.5h / 0.75h offsets are always preserved.
            active.BedtimeStart = ShiftHours(active.Bedtime,  -2.5);
            active.WakeupStart  = ShiftHours(active.WakeTime, -0.75);
            active.BedtimeStartKelvin = 6500;
            active.WakeTimeKelvin     = 6500;
            int deep = Math.Clamp(active.NightWarmthKelvin, 1500, 6500);
            active.BedtimeKelvin     = deep;
            active.WakeupStartKelvin = deep;
        }
        else
        {
            active.BedtimeStart = ParseTime(TxtBedtimeStart.Text, active.BedtimeStart);
            active.WakeupStart  = ParseTime(TxtWakeupStart.Text,  active.WakeupStart);
        }
    }

    /// <summary>
    /// v0.6.45: make a value-chip TextBlock draggable to adjust its
    /// associated slider, and double-clickable to type a value
    /// directly. The chip stays a TextBlock for layout simplicity; on
    /// double-click we overlay an in-place TextBox in the same Grid
    /// cell, focus it, and commit on Enter / LostFocus.
    /// </summary>
    private void WireValueChipDragAndEdit(TextBlock chip, Slider slider, double scalePerPixel)
    {
        Point dragOrigin = default;
        double dragStartValue = 0;
        bool dragging = false;

        chip.MouseLeftButtonDown += (s, e) =>
        {
            // TextBlock doesn't expose MouseDoubleClick (that lives on
            // Control), so we detect a double-click by ClickCount on the
            // bubbling MouseLeftButtonDown and route to edit mode there.
            if (e.ClickCount >= 2)
            {
                if (dragging)
                {
                    dragging = false;
                    chip.ReleaseMouseCapture();
                }
                BeginValueChipEdit(chip, slider);
                e.Handled = true;
                return;
            }
            dragOrigin = e.GetPosition(this);
            dragStartValue = slider.Value;
            dragging = true;
            chip.CaptureMouse();
            e.Handled = true;
        };
        chip.MouseMove += (s, e) =>
        {
            if (!dragging) return;
            double dx = e.GetPosition(this).X - dragOrigin.X;
            double newValue = dragStartValue + dx * scalePerPixel;
            // The slider has Minimum/Maximum/IsSnapToTickEnabled set;
            // assigning Value lets WPF coerce + round for us.
            slider.Value = newValue;
        };
        chip.MouseLeftButtonUp += (s, e) =>
        {
            if (!dragging) return;
            dragging = false;
            chip.ReleaseMouseCapture();
            e.Handled = true;
        };
    }

    /// <summary>Show an inline TextBox over the chip, pre-filled with the
    /// current slider value, focus it, and commit on Enter or
    /// LostFocus. Escape cancels.</summary>
    private void BeginValueChipEdit(TextBlock chip, Slider slider)
    {
        if (chip.Parent is not Grid parent) return;

        var editor = new TextBox
        {
            Text = ((int)Math.Round(slider.Value)).ToString(),
            Style = (Style?)TryFindResource("AppleTextBox"),
            Margin = chip.Margin,
            Width = Math.Max(72, chip.ActualWidth + 12),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(editor, Grid.GetColumn(chip));
        Grid.SetColumnSpan(editor, Grid.GetColumnSpan(chip));
        Grid.SetRow(editor, Grid.GetRow(chip));

        chip.Visibility = Visibility.Collapsed;
        parent.Children.Add(editor);
        editor.Focus();
        editor.SelectAll();

        void Close(bool commit)
        {
            if (commit && double.TryParse(editor.Text.Trim(),
                System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowThousands,
                System.Globalization.CultureInfo.CurrentCulture, out var parsed))
            {
                slider.Value = parsed;
            }
            parent.Children.Remove(editor);
            chip.Visibility = Visibility.Visible;
        }

        editor.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter)  { Close(commit: true);  e.Handled = true; }
            if (e.Key == Key.Escape) { Close(commit: false); e.Handled = true; }
        };
        editor.LostFocus += (s, e) => Close(commit: true);
    }

    /// <summary>
    /// v0.6.48: restore Settings window geometry from settings.json. The
    /// snapshot fields default to -1 ("no saved value"), in which case
    /// we let WPF use WindowStartupLocation=CenterScreen with the XAML-
    /// declared 760x600. Width/height are clamped to a minimum so a
    /// borked save can't render the window unusable.
    /// </summary>
    private void RestoreWindowGeometry()
    {
        if (_snapshot.SettingsWindowWidth  > 200 &&
            _snapshot.SettingsWindowHeight > 200)
        {
            Width  = Math.Max(MinWidth,  _snapshot.SettingsWindowWidth);
            Height = Math.Max(MinHeight, _snapshot.SettingsWindowHeight);
        }
        if (_snapshot.SettingsWindowLeft >= 0 && _snapshot.SettingsWindowTop >= 0)
        {
            // Clamp to virtual screen bounds so a window saved on a
            // disconnected monitor doesn't reopen off-screen.
            var virtualLeft   = SystemParameters.VirtualScreenLeft;
            var virtualTop    = SystemParameters.VirtualScreenTop;
            var virtualRight  = virtualLeft + SystemParameters.VirtualScreenWidth;
            var virtualBottom = virtualTop  + SystemParameters.VirtualScreenHeight;
            Left = Math.Min(Math.Max(_snapshot.SettingsWindowLeft, virtualLeft), virtualRight - 200);
            Top  = Math.Min(Math.Max(_snapshot.SettingsWindowTop,  virtualTop),  virtualBottom - 200);
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
    }

    private void SaveWindowGeometry()
    {
        if (WindowState == WindowState.Maximized || WindowState == WindowState.Minimized)
            return;  // skip pathological states; restore the last "normal" geometry on next open
        _snapshot.SettingsWindowLeft   = (int)Math.Round(Left);
        _snapshot.SettingsWindowTop    = (int)Math.Round(Top);
        _snapshot.SettingsWindowWidth  = (int)Math.Round(Width);
        _snapshot.SettingsWindowHeight = (int)Math.Round(Height);
        // The host saves settings.json on its own when Applied fires;
        // we trigger that here so the geometry persists immediately.
        try { Applied?.Invoke(_snapshot); } catch { /* host handles */ }
    }

    /// <summary>
    /// v0.6.48: nuke every setting and reload from defaults. Replaces
    /// _snapshot with a fresh AppSettings, re-loads the UI, and pushes
    /// so the host applies the reset everywhere downstream. Confirmation
    /// dialog because there's no undo.
    /// </summary>
    private void RestoreAllDefaults()
    {
        var result = MessageBox.Show(
            this,
            "This wipes every setting (hotkeys, schedule profiles, Hue pairing, monitor offsets, exclusions) and resets to the factory defaults. There is no undo.\n\nContinue?",
            "Restore all defaults",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (result != MessageBoxResult.OK) return;

        _snapshot = new AppSettings();
        _snapshot.EnsureProfilesInitialized();
        LoadFromSettings();
        UpdateAllValueChips();
        PushSettings();
    }

    // ============================================================
    // About footer (v0.6.48)
    // ============================================================

    /// <summary>Public-facing GitHub URL the About icon opens. Uses the
    /// releases page directly so the user lands on downloadable
    /// binaries, not the source root.</summary>
    private const string GitHubReleasesUrl = "https://github.com/DietervanDortmont/underlit/releases/latest";

    /// <summary>GitHub Releases API endpoint for the latest release.
    /// Returns JSON with a <c>tag_name</c> field which is what we
    /// compare against the running assembly version.</summary>
    private const string GitHubReleasesApiUrl = "https://api.github.com/repos/DietervanDortmont/underlit/releases/latest";

    /// <summary>Where Logger writes daily logs. Surfaced in the About
    /// footer so users posting bug reports can find them.</summary>
    private static string LogFolder =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Underlit", "logs");

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { Logger.Warn($"Failed to open {url}", ex); }
    }

    private void OpenLogFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(LogFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = LogFolder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { Logger.Warn($"Failed to open log folder", ex); }
    }

    /// <summary>
    /// Hit the GitHub Releases API, compare the latest tag to the
    /// running assembly version, and show a status line in the About
    /// footer. Network errors are reported to the user inline.
    /// </summary>
    private async System.Threading.Tasks.Task CheckForUpdatesAsync()
    {
        BtnAboutCheckUpdate.IsEnabled = false;
        LblAboutUpdateStatus.Visibility = Visibility.Visible;
        LblAboutUpdateStatus.Text = "Checking…";
        try
        {
            using var http = new System.Net.Http.HttpClient
            {
                Timeout = TimeSpan.FromSeconds(8),
            };
            // GitHub requires a User-Agent on every API request.
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Underlit-UpdateCheck");
            string body = await http.GetStringAsync(GitHubReleasesApiUrl);
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            string? tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(tag))
            {
                LblAboutUpdateStatus.Text = "Couldn't read the latest version. Try again later.";
                return;
            }

            string current = System.Reflection.Assembly.GetExecutingAssembly()
                .GetName().Version?.ToString(3) ?? "0.0.0";
            string latest = tag.TrimStart('v', 'V');
            if (string.Equals(latest, current, StringComparison.OrdinalIgnoreCase))
            {
                LblAboutUpdateStatus.Text = $"You're up to date (v{current}).";
            }
            else
            {
                LblAboutUpdateStatus.Text = $"v{latest} available (you have v{current}). Click the GitHub icon to download.";
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Update check failed", ex);
            LblAboutUpdateStatus.Text = "Couldn't reach GitHub. Check your network.";
        }
        finally
        {
            BtnAboutCheckUpdate.IsEnabled = true;
        }
    }

    /// <summary>
    /// v0.6.45: dampen mouse-wheel scrolling on the settings panes so a
    /// trackpad's high-frequency low-delta wheel events don't fling the
    /// page or the running-apps list. Default WPF treats every wheel
    /// event as ~3 lines of scroll regardless of delta magnitude;
    /// trackpads can fire 30+ events per second on a fast swipe, which
    /// reads as "twitchy". We override with a delta-proportional offset
    /// so trackpad inertia translates to a soft, mouse-comparable scroll.
    /// </summary>
    private void OnSmoothScroll(object sender, MouseWheelEventArgs e)
    {
        ScrollViewer? sv = sender as ScrollViewer;
        if (sv == null && sender is DependencyObject d)
        {
            // The handler may be attached to a child of the ScrollViewer
            // that owns the wheel routing. Walk up.
            for (var p = d; p != null; p = VisualTreeHelper.GetParent(p))
            {
                if (p is ScrollViewer match) { sv = match; break; }
            }
        }
        if (sv == null) return;

        // 0.35 was tuned by hand against an Apple-style trackpad — a
        // deliberate swipe scrolls the visible card area in roughly two
        // seconds, which feels controlled rather than flicked.
        double delta = e.Delta * 0.35;
        sv.ScrollToVerticalOffset(sv.VerticalOffset - delta);
        e.Handled = true;
    }

    /// <summary>v0.6.45: when the active profile is built-in (Recommended),
    /// the BedtimeStart and WakeupStart text fields show derived values
    /// (Bedtime - 2.5h, WakeTime - 0.75h). After the user edits Bedtime
    /// or WakeTime those derived fields are stale, so re-render them
    /// from the active profile. No-op for custom profiles.</summary>
    private void RefreshDerivedTimesIfBuiltIn()
    {
        var active = _snapshot.WarmthProfiles.FirstOrDefault(p => p.Name == _snapshot.ActiveProfileName);
        if (active == null || !active.IsBuiltIn) return;
        active.Bedtime  = ParseTime(TxtBedtime.Text,  active.Bedtime);
        active.WakeTime = ParseTime(TxtWakeTime.Text, active.WakeTime);
        active.BedtimeStart = ShiftHours(active.Bedtime,  -2.5);
        active.WakeupStart  = ShiftHours(active.WakeTime, -0.75);
        TxtBedtimeStart.Text = Fmt(active.BedtimeStart);
        TxtWakeupStart.Text  = Fmt(active.WakeupStart);
    }

    /// <summary>Shift a TimeOfDay by a fractional number of hours, wrapping
    /// across midnight. Mirror of <c>AppSettings.ShiftHours</c>; duplicated
    /// here so the settings UI doesn't need to expose the private helper.</summary>
    private static TimeOfDay ShiftHours(TimeOfDay t, double hours)
    {
        double h = t.AsHourFractional + hours;
        while (h < 0)  h += 24;
        while (h >= 24) h -= 24;
        int hh = (int)Math.Floor(h);
        int mm = (int)Math.Round((h - hh) * 60);
        if (mm >= 60) { hh = (hh + 1) % 24; mm = 0; }
        return new TimeOfDay(hh, mm);
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

        // v0.6.48: confirmation prompt — profile deletion is destructive
        // and the user has no undo. WarmthProfiles[0] is always the
        // built-in Recommended preset (EnsureProfilesInitialized seeds
        // it at index 0), so falling back there is safe.
        var ok = MessageBox.Show(
            this,
            $"Delete profile \"{active.Name}\"? Underlit will switch back to the Recommended preset. There is no undo.",
            "Delete profile",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (ok != MessageBoxResult.OK) return;

        _snapshot.WarmthProfiles.Remove(active);
        _snapshot.ActiveProfileName = _snapshot.WarmthProfiles[0].Name;
        RefreshProfileDropdown();
        LoadActiveProfileIntoFields();
        UpdateAllValueChips();
        PushSettings();
        RedrawScheduleGraph();
    }

    /// <summary>
    /// v0.6.42: pencil click opens an inline rename in the dropdown's slot
    /// instead of a modal dialog. The TextBox sibling
    /// <c>TxtProfileRename</c> overlays the dropdown, takes focus, selects
    /// the current name, and commits on Enter or LostFocus. Escape cancels.
    /// </summary>
    private void OnRenameProfile(object sender, RoutedEventArgs e)
    {
        var active = _snapshot.WarmthProfiles.FirstOrDefault(p => p.Name == _snapshot.ActiveProfileName);
        if (active == null) return;

        TxtProfileRename.Text = active.Name;
        CboProfile.Visibility = Visibility.Collapsed;
        TxtProfileRename.Visibility = Visibility.Visible;

        // Defer focus to the next dispatcher tick so WPF has finished the
        // visibility transition; otherwise Focus()/SelectAll race with
        // layout and leave the caret in the wrong place.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            TxtProfileRename.Focus();
            Keyboard.Focus(TxtProfileRename);
            TxtProfileRename.SelectAll();
        }), DispatcherPriority.Input);
    }

    private void OnProfileRenameKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            CommitProfileRename();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            CancelProfileRename();
            e.Handled = true;
        }
    }

    private void OnProfileRenameLostFocus(object sender, RoutedEventArgs e)
    {
        if (TxtProfileRename.Visibility == Visibility.Visible)
        {
            CommitProfileRename();
        }
    }

    private void CommitProfileRename()
    {
        var active = _snapshot.WarmthProfiles.FirstOrDefault(p => p.Name == _snapshot.ActiveProfileName);
        if (active == null) { CancelProfileRename(); return; }

        string newName = TxtProfileRename.Text.Trim();
        if (string.IsNullOrEmpty(newName) || newName == active.Name)
        {
            CancelProfileRename();
            return;
        }
        // Reject collisions with other profile names.
        if (_snapshot.WarmthProfiles.Any(p => p != active && p.Name == newName))
        {
            CancelProfileRename();
            return;
        }

        active.Name = newName;
        _snapshot.ActiveProfileName = newName;
        RefreshProfileDropdown();
        PushSettings();
        CancelProfileRename();
    }

    private void CancelProfileRename()
    {
        TxtProfileRename.Visibility = Visibility.Collapsed;
        CboProfile.Visibility = Visibility.Visible;
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
        SldOsdGap.Value         = _snapshot.OsdGapAboveTaskbarDip;

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

        // Hue Phase 3: brightness slider, warmth-offset slider, ignore-boost toggle, four hotkeys.
        // v0.6.31: slider is 0..100 (user-friendly), AppSettings.HueBrightness
        // stays in the bridge's native 1..254 range. Convert each direction.
        SldHueBrightness.Value          = HueBriNativeToPercent(_snapshot.HueBrightness);
        SldHueWarmthOffset.Value        = _snapshot.HueWarmthOffsetKelvin;
        ChkHueIgnoreBoost.IsChecked     = _snapshot.HueIgnoreBoost;
        LblHueBrightness.Text           = ((int)SldHueBrightness.Value) + "%";
        LblHueWarmthOffset.Text         = FormatKelvinOffset((int)SldHueWarmthOffset.Value);
        TxtHkHueBrDown.Value = _snapshot.HotkeyHueBrightnessDown ?? string.Empty;
        TxtHkHueBrUp.Value   = _snapshot.HotkeyHueBrightnessUp   ?? string.Empty;
        TxtHkHueWrDown.Value = _snapshot.HotkeyHueWarmthDown     ?? string.Empty;
        TxtHkHueWrUp.Value   = _snapshot.HotkeyHueWarmthUp       ?? string.Empty;

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

    /// <summary>
    /// True while OnMonitorSelected is loading the picked monitor's
    /// stored offsets back into the sliders. v0.6.42: gates
    /// OnMonitorOffsetChanged so the brightness-slider's first
    /// ValueChanged (fired BEFORE the warmth slider has been updated)
    /// doesn't write the OLD monitor's warmth value into the NEW
    /// monitor's row, which is what made warmth offsets appear to leak
    /// across displays.
    /// </summary>
    private bool _loadingMonitorRow;

    private void OnMonitorSelected(object sender, SelectionChangedEventArgs e)
    {
        if (LstMonitors.SelectedItem is MonitorRow row)
        {
            _loadingMonitorRow = true;
            try
            {
                SldMonBrOffset.Value = row.BrightnessOffset;
                SldMonWrOffset.Value = row.WarmthOffset;
            }
            finally
            {
                _loadingMonitorRow = false;
            }
            UpdateAllValueChips();
        }
    }

    private void OnMonitorOffsetChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingMonitorRow) return;
        if (LstMonitors.SelectedItem is MonitorRow row)
        {
            row.BrightnessOffset = (int)Math.Round(SldMonBrOffset.Value);
            row.WarmthOffset     = (int)Math.Round(SldMonWrOffset.Value);
            LstMonitors.Items.Refresh();
            UpdateAllValueChips();
            PushSettings();
        }
    }

    /// <summary>
    /// v0.6.43: rebuild the Exclusions page's running-app list. Walks the
    /// process table, keeps only processes that own a top-level window
    /// (i.e. things the user can actually interact with), and matches
    /// each one's checked state against the saved exclusion list.
    /// Toggling a row mutates <see cref="AppSettings.ExcludedProcessNames"/>
    /// directly and pushes — no save-on-close required.
    /// </summary>
    private void RefreshRunningApps()
    {
        // Snapshot current saved exclusions so we can mark new rows
        // checked if the user has already excluded them by name.
        var excluded = new HashSet<string>(
            _snapshot.ExcludedProcessNames ?? new List<string>(),
            StringComparer.OrdinalIgnoreCase);

        // Group by process name to avoid duplicates (Chrome typically
        // spawns ~20 processes; we only need one row per distinct name).
        var seen = new Dictionary<string, RunningAppRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle == IntPtr.Zero) continue;  // headless / service
                if (string.IsNullOrEmpty(p.ProcessName)) continue;
                string key = p.ProcessName;
                if (seen.ContainsKey(key)) continue;

                string display;
                try
                {
                    display = p.MainModule?.FileVersionInfo?.FileDescription ?? "";
                    if (string.IsNullOrWhiteSpace(display))
                        display = !string.IsNullOrWhiteSpace(p.MainWindowTitle)
                            ? p.MainWindowTitle
                            : p.ProcessName;
                }
                catch
                {
                    // MainModule access can throw on protected processes.
                    display = !string.IsNullOrWhiteSpace(p.MainWindowTitle)
                        ? p.MainWindowTitle
                        : p.ProcessName;
                }

                var row = new RunningAppRow
                {
                    ProcessName = key,
                    DisplayName = display,
                    IsExcluded  = excluded.Contains(key),
                };
                row.ExclusionToggled += OnRunningAppExclusionToggled;
                seen[key] = row;
            }
            catch
            {
                // Process may have exited mid-enumeration; skip.
            }
            finally { try { p.Dispose(); } catch { } }
        }

        // Sort alphabetically by display name so the list is scannable.
        _runningAppRows.Clear();
        foreach (var r in seen.Values.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase))
            _runningAppRows.Add(r);
    }

    /// <summary>
    /// One row's switch flipped. Update the snapshot's exclusion list and
    /// also mirror the change into the Custom textbox below so power
    /// users see what they've added without having to scroll back.
    /// </summary>
    private void OnRunningAppExclusionToggled(RunningAppRow row)
    {
        var list = _snapshot.ExcludedProcessNames ?? new List<string>();
        if (row.IsExcluded)
        {
            if (!list.Any(n => n.Equals(row.ProcessName, StringComparison.OrdinalIgnoreCase)))
                list.Add(row.ProcessName);
        }
        else
        {
            list.RemoveAll(n => n.Equals(row.ProcessName, StringComparison.OrdinalIgnoreCase));
        }
        _snapshot.ExcludedProcessNames = list;

        // Reflect into the textarea (without re-firing PushSettings via
        // its LostFocus handler — we update the text directly).
        TxtExclusions.Text = string.Join(Environment.NewLine, list);
        PushSettings();
    }

    /// <summary>
    /// v0.6.49: open an OpenFileDialog filtered to executables and add
    /// the chosen file's base name (no path, no .exe suffix) to the
    /// Custom exclusions textbox. We dedupe case-insensitively against
    /// existing lines so the same browse twice is a no-op.
    /// </summary>
    private void OnBrowseExclusionExe()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose an executable to exclude",
            Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            // Default to Program Files so the user lands somewhere
            // useful instead of Documents.
            InitialDirectory = Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFiles)
        };
        if (dlg.ShowDialog(this) != true) return;

        string baseName = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
        if (string.IsNullOrWhiteSpace(baseName)) return;

        // Existing lines, trimmed and non-empty, used for dedupe.
        var existing = (TxtExclusions.Text ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
        if (existing.Any(e => e.Equals(baseName, StringComparison.OrdinalIgnoreCase)))
            return; // already there — silently skip

        existing.Add(baseName);
        TxtExclusions.Text = string.Join(Environment.NewLine, existing);
        PushSettings();
    }

    /// <summary>v0.6.42: Reset every monitor's per-monitor offsets to 0
    /// in one click. Mirrors the change into the sliders for the
    /// currently-selected row so the UI reflects the reset immediately.</summary>
    private void OnResetAllMonitorOffsets(object sender, RoutedEventArgs e)
    {
        foreach (var r in _monRows)
        {
            r.BrightnessOffset = 0;
            r.WarmthOffset     = 0;
        }
        LstMonitors.Items.Refresh();
        _loadingMonitorRow = true;
        try
        {
            SldMonBrOffset.Value = 0;
            SldMonWrOffset.Value = 0;
        }
        finally { _loadingMonitorRow = false; }
        UpdateAllValueChips();
        PushSettings();
    }

    /// <summary>v0.6.30: respect the user's Windows time-format setting.
    /// CurrentCulture's "t" pattern is "HH:mm" in 24-hour locales (e.g. nl-NL,
    /// en-GB) and "h:mm tt" in 12-hour locales (e.g. en-US). Use a fixed
    /// non-DST date so DateTime can hold the time without surprises.</summary>
    private static string Fmt(TimeOfDay t)
    {
        var dt = new DateTime(2000, 1, 1, t.Hour, t.Minute, 0);
        return dt.ToString("t", System.Globalization.CultureInfo.CurrentCulture);
    }

    /// <summary>Format a kelvin offset (positive or negative integer) for the
    /// Hue warmth-offset value chip. "0 K" for neutral, "+500 K" for cooler,
    /// "-1200 K" for warmer.</summary>
    private static string FormatKelvinOffset(int k) => k switch
    {
        0   => "0 K",
        > 0 => $"+{k} K",
        _   => $"{k} K",
    };

    /// <summary>
    /// v0.6.43: keep the Hue Brightness / Warmth offset sliders in sync with
    /// the host's authoritative settings after a Hue hotkey was pressed.
    /// Without this the open settings window kept showing whatever the
    /// sliders were when the user last touched them, while the engine and
    /// the bridge had moved on. We mirror the new values into _snapshot
    /// AND into the slider visual state, suppressing PushSettings during
    /// the assignment so we don't bounce a redundant Applied event back at
    /// the host.
    /// </summary>
    public void SyncHueValuesFromHost(AppSettings authoritative)
    {
        _snapshot.HueBrightness         = authoritative.HueBrightness;
        _snapshot.HueWarmthOffsetKelvin = authoritative.HueWarmthOffsetKelvin;

        // Programmatic slider writes — the slider-loop's PushSettings
        // handler would otherwise fire and round-trip the value back to
        // the host. The _suppressHueSync flag makes those handlers no-op
        // while we're loading.
        _suppressHueSync = true;
        try
        {
            SldHueBrightness.Value   = HueBriNativeToPercent(authoritative.HueBrightness);
            SldHueWarmthOffset.Value = authoritative.HueWarmthOffsetKelvin;
        }
        finally
        {
            _suppressHueSync = false;
        }
        // Refresh the value chips so the user sees the new percentage / K.
        LblHueBrightness.Text   = ((int)SldHueBrightness.Value) + "%";
        LblHueWarmthOffset.Text = FormatKelvinOffset((int)SldHueWarmthOffset.Value);
    }

    /// <summary>True while <see cref="SyncHueValuesFromHost"/> is loading
    /// values into the Hue sliders. Used by the slider's ValueChanged
    /// handlers (which would otherwise call PushSettings) to no-op
    /// during the programmatic update.</summary>
    private bool _suppressHueSync;

    /// <summary>v0.6.31: convert the Hue brightness slider's 0..100 user value
    /// into the bridge's native 1..254 range. We never emit 0 because the
    /// bridge's <c>bri</c> field has min=1; treat slider=0 as the dimmest
    /// usable level.</summary>
    private static int HueBriPercentToNative(double percent)
    {
        int n = (int)Math.Round(percent / 100.0 * 254.0);
        return Math.Clamp(n, 1, 254);
    }

    /// <summary>Inverse of <see cref="HueBriPercentToNative"/>: bridge native
    /// 1..254 → 0..100 for the slider's display value.</summary>
    private static int HueBriNativeToPercent(int native)
    {
        int p = (int)Math.Round(Math.Clamp(native, 1, 254) / 254.0 * 100.0);
        return Math.Clamp(p, 0, 100);
    }

    /// <summary>Parse a time string the user typed in either the locale-current
    /// short-time format ("9:30 PM" or "21:30" depending on Windows setting),
    /// falling back to invariant H:mm so legacy 24-hour entries keep working
    /// in 12-hour locales. Returns <paramref name="fallback"/> if neither
    /// parser can make sense of the input.</summary>
    private static TimeOfDay ParseTime(string? s, TimeOfDay fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        s = s.Trim();
        if (DateTime.TryParse(s, System.Globalization.CultureInfo.CurrentCulture,
                                System.Globalization.DateTimeStyles.None, out var dt))
            return new TimeOfDay(dt.Hour, dt.Minute);
        if (DateTime.TryParseExact(s, new[] { "H:mm", "HH:mm" },
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    System.Globalization.DateTimeStyles.None, out dt))
            return new TimeOfDay(dt.Hour, dt.Minute);
        return fallback;
    }

    /// <summary>True if the user's current locale formats short times in
    /// 12-hour mode (e.g. en-US "h:mm tt"). Detected via the presence of an
    /// AM/PM designator slot in the short-time pattern.</summary>
    private static bool Is12HourLocale()
        => System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('t');

    /// <summary>Format an integer hour (0..24) for the graph's X-axis label
    /// strip. v0.6.42: always 24-hour format regardless of Windows locale.
    /// Compact "00".."24" labels are easier to read than "12 AM" / "6 PM"
    /// strings in the limited horizontal space the strip has, and they
    /// match the 24-hour convention of the underlying schedule curve.</summary>
    private static string FmtAxisHour(int hour) => $"{hour:D2}:00";

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
        _snapshot.OsdGapAboveTaskbarDip = (int)SldOsdGap.Value;

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

        // Hue Phase 3: live-pushed sliders + ignore-boost toggle + four hotkeys.
        _snapshot.HueBrightness            = HueBriPercentToNative(SldHueBrightness.Value);
        _snapshot.HueWarmthOffsetKelvin    = (int)SldHueWarmthOffset.Value;
        _snapshot.HueIgnoreBoost           = ChkHueIgnoreBoost.IsChecked == true;
        _snapshot.HotkeyHueBrightnessDown  = TxtHkHueBrDown.Value.Trim();
        _snapshot.HotkeyHueBrightnessUp    = TxtHkHueBrUp.Value.Trim();
        _snapshot.HotkeyHueWarmthDown      = TxtHkHueWrDown.Value.Trim();
        _snapshot.HotkeyHueWarmthUp        = TxtHkHueWrUp.Value.Trim();

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
