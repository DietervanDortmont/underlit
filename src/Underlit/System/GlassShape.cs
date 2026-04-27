using System;

namespace Underlit.Sys;

/// <summary>
/// Geometry of the Liquid Glass surface — a rounded rectangle with arbitrary corner
/// radius. When cornerRadius equals pillHeight/2, this collapses to the full pill
/// shape; smaller radii produce squircle-ish or near-rectangular shapes.
///
/// SDF computation:
///   • Inside the inner "core rectangle" (offset inward by cornerRadius from each
///     edge): SDF = distance to the nearest STRAIGHT edge of the rounded rect.
///   • Outside the core (i.e., in one of the rounded corner zones): SDF =
///     cornerRadius − distance to the nearest core point. Negative outside, positive
///     inside, zero at the rim.
///
/// Normal direction:
///   • In a corner zone: radial — points outward from the corner-arc center.
///   • Along a straight edge: perpendicular to that edge.
///
/// Height profile (the bevel) is unchanged from v0.3.x — within bevelPx of the rim
/// the surface rolls over with a quarter-circle (cosine-of-t) profile, beyond bevelPx
/// the top is flat.
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
        public int BevelPx;
        public int CornerRadiusPx;
        public double BevelMaxSlope;

        public float[] Normals = Array.Empty<float>();
        public float[] Sdf     = Array.Empty<float>();
    }

    public const double DefaultBevelMaxSlope = 2.5;

    public static NormalMap ComputePill(int fullW, int fullH, int padX, int padY,
                                         int pillW, int pillH, int bevelPx,
                                         double bevelMaxSlope, int cornerRadiusPx)
    {
        var map = new NormalMap
        {
            Width = fullW, Height = fullH,
            PadX = padX, PadY = padY,
            PillW = pillW, PillH = pillH,
            BevelPx = bevelPx,
            BevelMaxSlope = bevelMaxSlope,
            CornerRadiusPx = cornerRadiusPx,
        };
        int total = fullW * fullH;
        map.Normals = new float[total * 3];
        map.Sdf     = new float[total];

        if (fullW <= 0 || fullH <= 0) return map;
        if (bevelPx < 1) bevelPx = 1;

        // Clamp corner radius to half the smaller dimension.
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
                // Pixel is inside the core rectangle. SDF = distance to nearest straight
                // pill edge; outward direction is perpendicular to that edge.
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

            // Bevel height profile.
            double nx, ny, nz;
            if (sdf >= bevelPx)
            {
                nx = 0; ny = 0; nz = 1;
            }
            else
            {
                double t = sdf / bevelPx;
                double horizSlope = bevelMaxSlope * Math.Cos(t * Math.PI / 2);
                nx = horizSlope * outwardX;
                ny = horizSlope * outwardY;
                nz = 1.0;

                double mag = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (mag > 1e-9) { nx /= mag; ny /= mag; nz /= mag; }
                else { nx = 0; ny = 0; nz = 1; }
            }

            map.Normals[i * 3 + 0] = (float)nx;
            map.Normals[i * 3 + 1] = (float)ny;
            map.Normals[i * 3 + 2] = (float)nz;
        }

        return map;
    }
}
