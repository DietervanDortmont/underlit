using System;
using System.Collections.Generic;
using System.Linq;
using Underlit.Settings;

namespace Underlit.Hue;

/// <summary>
/// v0.7.0: pure maths for the time-of-day lights timeline. Shared by the
/// <see cref="HueController"/> (what to push right now) and the Settings
/// timeline (what to draw), so the preview and the lamps can't disagree.
///
/// <para>Stops are resolved to a minute of the day (anchored stops follow
/// the active schedule's wake-up / bedtime), sorted, and the lamps fade
/// linearly from one stop to the next, wrapping past midnight. Colours are
/// mixed in linear light (like real light mixing), brightness linearly.
/// An "off" stop fades brightness to zero while keeping the neighbouring
/// stop's colour, so a lamp dims out instead of drifting to black.</para>
/// </summary>
public static class TimeStopSampler
{
    public const int MinutesPerDay = 24 * 60;

    public readonly record struct Sample(
        byte R, byte G, byte B,
        double BrightnessPercent, // 0..100, already faded between the bracketing stops
        int SegmentIndex,         // index (sorted order) of the stop we're past
        int NextStopMinute)       // minute of the day of the next stop
    {
        public bool IsOff => BrightnessPercent < 0.5;
    }

    /// <summary>Minute of the day (0..1439) this stop lands on.</summary>
    public static int ResolveMinute(HueTimeStop stop, AppSettings s)
    {
        int baseMin = stop.Anchor switch
        {
            HueTimeAnchor.WakeTime => s.WakeupEnd.Hour * 60 + s.WakeupEnd.Minute,
            HueTimeAnchor.Bedtime  => s.BedtimeEnd.Hour * 60 + s.BedtimeEnd.Minute,
            _                      => 0,
        };
        return Mod(baseMin + stop.Minutes, MinutesPerDay);
    }

    /// <summary>Inverse of <see cref="ResolveMinute"/>: set a stop so it
    /// lands on <paramref name="minuteOfDay"/>, keeping its anchor. Picks
    /// the offset closest to the anchor (so -30 rather than +1410).</summary>
    public static void SetResolvedMinute(HueTimeStop stop, int minuteOfDay, AppSettings s)
    {
        minuteOfDay = Mod(minuteOfDay, MinutesPerDay);
        int baseMin = stop.Anchor switch
        {
            HueTimeAnchor.WakeTime => s.WakeupEnd.Hour * 60 + s.WakeupEnd.Minute,
            HueTimeAnchor.Bedtime  => s.BedtimeEnd.Hour * 60 + s.BedtimeEnd.Minute,
            _                      => 0,
        };
        if (stop.Anchor == HueTimeAnchor.Clock) { stop.Minutes = minuteOfDay; return; }
        int off = Mod(minuteOfDay - baseMin, MinutesPerDay);
        if (off > MinutesPerDay / 2) off -= MinutesPerDay;
        stop.Minutes = off;
    }

    public static Sample At(IReadOnlyList<HueTimeStop>? stops, AppSettings s, DateTime when)
        => At(stops, s, when.TimeOfDay.TotalMinutes);

    public static Sample At(IReadOnlyList<HueTimeStop>? stops, AppSettings s, double minuteOfDay)
    {
        if (stops == null || stops.Count == 0)
            return new Sample(255, 255, 255, 100, 0, 0);

        var sorted = stops
            .Where(x => x != null)
            .Select(x => (Min: ResolveMinute(x, s), Stop: x))
            .OrderBy(x => x.Min)
            .ToList();
        int n = sorted.Count;
        double t = ((minuteOfDay % MinutesPerDay) + MinutesPerDay) % MinutesPerDay;

        if (n == 1)
        {
            var only = sorted[0].Stop;
            var (r1, g1, b1) = HexToRgb(only.Color);
            return new Sample(r1, g1, b1, only.IsOff ? 0 : Math.Clamp(only.BrightnessPercent, 0, 100), 0, sorted[0].Min);
        }

        // Last stop at or before t; if none, wrap to the last stop of the day.
        int ai = n - 1;
        for (int i = 0; i < n; i++)
            if (sorted[i].Min <= t) ai = i;
        int bi = (ai + 1) % n;
        var a = sorted[ai];
        var b = sorted[bi];

        double span = Mod(b.Min - a.Min, MinutesPerDay);
        if (span == 0) span = MinutesPerDay;
        double dt = ((t - a.Min) % MinutesPerDay + MinutesPerDay) % MinutesPerDay;
        double f = a.Stop.Hold ? 0 : Math.Clamp(dt / span, 0, 1);

        double briA = a.Stop.IsOff ? 0 : Math.Clamp(a.Stop.BrightnessPercent, 0, 100);
        double briB = b.Stop.IsOff ? 0 : Math.Clamp(b.Stop.BrightnessPercent, 0, 100);

        // Off stops borrow their neighbour's colour so the lamp dims out
        // in colour rather than greying towards black.
        string colA = a.Stop.IsOff ? b.Stop.Color : a.Stop.Color;
        string colB = b.Stop.IsOff ? a.Stop.Color : b.Stop.Color;
        var (r, g, bl) = MixLinear(HexToRgb(colA), HexToRgb(colB), f);

        return new Sample(r, g, bl, briA + (briB - briA) * f, ai, b.Min);
    }

    // ------------------------------------------------------------------
    // Colour helpers
    // ------------------------------------------------------------------

    public static (byte r, byte g, byte b) HexToRgb(string? hex)
    {
        if (string.IsNullOrEmpty(hex) || hex[0] != '#') return (255, 255, 255);
        try
        {
            int off = hex.Length == 9 ? 3 : 1;
            return (Convert.ToByte(hex.Substring(off, 2), 16),
                    Convert.ToByte(hex.Substring(off + 2, 2), 16),
                    Convert.ToByte(hex.Substring(off + 4, 2), 16));
        }
        catch { return (255, 255, 255); }
    }

    public static string RgbToHex(byte r, byte g, byte b) => $"#FF{r:X2}{g:X2}{b:X2}";

    private static (byte, byte, byte) MixLinear((byte r, byte g, byte b) a, (byte r, byte g, byte b) b, double f)
    {
        static byte Mix(byte x, byte y, double f)
        {
            double lx = Decode(x / 255.0), ly = Decode(y / 255.0);
            return (byte)Math.Round(Math.Clamp(Encode(lx + (ly - lx) * f), 0, 1) * 255);
        }
        return (Mix(a.r, b.r, f), Mix(a.g, b.g, f), Mix(a.b, b.b, f));
    }

    /// <summary>sRGB to CIE 1931 xy (D65 primaries).</summary>
    public static (double x, double y) RgbToXy(byte r, byte g, byte b)
    {
        double rL = Decode(r / 255.0), gL = Decode(g / 255.0), bL = Decode(b / 255.0);
        double X = 0.4124 * rL + 0.3576 * gL + 0.1805 * bL;
        double Y = 0.2126 * rL + 0.7152 * gL + 0.0722 * bL;
        double Z = 0.0193 * rL + 0.1192 * gL + 0.9505 * bL;
        double sum = X + Y + Z;
        if (sum < 1e-9) return (0.3128, 0.3290);
        return (X / sum, Y / sum);
    }

    /// <summary>Closest colour temperature for a colour, for bulbs that
    /// only do tunable white (McCamy's approximation). Saturated reds and
    /// oranges land at the warm end, which the caller clamps to the bulb.</summary>
    public static int RgbToKelvin(byte r, byte g, byte b)
    {
        var (x, y) = RgbToXy(r, g, b);
        double n = (x - 0.3320) / (0.1858 - y);
        double cct = 449 * n * n * n + 3525 * n * n + 6823.3 * n + 5520.33;
        if (double.IsNaN(cct) || cct < 1000) cct = 1500;
        return (int)Math.Clamp(cct, 1500, 6500);
    }

    private static double Decode(double c) =>
        c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    private static double Encode(double c) =>
        c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;

    private static int Mod(int a, int m) => ((a % m) + m) % m;
    private static double Mod(double a, double m) => ((a % m) + m) % m;
}
