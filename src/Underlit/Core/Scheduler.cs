using System;
using System.Windows.Threading;
using Underlit.Settings;

namespace Underlit.Core;

/// <summary>
/// Computes a target warmth (Kelvin) from the clock and the user's bedtime/wakeup window.
///
/// Brightness used to be part of this too but was removed — a scheduled brightness change
/// jumping the screen up/down at an arbitrary time feels disorienting. Warmth shifting
/// is subtler and doesn't mess with the user's sense of "how bright is this screen".
///
/// The curve:
///   • Between wakeupEnd and bedtimeStart — neutral day warmth (6500 K).
///   • Between bedtimeStart and bedtimeEnd — linear ramp toward the night warmth floor.
///   • Between bedtimeEnd and wakeupStart — at the night warmth floor.
///   • Between wakeupStart and wakeupEnd — linear ramp back toward neutral.
/// </summary>
public sealed class Scheduler : IDisposable
{
    public event Action<int>? BaselineWarmthTick;

    private AppSettings _settings;
    private readonly DispatcherTimer _timer;

    public Scheduler(AppSettings settings, Dispatcher dispatcher)
    {
        _settings = settings;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _timer.Tick += OnTick;
    }

    public void UpdateSettings(AppSettings s) => _settings = s;

    public void Start()
    {
        _timer.Start();
        OnTick(null, EventArgs.Empty);
    }

    public void Stop() => _timer.Stop();

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_settings.ScheduleEnabled) return;
        int w = ComputeWarmth(DateTime.Now, _settings);
        BaselineWarmthTick?.Invoke(w);
    }

    public static int ComputeWarmth(DateTime now, AppSettings s)
    {
        double t = now.Hour + now.Minute / 60.0 + now.Second / 3600.0;

        double bs = s.BedtimeStart.AsHourFractional;
        double be = s.BedtimeEnd.AsHourFractional;
        double ws = s.WakeupStart.AsHourFractional;
        double we = s.WakeupEnd.AsHourFractional;

        int nightW = Math.Clamp(s.NightWarmthKelvin, 1500, 6500);
        const int dayW = 6500;

        bool inDay      = IsBetween(t, we, bs);
        bool inRampDown = IsBetween(t, bs, be);
        bool inNight    = IsBetween(t, be, ws);
        bool inRampUp   = IsBetween(t, ws, we);

        if (inDay)   return dayW;
        if (inNight) return nightW;
        if (inRampDown)
        {
            double f = (t - bs) / Span(bs, be);
            return LerpInt(dayW, nightW, f);
        }
        if (inRampUp)
        {
            double f = (t - ws) / Span(ws, we);
            return LerpInt(nightW, dayW, f);
        }
        return dayW;
    }

    private static bool IsBetween(double t, double a, double b)
    {
        if (Math.Abs(a - b) < 1e-6) return false;
        if (a < b) return t >= a && t < b;
        return t >= a || t < b;
    }

    private static double Span(double a, double b) => a < b ? b - a : (24 - a) + b;

    private static int LerpInt(int a, int b, double f)
    {
        f = Math.Clamp(f, 0, 1);
        return (int)(a + (b - a) * f);
    }

    public void Dispose() => _timer.Stop();
}
