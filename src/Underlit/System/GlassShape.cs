using System;

namespace Underlit.Sys;

/// <summary>
/// Geometry of the Liquid Glass surface — a rounded rectangle whose top surface is
/// a CONVEX SQUIRCLE dome (NOT a flat top with a bevel ring, as in v0.3.x).
///
/// The video / Kube.io article on real Liquid Glass shows that Apple's effect uses
/// the squircle height function:
///
///     f(t) = (1 − (1 − t)^n)^(1/n)
///
/// where t is the normalised distance from the rim (0 at rim, 1 at the centre line)
/// and n is an exponent that controls the shape:
///   n = 2  → spherical dome — strong lens warping across the entire body
///   n = 4  → Apple's preferred squircle — gentle curvature with sharp rim
///   n = 8  → very flat top with sharp rim drop (close to the v0.3.x look)
///
/// Slope (∂f/∂t) is the magnitude of the surface tilt. It's infinite at the rim
/// (t = 0) and zero at the centre (t = 1). We cap at 8.0 to avoid singularities.
///
/// Critically, EVERY pixel inside the pill has a non-flat surface normal — so
/// refraction happens across the entire body, not just at a 10-px bevel ring.
/// That's the lens effect the user has been asking for.
///
/// SDF computation handles arbitrary corner radius (full pill, squircle, sharp
/// rectangle). Outward direction at corners is radial; along straight edges it
/// is perpendicular to that edge.
/// </summary>
public static class GlassShape
{
    public sealed class NormalMap
    {
        public int Width;
        public int Height;
        public int PadX;
        public int PadY;
        public int PillW;
        public int PillH;
        public int CornerRadiusPx;
        public double SquircleExponent;

        public float[] Normals = Array.Empty<float>();
        public float[] Sdf     = Array.Empty<float>();
    }

    /// <summary>Cap on the bevel slope at the rim. Without a cap we'd get infinite
    /// slope (vertical normals) and refraction sampling would shoot off-screen.</summary>
    public const double MaxRimSlope = 8.0;

    public static NormalMap ComputePill(int fullW, int fullH, int padX, int padY,
                                         int pillW, int pillH, int cornerRadiusPx,
                                         double squircleExponent)
    {
        var map = new NormalMap
        {
            Width = fullW, Height = fullH,
            PadX = padX, PadY = padY,
            PillW = pillW, PillH = pillH,
            CornerRadiusPx = cornerRadiusPx,
            SquircleExponent = squircleExponent,
        };
        int total = fullW * fullH;
        map.Normals = new float[total * 3];
        map.Sdf     = new float[total];

        if (fullW <= 0 || fullH <= 0) return map;

        int maxR = Math.Min(pillW, pillH) / 2;
        int rPx = Math.Clamp(cornerRadiusPx, 0, maxR);

        double pillLeft   = padX;
        double pillRight  = padX + pillW;
        double pillTop    = padY;
        double pillBottom = padY + pillH;
        double coreLeft   = pillLeft   + rPx;
        double coreRight  = Math.Max(pillRight  - rPx, coreLeft);
        double coreTop    = pillTop    + rPx;
        double coreBottom = Math.Max(pillBottom - rPx, coreTop);

        // Maximum SDF is half of the smaller pill dimension. This is the t=1 anchor
        // for the squircle: pixels at the centre line have sdf == maxSdf, where the
        // surface is flat (slope 0).
        double maxSdf = Math.Min(pillW, pillH) / 2.0;
        double n = Math.Max(1.5, squircleExponent);   // floor for numerical stability

        for (int y = 0; y < fullH; y++)
        for (int x = 0; x < fullW; x++)
        {
            int i = y * fullW + x;

            double px = x + 0.5;
            double py = y + 0.5;

            double xc = Math.Clamp(px, coreLeft, coreRight);
            double yc = Math.Clamp(py, coreTop, coreBottom);
            double dxc = px - xc;
            double dyc = py - yc;
            double cornerDist = Math.Sqrt(dxc * dxc + dyc * dyc);

            double sdf;
            double outwardX, outwardY;

            if (cornerDist > 0)
            {
                // Pixel was clamped — it's in a rounded-corner zone OR fully outside.
                sdf = rPx - cornerDist;
                if (cornerDist > 1e-9)
                {
                    outwardX = dxc / cornerDist;
                    outwardY = dyc / cornerDist;
                }
                else
                {
                    outwardX = 0;
                    outwardY = 0;
                }
            }
            else
            {
                // Inside the core rectangle. SDF = distance to nearest straight edge;
                // outward direction is perpendicular to that edge.
                double dLeft   = px - pillLeft;
                double dRight  = pillRight - px;
                double dTop    = py - pillTop;
                double dBottom = pillBottom - py;
                sdf = Math.Min(Math.Min(dLeft, dRight), Math.Min(dTop, dBottom));

                if (dTop <= dBottom && dTop <= dLeft && dTop <= dRight)
                { outwardX = 0; outwardY = -1; }
                else if (dBottom <= dLeft && dBottom <= dRight)
                { outwardX = 0; outwardY = 1; }
                else if (dLeft <= dRight)
                { outwardX = -1; outwardY = 0; }
                else
                { outwardX = 1; outwardY = 0; }
            }

            map.Sdf[i] = (float)sdf;

            if (sdf <= 0)
            {
                map.Normals[i * 3 + 0] = 0f;
                map.Normals[i * 3 + 1] = 0f;
                map.Normals[i * 3 + 2] = 1f;
                continue;
            }

            // Squircle profile: slope at normalised position t = sdf / maxSdf.
            double t = Math.Min(sdf / maxSdf, 1.0);
            double slope = SquircleSlope(t, n);

            double nx = slope * outwardX;
            double ny = slope * outwardY;
            double nz = 1.0;

            double mag = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (mag > 1e-9) { nx /= mag; ny /= mag; nz /= mag; }
            else { nx = 0; ny = 0; nz = 1; }

            map.Normals[i * 3 + 0] = (float)nx;
            map.Normals[i * 3 + 1] = (float)ny;
            map.Normals[i * 3 + 2] = (float)nz;
        }

        return map;
    }

    /// <summary>
    /// Slope ∂f/∂t for f(t) = (1 − (1−t)^n)^(1/n) at t ∈ [0, 1]. Diverges at t = 0;
    /// we cap at MaxRimSlope. Returns 0 at t = 1.
    ///
    /// Derivation:
    ///   Let u = 1 − (1−t)^n.  Then f = u^(1/n).
    ///   df/dt = u^(1/n − 1) × n(1−t)^(n−1) × (1/n) = (1−t)^(n−1) / u^(1 − 1/n).
    /// </summary>
    private static double SquircleSlope(double t, double n)
    {
        if (t <= 1e-6) return MaxRimSlope;
        if (t >= 1.0 - 1e-6) return 0.0;

        double oneMinusT = 1.0 - t;
        double oneMinusTn = Math.Pow(oneMinusT, n);
        double u = 1.0 - oneMinusTn;
        if (u < 1e-9) return MaxRimSlope;

        double numerator   = Math.Pow(oneMinusT, n - 1);
        double denominator = Math.Pow(u, 1.0 - 1.0 / n);
        double slope = numerator / denominator;

        return Math.Min(slope, MaxRimSlope);
    }
}
