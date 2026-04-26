using System;

namespace Underlit.Sys;

/// <summary>
/// Geometry of the Liquid Glass surface.
///
/// IMPORTANT (v0.3.1): the shape is a FLAT PUCK with a small bevel where the top
/// face rolls over to the side, NOT a hemispherical dome. This is the actual
/// shape Apple uses (verifiable from their lab footage and any side-on render
/// of a Liquid Glass element):
///
///                     ┌─────────────────────────────────┐
///                    ╱                                   ╲     ← bevel (rounded edge)
///   ╭─────────────────────────────────────────────────────╮
///   │                  flat top face                      │   ← top of puck
///   ╰─────────────────────────────────────────────────────╯
///                    ╲                                   ╱     ← bevel (rounded edge)
///                     └─────────────────────────────────┘
///
/// Implications for shading:
///   • The flat top has normal (0,0,1) everywhere → no refraction across the body
///     and no Phong/Fresnel response. The body just shows through.
///   • The bevel is a thin ring (a few pixels wide) where the normal rolls from
///     vertical to horizontal. This is where Phong specular and Fresnel rim
///     happen, and where light gets bent.
///   • Because the bevel is a ring, the highlight is automatically LOCALIZED —
///     it appears wherever the bevel's outward direction aligns with the light's
///     half-vector, and nowhere else. The opposite corner gets nothing. This
///     matches every Apple reference perfectly.
///
/// Bevel profile: quarter-circle (cosine slope). At the very rim (sdf=0) the
/// surface is steepest (normal nearly horizontal). At sdf = bevelWidth it
/// smoothly meets the flat top (normal vertical).
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
        public double BevelMaxSlope;

        public float[] Normals = Array.Empty<float>();
        public float[] Sdf     = Array.Empty<float>();
    }

    /// <summary>Default rim slope (overridden by GlassParams.Depth at runtime).</summary>
    public const double DefaultBevelMaxSlope = 2.5;

    public static NormalMap ComputePill(int fullW, int fullH, int padX, int padY,
                                         int pillW, int pillH, int bevelPx, double bevelMaxSlope)
    {
        var map = new NormalMap
        {
            Width = fullW, Height = fullH,
            PadX = padX, PadY = padY,
            PillW = pillW, PillH = pillH,
            BevelPx = bevelPx,
            BevelMaxSlope = bevelMaxSlope,
        };
        int total = fullW * fullH;
        map.Normals = new float[total * 3];
        map.Sdf     = new float[total];

        if (fullW <= 0 || fullH <= 0) return map;
        if (bevelPx < 1) bevelPx = 1;

        double r = pillH / 2.0;
        double pillLeft  = padX;
        double pillRight = padX + pillW;
        double pillTop   = padY;
        double coreLeft  = pillLeft  + r;
        double coreRight = Math.Max(pillRight - r, coreLeft);

        for (int y = 0; y < fullH; y++)
        for (int x = 0; x < fullW; x++)
        {
            int i = y * fullW + x;

            double xc = Math.Clamp(x + 0.5, coreLeft, coreRight);
            double yc = pillTop + r;
            double dx = (x + 0.5) - xc;
            double dy = (y + 0.5) - yc;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            double sdf = r - dist;

            map.Sdf[i] = (float)sdf;

            if (sdf <= 0)
            {
                // Outside the pill.
                map.Normals[i * 3 + 0] = 0f;
                map.Normals[i * 3 + 1] = 0f;
                map.Normals[i * 3 + 2] = 1f;
                continue;
            }

            // Bevel profile.
            double nx, ny, nz;
            if (sdf >= bevelPx)
            {
                // Flat top — perfectly horizontal surface, normal points straight up.
                nx = 0; ny = 0; nz = 1;
            }
            else
            {
                // In the bevel ring. Slope decays from BevelMaxSlope at the rim to 0
                // at the top of the bevel (cosine profile = quarter circle).
                double t = sdf / bevelPx;                 // 0 at rim, 1 at top of bevel
                double horizSlope = bevelMaxSlope * Math.Cos(t * Math.PI / 2);

                // Outward direction in xy plane = unit vector from pill axis toward
                // this pixel. (For straight sides of pill: (0, ±1). For rounded ends:
                // (dx, dy)/dist.)
                double outwardX, outwardY;
                if (dist > 1e-6)
                {
                    outwardX = dx / dist;
                    outwardY = dy / dist;
                }
                else
                {
                    outwardX = 0;
                    outwardY = 0;
                }

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
