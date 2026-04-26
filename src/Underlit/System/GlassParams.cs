using System;

namespace Underlit.Sys;

/// <summary>
/// Runtime-tunable parameters for the Liquid Glass renderer. Exposed in the Settings
/// UI as 6 sliders, modeled on the Figma "Glass" plugin's controls.
///
/// Scales / units:
///   • LightAngleDeg     :  -180..180 ; 0 = top, -45 = upper-left, 90 = right.
///   • LightIntensity    :  0..200    ; percentage; 100 = baseline brightness.
///   • Refraction        :  0..30     ; pixels of peak displacement at the rim.
///   • Depth             :  0..100    ; 0 = no bevel (no spec/Fresnel/refraction),
///                                       100 = sharp bevel (very horizontal at rim).
///   • Dispersion        :  0..10     ; pixels of chromatic aberration. R/G/B sample
///                                       at slightly different offsets — that's the
///                                       rainbow tint you see at glass edges in real
///                                       life and in Apple's Liquid Glass.
///   • Frost             :  0..30     ; box-blur radius in pixels.
/// </summary>
public sealed class GlassParams
{
    public double LightAngleDeg  { get; set; } = -45;
    public double LightIntensity { get; set; } = 100;
    public double Refraction     { get; set; } = 8;
    public double Depth          { get; set; } = 50;
    public double Dispersion     { get; set; } = 0;
    public double Frost          { get; set; } = 10;

    public GlassParams Clone() => new()
    {
        LightAngleDeg  = LightAngleDeg,
        LightIntensity = LightIntensity,
        Refraction     = Refraction,
        Depth          = Depth,
        Dispersion     = Dispersion,
        Frost          = Frost,
    };

    /// <summary>Returns Light direction (x, y, z) — y axis is +Y down (screen coords).</summary>
    public (float x, float y, float z) LightDirection()
    {
        // Convert angle to radians. 0 = up = (0, -1). Clockwise positive.
        double rad = LightAngleDeg * Math.PI / 180.0;
        // Elevation factor — light is always slightly toward the viewer; tunable.
        // We use a fixed ~25° elevation so the bevel can always reflect into the eye.
        const double elevation = 0.45;   // z component before normalization
        double xy = Math.Sqrt(1.0 - elevation * elevation);
        float lx = (float)(xy * Math.Sin(rad));
        float ly = (float)(-xy * Math.Cos(rad));    // -cos because +Y is down
        float lz = (float)elevation;
        // Already unit-length (xy² + z² = 1), so no normalisation needed.
        return (lx, ly, lz);
    }

    /// <summary>Maps Depth slider (0..100) to BevelMaxSlope (0.5..6.0).</summary>
    public double BevelMaxSlope() => 0.5 + (Depth / 100.0) * 5.5;

    /// <summary>Maps LightIntensity slider (0..200) to a multiplier (0..2).</summary>
    public double IntensityMul() => LightIntensity / 100.0;
}
