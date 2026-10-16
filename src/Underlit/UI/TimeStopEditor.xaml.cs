using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Underlit.Hue;
using Underlit.Settings;

namespace Underlit.UI;

/// <summary>
/// v0.7.0: modal editor for one time-of-day colour stop. Works on a copy;
/// on Save, <see cref="Result"/> holds the edited stop. On Delete,
/// <see cref="Deleted"/> is true. While open, <see cref="PreviewChanged"/>
/// fires (debounced) so the caller can show the colour on the real bulbs.
/// </summary>
public partial class TimeStopEditor : Window
{
    public HueTimeStop? Result { get; private set; }
    public bool Deleted { get; private set; }

    /// <summary>(hex colour, brightness 0..100). Brightness 0 = off.</summary>
    public event Action<string, int>? PreviewChanged;
    /// <summary>Preview checkbox was switched off: caller should put the
    /// lights back on the schedule.</summary>
    public event Action? PreviewStopped;

    private readonly HueTimeStop _editing;
    private readonly AppSettings _sched;
    private readonly DispatcherTimer _previewDebounce;
    private bool _loading = true;

    private static readonly (string Name, string Hex)[] Presets =
    {
        ("Daylight",   "#FFFFFFFF"),
        ("Warm white", "#FFFFE2B8"),
        ("Candle",     "#FFFFC478"),
        ("Amber",      "#FFFFA040"),
        ("Orange",     "#FFFF8000"),
        ("Sunset",     "#FFFF4A10"),
        ("Red",        "#FFFF0000"),
        ("Deep red",   "#FFB00010"),
        ("Pink",       "#FFFF60A0"),
        ("Purple",     "#FF8040FF"),
        ("Blue",       "#FF3060FF"),
        ("Teal",       "#FF00C0B0"),
    };

    /// <param name="stop">Stop to edit (not modified; a copy is edited).</param>
    /// <param name="isNew">Changes the title and hides nothing else.</param>
    /// <param name="canDelete">False for the last remaining stop on a row.</param>
    /// <param name="rowName">Shown under the title.</param>
    /// <param name="sched">Settings used to resolve wake-up / bedtime anchors.</param>
    public TimeStopEditor(HueTimeStop stop, bool isNew, bool canDelete, string rowName, AppSettings sched)
    {
        InitializeComponent();
        _editing = stop.Copy();
        _sched = sched;

        LblTitle.Text = isNew ? "New colour stop" : "Colour stop";
        LblRow.Text = rowName;
        BtnDelete.Visibility = canDelete && !isNew ? Visibility.Visible : Visibility.Collapsed;

        CbiWake.Content = $"Moves with wake-up ({Fmt(Min(sched.WakeupEnd))})";
        CbiBed.Content  = $"Moves with bedtime ({Fmt(Min(sched.BedtimeEnd))})";
        CboAnchor.SelectedIndex = (int)_editing.Anchor;
        TxtTime.Text = Fmt(TimeStopSampler.ResolveMinute(_editing, sched));
        SldBri.Value = Math.Clamp(_editing.BrightnessPercent, 0, 100);
        ChkHold.IsChecked = _editing.Hold;

        foreach (var (name, hex) in Presets)
        {
            var b = new Button
            {
                Style = (Style)FindResource("SwatchButton"),
                Background = new SolidColorBrush(ToColor(hex)),
                ToolTip = name,
                Tag = hex,
            };
            b.Click += (_, _) => SetColor((string)b.Tag);
            PresetPanel.Children.Add(b);
        }

        // Short: the preview itself paces what goes to the bridge.
        _previewDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _previewDebounce.Tick += (_, _) =>
        {
            _previewDebounce.Stop();
            if (ChkPreview.IsChecked == true)
                PreviewChanged?.Invoke(_editing.Color, (int)SldBri.Value);
        };

        WireLivePicker();
        UpdateColorUi();
        UpdateBriLabel();
        UpdateAnchorHint();

        SldBri.ValueChanged += (_, _) => { UpdateBriLabel(); QueuePreview(); };
        TxtTime.TextChanged += (_, _) => UpdateAnchorHint();
        CboAnchor.SelectionChanged += (_, _) => UpdateAnchorHint();
        BtnColor.Click     += (_, _) => PickCustomColor();
        BtnColorPick.Click += (_, _) => PickCustomColor();
        ChkPreview.Checked   += (_, _) => QueuePreview();
        ChkPreview.Unchecked += (_, _) => PreviewStopped?.Invoke();
        BtnCancel.Click += (_, _) => { DialogResult = false; Close(); };
        BtnSave.Click   += (_, _) => Save();
        BtnDelete.Click += (_, _) => { Deleted = true; DialogResult = true; Close(); };

        Loaded += (_, _) => { _loading = false; QueuePreview(); TxtTime.Focus(); TxtTime.SelectAll(); };
        Closed += (_, _) => _previewDebounce.Stop();
    }

    private void QueuePreview()
    {
        if (_loading) return;
        _previewDebounce.Stop();
        _previewDebounce.Start();
    }

    private void SetColor(string hex) => SetColor(hex, fromPicker: false);

    private void SetColor(string hex, bool fromPicker)
    {
        _editing.Color = hex;
        if (!fromPicker) SyncHsvFromColor();
        // Picking a colour on an "off" stop almost always means "turn it on".
        if (SldBri.Value <= 0) SldBri.Value = 50;
        UpdateColorUi();
        QueuePreview();
    }

    private void PickCustomColor()
    {
        var c = ToColor(_editing.Color);
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B),
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        SetColor(TimeStopSampler.RgbToHex(dlg.Color.R, dlg.Color.G, dlg.Color.B));
    }

    private void UpdateColorUi()
    {
        BtnColor.Background = new SolidColorBrush(ToColor(_editing.Color));
        var (r, g, b) = TimeStopSampler.HexToRgb(_editing.Color);
        LblHex.Text = $"#{r:X2}{g:X2}{b:X2}";
        UpdatePickerMarkers();
    }

    // ---- v0.7.1: live HSV picker -------------------------------------

    private double _h, _s = 1, _v = 1; // 0..360, 0..1, 0..1

    private void WireLivePicker()
    {
        // Whites strip: real black-body colours, warm to cool.
        var wb = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i <= 10; i++)
        {
            int k = 2000 + 450 * i;
            wb.GradientStops.Add(new GradientStop(WhiteColor(k), i / 10.0));
        }
        WhiteStripFill.Background = wb;

        WirePickerSurface(SvArea, p =>
        {
            _s = Math.Clamp(p.X / Math.Max(1, SvArea.ActualWidth), 0, 1);
            _v = 1 - Math.Clamp(p.Y / Math.Max(1, SvArea.ActualHeight), 0, 1);
            ApplyHsv();
        });
        WirePickerSurface(HueStrip, p =>
        {
            _h = Math.Clamp(p.X / Math.Max(1, HueStrip.ActualWidth), 0, 1) * 360;
            if (_s < 0.05) _s = 1; // picking a hue on a white means "I want colour"
            if (_v < 0.05) _v = 1;
            ApplyHsv();
        });
        WirePickerSurface(WhiteStrip, p =>
        {
            double f = Math.Clamp(p.X / Math.Max(1, WhiteStrip.ActualWidth), 0, 1);
            var c = WhiteColor((int)Math.Round(2000 + 4500 * f));
            SetColor(TimeStopSampler.RgbToHex(c.R, c.G, c.B));
        });
        SvArea.SizeChanged += (_, _) => UpdatePickerMarkers();
        HueStrip.SizeChanged += (_, _) => UpdatePickerMarkers();
        SyncHsvFromColor();
    }

    private static void WirePickerSurface(FrameworkElement el, Action<Point> onPick)
    {
        el.MouseLeftButtonDown += (_, e) => { el.CaptureMouse(); onPick(e.GetPosition(el)); e.Handled = true; };
        el.MouseMove += (_, e) => { if (el.IsMouseCaptured) onPick(e.GetPosition(el)); };
        el.MouseLeftButtonUp += (_, _) => el.ReleaseMouseCapture();
    }

    private void ApplyHsv()
    {
        var (r, g, b) = HsvToRgb(_h, _s, _v);
        SetColor(TimeStopSampler.RgbToHex(r, g, b), fromPicker: true);
    }

    private void SyncHsvFromColor()
    {
        var (r, g, b) = TimeStopSampler.HexToRgb(_editing.Color);
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        double max = Math.Max(rf, Math.Max(gf, bf)), min = Math.Min(rf, Math.Min(gf, bf));
        double d = max - min;
        double h = 0;
        if (d > 1e-6)
        {
            if (max == rf) h = 60 * (((gf - bf) / d) % 6);
            else if (max == gf) h = 60 * ((bf - rf) / d + 2);
            else h = 60 * ((rf - gf) / d + 4);
        }
        if (h < 0) h += 360;
        if (d > 1e-6) _h = h; // keep the previous hue for greys/whites
        _s = max <= 0 ? 0 : d / max;
        _v = max;
    }

    private void UpdatePickerMarkers()
    {
        if (SvMarker == null || SvArea.ActualWidth <= 0) return;
        var (hr, hg, hb) = HsvToRgb(_h, 1, 1);
        SvHueFill.Background = new SolidColorBrush(Color.FromRgb(hr, hg, hb));
        Canvas.SetLeft(SvMarker, _s * SvArea.ActualWidth - SvMarker.Width / 2);
        Canvas.SetTop(SvMarker, (1 - _v) * SvArea.ActualHeight - SvMarker.Height / 2);
        Canvas.SetLeft(HueMarker, _h / 360 * HueStrip.ActualWidth - HueMarker.Width / 2);
    }

    private static (byte, byte, byte) HsvToRgb(double h, double s, double v)
    {
        double c = v * s;
        double hp = (h % 360) / 60.0;
        double x = c * (1 - Math.Abs(hp % 2 - 1));
        (double r, double g, double b) = hp switch
        {
            < 1 => (c, x, 0.0),
            < 2 => (x, c, 0.0),
            < 3 => (0.0, c, x),
            < 4 => (0.0, x, c),
            < 5 => (x, 0.0, c),
            _   => (c, 0.0, x),
        };
        double m = v - c;
        return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    private static Color WhiteColor(int kelvin)
    {
        var (r, g, b) = Underlit.Display.GammaRampApplier.KelvinToRgbMultipliers(Math.Clamp(kelvin, 1500, 6500));
        return Color.FromRgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    private void UpdateBriLabel()
        => LblBri.Text = SldBri.Value <= 0 ? "Off" : $"{(int)SldBri.Value} %";

    private void UpdateAnchorHint()
    {
        if (!TryParseTime(TxtTime.Text, out int minute))
        {
            LblAnchorHint.Text = "Type a time like 21:15";
            return;
        }
        var anchor = (HueTimeAnchor)Math.Max(0, CboAnchor.SelectedIndex);
        int baseMin = anchor switch
        {
            HueTimeAnchor.WakeTime => Min(_sched.WakeupEnd),
            HueTimeAnchor.Bedtime  => Min(_sched.BedtimeEnd),
            _                      => -1,
        };
        if (baseMin < 0)
        {
            LblAnchorHint.Text = "Stays at this time when you change your schedule.";
            return;
        }
        int off = ((minute - baseMin) % 1440 + 1440) % 1440;
        if (off > 720) off -= 1440;
        string what = anchor == HueTimeAnchor.WakeTime ? "wake-up" : "bedtime";
        string rel = off == 0 ? $"at {what}"
                   : off < 0 ? $"{FmtDuration(-off)} before {what}"
                             : $"{FmtDuration(off)} after {what}";
        LblAnchorHint.Text = $"{char.ToUpper(rel[0])}{rel[1..]}. Shifts when you change your {what} time.";
    }

    private void Save()
    {
        if (!TryParseTime(TxtTime.Text, out int minute))
        {
            TxtTime.Focus();
            TxtTime.SelectAll();
            LblAnchorHint.Text = "Type a time like 21:15";
            return;
        }
        _editing.Anchor = (HueTimeAnchor)Math.Max(0, CboAnchor.SelectedIndex);
        TimeStopSampler.SetResolvedMinute(_editing, minute, _sched);
        _editing.BrightnessPercent = (int)SldBri.Value;
        _editing.Hold = ChkHold.IsChecked == true;
        Result = _editing;
        DialogResult = true;
        Close();
    }

    // ---- helpers ----

    internal static bool TryParseTime(string? text, out int minuteOfDay)
    {
        minuteOfDay = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().Replace('.', ':');
        if (!t.Contains(':') && t.Length is 3 or 4 && int.TryParse(t, out int compact))
            t = $"{compact / 100}:{compact % 100:D2}";
        if (DateTime.TryParseExact(t, new[] { "H:mm", "HH:mm", "H:m", "H" }, CultureInfo.InvariantCulture,
                                   DateTimeStyles.None, out var dt)
            || DateTime.TryParse(t, CultureInfo.CurrentCulture, DateTimeStyles.None, out dt))
        {
            minuteOfDay = dt.Hour * 60 + dt.Minute;
            return true;
        }
        return false;
    }

    private static int Min(TimeOfDay t) => t.Hour * 60 + t.Minute;
    internal static string Fmt(int minuteOfDay) => $"{minuteOfDay / 60:D2}:{minuteOfDay % 60:D2}";
    private static string FmtDuration(int minutes)
        => minutes < 60 ? $"{minutes} min"
         : minutes % 60 == 0 ? $"{minutes / 60} h"
         : $"{minutes / 60} h {minutes % 60} min";

    private static Color ToColor(string hex)
    {
        var (r, g, b) = TimeStopSampler.HexToRgb(hex);
        return Color.FromRgb(r, g, b);
    }
}
