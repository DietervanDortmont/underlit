using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace Underlit.Sys;

/// <summary>
/// Liquid-Glass renderer (v0.5). Translation of the liquidGL.js (NaughtyDuk) WebGL
/// shader to a CPU rasteriser. The core formula is a single line:
///
///     offsetAmt = edge × refraction + edge¹⁰ × bevelDepth
///
/// where:
///     edge       = 1 − smoothstep(0, bevelWidthPx, sdfFromRim)
///     refraction = base body warping (pixels at the rim)
///     bevelDepth = peak edge spike on top of the body warping (pixels at rim)
///
/// The two terms overlap: at the rim both contribute, in the body only the linear
/// term, in the centre neither. Sample is at the displaced position, with the
/// outward direction taken from GlassShape's per-pixel perpendicular-to-rim vector.
///
/// Per-channel chromatic aberration: scale offsetAmt slightly differently per
/// R/G/B (red bends least, blue bends most).
///
/// Specular highlights (Phong + Fresnel from a fixed top-left light) and vibrancy
/// (luminance-aware darkening) are kept from earlier versions; they're additive
/// and don't interfere with the displacement math.
/// </summary>
public static class GlassRenderer
{
    // ---- Constants ----
    public const double SaturationBoost   = 1.10;
    public const int    BlurPasses        = 3;

    public const float  SpecShininess = 25f;
    public const float  SpecIntensity = 1.6f;
    public const float  FresnelF0     = 0.04f;
    public const float  FresnelExp    = 5.0f;
    public const float  RimIntensity  = 0.55f;

    public const float  VibrancyStartLum = 0.78f;
    public const float  VibrancyMaxDark  = 0.15f;

    public const float  EdgeAaWidth   = 1.2f;
    public const float  MaxOffsetPx   = 80f;

    public sealed class Scratch
    {
        public byte[] Buffer1 = Array.Empty<byte>();
        public byte[] Buffer2 = Array.Empty<byte>();
        public byte[] Output  = Array.Empty<byte>();
        public int FullStride;
        public int FullW;
        public int FullH;
        public int PadX;
        public int PadY;
        public int PillW;
        public int PillH;

        public GlassShape.DispMap? DispMap;

        public void Configure(int fullW, int fullH, int padX, int padY, int cornerRadiusPx)
        {
            int pillW = fullW - padX * 2;
            int pillH = fullH - padY * 2;
            int fullStride = fullW * 4;
            int fullSize = fullStride * fullH;

            if (Buffer1.Length < fullSize) Buffer1 = new byte[fullSize];
            if (Buffer2.Length < fullSize) Buffer2 = new byte[fullSize];
            if (Output.Length  < fullSize) Output  = new byte[fullSize];

            FullStride = fullStride;
            FullW = fullW;
            FullH = fullH;
            PadX = padX;
            PadY = padY;
            PillW = pillW;
            PillH = pillH;

            if (DispMap == null
                || DispMap.Width != fullW
                || DispMap.Height != fullH
                || DispMap.PadX != padX
                || DispMap.PadY != padY
                || DispMap.PillW != pillW
                || DispMap.PillH != pillH
                || DispMap.CornerRadiusPx != cornerRadiusPx)
            {
                DispMap = GlassShape.ComputePill(fullW, fullH, padX, padY, pillW, pillH, cornerRadiusPx);
            }
        }
    }

    public static float LastPillLuminance { get; private set; } = 0.5f;

    public static bool Render(Bitmap fullCapture, Scratch scratch, GlassParams p)
    {
        int fullW = fullCapture.Width;
        int fullH = fullCapture.Height;
        if (fullW <= 0 || fullH <= 0) return false;
        if (fullW != scratch.FullW || fullH != scratch.FullH) return false;

        int fullStride = scratch.FullStride;
        byte[] a = scratch.Buffer1;
        byte[] b = scratch.Buffer2;
        var dmap = scratch.DispMap!;

        // 1. Read full-window capture into A.
        var rect = new Rectangle(0, 0, fullW, fullH);
        var data = fullCapture.LockBits(rect, ImageLockMode.ReadOnly, DPixelFormat.Format32bppArgb);
        try
        {
            if (data.Stride == fullStride)
                Marshal.Copy(data.Scan0, a, 0, fullStride * fullH);
            else
                for (int y = 0; y < fullH; y++)
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), a, y * fullStride, fullStride);
        }
        finally { fullCapture.UnlockBits(data); }

        // 2. Average pre-blur luminance of the icon zone for adaptive icon colour.
        LastPillLuminance = ComputePillLuminance(a, fullW, fullH, fullStride, dmap);

        // 3. Saturation + tint wash. TintStrength = 0 means raw refracted backdrop
        // with no undertone (pure transparent glass).
        double tintT = Math.Clamp(p.TintStrength, 0, 100) / 100.0;
        ApplyGlassWashAndSaturation(a, fullW, fullH, fullStride,
                                     tintT, SaturationBoost,
                                     p.TintR, p.TintG, p.TintB);

        // 4. Box blur (frost).
        int blurRadius = Math.Max(0, (int)Math.Round(p.Frost));
        if (blurRadius > 0)
        {
            for (int pass = 0; pass < BlurPasses; pass++)
            {
                BoxBlurHorizontal(a, b, fullW, fullH, fullStride, blurRadius);
                BoxBlurVertical  (b, a, fullW, fullH, fullStride, blurRadius);
            }
        }

        // 5. Composite — uses the liquidGL two-term displacement formula.
        var light = p.LightDirection();
        var halfVec = NormalizeVec(light.x, light.y, light.z + 1f);
        ShadeAndComposite(a, scratch.Output, fullW, fullH, fullStride, dmap,
                           halfVec, (float)p.IntensityMul(),
                           (float)p.Refraction, (float)p.BevelDepth(), (float)p.BevelWidthPx(dmap.PillH),
                           (float)p.Dispersion,
                           (float)p.RimBrightnessMul(), (float)p.RimWidthExponent(),
                           (float)p.SecondaryRimMul());

        return true;
    }

    private static void ShadeAndComposite(byte[] body, byte[] dst, int fullW, int fullH, int fullStride,
                                           GlassShape.DispMap dmap,
                                           (float x, float y, float z) halfVec,
                                           float intensityMul,
                                           float refraction, float bevelDepth, float bevelWidthPx,
                                           float dispersion,
                                           float rimBrightnessMul, float rimWidthExponent,
                                           float secondaryRimMul)
    {
        float[] sdf = dmap.Sdf;
        float[] outX = dmap.OutwardX;
        float[] outY = dmap.OutwardY;

        if (bevelWidthPx < 1f) bevelWidthPx = 1f;

        for (int y = 0; y < fullH; y++)
        {
            for (int x = 0; x < fullW; x++)
            {
                int pi = y * fullW + x;
                int dstIdx = y * fullStride + x * 4;
                float pixSdf = sdf[pi];

                if (pixSdf <= 0)
                {
                    dst[dstIdx + 0] = 0;
                    dst[dstIdx + 1] = 0;
                    dst[dstIdx + 2] = 0;
                    dst[dstIdx + 3] = 0;
                    continue;
                }

                float ox = outX[pi];
                float oy = outY[pi];

                // ---- The liquidGL displacement formula ----
                // edge: 1 at rim, smoothly down to 0 at bevelWidthPx into the body.
                float edge = 1f - Smoothstep(0f, bevelWidthPx, pixSdf);
                // Two-term offset: linear body + sharp rim spike (pow 10).
                float edge10 = edge * edge; edge10 *= edge10; edge10 *= edge10 * edge * edge;
                float offsetAmt = edge * refraction + edge10 * bevelDepth;
                if (offsetAmt > MaxOffsetPx) offsetAmt = MaxOffsetPx;

                // Per-channel aberration: scale the offset slightly differently per R/G/B.
                // Red bends least → smaller offset; blue bends most → larger.
                float dispScale = dispersion * 0.04f;
                float offR = offsetAmt * (1f - dispScale);
                float offG = offsetAmt;
                float offB = offsetAmt * (1f + dispScale);

                int sxR = SampleX(x, ox, offR, fullW);
                int syR = SampleY(y, oy, offR, fullH);
                int sxG = SampleX(x, ox, offG, fullW);
                int syG = SampleY(y, oy, offG, fullH);
                int sxB = SampleX(x, ox, offB, fullW);
                int syB = SampleY(y, oy, offB, fullH);

                int R = body[syR * fullStride + sxR * 4 + 2];
                int G = body[syG * fullStride + sxG * 4 + 1];
                int B = body[syB * fullStride + sxB * 4 + 0];

                // Vibrancy: darken on bright backdrops so foreground icons stay legible.
                float lum = (R * 0.299f + G * 0.587f + B * 0.114f) / 255f;
                if (lum > VibrancyStartLum)
                {
                    float over = (lum - VibrancyStartLum) / (1f - VibrancyStartLum);
                    if (over > 1f) over = 1f;
                    float darken = 1f - over * VibrancyMaxDark;
                    R = (int)(R * darken);
                    G = (int)(G * darken);
                    B = (int)(B * darken);
                }

                // Specular + Fresnel rim (additive). We synthesize a normal from the
                // outward direction and edge factor: nx,ny tilt outward proportional
                // to edge (rim is steep, body is flat-ish), nz = sqrt(1 − xy²).
                float nxyMag = edge * 0.95f; // tilt magnitude
                float nx = ox * nxyMag;
                float ny = oy * nxyMag;
                float nzSq = 1f - (nx * nx + ny * ny);
                float nz = nzSq > 0 ? MathF.Sqrt(nzSq) : 0.05f;

                float NdotH = nx * halfVec.x + ny * halfVec.y + nz * halfVec.z;
                if (NdotH < 0f) NdotH = 0f;
                float spec = MathF.Pow(NdotH, SpecShininess) * SpecIntensity * intensityMul;

                // Rim highlight — Phong specular masked to a thin rim band.
                //   • Mask peak SHIFTED INWARD by the AA width so the brightest band
                //     lands on the visible edge (otherwise the peak is hidden behind
                //     AA-fade transparency).
                //   • Symmetric: a primary highlight at NdotH (lit-side rim) AND a
                //     secondary one at NdotH_inv (opposite-side rim) using a flipped
                //     half-vector. RimSecondary slider weights the secondary.
                //   • Width-compensated brightness: thin bands get a small boost so
                //     perceived brightness doesn't fall off as the user narrows it.
                //   • Independent of intensityMul (light-intensity slider) so user can
                //     disable the bevel and keep the rim glowing.
                float adjustedSdf = pixSdf - EdgeAaWidth;
                if (adjustedSdf < 0) adjustedSdf = 0;
                float adjustedBevel = bevelWidthPx - EdgeAaWidth;
                if (adjustedBevel < 1f) adjustedBevel = 1f;
                float edgeShifted = 1f - Smoothstep(0f, adjustedBevel, adjustedSdf);
                float rimMask = MathF.Pow(edgeShifted, rimWidthExponent);

                // Inverted half-vector for the opposing-corner highlight (flip xy, keep z).
                float NdotH_inv = -nx * halfVec.x - ny * halfVec.y + nz * halfVec.z;
                if (NdotH_inv < 0f) NdotH_inv = 0f;

                float rimPhong1 = MathF.Pow(NdotH, SpecShininess);
                float rimPhong2 = MathF.Pow(NdotH_inv, SpecShininess) * secondaryRimMul;

                // Compensate so per-pixel peak brightness stays ~constant when the
                // user changes Rim width. (Higher exponent = thinner band = less area;
                // bump per-pixel intensity to keep total visible energy steady.)
                float widthComp = MathF.Pow((rimWidthExponent + 1f) / 14f, 0.5f);
                float thinRim = rimMask * (rimPhong1 + rimPhong2) * rimBrightnessMul * widthComp;
                spec += thinRim;

                float NdotV = nz; if (NdotV < 0f) NdotV = 0f;
                float fresnel = FresnelF0 + (1f - FresnelF0) * MathF.Pow(1f - NdotV, FresnelExp);
                fresnel *= RimIntensity * intensityMul;

                int specAdd = (int)(spec * 255f);
                int rimAdd  = (int)(fresnel * 255f);

                int rOut = R + specAdd + rimAdd;
                int gOut = G + specAdd + rimAdd;
                int bOut = B + specAdd + (int)(rimAdd * 1.04f);
                if (rOut > 255) rOut = 255;
                if (gOut > 255) gOut = 255;
                if (bOut > 255) bOut = 255;

                // AA at pill rim.
                float aaAlpha = pixSdf / EdgeAaWidth;
                if (aaAlpha > 1f) aaAlpha = 1f;
                if (aaAlpha < 0f) aaAlpha = 0f;
                byte pillAlpha = (byte)(aaAlpha * 255f);

                dst[dstIdx + 0] = (byte)bOut;
                dst[dstIdx + 1] = (byte)gOut;
                dst[dstIdx + 2] = (byte)rOut;
                dst[dstIdx + 3] = pillAlpha;
            }
        }
    }

    private static int SampleX(int x, float ox, float off, int w)
    {
        int sx = x + (int)MathF.Round(ox * off);
        if (sx < 0) sx = 0; else if (sx >= w) sx = w - 1;
        return sx;
    }

    private static int SampleY(int y, float oy, float off, int h)
    {
        int sy = y + (int)MathF.Round(oy * off);
        if (sy < 0) sy = 0; else if (sy >= h) sy = h - 1;
        return sy;
    }

    /// <summary>GLSL-style smoothstep, 0 at edge0, 1 at edge1, cubic in between.</summary>
    private static float Smoothstep(float edge0, float edge1, float v)
    {
        if (edge1 <= edge0) return v < edge0 ? 0 : 1;
        float t = (v - edge0) / (edge1 - edge0);
        if (t < 0) t = 0; else if (t > 1) t = 1;
        return t * t * (3f - 2f * t);
    }

    private static void ApplyGlassWashAndSaturation(byte[] buf, int w, int h, int stride,
                                                     double washT, double saturation,
                                                     byte tintR, byte tintG, byte tintB)
    {
        int it  = (int)(washT * 256);
        int omt = 256 - it;
        int satFixed = (int)(saturation * 256);

        for (int y = 0; y < h; y++)
        {
            int row = y * stride;
            for (int x = 0; x < w; x++)
            {
                int i = row + x * 4;
                int B = buf[i + 0];
                int G = buf[i + 1];
                int R = buf[i + 2];

                int grey = (R * 77 + G * 150 + B * 29) >> 8;
                R = grey + (((R - grey) * satFixed) >> 8);
                G = grey + (((G - grey) * satFixed) >> 8);
                B = grey + (((B - grey) * satFixed) >> 8);
                if (R < 0) R = 0; else if (R > 255) R = 255;
                if (G < 0) G = 0; else if (G > 255) G = 255;
                if (B < 0) B = 0; else if (B > 255) B = 255;

                // Tint mix: pull pixel toward the user's tint color by `washT`.
                B = (B * omt + tintB * it) >> 8;
                G = (G * omt + tintG * it) >> 8;
                R = (R * omt + tintR * it) >> 8;

                buf[i + 0] = (byte)B;
                buf[i + 1] = (byte)G;
                buf[i + 2] = (byte)R;
                buf[i + 3] = 255;
            }
        }
    }

    private static void BoxBlurHorizontal(byte[] src, byte[] dst, int w, int h, int stride, int r)
    {
        int span = r * 2 + 1;
        for (int y = 0; y < h; y++)
        {
            int row = y * stride;
            int sumB = 0, sumG = 0, sumR = 0;
            for (int x = -r; x <= r; x++)
            {
                int sx = x < 0 ? 0 : (x >= w ? w - 1 : x);
                int i = row + sx * 4;
                sumB += src[i + 0]; sumG += src[i + 1]; sumR += src[i + 2];
            }
            for (int x = 0; x < w; x++)
            {
                int oi = row + x * 4;
                dst[oi + 0] = (byte)(sumB / span);
                dst[oi + 1] = (byte)(sumG / span);
                dst[oi + 2] = (byte)(sumR / span);
                dst[oi + 3] = 255;
                int xOut = x - r, xIn = x + r + 1;
                int sxOut = xOut < 0 ? 0 : (xOut >= w ? w - 1 : xOut);
                int sxIn  = xIn  < 0 ? 0 : (xIn  >= w ? w - 1 : xIn);
                int io = row + sxOut * 4;
                int ii = row + sxIn  * 4;
                sumB += src[ii + 0] - src[io + 0];
                sumG += src[ii + 1] - src[io + 1];
                sumR += src[ii + 2] - src[io + 2];
            }
        }
    }

    private static void BoxBlurVertical(byte[] src, byte[] dst, int w, int h, int stride, int r)
    {
        int span = r * 2 + 1;
        for (int x = 0; x < w; x++)
        {
            int colOff = x * 4;
            int sumB = 0, sumG = 0, sumR = 0;
            for (int y = -r; y <= r; y++)
            {
                int sy = y < 0 ? 0 : (y >= h ? h - 1 : y);
                int i = sy * stride + colOff;
                sumB += src[i + 0]; sumG += src[i + 1]; sumR += src[i + 2];
            }
            for (int y = 0; y < h; y++)
            {
                int oi = y * stride + colOff;
                dst[oi + 0] = (byte)(sumB / span);
                dst[oi + 1] = (byte)(sumG / span);
                dst[oi + 2] = (byte)(sumR / span);
                dst[oi + 3] = 255;
                int yOut = y - r, yIn = y + r + 1;
                int syOut = yOut < 0 ? 0 : (yOut >= h ? h - 1 : yOut);
                int syIn  = yIn  < 0 ? 0 : (yIn  >= h ? h - 1 : yIn);
                int io = syOut * stride + colOff;
                int ii = syIn  * stride + colOff;
                sumB += src[ii + 0] - src[io + 0];
                sumG += src[ii + 1] - src[io + 1];
                sumR += src[ii + 2] - src[io + 2];
            }
        }
    }

    private static (float, float, float) NormalizeVec(float x, float y, float z)
    {
        float mag = MathF.Sqrt(x * x + y * y + z * z);
        if (mag < 1e-9f) return (0, 0, 1);
        return (x / mag, y / mag, z / mag);
    }

    private static float ComputePillLuminance(byte[] body, int fullW, int fullH, int fullStride,
                                                GlassShape.DispMap dmap)
    {
        float[] sdf = dmap.Sdf;
        int leftThirdEnd = dmap.PadX + dmap.PillW / 3;
        long sumLum = 0;
        int count = 0;
        for (int y = 0; y < fullH; y++)
        for (int x = 0; x < leftThirdEnd; x++)
        {
            int pi = y * fullW + x;
            if (sdf[pi] <= 0) continue;
            int idx = y * fullStride + x * 4;
            int B = body[idx + 0];
            int G = body[idx + 1];
            int R = body[idx + 2];
            sumLum += (R * 77 + G * 150 + B * 29) >> 8;
            count++;
        }
        return count > 0 ? (sumLum / (float)count) / 255f : 0.5f;
    }
}
