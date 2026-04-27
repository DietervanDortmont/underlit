using System;

namespace Underlit.Sys;

/// <summary>
/// Geometry of the Liquid Glass surface. v0.5 simplified to match the liquidGL.js
/// (NaughtyDuk) shader approach: per-pixel signed distance to the rim plus an
/// outward direction (perpendicular to the nearest rim point). The actual
/// displacement formula lives in GlassRenderer using:
///
///     edge      = 1 − smoothstep(0, bevelPx, sdf)              // 1 at rim, 0 in body
///     offsetAmt = edge × refraction + edge¹⁰ × bevelDepth      // body + rim spike
///     uv_offset = (outwardX, outwardY) × offsetAmt
///
/// Two-term form is what makes this look like a real lens: a smooth body ramp PLUS
/// a sharp rim spike that overlap seamlessly. No more normal maps, sine curves,
/// squircles, or Snell math.
/// </summary>
public static class GlassShape
{
    public sealed class DispMap
    {
        public int Width;
        public int Height;
        public int PadX;
        public int PadY;
        public int PillW;
        public int PillH;
        public int CornerRadiusPx;

        /// <summary>Signed distance to pill rim (positive inside, 0 at rim, negative outside).</summary>
        public float[] Sdf = Array.Empty<float>();
        /// <summary>Unit outward direction (perpendicular to nearest rim point), x component.</summary>
        public float[] OutwardX = Array.Empty<float>();
        /// <summary>Unit outward direction, y component.</summary>
        public float[] OutwardY = Array.Empty<float>();
    }

    public static DispMap ComputePill(int fullW, int fullH, int padX, int padY,
                                       int pillW, int pillH, int cornerRadiusPx)
    {
        var map = new DispMap
        {
            Width = fullW, Height = fullH,
            PadX = padX, PadY = padY,
            PillW = pillW, PillH = pillH,
            CornerRadiusPx = cornerRadiusPx,
        };
        int total = fullW * fullH;
        map.Sdf      = new float[total];
        map.OutwardX = new float[total];
        map.OutwardY = new float[total];

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
                sdf = rPx - cornerDist;
                if (cornerDist > 1e-9)
                {
                    outwardX = dxc / cornerDist;
                    outwardY = dyc / cornerDist;
                }
                else { outwardX = 0; outwardY = 0; }
            }
            else
            {
                // Inside core rect — outward is perpendicular to the nearest straight edge.
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

            map.Sdf[i]      = (float)sdf;
            map.OutwardX[i] = (float)outwardX;
            map.OutwardY[i] = (float)outwardY;
        }

        return map;
    }
}
