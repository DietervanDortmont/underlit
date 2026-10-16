using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Underlit.Hue;
using Underlit.Settings;

namespace Underlit.UI;

/// <summary>
/// v0.7.1: bulb chips under the lights timeline. One zone per colour group,
/// plus Default (controlled but not in a group), Not controlled, and a
/// "new group" drop target. Drag a chip between zones, or onto a timeline
/// bar, to move a bulb. Assignment follows the same rule as the
/// HueController: the first enabled group that contains a bulb drives it,
/// otherwise Default if it's selected, otherwise Underlit leaves it alone.
/// </summary>
public partial class SettingsWindow
{
    private const string NoneZoneKey = "none";
    private const string NewZoneKey  = "new";
    private const string BulbDragFormat = "Underlit.BulbId";
    private bool _bulbDragActive;

    // -------------------------------------------------------------------
    // Assignment (mirrors HueController.ResolveExclusiveLights)
    // -------------------------------------------------------------------

    /// <summary>bulb id -> group id / <see cref="DefaultRowKey"/>.
    /// Bulbs Underlit doesn't control are absent.</summary>
    private Dictionary<string, string> ResolveBulbAssignment()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var groups = _snapshot.ColorGroups ?? new List<LightColorGroup>();
        // Enabled groups first, in order (that's who actually drives a
        // bulb), then disabled groups so their members still show there.
        foreach (var g in groups.Where(g => g.Enabled).Concat(groups.Where(g => !g.Enabled)))
            foreach (var id in ExpandToBulbs(g.LightIds, g.GroupIds))
                map.TryAdd(id, g.Id);
        foreach (var id in ExpandToBulbs(_snapshot.HueSelectedLightIds, _snapshot.HueSelectedGroupIds))
            map.TryAdd(id, DefaultRowKey);
        return map;
    }

    private static HashSet<string> BulbsAssignedTo(Dictionary<string, string> map, string key)
        => new(map.Where(kv => kv.Value == key).Select(kv => kv.Key), StringComparer.OrdinalIgnoreCase);

    private HashSet<string> ExpandToBulbs(IEnumerable<string> lightIds, IEnumerable<string> groupIds)
    {
        var set = new HashSet<string>(lightIds, StringComparer.OrdinalIgnoreCase);
        foreach (var gid in groupIds)
            foreach (var lid in HueRoomMembers(gid)) set.Add(lid);
        if (_hueLightsByIdSnapshot != null)
            set.RemoveWhere(id => _hueLightsByIdSnapshot.TryGetValue(id, out var l) && l.IsPlug);
        return set;
    }

    private IEnumerable<string> HueRoomMembers(string hueGroupId)
    {
        var grp = _hueGroupsSnapshot?.FirstOrDefault(x => string.Equals(x.Id, hueGroupId, StringComparison.OrdinalIgnoreCase));
        return grp?.LightIds ?? (IEnumerable<string>)Array.Empty<string>();
    }

    private bool IsPlugId(string id)
        => _hueLightsByIdSnapshot != null && _hueLightsByIdSnapshot.TryGetValue(id, out var l) && l.IsPlug;

    // -------------------------------------------------------------------
    // Moving bulbs
    // -------------------------------------------------------------------

    /// <summary>Move one bulb to a group id, Default, Not controlled, or a
    /// new group. Hue rooms that contained the bulb are expanded to their
    /// other bulbs so nothing else changes.</summary>
    private void MoveBulb(string lightId, string targetKey)
    {
        var groups = _snapshot.ColorGroups;
        LightColorGroup? target = groups.FirstOrDefault(g => g.Id == targetKey);
        string? sourceKey = ResolveBulbAssignment().TryGetValue(lightId, out var k) ? k : null;
        if (sourceKey == targetKey && targetKey != NewZoneKey) return;

        if (targetKey == NewZoneKey)
        {
            string bulbName = _hueLightsByIdSnapshot != null && _hueLightsByIdSnapshot.TryGetValue(lightId, out var bl) ? bl.Name : "bulb";
            var box = new InputBox { Owner = this, Title = "New group", Prompt = $"Name for the new group (starting with {bulbName})", Value = "New group" };
            if (box.ShowDialog() != true || string.IsNullOrWhiteSpace(box.Value)) return;
            // Start from the stops of the row the bulb came from, so the
            // bulb keeps doing what it did until the new row is edited.
            var fromStops = groups.FirstOrDefault(g => g.Id == sourceKey) is { TimeStops.Count: > 0 } src
                ? src.TimeStops : _snapshot.HueTimeStops;
            target = new LightColorGroup
            {
                Name = box.Value.Trim(),
                TimeStops = fromStops.Select(s => s.Copy()).ToList(),
            };
            groups.Add(target);
        }

        foreach (var g in groups) RemoveBulbFromGroup(g, lightId);

        if (targetKey == NoneZoneKey) Deselect(lightId);
        else EnsureSelected(lightId); // so leaving a group later lands it in Default

        if (target != null && !target.LightIds.Contains(lightId, StringComparer.OrdinalIgnoreCase))
        {
            target.LightIds.Add(lightId);
            target.Enabled = true;
        }
        AfterBulbsChanged();
    }

    private void RemoveBulbFromGroup(LightColorGroup g, string lightId)
    {
        g.LightIds.RemoveAll(x => string.Equals(x, lightId, StringComparison.OrdinalIgnoreCase));
        foreach (var gid in g.GroupIds.ToList())
        {
            var members = HueRoomMembers(gid).ToList();
            if (!members.Contains(lightId, StringComparer.OrdinalIgnoreCase)) continue;
            g.GroupIds.Remove(gid);
            foreach (var m in members)
                if (!string.Equals(m, lightId, StringComparison.OrdinalIgnoreCase) && !IsPlugId(m)
                    && !g.LightIds.Contains(m, StringComparer.OrdinalIgnoreCase))
                    g.LightIds.Add(m);
        }
    }

    private void Deselect(string lightId)
    {
        _snapshot.HueSelectedLightIds.RemoveAll(x => string.Equals(x, lightId, StringComparison.OrdinalIgnoreCase));
        foreach (var gid in _snapshot.HueSelectedGroupIds.ToList())
        {
            var members = HueRoomMembers(gid).ToList();
            if (!members.Contains(lightId, StringComparer.OrdinalIgnoreCase)) continue;
            _snapshot.HueSelectedGroupIds.Remove(gid);
            foreach (var m in members)
                if (!string.Equals(m, lightId, StringComparison.OrdinalIgnoreCase)
                    && !_snapshot.HueSelectedLightIds.Contains(m, StringComparer.OrdinalIgnoreCase))
                    _snapshot.HueSelectedLightIds.Add(m);
        }
    }

    private void EnsureSelected(string lightId)
    {
        bool viaRoom = _snapshot.HueSelectedGroupIds.Any(gid => HueRoomMembers(gid).Contains(lightId, StringComparer.OrdinalIgnoreCase));
        if (!viaRoom && !_snapshot.HueSelectedLightIds.Contains(lightId, StringComparer.OrdinalIgnoreCase))
            _snapshot.HueSelectedLightIds.Add(lightId);
    }

    /// <summary>Refresh everything that shows bulb membership, then push.</summary>
    private void AfterBulbsChanged()
    {
        PushSettings();
        HueTakeBackRequested?.Invoke();
        BuildColorProfilesList();
        if (_hueGroupsSnapshot != null && _hueLightsByIdSnapshot != null)
            BuildHueLightRows(_hueGroupsSnapshot, _hueLightsByIdSnapshot.Values.Where(l => !l.IsPlug).ToList());
        RedrawLightsTimeline();
    }

    /// <summary>One click: every controlled bulb into "Colour bulbs" or
    /// "White bulbs", each with its own copy of the stops.</summary>
    private void SplitByBulbType()
    {
        if (_hueLightsByIdSnapshot == null)
        {
            LblTimelineStatus.Text = "Waiting for the bridge to list your bulbs - try again in a moment.";
            return;
        }
        var controlled = ResolveBulbAssignment().Keys
            .Where(id => _hueLightsByIdSnapshot.ContainsKey(id))
            .ToList();
        var colour = controlled.Where(id => _hueLightsByIdSnapshot[id].HasColor).ToList();
        var white  = controlled.Where(id => !_hueLightsByIdSnapshot[id].HasColor).ToList();
        if (controlled.Count == 0)
        {
            LblTimelineStatus.Text = "No bulbs are controlled yet - tick some in ROOMS & LIGHTS first.";
            return;
        }

        var groups = _snapshot.ColorGroups;
        if (groups.Count > 0)
        {
            var answer = MessageBox.Show(this,
                $"This replaces the {groups.Count} group(s) in this profile with two groups:\n\n" +
                $"  Colour bulbs ({colour.Count})\n  White bulbs ({white.Count})\n\n" +
                "Both start with the Default row's colour stops. Tip: make a new profile first (+ New) if you want to keep the current groups.",
                "Split into colour + white groups", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;
        }

        groups.Clear();
        if (colour.Count > 0)
            groups.Add(new LightColorGroup { Name = "Colour bulbs", LightIds = colour, TimeStops = _snapshot.HueTimeStops.Select(s => s.Copy()).ToList() });
        if (white.Count > 0)
            groups.Add(new LightColorGroup { Name = "White bulbs", LightIds = white, TimeStops = _snapshot.HueTimeStops.Select(s => s.Copy()).ToList() });
        foreach (var id in controlled) EnsureSelected(id);
        AfterBulbsChanged();
    }

    // -------------------------------------------------------------------
    // Drawing the zones
    // -------------------------------------------------------------------

    private void RebuildBulbZones()
    {
        if (BulbZonesPanel == null || _bulbDragActive) return;
        BulbZonesPanel.Children.Clear();
        if (_hueLightsByIdSnapshot == null)
        {
            BulbZonesPanel.Children.Add(new TextBlock
            {
                Text = "Loading bulbs from the bridge...", FontSize = 11, FontStyle = FontStyles.Italic,
                Foreground = (Brush?)TryFindResource("App.TextSecondary") ?? Brushes.Gray,
            });
            return;
        }

        var map = ResolveBulbAssignment();
        var groups = _snapshot.ColorGroups ?? new List<LightColorGroup>();
        var now = _simulatedNow ?? DateTime.Now;

        foreach (var g in groups)
            AddZone(g.Id, string.IsNullOrWhiteSpace(g.Name) ? "(unnamed group)" : g.Name, g.Enabled ? null : "group is off",
                    BulbsAssignedTo(map, g.Id), g.TimeStops is { Count: > 0 } ? g.TimeStops : _snapshot.HueTimeStops, now);
        AddZone(DefaultRowKey, "Default", groups.Count > 0 ? "not in a group" : null,
                BulbsAssignedTo(map, DefaultRowKey), _snapshot.HueTimeStops, now);
        var none = new HashSet<string>(
            _hueLightsByIdSnapshot.Values.Where(l => !l.IsPlug && !map.ContainsKey(l.Id)).Select(l => l.Id),
            StringComparer.OrdinalIgnoreCase);
        AddZone(NoneZoneKey, "Not controlled", "Underlit leaves these alone", none, null, now);
        AddZone(NewZoneKey, "+ New group", null, new HashSet<string>(), null, now);
    }

    private void AddZone(string key, string title, string? note, HashSet<string> bulbIds,
                         List<HueTimeStop>? stops, DateTime now)
    {
        var secondary = (Brush?)TryFindResource("App.TextSecondary") ?? Brushes.Gray;
        var primary   = (Brush?)TryFindResource("App.TextPrimary") ?? Brushes.Black;
        var zoneBg    = (Brush?)TryFindResource("App.HoverBg") ?? new SolidColorBrush(Color.FromArgb(0x10, 0x80, 0x80, 0x80));
        var border    = (Brush?)TryFindResource("App.Divider") ?? new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TlLabelWidth - 14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var head = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        head.Children.Add(new TextBlock
        {
            Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = key == NewZoneKey ? secondary : primary,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (note != null)
            head.Children.Add(new TextBlock { Text = note, FontSize = 10, Foreground = secondary, TextTrimming = TextTrimming.CharacterEllipsis });
        grid.Children.Add(head);

        var wrap = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, MinHeight = 26 };
        Grid.SetColumn(wrap, 1);
        grid.Children.Add(wrap);

        if (key == NewZoneKey)
        {
            wrap.Children.Add(new TextBlock
            {
                Text = "Drop a bulb here to start a new group with it.",
                FontSize = 11, FontStyle = FontStyles.Italic, Foreground = secondary,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 4, 0, 4),
            });
        }
        else if (bulbIds.Count == 0)
        {
            wrap.Children.Add(new TextBlock
            {
                Text = "No bulbs - drag some here.", FontSize = 11, FontStyle = FontStyles.Italic,
                Foreground = secondary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 4, 0, 4),
            });
        }
        else
        {
            foreach (var id in bulbIds.OrderBy(i => _hueLightsByIdSnapshot![i].HasColor ? 0 : 1)
                                      .ThenBy(i => _hueLightsByIdSnapshot![i].Name, StringComparer.OrdinalIgnoreCase))
                wrap.Children.Add(BuildBulbChip(_hueLightsByIdSnapshot![id], stops, now));
        }

        var zone = new Border
        {
            Child = grid,
            Background = zoneBg,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 6),
            AllowDrop = true,
        };
        WireBulbDropTarget(zone, key);
        BulbZonesPanel.Children.Add(zone);
    }

    private FrameworkElement BuildBulbChip(HueLight bulb, List<HueTimeStop>? stops, DateTime now)
    {
        var primary = (Brush?)TryFindResource("App.TextPrimary") ?? Brushes.Black;
        var secondary = (Brush?)TryFindResource("App.TextSecondary") ?? Brushes.Gray;
        var chipBg = (Brush?)TryFindResource("App.InputBg") ?? new SolidColorBrush(Color.FromArgb(0x22, 0x80, 0x80, 0x80));

        // Dot = what this bulb is doing right now in time-of-day mode.
        Color dot = Color.FromRgb(0x9A, 0x9A, 0x9E);
        if (IsTimeMode && stops != null)
            dot = BulbDisplayColor(bulb, TimeStopSampler.At(stops, _snapshot, now));

        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new Ellipse
        {
            Width = 10, Height = 10, Fill = new SolidColorBrush(dot),
            Stroke = new SolidColorBrush(Color.FromArgb(0x66, 0x80, 0x80, 0x80)), StrokeThickness = 1,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
        });
        sp.Children.Add(new TextBlock
        {
            Text = bulb.Name, FontSize = 12, Foreground = primary, VerticalAlignment = VerticalAlignment.Center,
        });
        sp.Children.Add(new TextBlock
        {
            Text = (bulb.HasColor ? "colour" : "white") + (bulb.Reachable ? "" : " · offline"),
            FontSize = 10, Foreground = secondary, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 1, 0, 0),
        });

        string range = bulb.CtMaxMireds is > 0 && bulb.CtMinMireds is > 0
            ? $"{(int)Math.Round(1e6 / bulb.CtMaxMireds.Value)}-{(int)Math.Round(1e6 / bulb.CtMinMireds.Value)} K"
            : "";
        var chip = new Border
        {
            Child = sp,
            Background = chipBg,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8, 3, 10, 3),
            Margin = new Thickness(0, 2, 6, 2),
            Cursor = Cursors.SizeAll,
            Opacity = bulb.Reachable ? 1.0 : 0.65,
            ToolTip = (bulb.HasColor ? "Full-colour bulb" : "White-only bulb")
                      + (range != "" ? $", {range}" : "")
                      + (bulb.Reachable ? "" : " - offline right now")
                      + ". Drag to another group, or click for a Move to menu.",
        };

        Point start = default;
        bool pressed = false, dragged = false;
        chip.PreviewMouseLeftButtonDown += (_, e) =>
        {
            start = e.GetPosition(this);
            pressed = true;
            dragged = false;
        };
        chip.PreviewMouseMove += (_, e) =>
        {
            if (!pressed || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - start.X) < 4 && Math.Abs(p.Y - start.Y) < 4) return;
            pressed = false;
            dragged = true;
            _bulbDragActive = true;
            try
            {
                chip.Opacity = 0.4;
                DragDrop.DoDragDrop(chip, new DataObject(BulbDragFormat, bulb.Id), DragDropEffects.Move);
            }
            catch (Exception ex) { Logger.Warn("Bulb drag failed", ex); }
            finally
            {
                _bulbDragActive = false;
            }
            // Rebuild after the drop (or a cancelled drag) so the chip lands in place.
            Dispatcher.BeginInvoke(new Action(() => RedrawLightsTimeline()));
        };
        // A plain click opens a "Move to" menu - same result as dragging,
        // handy for touchpads and when the target is far down the list.
        chip.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!pressed || dragged) return;
            pressed = false;
            e.Handled = true;
            ShowMoveMenu(chip, bulb);
        };
        return chip;
    }

    private void ShowMoveMenu(FrameworkElement chip, HueLight bulb)
    {
        var map = ResolveBulbAssignment();
        string? current = map.TryGetValue(bulb.Id, out var k) ? k : NoneZoneKey;
        var menu = new ContextMenu { PlacementTarget = chip };
        menu.Items.Add(new MenuItem { Header = $"Move {bulb.Name} to", IsEnabled = false });
        void Add(string header, string key)
        {
            var mi = new MenuItem { Header = header, IsChecked = key == current, IsEnabled = key != current };
            mi.Click += (_, _) => MoveBulb(bulb.Id, key);
            menu.Items.Add(mi);
        }
        foreach (var g in _snapshot.ColorGroups)
            Add(string.IsNullOrWhiteSpace(g.Name) ? "(unnamed group)" : g.Name, g.Id);
        Add("Default", DefaultRowKey);
        Add("Not controlled", NoneZoneKey);
        menu.Items.Add(new Separator());
        Add("New group...", NewZoneKey);
        menu.IsOpen = true;
    }

    /// <summary>Make any element accept dropped bulb chips for a row key.</summary>
    private void WireBulbDropTarget(Border target, string key)
    {
        target.AllowDrop = true;
        Brush? originalBorder = null;
        Thickness originalThickness = default;
        var accent = (Brush?)TryFindResource("App.Accent") ?? new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));

        target.DragEnter += (_, e) =>
        {
            if (!e.Data.GetDataPresent(BulbDragFormat)) return;
            originalBorder = target.BorderBrush;
            originalThickness = target.BorderThickness;
            target.BorderBrush = accent;
            target.BorderThickness = new Thickness(2);
        };
        target.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(BulbDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        target.DragLeave += (_, _) =>
        {
            if (originalBorder != null) { target.BorderBrush = originalBorder; target.BorderThickness = originalThickness; }
        };
        target.Drop += (_, e) =>
        {
            if (originalBorder != null) { target.BorderBrush = originalBorder; target.BorderThickness = originalThickness; }
            if (e.Data.GetData(BulbDragFormat) is not string id) return;
            e.Handled = true;
            // Run after DoDragDrop has returned so the dialog for a new
            // group (and the rebuild) don't happen inside the drag loop.
            Dispatcher.BeginInvoke(new Action(() => MoveBulb(id, key)));
        };
    }
}
