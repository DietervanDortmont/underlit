using System;

namespace Underlit.Sys;

/// <summary>
/// Geometry of the Liquid Glass surface. We model the OSD as a pill (rounded rect
/// where the corners are full semicircles) with a curved top — like a polished
/// pebble or a half-cylinder lying on its side, with hemispherical ends.
///
/// The renderer uses the per-pixel surface normal of this shape to compute:
///   • Refraction displacement — proportional to the (x,y) component of the normal,
///     scaled by glass thickness. Pixels in the flat centre have N≈(0,0,1) and zero
///     displacement; pixels at the rim have nearly-horizontal normals and strong
///     displacement, exactly matching how light bends through a curved lens edge.
///   • Phong specular — bright spot where the normal aligns with the half-vector
///     between viewer and a 45° top-left key light. This is the bright crescent you
///     see in every Apple Liquid Glass reference shot.
///   • Fresnel rim — reflectivity rises sharply at grazing angles (where N·V → 0),
///     which is why glass edges always look brighter than glass interiors.
///
/// The shape only depends on dimensions, so the normal map is computed ONCE and
/// cached — per render we just sample.
/// </summary>
public static class GlassShape
{
    public sealed class NormalMap
    {
        public int Width;
        public int Height;
        /// <summary>Packed normals as [pixel][nx,ny,nz] floats; length = w*h*3.</summary>
        public float[] Normals = Array.Empty<float>();
        /// <summary>Inside-mask. true when the pixel is on the pill's surface.</summary>
        public bool[]  Inside  = Array.Empty<bool>();
    }

    /// <summary>
    /// Compute the normal map for a pill of the given dimensions in physical pixels.
    /// The pill axis runs horizontally (W ≥ H assumed). For a square (W=H) it degenerates
    /// to a circle, which is fine.
    /// </summary>
    public static NormalMap ComputePill(int width, int height)
    {
        var map = new NormalMap { Width = width, Height = height };
        int total = width * height;
        map.Normals = new float[total * 3];
        map.Inside  = new bool[total];

        if (width <= 0 || height <= 0) return map;

        // Pill geometry. The "core line" is the central horizontal axis between the two
        // semicircular ends; on the pill's surface the SDF (distance to nearest edge) is
        // simply r minus the distance to that core line.
        double r = height / 2.0;
        double coreLeft  = r;
        double coreRight = Math.Max(width - r, r);

        for (int y = 0; y < height; y++)
        for (int x = 0; x < width;  x++)
        {
            double xc = Math.Clamp(x + 0.5, coreLeft, coreRight);
            double yc = r;
            double dx = (x + 0.5) - xc;
            double dy = (y + 0.5) - yc;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            double sdf = r - dist;            // positive inside, 0 at edge, negative outside

            int i = y * width + x;
            if (sdf <= 0)
            {
                map.Inside[i] = false;
                map.Normals[i * 3 + 0] = 0f;
                map.Normals[i * 3 + 1] = 0f;
                map.Normals[i * 3 + 2] = 1f;
                continue;
            }

            map.Inside[i] = true;

            // Surface height profile h(sdf): a hemispherical dome with radius r.
            //   h(sdf) = sqrt(r² - (r - sdf)²) for sdf in [0, r]
            //   h(sdf) = r                       for sdf >  r   (impossible for pill since max sdf = r)
            // h'(sdf) = (r - sdf) / h            ≥ 0
            //
            // Normal in 3D = ( -∂h/∂x , -∂h/∂y , 1 ) normalized.
            // ∂h/∂x = h'(sdf) * ∂sdf/∂x ; ∂sdf/∂x = -dx/dist (since sdf = r - dist).
            // So -∂h/∂x = h'(sdf) * dx/dist. Same for y.
            //
            // Result: at the centre of the dome (sdf = r) h' = 0 → normal = (0,0,1).
            // At the rim (sdf → 0) h → 0, h' → ∞: the normal goes nearly horizontal.
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
                    // Right at the rim. Normal direction is purely outward in 2D, with a
                    // small but nonzero +Z so dot products don't blow up.
                    if (dist > 1e-6)
                    {
                        nx = dx / dist;
                        ny = dy / dist;
                    }
                    else
                    {
                        nx = 0;
                        ny = 0;
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
                if (mag > 1e-9)
                {
                    nx /= mag; ny /= mag; nz /= mag;
                }
                else
                {
                    nx = 0; ny = 0; nz = 1;
                }
            }

            map.Normals[i * 3 + 0] = (float)nx;
            map.Normals[i * 3 + 1] = (float)ny;
            map.Normals[i * 3 + 2] = (float)nz;
        }

        return map;
    }
}
