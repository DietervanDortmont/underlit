using System;

namespace Underlit.Sys;

/// <summary>
/// Geometry of the Liquid Glass surface. The image is now larger than the visible
/// pill — there's a `padding`-pixel margin around it for the soft shadow halo.
///
/// Per pixel we store:
///   • SDF — signed distance to the pill's edge (positive INSIDE the pill,
///           negative outside, 0 at the rim). Outside SDF is the negative
///           Euclidean distance from the rim, which lets the renderer fade a
///           soft shadow that decays smoothly into the transparent corners.
///   • Normal — surface normal of the curved-glass dome at this pixel. Only
///           valid (non-zero-xy) inside the pill. Used for Phong / Fresnel /
///           refraction sampling.
///
/// Geometry: hemispherical-pill lens. The flat top has normal (0,0,1); the rim
/// has normals tilted strongly outward. Light bends through this shape exactly
/// the way it bends through a polished river-stone, which is what Apple's
/// Liquid Glass is modeling.
/// </summary>
public static class GlassShape
{
    public sealed class NormalMap
    {
        public int Width;
        public int Height;
        public int PadX;     // shadow padding on the left/right
        public int PadY;     // shadow padding on the top/bottom
        public int PillW;    // visible pill width
        public int PillH;    // visible pill height

        /// <summary>Packed normals as [pixel][nx,ny,nz] floats; length = w*h*3.</summary>
        public float[] Normals = Array.Empty<float>();
        /// <summary>Signed distance to pill rim. Positive inside, negative outside, 0 at rim.</summary>
        public float[] Sdf    = Array.Empty<float>();
    }

    /// <summary>
    /// Compute the normal+SDF map for a fullW × fullH bitmap whose pill occupies
    /// the central pillW × pillH region with the given padding.
    /// </summary>
    public static NormalMap ComputePill(int fullW, int fullH, int padX, int padY, int pillW, int pillH)
    {
        var map = new NormalMap
        {
            Width = fullW,
            Height = fullH,
            PadX = padX, PadY = padY,
            PillW = pillW, PillH = pillH,
        };
        int total = fullW * fullH;
        map.Normals = new float[total * 3];
        map.Sdf     = new float[total];

        if (fullW <= 0 || fullH <= 0) return map;

        // Pill geometry, centred in the bitmap.
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
            double sdf = r - dist;            // + inside, − outside

            map.Sdf[i] = (float)sdf;

            if (sdf <= 0)
            {
                // Outside the pill — surface normal is meaningless. We still set z=1
                // so any accidental sampling doesn't NaN.
                map.Normals[i * 3 + 0] = 0f;
                map.Normals[i * 3 + 1] = 0f;
                map.Normals[i * 3 + 2] = 1f;
                continue;
            }

            double nx, ny, nz;
            if (sdf >= r - 1e-6)
            {
                nx = 0; ny = 0; nz = 1;
            }
            else
            {
                double h = Math.Sqrt(r * r - (r - sdf) * (r - sdf));
                if (h < 1e-3 || dist < 1e-6)
                {
                    if (dist > 1e-6)
                    {
                        nx = dx / dist;
                        ny = dy / dist;
                    }
                    else
                    {
                        nx = 0; ny = 0;
                    }
                    nz = 0.10;
                }
                else
                {
                    double slope = (r - sdf) / h;
                    nx = slope * dx / dist;
                    ny = slope * dy / dist;
                    nz = 1.0;
                }

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
