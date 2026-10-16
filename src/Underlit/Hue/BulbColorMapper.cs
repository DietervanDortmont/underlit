using System;

namespace Underlit.Hue;

/// <summary>
/// v0.7.1: turns "this colour at this brightness" into what one specific
/// bulb should actually do. Shared by the HueController, the colour-stop
/// preview and the timeline drawing, so all three agree.
///
/// <para>Full-colour bulbs simply get the colour. White-only bulbs (tunable
/// white like IKEA Tradfri, or the Hue "white ambiance" range) get the
/// closest white they can make, and the further the requested colour is
/// from anything they can make, the dimmer they go, until they switch off.
/// Distance is measured in CIE xy between the requested colour and the
/// nearest point on the black-body (white) curve inside the bulb's own
/// colour-temperature range. So a warm white past the bulb's limit dims a
/// little, amber dims more, orange fades out, and red is off.</para>
/// </summary>
public static class BulbColorMapper
{
    public readonly record struct Command(
        bool Off,
        double? X, double? Y,   // set for colour bulbs
        int? Mireds,            // set for tunable-white bulbs
        int Bri254,
        double Fade)            // 1 = full, 0 = faded out (white-only bulbs)
    {
        public static Command OffCmd => new(true, null, null, null, 0, 0);
    }

    /// <summary>xy distance at which a white-only bulb is fully off, from
    /// the user's "how far white bulbs follow" setting (0..100). 0 = they
    /// bow out as soon as the colour leaves what they can make; 100 = they
    /// hang on until deep orange / red.</summary>
    public static double FadeEndDistance(int followPercent)
        => 0.02 + 0.12 * Math.Clamp(followPercent, 0, 100) / 100.0;

    /// <summary>Distance (xy) at which a white-only bulb starts dimming.</summary>
    public static double FadeStartDistance(int followPercent)
        => Math.Min(0.012, FadeEndDistance(followPercent) * 0.4);

    /// <param name="hasColor">Bulb can do any colour.</param>
    /// <param name="ctMinMireds">Coolest the bulb goes (null = unknown).</param>
    /// <param name="ctMaxMireds">Warmest the bulb goes (null = unknown).</param>
    /// <param name="hasCt">Bulb has a colour-temperature control at all.</param>
    /// <param name="briPercent">0..100 from the stops.</param>
    /// <param name="master">0..1 master brightness.</param>
    /// <param name="followPercent">How far white-only bulbs follow into colour.</param>
    public static Command For(bool hasColor, bool hasCt, int? ctMinMireds, int? ctMaxMireds,
                              byte r, byte g, byte b, double briPercent, double master, int followPercent)
    {
        if (briPercent < 0.5 || r + g + b < 24) return Command.OffCmd;
        int bri = Math.Clamp((int)Math.Round(briPercent / 100.0 * 254 * master), 1, 254);

        var (x, y) = TimeStopSampler.RgbToXy(r, g, b);
        if (hasColor)
            return new Command(false, x, y, null, bri, 1.0);

        // White-only. A bulb without CT control is a fixed warm white.
        int warmK = ctMaxMireds is > 0 ? (int)Math.Round(1_000_000.0 / ctMaxMireds.Value) : (hasCt ? 2000 : 2700);
        int coolK = ctMinMireds is > 0 ? (int)Math.Round(1_000_000.0 / ctMinMireds.Value) : (hasCt ? 6500 : 2700);
        if (coolK < warmK) (coolK, warmK) = (warmK, coolK);

        // Distance is measured against the whole white curve from the
        // bulb's warm end upward, so a cooler white than the bulb can make
        // just clamps (no dimming); only too-warm / too-saturated colours fade.
        var (_, dist) = NearestWhite(x, y, warmK, 10000);
        var (k, _) = NearestWhite(x, y, warmK, coolK);
        double fade = FadeFor(dist, followPercent);
        int fadedBri = (int)Math.Round(bri * fade);
        if (fade <= 0.001 || fadedBri < 1) return Command.OffCmd with { Fade = 0 };

        int? mireds = hasCt ? (int)Math.Round(1_000_000.0 / k) : null;
        return new Command(false, null, null, mireds, fadedBri, fade);
    }

    /// <summary>1 near the white curve, easing to 0 at the fade end.</summary>
    public static double FadeFor(double dist, int followPercent)
    {
        double end = FadeEndDistance(followPercent);
        double start = FadeStartDistance(followPercent);
        if (dist <= start) return 1;
        if (dist >= end) return 0;
        double t = (dist - start) / (end - start);
        return 1 - t * t * (3 - 2 * t); // smoothstep: gentle at both ends
    }

    /// <summary>Closest point on the black-body curve within
    /// [warmK, coolK]: returns its kelvin and the xy distance to it.</summary>
    public static (int Kelvin, double Distance) NearestWhite(double x, double y, int warmK, int coolK)
    {
        warmK = Math.Clamp(warmK, 1667, 25000);
        coolK = Math.Clamp(coolK, warmK, 25000);
        int bestK = warmK;
        double best = double.MaxValue;
        // Step in mireds (perceptually even, and what bulbs use).
        int mWarm = (int)Math.Round(1_000_000.0 / warmK), mCool = (int)Math.Round(1_000_000.0 / coolK);
        for (int m = mWarm; m >= mCool; m -= 2)
        {
            int k = (int)Math.Round(1_000_000.0 / m);
            var (px, py) = PlanckianXy(k);
            double d = (px - x) * (px - x) + (py - y) * (py - y);
            if (d < best) { best = d; bestK = k; }
        }
        {
            var (px, py) = PlanckianXy(coolK);
            double d = (px - x) * (px - x) + (py - y) * (py - y);
            if (d < best) { best = d; bestK = coolK; }
        }
        return (bestK, Math.Sqrt(best));
    }

    /// <summary>CIE 1931 xy of a black body at <paramref name="kelvin"/>
    /// (Kim et al. cubic approximation, valid 1667..25000 K).</summary>
    public static (double x, double y) PlanckianXy(double kelvin)
    {
        double t = Math.Clamp(kelvin, 1667, 25000);
        double t2 = t * t, t3 = t2 * t;
        double x = t <= 4000
            ? -0.2661239e9 / t3 - 0.2343589e6 / t2 + 0.8776956e3 / t + 0.179910
            : -3.0258469e9 / t3 + 2.1070379e6 / t2 + 0.2226347e3 / t + 0.240390;
        double x2 = x * x, x3 = x2 * x;
        double y = t <= 2222
            ? -1.1063814 * x3 - 1.34811020 * x2 + 2.18555832 * x - 0.20219683
            : t <= 4000
                ? -0.9549476 * x3 - 1.37418593 * x2 + 2.09137015 * x - 0.16748867
                : 3.0817580 * x3 - 5.87338670 * x2 + 3.75112997 * x - 0.37001483;
        return (x, y);
    }
}
