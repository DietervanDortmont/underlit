using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Underlit.Hue;
using Underlit.Settings;

namespace Underlit.UI;

/// <summary>
/// v0.7.0: time-of-day lights timeline. When "Colour lights by" is set to
/// Time of day, every timeline row becomes a 24 h bar of colour stops the
/// user edits directly: click the bar to add a stop, drag a stop to move
/// it, click a stop to edit colour / brightness / time. Colour and
/// brightness come from <see cref="TimeStopSampler"/> and
/// <see cref="BulbColorMapper"/>, the same code the HueController uses, so
/// the bar is exactly what the lamps will do.
///
/// <para>v0.7.1: a row with white-only bulbs shows what those bulbs do in a
/// thin strip along the bottom of its bar (they fade out where the colour
/// leaves white). Bulb chips + drag-and-drop live in SettingsWindow.BulbZones.cs.</para>
/// </summary>
public partial class SettingsWindow
{
    /// <summary>Host pauses the HueController while the stop editor
    /// previews colours on the real bulbs (true), and takes the bulbs back
    /// afterwards (false).</summary>
    public event Action<bool>? HuePreviewActiveChanged;

    /// <summary>v0.7.1: the user just changed which bulbs go where or what a
    /// row does; take back any bulbs Google / the Hue app had taken over so
    /// the change shows up straight away.</summary>
    public event Action? HueTakeBackRequested;

    private const double TlLabelWidth = 130;
    private const double TlRowH       = 34;
    private const double TlRowSpacing = 10;
    private const double TlAxisH      = 18;
    private const int    TlSnapMinutes = 5;
    private const double TlWhiteStripH = 8;

    private sealed class TimeRow
    {
        /// <summary>Group id, or <see cref="DefaultRowKey"/>.</summary>
        public string Key = DefaultRowKey;
        public string Label = "";
        public string Sublabel = "";
        public bool Enabled = true;
        /// <summary>Null = the Default row (AppSettings.HueTimeStops).</summary>
        public LightColorGroup? Group;
        /// <summary>The list currently shown. For a group without its own
        /// stops yet this is the Default list, copied on first edit.</summary>
        public List<HueTimeStop> Stops = new();
        public bool UsesDefaultStops;
        public HashSet<string> LightIds = new(StringComparer.OrdinalIgnoreCase);
        public int ColourBulbs;
        public int WhiteBulbs;
        /// <summary>The white-only bulb in this row that gives up first
        /// (highest warm limit); drives the strip under the bar.</summary>
        public HueLight? LimitingWhiteBulb;
        // Drawn elements, kept so a drag can update them without a full redraw.
        public Border? Bar;
        public double Y;
    }

    private const string DefaultRowKey = "default";

    private bool IsTimeMode => _snapshot.HueColorSource == HueColorSource.TimeOfDay;

    /// <summary>Called once from the constructor.</summary>
    private void WireTimeOfDayLights()
    {
        CboHueColorSource.SelectedIndex     = (int)_snapshot.HueColorSource;
        CboHueOverrideRelease.SelectedIndex = (int)_snapshot.HueOverrideRelease;
        SldWhiteFollow.Value = Math.Clamp(_snapshot.HueWhiteBulbFollowPercent, 0, 100);
        UpdateWhiteFollowLabel();
        UpdateTimeModeVisibility();

        CboHueColorSource.SelectionChanged += (_, _) =>
        {
            var src = (HueColorSource)Math.Max(0, CboHueColorSource.SelectedIndex);
            if (src == _snapshot.HueColorSource) return;
            _snapshot.HueColorSource = src;
            UpdateTimeModeVisibility();

            // In time-of-day mode the Brightness slider scales every stop.
            // Someone who had it turned right down (hotkey at night) would
            // otherwise see almost nothing, so start from full.
            if (IsTimeMode && SldHueBrightness.Value < 50)
                SldHueBrightness.Value = 100;

            PushSettings();
            RedrawLightsTimeline();
        };
        CboHueOverrideRelease.SelectionChanged += (_, _) =>
        {
            _snapshot.HueOverrideRelease = (HueOverrideRelease)Math.Max(0, CboHueOverrideRelease.SelectedIndex);
            PushSettings();
        };
        SldWhiteFollow.ValueChanged += (_, _) =>
        {
            _snapshot.HueWhiteBulbFollowPercent = (int)SldWhiteFollow.Value;
            UpdateWhiteFollowLabel();
            PushSettings();
            RedrawLightsTimeline();
        };
        BtnSplitByBulbType.Click += (_, _) => SplitByBulbType();
    }

    private void UpdateTimeModeVisibility()
    {
        LblTimeModeHint.Visibility = IsTimeMode ? Visibility.Visible : Visibility.Collapsed;
        WhiteFollowRow.Visibility  = IsTimeMode ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateWhiteFollowLabel()
    {
        int v = (int)SldWhiteFollow.Value;
        LblWhiteFollow.Text = v <= 15 ? "Early (warm white)"
                            : v <= 45 ? "Around orange"
                            : v <= 75 ? "Late (deep orange)"
                            : "Very late";
    }

    // -------------------------------------------------------------------
    // Rows
    // -------------------------------------------------------------------

    private List<TimeRow> BuildTimeRows()
    {
        var rows = new List<TimeRow>();
        var groups = _snapshot.ColorGroups ?? new List<LightColorGroup>();
        var assignment = ResolveBulbAssignment();

        foreach (var g in groups)
        {
            bool own = g.TimeStops is { Count: > 0 };
            var row = new TimeRow
            {
                Key = g.Id,
                Label = string.IsNullOrWhiteSpace(g.Name) ? "(unnamed group)" : g.Name,
                Enabled = g.Enabled,
                Group = g,
                Stops = own ? g.TimeStops : _snapshot.HueTimeStops,
                UsesDefaultStops = !own,
                LightIds = BulbsAssignedTo(assignment, g.Id),
            };
            FillBulbMix(row);
            row.Sublabel = !g.Enabled ? "group is off"
                         : BulbMixLabel(row) + (own ? "" : " · same as Default");
            rows.Add(row);
        }

        var def = new TimeRow
        {
            Key = DefaultRowKey,
            Label = "Default",
            Stops = _snapshot.HueTimeStops,
            LightIds = BulbsAssignedTo(assignment, DefaultRowKey),
        };
        FillBulbMix(def);
        def.Sublabel = BulbMixLabel(def) + (groups.Count == 0 ? "" : " not in a group");
        rows.Add(def);
        return rows;
    }

    private void FillBulbMix(TimeRow row)
    {
        if (_hueLightsByIdSnapshot == null) return;
        foreach (var id in row.LightIds)
        {
            if (!_hueLightsByIdSnapshot.TryGetValue(id, out var l)) continue;
            if (l.HasColor) { row.ColourBulbs++; continue; }
            row.WhiteBulbs++;
            // Gives up first = warmest limit is the highest kelvin = smallest max mireds.
            int mine = l.CtMaxMireds ?? 370;
            int theirs = row.LimitingWhiteBulb?.CtMaxMireds ?? 370;
            if (row.LimitingWhiteBulb == null || mine < theirs) row.LimitingWhiteBulb = l;
        }
    }

    private string BulbMixLabel(TimeRow row)
    {
        if (_hueLightsByIdSnapshot == null) return "";
        if (row.ColourBulbs + row.WhiteBulbs == 0) return "no bulbs";
        var parts = new List<string>();
        if (row.ColourBulbs > 0) parts.Add($"{row.ColourBulbs} colour");
        if (row.WhiteBulbs > 0)  parts.Add($"{row.WhiteBulbs} white");
        return string.Join(" · ", parts);
    }

    /// <summary>Give a group row its own copy of the stops before the
    /// first edit, so editing it doesn't change the Default row.</summary>
    private void EnsureOwnStops(TimeRow row)
    {
        if (row.Group == null || !row.UsesDefaultStops) return;
        row.Group.TimeStops = _snapshot.HueTimeStops.Select(s => s.Copy()).ToList();
        row.Stops = row.Group.TimeStops;
        row.UsesDefaultStops = false;
    }

    // -------------------------------------------------------------------
    // Drawing
    // -------------------------------------------------------------------

    private void RedrawTimeModeTimeline(double w)
    {
        var rows = BuildTimeRows();
        double timelineX = TlLabelWidth;
        double timelineW = Math.Max(1, w - TlLabelWidth - 10);

        double neededH = rows.Count * (TlRowH + TlRowSpacing) + TlAxisH + 4;
        if (Math.Abs(LightsTimelineCanvas.Height - neededH) > 0.5)
            LightsTimelineCanvas.Height = neededH; // SizeChanged will redraw again; cheap.

        var gridBrush = new SolidColorBrush(Color.FromArgb(0x22, 0x80, 0x80, 0x80));
        double barsBottom = rows.Count * (TlRowH + TlRowSpacing) - TlRowSpacing;
        for (int hour = 0; hour <= 24; hour += 3)
        {
            double x = timelineX + hour / 24.0 * timelineW;
            LightsTimelineCanvas.Children.Add(new Line
            {
                X1 = x, X2 = x, Y1 = 0, Y2 = barsBottom,
                Stroke = gridBrush, StrokeThickness = 1, IsHitTestVisible = false,
            });
        }

        double y = 0;
        foreach (var row in rows)
        {
            row.Y = y;
            DrawTimeRow(row, timelineX, timelineW);
            y += TlRowH + TlRowSpacing;
        }

        // Hour labels.
        var axisBrush = (Brush?)TryFindResource("App.TextSecondary")
                        ?? new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        for (int hour = 0; hour <= 24; hour += 3)
        {
            var label = new TextBlock { Text = $"{hour:D2}:00", FontSize = 10, Foreground = axisBrush };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double x = timelineX + hour / 24.0 * timelineW;
            double off = hour == 0 ? 0 : hour == 24 ? -label.DesiredSize.Width : -label.DesiredSize.Width / 2;
            Canvas.SetLeft(label, x + off);
            Canvas.SetTop(label, barsBottom + 4);
            LightsTimelineCanvas.Children.Add(label);
        }

        // "Now" (or simulated now) line.
        var now = _simulatedNow ?? DateTime.Now;
        double nx = timelineX + now.TimeOfDay.TotalMinutes / 1440.0 * timelineW;
        var accent = (Brush?)TryFindResource("App.Accent") ?? new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF));
        _timelinePlayhead = new Line
        {
            X1 = nx, X2 = nx, Y1 = -2, Y2 = barsBottom + 2,
            Stroke = accent, StrokeThickness = 2,
            StrokeDashArray = _simulatedNow.HasValue ? new DoubleCollection(new[] { 4.0, 3.0 }) : null,
            IsHitTestVisible = false,
        };
        LightsTimelineCanvas.Children.Add(_timelinePlayhead);

        LblTimelineStatus.Text = _simulatedNow.HasValue
            ? $"Simulating {now:HH:mm}"
            : $"Now {now:HH:mm}. The Brightness slider (Adjustment card) scales every stop: {(int)SldHueBrightness.Value}%.";
    }

    private void DrawTimeRow(TimeRow row, double timelineX, double timelineW)
    {
        var primary   = (Brush?)TryFindResource("App.TextPrimary") ?? Brushes.White;
        var secondary = (Brush?)TryFindResource("App.TextSecondary")
                        ?? new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));

        var labels = new StackPanel { Width = TlLabelWidth - 10, Background = Brushes.Transparent };
        labels.Children.Add(new TextBlock
        {
            Text = row.Label, FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = primary, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (!string.IsNullOrEmpty(row.Sublabel))
            labels.Children.Add(new TextBlock
            {
                Text = row.Sublabel, FontSize = 10, Foreground = secondary,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        labels.ToolTip = "Right-click for options";
        labels.ContextMenu = BuildRowContextMenu(row);
        Canvas.SetLeft(labels, 0);
        Canvas.SetTop(labels, row.Y + 2);
        LightsTimelineCanvas.Children.Add(labels);

        // Main bar: the colour bulbs. A row of only white bulbs shows them instead.
        bool onlyWhite = row.ColourBulbs == 0 && row.LimitingWhiteBulb != null;
        bool strip = row.ColourBulbs > 0 && row.LimitingWhiteBulb != null;

        var grid = new Grid();
        grid.Children.Add(new Border
        {
            Background = BuildTimeBrush(row.Stops, onlyWhite ? row.LimitingWhiteBulb : null),
            CornerRadius = new CornerRadius(7),
        });
        if (strip)
        {
            grid.Children.Add(new Border
            {
                Height = TlWhiteStripH,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = BuildTimeBrush(row.Stops, row.LimitingWhiteBulb),
                CornerRadius = new CornerRadius(0, 0, 7, 7),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                ToolTip = $"White-only bulbs ({row.WhiteBulbs}): they follow as far as they can, then dim out.",
            });
        }

        var bar = new Border
        {
            Width = timelineW,
            Height = TlRowH,
            CornerRadius = new CornerRadius(7),
            Child = grid,
            BorderBrush = (Brush?)TryFindResource("App.CardBorder") ?? new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Opacity = row.Enabled ? 1.0 : 0.4,
            Cursor = Cursors.Cross,
            ToolTip = onlyWhite
                ? "White-only bulbs: shown as they'll really look. Click to add a colour stop here."
                : "Click to add a colour stop here. Drop a bulb here to move it into this row.",
            AllowDrop = true,
            Tag = row.Key,
        };
        bar.MouseLeftButtonDown += (_, e) =>
        {
            double x = e.GetPosition(LightsTimelineCanvas).X;
            int minute = SnapMinute((x - timelineX) / timelineW * 1440, 15);
            e.Handled = true;
            AddStopAt(row, minute);
        };
        WireBulbDropTarget(bar, row.Key);
        Canvas.SetLeft(bar, timelineX);
        Canvas.SetTop(bar, row.Y);
        LightsTimelineCanvas.Children.Add(bar);
        row.Bar = bar;

        for (int i = 0; i < row.Stops.Count; i++)
            DrawStopMarker(row, i, timelineX, timelineW);
    }

    /// <summary>The bar's fill: what the lamps will look like through the
    /// day, darkened with brightness so dim stretches read as dim. With
    /// <paramref name="whiteBulb"/> set, shows that white-only bulb instead
    /// (nearest white, faded where it can't follow).</summary>
    private LinearGradientBrush BuildTimeBrush(List<HueTimeStop> stops, HueLight? whiteBulb = null)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            ColorInterpolationMode = ColorInterpolationMode.ScRgbLinearInterpolation,
        };
        const int samples = 144; // every 10 minutes
        for (int i = 0; i <= samples; i++)
        {
            double minute = i * 1440.0 / samples;
            var s = TimeStopSampler.At(stops, _snapshot, Math.Min(minute, 1439.9));
            var c = whiteBulb == null
                ? DisplayColor(s.R, s.G, s.B, s.BrightnessPercent)
                : BulbDisplayColor(whiteBulb, s);
            brush.GradientStops.Add(new GradientStop(c, i / (double)samples));
        }
        return brush;
    }

    private static Color DisplayColor(byte r, byte g, byte b, double briPercent)
    {
        if (briPercent < 0.5) return Color.FromRgb(0x1E, 0x1E, 0x20);
        double k = 0.22 + 0.78 * Math.Clamp(briPercent / 100.0, 0, 1);
        return Color.FromRgb((byte)(r * k), (byte)(g * k), (byte)(b * k));
    }

    /// <summary>What one real bulb shows for a sample, for on-screen use.</summary>
    private Color BulbDisplayColor(HueLight bulb, TimeStopSampler.Sample s)
    {
        var cmd = BulbColorMapper.For(bulb.HasColor, bulb.CtMinMireds.HasValue || bulb.CtMaxMireds.HasValue,
                                      bulb.CtMinMireds, bulb.CtMaxMireds, s.R, s.G, s.B,
                                      s.BrightnessPercent, 1.0, _snapshot.HueWhiteBulbFollowPercent);
        if (cmd.Off) return Color.FromRgb(0x1E, 0x1E, 0x20);
        if (bulb.HasColor) return DisplayColor(s.R, s.G, s.B, s.BrightnessPercent);
        int kelvin = cmd.Mireds is > 0 ? (int)Math.Round(1_000_000.0 / cmd.Mireds.Value) : 2700;
        var (mr, mg, mb) = Underlit.Display.GammaRampApplier.KelvinToRgbMultipliers(Math.Clamp(kelvin, 1500, 6500));
        return DisplayColor((byte)Math.Round(mr * 255), (byte)Math.Round(mg * 255), (byte)Math.Round(mb * 255),
                            cmd.Bri254 / 254.0 * 100);
    }

    private void DrawStopMarker(TimeRow row, int index, double timelineX, double timelineW)
    {
        var stop = row.Stops[index];
        int minute = TimeStopSampler.ResolveMinute(stop, _snapshot);
        double cx = timelineX + minute / 1440.0 * timelineW;
        double cy = row.Y + (TlRowH - (row.ColourBulbs > 0 && row.LimitingWhiteBulb != null ? TlWhiteStripH : 0)) / 2;
        const double d = 16;

        var (r, g, b) = TimeStopSampler.HexToRgb(stop.Color);
        var marker = new Ellipse
        {
            Width = d, Height = d,
            Fill = stop.IsOff ? new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x20)) : new SolidColorBrush(Color.FromRgb(r, g, b)),
            // Light stops get a dark ring so they stay visible on a white bar.
            Stroke = !stop.IsOff && (0.2126 * r + 0.7152 * g + 0.0722 * b) > 190
                ? new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C))
                : Brushes.White,
            StrokeThickness = 2,
            Cursor = Cursors.SizeWE,
            ToolTip = StopTooltip(stop),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = 0.8 },
        };
        if (row.UsesDefaultStops) marker.StrokeDashArray = new DoubleCollection(new[] { 1.5, 1.0 });
        Canvas.SetLeft(marker, cx - d / 2);
        Canvas.SetTop(marker, cy - d / 2);
        LightsTimelineCanvas.Children.Add(marker);

        // Drag-to-move, click-to-edit. A press that moves less than 3 px is a click.
        Point start = default;
        bool moved = false;
        TextBlock? dragLabel = null;
        marker.MouseLeftButtonDown += (_, e) =>
        {
            start = e.GetPosition(LightsTimelineCanvas);
            moved = false;
            marker.CaptureMouse();
            e.Handled = true;
        };
        marker.MouseMove += (_, e) =>
        {
            if (!marker.IsMouseCaptured) return;
            var p = e.GetPosition(LightsTimelineCanvas);
            if (!moved && Math.Abs(p.X - start.X) < 3) return;
            if (!moved)
            {
                moved = true;
                EnsureOwnStops(row);
                stop = row.Stops[index];
                dragLabel = new TextBlock
                {
                    FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
                    Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x20, 0x20, 0x22)),
                    Padding = new Thickness(5, 1, 5, 1), IsHitTestVisible = false,
                };
                LightsTimelineCanvas.Children.Add(dragLabel);
            }
            int m = SnapMinute((p.X - timelineX) / timelineW * 1440, TlSnapMinutes);
            m = Math.Clamp(m, 0, 1439);
            TimeStopSampler.SetResolvedMinute(stop, m, _snapshot);
            double x = timelineX + m / 1440.0 * timelineW;
            Canvas.SetLeft(marker, x - d / 2);
            if (row.Bar?.Child is Grid g && g.Children.Count > 0 && g.Children[0] is Border main)
            {
                bool onlyWhite = row.ColourBulbs == 0 && row.LimitingWhiteBulb != null;
                main.Background = BuildTimeBrush(row.Stops, onlyWhite ? row.LimitingWhiteBulb : null);
                if (g.Children.Count > 1 && g.Children[1] is Border stripB)
                    stripB.Background = BuildTimeBrush(row.Stops, row.LimitingWhiteBulb);
            }
            if (dragLabel != null)
            {
                dragLabel.Text = TimeStopEditor.Fmt(m);
                Canvas.SetLeft(dragLabel, x - 18);
                Canvas.SetTop(dragLabel, Math.Max(0, row.Y - 18));
            }
        };
        marker.MouseLeftButtonUp += (_, e) =>
        {
            if (!marker.IsMouseCaptured) return;
            marker.ReleaseMouseCapture();
            e.Handled = true;
            if (moved)
            {
                PushSettings();
                RedrawLightsTimeline();
            }
            else
            {
                OpenStopEditor(row, index, isNew: false);
            }
        };
    }

    private string StopTooltip(HueTimeStop s)
    {
        string when = TimeStopEditor.Fmt(TimeStopSampler.ResolveMinute(s, _snapshot));
        string anchor = s.Anchor switch
        {
            HueTimeAnchor.WakeTime => " (moves with wake-up)",
            HueTimeAnchor.Bedtime  => " (moves with bedtime)",
            _ => "",
        };
        string what = s.IsOff ? "off" : $"{s.BrightnessPercent}%";
        return $"{when}{anchor}: {what}{(s.Hold ? ", holds until next stop" : "")}. Drag to move, click to edit.";
    }

    private static int SnapMinute(double minute, int snap)
        => (int)(Math.Round(minute / snap) * snap) % 1440;

    private ContextMenu BuildRowContextMenu(TimeRow row)
    {
        var menu = new ContextMenu();
        void Add(string header, Action act, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => act();
            menu.Items.Add(mi);
        }

        Add("Reset to the starter day (red > orange > warm white > white)", () =>
        {
            var fresh = AppSettings.DefaultTimeStops();
            if (row.Group != null) row.Group.TimeStops = fresh;
            else _snapshot.HueTimeStops = fresh;
            PushSettings();
            HueTakeBackRequested?.Invoke();
            RedrawLightsTimeline();
        });
        if (row.Group != null)
        {
            Add("Use the Default row's stops (copy)", () =>
            {
                row.Group.TimeStops = _snapshot.HueTimeStops.Select(s => s.Copy()).ToList();
                PushSettings();
                RedrawLightsTimeline();
            });
            menu.Items.Add(new Separator());
            Add("Rename group...", () =>
            {
                var box = new InputBox { Owner = this, Title = "Rename group", Prompt = "Group name", Value = row.Group.Name };
                if (box.ShowDialog() != true || string.IsNullOrWhiteSpace(box.Value)) return;
                row.Group.Name = box.Value.Trim();
                AfterBulbsChanged();
            });
            Add(row.Group.Enabled ? "Turn group off (its bulbs follow Default)" : "Turn group on", () =>
            {
                row.Group.Enabled = !row.Group.Enabled;
                AfterBulbsChanged();
            });
            Add("Delete group (its bulbs go to Default)", () =>
            {
                _snapshot.ColorGroups.Remove(row.Group);
                AfterBulbsChanged();
            });
        }
        return menu;
    }

    // -------------------------------------------------------------------
    // Editing
    // -------------------------------------------------------------------

    private void AddStopAt(TimeRow row, int minute)
    {
        EnsureOwnStops(row);
        // Start from what the lamps are doing at that moment, so adding a
        // stop never changes anything until the user edits it.
        var s = TimeStopSampler.At(row.Stops, _snapshot, minute);
        var stop = new HueTimeStop
        {
            Anchor = HueTimeAnchor.Clock,
            Minutes = minute,
            Color = TimeStopSampler.RgbToHex(s.R, s.G, s.B),
            BrightnessPercent = (int)Math.Round(s.BrightnessPercent),
        };
        row.Stops.Add(stop);
        RedrawLightsTimeline();
        bool kept = OpenStopEditor(row, row.Stops.Count - 1, isNew: true);
        if (!kept)
        {
            row.Stops.Remove(stop);
            RedrawLightsTimeline();
        }
    }

    /// <summary>Returns false when a new stop was cancelled.</summary>
    private bool OpenStopEditor(TimeRow row, int index, bool isNew)
    {
        if (index < 0 || index >= row.Stops.Count) return false;
        var lightIds = row.LightIds.ToList();
        string rowName = row.Label
            + (lightIds.Count > 0 ? $" · {BulbMixLabel(row)}" : "")
            + (row.UsesDefaultStops ? " (editing gives this group its own stops)" : "");
        var editor = new TimeStopEditor(row.Stops[index], isNew, canDelete: row.Stops.Count > 1, rowName, _snapshot)
        {
            Owner = this,
        };

        var preview = new StopPreview(this, lightIds);
        editor.PreviewChanged += (hex, bri) => preview.Show(hex, bri);
        editor.PreviewStopped += () => _ = preview.EndAsync();

        bool? ok;
        try { ok = editor.ShowDialog(); }
        finally { _ = preview.EndAsync(); }

        if (ok != true) return false;

        EnsureOwnStops(row);
        if (editor.Deleted)
        {
            if (row.Stops.Count > 1) row.Stops.RemoveAt(index);
        }
        else if (editor.Result != null)
        {
            row.Stops[index] = editor.Result;
        }
        PushSettings();
        HueTakeBackRequested?.Invoke();
        RedrawLightsTimeline();
        return true;
    }

    /// <summary>Shows a stop's colour on the row's real bulbs while the
    /// editor is open, exactly as the schedule would (white-only bulbs dim
    /// out where they can't follow). Latest-wins: while the user drags
    /// through the colour picker only the newest colour is sent, paced to
    /// what the bridge can take. Pauses the HueController for the duration
    /// and puts back the on/off state each bulb had before.</summary>
    private sealed class StopPreview
    {
        private readonly SettingsWindow _w;
        private readonly List<string> _lightIds;
        private HashSet<string>? _wasOff;
        private bool _active;
        private bool _running;
        private (string Hex, int Bri)? _pending;

        public StopPreview(SettingsWindow w, List<string> lightIds) { _w = w; _lightIds = lightIds; }

        public async void Show(string hex, int briPercent)
        {
            var client = _w._hueClient;
            if (client == null || _lightIds.Count == 0) return;
            _pending = (hex, briPercent);
            if (_running) return; // the loop below picks up the newest value
            _running = true;
            try
            {
                if (!_active)
                {
                    _active = true;
                    _w.HuePreviewActiveChanged?.Invoke(true);
                    var states = await Task.WhenAll(_lightIds.Select(id => client.GetLightAsync(id)));
                    _wasOff = new HashSet<string>(
                        states.Where(s => s != null && !s.On).Select(s => s!.Id), StringComparer.OrdinalIgnoreCase);
                }
                // The bridge handles ~10 light commands a second; pace so a
                // fast drag never builds a backlog.
                int gapMs = Math.Max(120, 90 * _lightIds.Count);
                while (_active && _pending is { } job)
                {
                    _pending = null;
                    await SendAsync(client, job.Hex, job.Bri);
                    await Task.Delay(gapMs);
                }
            }
            catch (Exception ex) { Logger.Warn("Time stop preview failed", ex); }
            finally { _running = false; }
        }

        private Task SendAsync(HueBridgeClient client, string hex, int briPercent)
        {
            double master = Math.Clamp(_w._snapshot.HueBrightness, 1, 254) / 254.0;
            var (r, g, b) = TimeStopSampler.HexToRgb(hex);
            return Task.WhenAll(_lightIds.Select(id =>
            {
                HueLight? info = null;
                _w._hueLightsByIdSnapshot?.TryGetValue(id, out info);
                bool hasColor = info?.HasColor ?? true;
                bool hasCt = info != null && (info.CtMinMireds.HasValue || info.CtMaxMireds.HasValue);
                var cmd = BulbColorMapper.For(hasColor, hasCt, info?.CtMinMireds, info?.CtMaxMireds,
                                              r, g, b, briPercent, master, _w._snapshot.HueWhiteBulbFollowPercent);
                if (cmd.Off)
                    return client.SetLightStateAsync(id, on: false, mireds: null, brightness254: null, transitionDeciseconds: 1);
                if (cmd.X.HasValue && cmd.Y.HasValue)
                    return client.SetLightColorAsync(id, cmd.X.Value, cmd.Y.Value, cmd.Bri254, transitionDeciseconds: 1);
                return client.SetLightStateAsync(id, on: true, mireds: cmd.Mireds, brightness254: cmd.Bri254, transitionDeciseconds: 1);
            }));
        }

        public async Task EndAsync()
        {
            if (!_active) return;
            _active = false;
            _pending = null;
            try
            {
                var client = _w._hueClient;
                if (client != null && _wasOff is { Count: > 0 })
                    await Task.WhenAll(_wasOff.Select(id =>
                        client.SetLightStateAsync(id, on: false, mireds: null, brightness254: null, transitionDeciseconds: 2)));
            }
            catch (Exception ex) { Logger.Warn("Time stop preview restore failed", ex); }
            finally
            {
                _w.HuePreviewActiveChanged?.Invoke(false);
            }
        }
    }
}
