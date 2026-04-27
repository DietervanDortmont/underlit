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
    public double Refraction     { get; set; } = 16;
    public double Depth          { get; set; } = 50;
    public double Dispersion     { get; set; } = 0;
    public double Frost          { get; set; } = 10;
    /// <summary>0..100 — percentage of the maximum corner radius (which is pillHeight/2).
    /// 100 = full pill. 0 = sharp rectangle. Drives both the renderer's SDF AND the
    /// SetWindowRgn region, so the visible shape and the OS clip are guaranteed to match.</summary>
    public double CornerRadius   { get; set; } = 100;

    public GlassParams Clone() => new()
    {
        LightAngleDeg  = LightAngleDeg,
        LightIntensity = LightIntensity,
        Refraction     = Refraction,
        Depth          = Depth,
        Dispersion     = Dispersion,
        Frost          = Frost,
        CornerRadius   = CornerRadius,
    };

    /// <summary>Resolve the slider's percentage to an actual radius in physical pixels.</summary>
    public int CornerRadiusPx(int pillHeightPx)
    {
        double pct = Math.Clamp(CornerRadius, 0, 100) / 100.0;
        return (int)Math.Round(pillHeightPx * 0.5 * pct);
    }

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

    /// <summary>
    /// Maps the Depth slider (0..100) to the convex-squircle exponent used by the
    /// height function f(t) = (1 − (1−t)^n)^(1/n).
    ///
    ///   Depth = 0   → n = 8   (very flat top, sharp rim — the "pill with bevel" look)
    ///   Depth = 50  → n = 5   (Apple-ish — close to their preferred squircle of n=4)
    ///   Depth = 100 → n = 2   (sphere-dome — strong lens warping across the whole body)
    /// </summary>
    public double SquircleExponent() => 8.0 - (Math.Clamp(Depth, 0, 100) / 100.0) * 6.0;

    /// <summary>Maps LightIntensity slider (0..200) to a multiplier (0..2).</summary>
    public double IntensityMul() => LightIntensity / 100.0;
}
