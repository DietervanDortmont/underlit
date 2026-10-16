using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Underlit.Settings;

namespace Underlit.UI;

/// <summary>
/// Modal editor for one LightColorGroup. v0.6.62: adds the inline
/// gradient editor with snap-to-schedule-anchor when editing a stop's
/// kelvin. Black colour stops mean "bulb off at that warmth". The
/// time-offset field is hidden for now per user feedback (semantics
/// caused too much confusion); the field stays in the data model for
/// backward compatibility but isn't read by the controller.
/// </summary>
public partial class ColorProfileEditor : Window
{
    public LightColorGroup? Result { get; private set; }

    private readonly LightColorGroup _editing;
    private readonly bool _isNew;
    /// <summary>v0.6.62: schedule's 4 anchor kelvins, deduped + sorted.
    /// Used as snap targets when the user types a kelvin near one.</summary>
    private readonly int[] _scheduleAnchorKelvins;

    public sealed class TargetRow : INotifyPropertyChanged
    {
        public string Id { get; init; } = "";
        public string DisplayName { get; init; } = "";
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected == value) return; _isSelected = value;
                  PropertyChanged?.Invoke(this, new(nameof(IsSelected))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>v0.6.62: row VM for one gradient stop. Backed directly
    /// by a HueGradientStop so edits propagate; the editor rebuilds the
    /// preview on every change via PropertyChanged.</summary>
    public sealed class GradientStopRow : INotifyPropertyChanged
    {
        public HueGradientStop Underlying { get; init; } = new();

        public int Kelvin
        {
            get => Underlying.Kelvin;
            set { if (Underlying.Kelvin == value) return; Underlying.Kelvin = value;
                  PropertyChanged?.Invoke(this, new(nameof(Kelvin))); RowChanged?.Invoke(); }
        }
        public string Color
        {
            get => Underlying.Color;
            set { if (Underlying.Color == value) return; Underlying.Color = value;
                  PropertyChanged?.Invoke(this, new(nameof(Color)));
                  PropertyChanged?.Invoke(this, new(nameof(Swatch)));
                  RowChanged?.Invoke(); }
        }
        public Brush Swatch
        {
            get
            {
                try
                {
                    var c = (System.Windows.Media.Color)ColorConverter.ConvertFromString(Underlying.Color);
                    return new SolidColorBrush(c);
                }
                catch { return new SolidColorBrush(Colors.White); }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public event Action? RowChanged;
    }

    public ColorProfileEditor(LightColorGroup? profile,
                              List<(string Id, string DisplayName)> availableLights,
                              List<(string Id, string DisplayName)> availableGroups,
                              int[]? scheduleAnchorKelvins = null)
    {
        InitializeComponent();
        _isNew = profile == null;
        _scheduleAnchorKelvins = (scheduleAnchorKelvins ?? new[] { 1500, 4000, 6500 })
            .Distinct().OrderBy(k => k).ToArray();
        _editing = profile == null
            ? new LightColorGroup { Name = "New profile", Enabled = true, TimeOffsetMinutes = 0 }
            : Clone(profile);

        LblTitle.Text = _isNew ? "New colour profile" : "Edit colour profile";
        TxtName.Text  = _editing.Name;
        SldOffset.Value = _editing.TimeOffsetMinutes;
        UpdateOffsetLabel();

        var selLights = new HashSet<string>(_editing.LightIds, StringComparer.OrdinalIgnoreCase);
        var selGroups = new HashSet<string>(_editing.GroupIds, StringComparer.OrdinalIgnoreCase);

        var groupRows = new System.Collections.ObjectModel.ObservableCollection<TargetRow>();
        foreach (var g in availableGroups)
            groupRows.Add(new TargetRow { Id = g.Id, DisplayName = g.DisplayName, IsSelected = selGroups.Contains(g.Id) });
        LstTargetGroups.ItemsSource = groupRows;

        var lightRows = new System.Collections.ObjectModel.ObservableCollection<TargetRow>();
        foreach (var l in availableLights)
            lightRows.Add(new TargetRow { Id = l.Id, DisplayName = l.DisplayName, IsSelected = selLights.Contains(l.Id) });
        LstTargetLights.ItemsSource = lightRows;

        BuildGradientRows();
        UpdateGradientPreview();
        UpdateAnchorLabel();

        SldOffset.ValueChanged += (_, _) => UpdateOffsetLabel();
        BtnAddStop.Click          += (_, _) => AddGradientStop(kelvin: 4500, hex: "#FFFFFFFF");
        BtnResetToSchedule.Click  += (_, _) => ResetGradientToScheduleAnchors();
        BtnCancel.Click += (_, _) => { DialogResult = false; Close(); };
        BtnSave.Click   += (_, _) => Save();
    }

    private void UpdateOffsetLabel()
    {
        int v = (int)SldOffset.Value;
        LblOffset.Text = v == 0 ? "0 min" : (v > 0 ? $"+{v} min" : $"{v} min");
    }

    private void UpdateAnchorLabel()
    {
        LblAnchorList.Text = _scheduleAnchorKelvins.Length == 0
            ? "(no anchors)"
            : string.Join(", ", _scheduleAnchorKelvins.Select(k => $"{k} K"));
    }

    private void BuildGradientRows()
    {
        var rows = new System.Collections.ObjectModel.ObservableCollection<GradientStopRow>();
        if (_editing.Gradient is { Count: > 0 } gs)
        {
            foreach (var s in gs.OrderBy(s => s.Kelvin))
            {
                var row = new GradientStopRow { Underlying = s };
                row.RowChanged += () => { UpdateGradientPreview(); };
                rows.Add(row);
            }
        }
        LstGradientStops.ItemsSource = rows;
    }

    private void AddGradientStop(int kelvin, string hex)
    {
        var stop = new HueGradientStop { Kelvin = SnapKelvin(kelvin), Color = hex };
        if (_editing.Gradient == null) _editing.Gradient = new List<HueGradientStop>();
        _editing.Gradient.Add(stop);
        BuildGradientRows();
        UpdateGradientPreview();
    }

    private void ResetGradientToScheduleAnchors()
    {
        // 4-stop gradient at the schedule's anchor kelvins. Rough
        // defaults: warmest -> deep amber; coolest -> neutral white;
        // mids interpolated. The user can tweak from there.
        var defaults = new (int K, string Hex)[]
        {
            (1500, "#FFFF6A1A"),
            (2700, "#FFFFB070"),
            (4500, "#FFFFE6C0"),
            (6500, "#FFFFFFFF"),
        };
        var stops = new List<HueGradientStop>();
        foreach (var k in _scheduleAnchorKelvins)
        {
            // pick the closest default colour for this anchor kelvin
            string hex = defaults.OrderBy(d => Math.Abs(d.K - k)).First().Hex;
            stops.Add(new HueGradientStop { Kelvin = k, Color = hex });
        }
        // Always include the floor + ceiling so the gradient covers
        // the full schedule range even when anchors don't.
        if (!stops.Any(s => s.Kelvin == 1500)) stops.Add(new HueGradientStop { Kelvin = 1500, Color = defaults[0].Hex });
        if (!stops.Any(s => s.Kelvin == 6500)) stops.Add(new HueGradientStop { Kelvin = 6500, Color = defaults[^1].Hex });
        _editing.Gradient = stops.OrderBy(s => s.Kelvin).ToList();
        BuildGradientRows();
        UpdateGradientPreview();
    }

    /// <summary>Snap to the nearest schedule anchor kelvin if within
    /// 100 K. Otherwise return as-is, clamped to [1500, 6500].</summary>
    private int SnapKelvin(int kelvin)
    {
        kelvin = Math.Clamp(kelvin, 1500, 6500);
        foreach (var anchor in _scheduleAnchorKelvins)
        {
            if (Math.Abs(anchor - kelvin) <= 100) return anchor;
        }
        return kelvin;
    }

    private void UpdateGradientPreview()
    {
        var stops = (LstGradientStops.ItemsSource as System.Collections.IEnumerable)?
            .Cast<GradientStopRow>().Select(r => r.Underlying).OrderBy(s => s.Kelvin).ToList()
            ?? new List<HueGradientStop>();

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint   = new Point(1, 0.5),
        };
        if (stops.Count == 0)
        {
            brush.GradientStops.Add(new GradientStop(Colors.Black, 0));
            brush.GradientStops.Add(new GradientStop(Colors.White, 1));
        }
        else
        {
            foreach (var s in stops)
            {
                double offset = (Math.Clamp(s.Kelvin, 1500, 6500) - 1500) / 5000.0;
                System.Windows.Media.Color c;
                try { c = (System.Windows.Media.Color)ColorConverter.ConvertFromString(s.Color); }
                catch { c = Colors.White; }
                brush.GradientStops.Add(new GradientStop(c, offset));
            }
        }
        GradientPreviewBar.Fill = brush;
    }

    /// <summary>Wired from the XAML on each stop row's swatch button.
    /// Opens the system colour picker pre-filled with the stop's
    /// current colour.</summary>
    public void OnStopSwatchClicked(GradientStopRow row)
    {
        System.Windows.Media.Color seed;
        try { seed = (System.Windows.Media.Color)ColorConverter.ConvertFromString(row.Color); }
        catch { seed = Colors.White; }
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            Color = System.Drawing.Color.FromArgb(seed.R, seed.G, seed.B),
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        row.Color = $"#FF{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
        UpdateGradientPreview();
    }

    public void OnStopMakeBlackClicked(GradientStopRow row)
    {
        row.Color = "#FF000000";
        UpdateGradientPreview();
    }

    public void OnStopRemoveClicked(GradientStopRow row)
    {
        if (_editing.Gradient == null) return;
        _editing.Gradient.RemoveAll(s => ReferenceEquals(s, row.Underlying));
        BuildGradientRows();
        UpdateGradientPreview();
    }

    public void OnStopKelvinTextChanged(GradientStopRow row, string text)
    {
        if (!int.TryParse(text, out int k)) return;
        row.Kelvin = SnapKelvin(k);
        UpdateGradientPreview();
    }

    // ---- XAML event handlers (per-row buttons + textboxes) ----
    // The DataTemplate inside the ItemsControl can't bind directly to
    // Window-level methods; route via Tag={Binding} + Click handlers
    // that look up the row from the sender's Tag.

    private void OnGradientStopSwatchClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is GradientStopRow row) OnStopSwatchClicked(row);
    }
    private void OnGradientStopMakeBlackClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is GradientStopRow row) OnStopMakeBlackClicked(row);
    }
    private void OnGradientStopRemoveClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is GradientStopRow row) OnStopRemoveClicked(row);
    }
    private void OnGradientStopKelvinLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb && tb.Tag is GradientStopRow row)
        {
            OnStopKelvinTextChanged(row, tb.Text ?? "");
            // Reflect snapped value back into the text box.
            tb.Text = row.Kelvin.ToString();
        }
    }
    private void OnGradientStopKelvinKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && sender is System.Windows.Controls.TextBox tb)
        {
            OnGradientStopKelvinLostFocus(tb, e);
            e.Handled = true;
        }
    }

    private static LightColorGroup Clone(LightColorGroup p) => new()
    {
        Id = p.Id, Name = p.Name, Enabled = p.Enabled,
        LightIds = new List<string>(p.LightIds),
        GroupIds = new List<string>(p.GroupIds),
        Gradient = p.Gradient.ConvertAll(s => new HueGradientStop { Kelvin = s.Kelvin, Color = s.Color }),
        TimeOffsetMinutes = p.TimeOffsetMinutes,
    };

    private void Save()
    {
        string name = (TxtName.Text ?? "").Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show(this, "Give your profile a name.", "Colour profile",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtName.Focus();
            return;
        }

        var saved = new LightColorGroup
        {
            Id = _editing.Id,
            Name = name,
            Enabled = _editing.Enabled,
            TimeOffsetMinutes = (int)SldOffset.Value,
            Gradient = _editing.Gradient,
        };

        if (LstTargetGroups.ItemsSource is System.Collections.IEnumerable gItems)
            foreach (var item in gItems)
                if (item is TargetRow t && t.IsSelected) saved.GroupIds.Add(t.Id);
        if (LstTargetLights.ItemsSource is System.Collections.IEnumerable lItems)
            foreach (var item in lItems)
                if (item is TargetRow t && t.IsSelected) saved.LightIds.Add(t.Id);

        Result = saved;
        DialogResult = true;
        Close();
    }
}
