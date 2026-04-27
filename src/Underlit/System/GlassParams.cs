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
    /// <summary>0..100. Width of the SHARP outer-bezel (lip) zone as a percentage of pillH/2.
    /// This is the dramatic-rim refraction band — keep small (~10) for a "lens lip" look.</summary>
    public double BevelWidth     { get; set; } = 100;

    /// <summary>0..100. Width of the GENTLE inner-dome zone as a percentage of pillH/2.
    /// Adds smooth body curvature INSIDE the bezel — pixels in this zone have a small
    /// extra slope that gives the glass its "blob"/lens shape leading up to the lip.
    /// 0 = bezel only, no body warping. 50 = dome covers half the surface (default,
    /// good with a thin bezel). 100 = dome spans the whole pill.</summary>
    public double BodyCurvature  { get; set; } = 50;
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
        BevelWidth     = BevelWidth,
        BodyCurvature  = BodyCurvature,
    };

    /// <summary>0..1 — the fraction of pillH/2 that the sharp bezel zone occupies.</summary>
    public double BevelWidthFraction() => Math.Clamp(BevelWidth, 0, 100) / 100.0;
    /// <summary>0..1 — the fraction of pillH/2 that the gentle inner-dome zone occupies.</summary>
    public double BodyCurvatureFraction() => Math.Clamp(BodyCurvature, 0, 100) / 100.0;

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
    /// Curvature distribution:
    ///   • High n  → very flat top, sharp drop at the rim. With heavy frost the
    ///               flat-vs-curved boundary becomes visible (banding).
    ///   • Low n   → smooth dome — slope decreases continuously from rim to centre,
    ///               so refraction varies smoothly. Frost-friendly.
    ///
    ///   Depth = 0   → n = 4    (squircle, Apple's preferred flat-top look — sharper
    ///                            transition; pair with low Frost to avoid banding)
    ///   Depth = 50  → n = 2.75 (default — smooth domed lens; works well with Frost)
    ///   Depth = 100 → n = 1.5  (near-spherical — strongest, most uniform body warping)
    /// </summary>
    public double SquircleExponent() => 4.0 - (Math.Clamp(Depth, 0, 100) / 100.0) * 2.5;

    /// <summary>Maps LightIntensity slider (0..200) to a multiplier (0..2).</summary>
    public double IntensityMul() => LightIntensity / 100.0;
}
