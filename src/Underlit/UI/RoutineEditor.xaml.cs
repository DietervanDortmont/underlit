using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using Underlit.Settings;

namespace Underlit.UI;

/// <summary>
/// Modal editor for one Routine. Self-contained dark palette so it
/// doesn't depend on the SettingsWindow's runtime theme. v0.6.55.
/// </summary>
public partial class RoutineEditor : Window
{
    public Routine? Result { get; private set; }

    private readonly Routine _editing;
    private readonly bool _isNew;

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

    public RoutineEditor(Routine? routine,
                         List<(string Id, string DisplayName)> availableLights,
                         List<(string Id, string DisplayName)> availablePlugs,
                         List<(string Id, string DisplayName)> availableGroups)
    {
        InitializeComponent();
        _isNew = routine == null;
        _editing = routine == null
            ? new Routine
                {
                    Name = "My routine",
                    TriggerKind = RoutineTriggerKind.AbsoluteTime,
                    TriggerHour = 7, TriggerMinute = 0,
                    Days = RoutineDays.EveryDay,
                    Action = RoutineAction.TurnOn,
                    DurationMinutes = 25,
                }
            : CloneRoutine(routine);

        LblTitle.Text = _isNew ? "New routine" : "Edit routine";
        TxtName.Text  = _editing.Name;

        CboTriggerKind.SelectedIndex = (int)_editing.TriggerKind;
        TxtTriggerHour.Text   = _editing.TriggerHour.ToString("D2");
        TxtTriggerMinute.Text = _editing.TriggerMinute.ToString("D2");
        TxtOffsetMinutes.Text = _editing.TriggerOffsetMinutes.ToString();
        UpdateTriggerRowVisibility();

        ChkMon.IsChecked = (_editing.Days & RoutineDays.Monday)    != 0;
        ChkTue.IsChecked = (_editing.Days & RoutineDays.Tuesday)   != 0;
        ChkWed.IsChecked = (_editing.Days & RoutineDays.Wednesday) != 0;
        ChkThu.IsChecked = (_editing.Days & RoutineDays.Thursday)  != 0;
        ChkFri.IsChecked = (_editing.Days & RoutineDays.Friday)    != 0;
        ChkSat.IsChecked = (_editing.Days & RoutineDays.Saturday)  != 0;
        ChkSun.IsChecked = (_editing.Days & RoutineDays.Sunday)    != 0;

        CboAction.SelectedIndex = (int)_editing.Action;
        TxtDurationMinutes.Text = _editing.DurationMinutes.ToString();
        UpdateDurationRowVisibility();

        var selLights = new HashSet<string>(_editing.TargetLightIds, StringComparer.OrdinalIgnoreCase);
        var selGroups = new HashSet<string>(_editing.TargetGroupIds, StringComparer.OrdinalIgnoreCase);

        var groupRows = new System.Collections.ObjectModel.ObservableCollection<TargetRow>();
        foreach (var g in availableGroups)
            groupRows.Add(new TargetRow { Id = g.Id, DisplayName = g.DisplayName, IsSelected = selGroups.Contains(g.Id) });
        LstTargetGroups.ItemsSource = groupRows;

        var lightRows = new System.Collections.ObjectModel.ObservableCollection<TargetRow>();
        foreach (var l in availableLights)
            lightRows.Add(new TargetRow { Id = l.Id, DisplayName = l.DisplayName, IsSelected = selLights.Contains(l.Id) });
        LstTargetLights.ItemsSource = lightRows;

        var plugRows = new System.Collections.ObjectModel.ObservableCollection<TargetRow>();
        foreach (var p in availablePlugs)
            plugRows.Add(new TargetRow { Id = p.Id, DisplayName = p.DisplayName, IsSelected = selLights.Contains(p.Id) });
        LstTargetPlugs.ItemsSource = plugRows;

        CboTriggerKind.SelectionChanged += (_, _) => UpdateTriggerRowVisibility();
        CboAction.SelectionChanged       += (_, _) => UpdateDurationRowVisibility();

        BtnCancel.Click += (_, _) => { DialogResult = false; Close(); };
        BtnSave.Click   += (_, _) => Save();
    }

    private static Routine CloneRoutine(Routine r) => new()
    {
        Id = r.Id, Name = r.Name, Enabled = r.Enabled,
        TriggerKind = r.TriggerKind,
        TriggerHour = r.TriggerHour, TriggerMinute = r.TriggerMinute,
        TriggerOffsetMinutes = r.TriggerOffsetMinutes,
        Days = r.Days,
        TargetLightIds = new List<string>(r.TargetLightIds),
        TargetGroupIds = new List<string>(r.TargetGroupIds),
        Action = r.Action,
        DurationMinutes = r.DurationMinutes,
        Kelvin = r.Kelvin,
        BrightnessPercent = r.BrightnessPercent,
    };

    private void UpdateTriggerRowVisibility()
    {
        bool absolute = CboTriggerKind.SelectedIndex == (int)RoutineTriggerKind.AbsoluteTime;
        AbsoluteTimeRow.Visibility   = absolute ? Visibility.Visible : Visibility.Collapsed;
        RelativeOffsetRow.Visibility = absolute ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateDurationRowVisibility()
    {
        DurationRow.Visibility = CboAction.SelectedIndex == (int)RoutineAction.TurnOnFor
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Save()
    {
        string name = (TxtName.Text ?? "").Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show(this, "Give your routine a name.", "Routine",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtName.Focus();
            return;
        }

        var saved = new Routine
        {
            Id = _editing.Id,
            Name = name,
            Enabled = _editing.Enabled,
            TriggerKind = (RoutineTriggerKind)Math.Max(0, CboTriggerKind.SelectedIndex),
            TriggerHour   = ParseInt(TxtTriggerHour.Text,    _editing.TriggerHour,   0, 23),
            TriggerMinute = ParseInt(TxtTriggerMinute.Text,  _editing.TriggerMinute, 0, 59),
            TriggerOffsetMinutes = ParseInt(TxtOffsetMinutes.Text, _editing.TriggerOffsetMinutes, -720, 720),
            Days = ReadDayFlags(),
            Action = (RoutineAction)Math.Max(0, CboAction.SelectedIndex),
            DurationMinutes = Math.Max(1, ParseInt(TxtDurationMinutes.Text, _editing.DurationMinutes, 1, 720)),
        };

        // Targets: groups go to TargetGroupIds; both lights and plugs go
        // to TargetLightIds (host fans them out via the same /lights API).
        if (LstTargetGroups.ItemsSource is System.Collections.IEnumerable gItems)
            foreach (var item in gItems)
                if (item is TargetRow t && t.IsSelected) saved.TargetGroupIds.Add(t.Id);
        if (LstTargetLights.ItemsSource is System.Collections.IEnumerable lItems)
            foreach (var item in lItems)
                if (item is TargetRow t && t.IsSelected) saved.TargetLightIds.Add(t.Id);
        if (LstTargetPlugs.ItemsSource is System.Collections.IEnumerable pItems)
            foreach (var item in pItems)
                if (item is TargetRow t && t.IsSelected) saved.TargetLightIds.Add(t.Id);

        if (saved.TargetLightIds.Count == 0 && saved.TargetGroupIds.Count == 0)
        {
            var ok = MessageBox.Show(this,
                "This routine has no targets - it won't do anything when it fires. Save anyway?",
                "Routine",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (ok != MessageBoxResult.OK) return;
        }

        Result = saved;
        DialogResult = true;
        Close();
    }

    private RoutineDays ReadDayFlags()
    {
        var d = RoutineDays.None;
        if (ChkMon.IsChecked == true) d |= RoutineDays.Monday;
        if (ChkTue.IsChecked == true) d |= RoutineDays.Tuesday;
        if (ChkWed.IsChecked == true) d |= RoutineDays.Wednesday;
        if (ChkThu.IsChecked == true) d |= RoutineDays.Thursday;
        if (ChkFri.IsChecked == true) d |= RoutineDays.Friday;
        if (ChkSat.IsChecked == true) d |= RoutineDays.Saturday;
        if (ChkSun.IsChecked == true) d |= RoutineDays.Sunday;
        return d;
    }

    private static int ParseInt(string s, int fallback, int min, int max)
    {
        if (!int.TryParse((s ?? "").Trim(), out var v)) return fallback;
        return Math.Clamp(v, min, max);
    }
}
