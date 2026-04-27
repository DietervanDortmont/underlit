using System;

namespace Underlit.Sys;

/// <summary>
/// Runtime-tunable parameters for the Liquid Glass renderer (v0.5 — matches the
/// liquidGL.js sliders so the same intuition applies).
///
/// Slider conventions:
///   • LightAngleDeg     :  -180..180 (where the key light is — controls Phong)
///   • LightIntensity    :  0..200 (%)
///   • Refraction        :  0..30   pixels of body warping at the rim
///   • BevelDepth        :  0..100  pixels of additional rim spike (the lens lip)
///   • BevelWidth        :  0..100  (% of pillH/2 — width of the bevel zone)
///   • Dispersion        :  0..10   chromatic aberration strength
///   • Frost             :  0..30   blur radius in pixels
///   • CornerRadius      :  0..100  (% of pillH/2 — pill corner roundness)
///
/// The "Depth" and "BodyCurvature" sliders from earlier versions are gone —
/// the liquidGL formula `edge·refraction + edge¹⁰·bevelDepth` covers what they
/// were trying to do.
/// </summary>
public sealed class GlassParams
{
    public double LightAngleDeg  { get; set; } = -45;
    public double LightIntensity { get; set; } = 100;
    public double Refraction     { get; set; } = 6;       // body warping (pixels at rim)
    public double Depth          { get; set; } = 50;      // legacy / unused — kept so saved settings load cleanly
    public double Dispersion     { get; set; } = 0;
    public double Frost          { get; set; } = 4;
    public double CornerRadius   { get; set; } = 100;     // 0..100 (% of pillH/2)
    public double BevelWidth     { get; set; } = 25;      // 0..100 (% of pillH/2 — bevel zone width)
    public double BodyCurvature  { get; set; } = 50;      // legacy / unused — kept so saved settings load cleanly
    public double BevelDepthSliderValue { get; set; } = 35;   // 0..100 (pixels of edge-spike refraction)

    public GlassParams Clone() => new()
    {
        LightAngleDeg  = LightAngleDeg,
        LightIntensity = LightIntensity,
        Refraction     = Refraction,
        Depth          = Depth,
        Dispersion     = Dispersion,
        Frost          = Frost,
        CornerRadius   = CornerRadius,
        BevelWidth     = BevelWidth,
        BodyCurvature  = BodyCurvature,
        BevelDepthSliderValue = BevelDepthSliderValue,
    };

    public (float x, float y, float z) LightDirection()
    {
        double rad = LightAngleDeg * Math.PI / 180.0;
        const double elevation = 0.45;
        double xy = Math.Sqrt(1.0 - elevation * elevation);
        return ((float)(xy * Math.Sin(rad)),
                (float)(-xy * Math.Cos(rad)),
                (float)elevation);
    }

    public double IntensityMul() => Math.Clamp(LightIntensity, 0, 200) / 100.0;

    public int CornerRadiusPx(int pillHeightPx)
    {
        double pct = Math.Clamp(CornerRadius, 0, 100) / 100.0;
        return (int)Math.Round(pillHeightPx * 0.5 * pct);
    }

    /// <summary>Bevel-zone width in PIXELS, computed from the slider's % of pillH/2.</summary>
    public double BevelWidthPx(int pillHeightPx) =>
        Math.Max(1.0, pillHeightPx * 0.5 * Math.Clamp(BevelWidth, 0, 100) / 100.0);

    /// <summary>Edge-spike depth in PIXELS at the rim (the bevelDepth in the liquidGL formula).</summary>
    public double BevelDepth() => Math.Clamp(BevelDepthSliderValue, 0, 100);
}
