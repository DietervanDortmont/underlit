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

    /// <summary>
    /// Start the scheduler timer. v0.6.40: idempotent — calling Start on an
    /// already-running scheduler is a no-op now, instead of firing OnTick
    /// again. The previous behaviour caused per-keystroke flicker during
    /// schedule-graph drags: every programmatic <c>SldNightWarmth.Value</c>
    /// write triggered <c>PushSettings → ApplySettings → Scheduler.Start →
    /// OnTick → SetWarmth(scheduleBaseline)</c>, so the screen oscillated
    /// between the drag's preview kelvin and the schedule baseline (often
    /// 6500 K mid-day, "white") on every mouse-move.
    /// </summary>
    public void Start()
    {
        if (_timer.IsEnabled) return;
        _timer.Start();
        OnTick(null, EventArgs.Empty);
    }

    /// <summary>Force a single immediate baseline computation, e.g. when
    /// the user has just enabled the schedule or changed their schedule
    /// curve and wants the screen to reflect that without waiting up to
    /// 30 s for the next regular tick.</summary>
    public void Pulse() => OnTick(null, EventArgs.Empty);

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

        // Each of the four schedule anchors carries its own kelvin (v0.6.25+),
        // so the user can drag any point on the graph vertically to set
        // warmth at that boundary. We sort by time-of-day, find the segment
        // containing `t`, and interpolate with smoothstep — that gives a C1-
        // continuous curve (zero slope at each anchor) so transitions feel
        // gradual rather than piecewise-linear.
        Span<(double time, int kelvin)> pts = stackalloc (double, int)[]
        {
            (s.WakeupStart.AsHourFractional, Math.Clamp(s.WakeupStartKelvin,  1500, 6500)),
            (s.WakeupEnd.AsHourFractional,   Math.Clamp(s.WakeupEndKelvin,    1500, 6500)),
            (s.BedtimeStart.AsHourFractional, Math.Clamp(s.BedtimeStartKelvin, 1500, 6500)),
            (s.BedtimeEnd.AsHourFractional,   Math.Clamp(s.BedtimeEndKelvin,   1500, 6500)),
        };
        // Sort in-place by time. Four-element bubble sort — readable, allocation-free.
        for (int i = 0; i < pts.Length - 1; i++)
        for (int j = 0; j < pts.Length - 1 - i; j++)
            if (pts[j].time > pts[j + 1].time)
                (pts[j], pts[j + 1]) = (pts[j + 1], pts[j]);

        // Find the segment that contains t. We iterate consecutive pairs (with
        // wrap from last → first); IsBetween already handles midnight wrap.
        for (int i = 0; i < pts.Length; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Length];
            if (!IsBetween(t, a.time, b.time)) continue;

            double span = Span(a.time, b.time);
            if (span < 1e-6) return a.kelvin;
            double dt = t >= a.time ? t - a.time : (t + 24 - a.time);
            double f  = Math.Clamp(dt / span, 0, 1);
            f = f * f * (3 - 2 * f);  // smoothstep — zero-slope at both endpoints
            return LerpInt(a.kelvin, b.kelvin, f);
        }

        return pts[0].kelvin;
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
