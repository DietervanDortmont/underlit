using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace Underlit.Sys;

/// <summary>
/// Curved-glass renderer (v0.3). Renders the OSD as a hemispherical-pill lens with
/// a soft shadow halo. The output is a fullW × fullH bitmap that includes pill,
/// shadow, and transparent corners — designed for an AllowsTransparency=true
/// window that has no DWM acrylic and no DWM rounded corners.
///
/// Pipeline (BGRA32, all CPU):
///   1.  Read captured pixels (cropped to the pill region) into buffer A.
///       Note: capture is the screen pixels behind the PILL, NOT the whole 300×66
///       window. The shadow halo is generated synthetically — it doesn't try to
///       refract behind-content.
///   2.  Saturation boost — Apple amplifies behind-content colour ~30%.
///   3.  Subtle white wash — small frostiness so the body isn't aggressively see-through.
///   4.  Box blur ×3 ≈ gaussian (radius 18) over the pill region.
///   5.  Per-pixel pass over the FULL bitmap using GlassShape's normal+SDF map:
///        a. SDF > 0  → inside pill: refract + spec + Fresnel + vibrancy. Alpha = 255
///                       (with anti-aliased softening at the very rim where |SDF| < 1).
///        b. SDF < 0  → shadow zone: black with alpha = shadowAlpha(distFromEdge).
///                       Decays smoothly from ShadowOpacity at the rim to 0 at
///                       ShadowRadius pixels out.
///        c. SDF outside shadow → fully transparent.
///
/// Because we only sample the captured pill region (not the full bitmap), the
/// scratch buffer only needs to be pillW × pillH for the body work; the shadow
/// pass writes directly into the output buffer using normals+SDF only.
/// </summary>
public static class GlassRenderer
{
    // ---- Body / blur ----
    public const double SaturationBoost   = 1.32;
    public const double GlassWashStrength = 0.06;
    public const int    BlurRadius        = 18;
    public const int    BlurPasses        = 3;

    // ---- Refraction ----
    public const float  RefractionStrength = 18f;

    // ---- Lighting (top-left key, ~45°) ----
    private static readonly (float x, float y, float z) LightDir = NormalizeVec(-0.55f, -0.70f, 0.45f);
    private static readonly (float x, float y, float z) ViewDir  = (0f, 0f, 1f);
    private static readonly (float x, float y, float z) HalfVec  =
        NormalizeVec(LightDir.x + ViewDir.x, LightDir.y + ViewDir.y, LightDir.z + ViewDir.z);
    public const float  SpecShininess = 50f;
    public const float  SpecIntensity = 1.10f;
    public const float  FresnelF0     = 0.04f;
    public const float  FresnelExp    = 5.0f;
    public const float  RimIntensity  = 0.85f;

    // ---- Vibrancy ----
    public const float  VibrancyStartLum = 0.78f;
    public const float  VibrancyMaxDark  = 0.18f;

    // ---- Shadow halo ----
    public const float  ShadowRadius   = 9.0f;    // pixels of halo extending past the pill edge
    public const float  ShadowOpacity  = 0.42f;   // peak alpha right at the rim

    // ---- Anti-aliasing at pill edge ----
    public const float  EdgeAaWidth    = 1.2f;    // SDF range over which alpha goes 0..255 at the rim

    public sealed class Scratch
    {
        // Scratch buffers sized to the PILL region (not the full bitmap).
        public byte[] Buffer1 = Array.Empty<byte>();
        public byte[] Buffer2 = Array.Empty<byte>();
        public int PillStride;
        public int PillW;
        public int PillH;

        // Output buffer is sized to the FULL bitmap.
        public byte[] Output = Array.Empty<byte>();
        public int FullStride;
        public int FullW;
        public int FullH;
        public int PadX;
        public int PadY;

        public GlassShape.NormalMap? NormalMap;

        public void Configure(int fullW, int fullH, int padX, int padY)
        {
            int pillW = fullW - padX * 2;
            int pillH = fullH - padY * 2;
            int pillStride = pillW * 4;
            int fullStride = fullW * 4;
            int pillSize = pillStride * pillH;
            int fullSize = fullStride * fullH;

            if (Buffer1.Length < pillSize) Buffer1 = new byte[pillSize];
            if (Buffer2.Length < pillSize) Buffer2 = new byte[pillSize];
            if (Output.Length  < fullSize) Output  = new byte[fullSize];

            PillStride = pillStride;
            PillW = pillW;
            PillH = pillH;
            FullStride = fullStride;
            FullW = fullW;
            FullH = fullH;
            PadX = padX;
            PadY = padY;

            if (NormalMap == null
                || NormalMap.Width != fullW
                || NormalMap.Height != fullH
                || NormalMap.PadX != padX
                || NormalMap.PadY != padY
                || NormalMap.PillW != pillW
                || NormalMap.PillH != pillH)
            {
                NormalMap = GlassShape.ComputePill(fullW, fullH, padX, padY, pillW, pillH);
            }
        }
    }

    /// <summary>
    /// Render `pillCapture` (a captured pillW×pillH bitmap of what's behind the pill)
    /// into scratch.Output (the full window bitmap). Caller must have called
    /// scratch.Configure(...) with the right dimensions first.
    /// </summary>
    public static bool Render(Bitmap pillCapture, Scratch scratch)
    {
        int pillW = pillCapture.Width;
        int pillH = pillCapture.Height;
        if (pillW <= 0 || pillH <= 0) return false;
        if (pillW != scratch.PillW || pillH != scratch.PillH) return false;

        int pillStride = scratch.PillStride;
        byte[] a = scratch.Buffer1;
        byte[] b = scratch.Buffer2;
        var nmap = scratch.NormalMap!;

        // 1. Read captured pixels into A.
        var rect = new Rectangle(0, 0, pillW, pillH);
        var data = pillCapture.LockBits(rect, ImageLockMode.ReadOnly, DPixelFormat.Format32bppArgb);
        try
        {
            if (data.Stride == pillStride)
            {
                Marshal.Copy(data.Scan0, a, 0, pillStride * pillH);
            }
            else
            {
                for (int y = 0; y < pillH; y++)
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), a, y * pillStride, pillStride);
            }
        }
        finally { pillCapture.UnlockBits(data); }

        // 2 + 3. Saturation + light wash.
        ApplyGlassWashAndSaturation(a, pillW, pillH, pillStride, GlassWashStrength, SaturationBoost);

        // 4. Box blur ×3 → A.
        for (int pass = 0; pass < BlurPasses; pass++)
        {
            BoxBlurHorizontal(a, b, pillW, pillH, pillStride, BlurRadius);
            BoxBlurVertical  (b, a, pillW, pillH, pillStride, BlurRadius);
        }

        // 5. Final composite over the full bitmap (pill + shadow + transparent).
        ShadeAndComposite(a, scratch.Output, scratch.FullW, scratch.FullH, scratch.FullStride,
                           scratch.PadX, scratch.PadY, pillW, pillH, pillStride, nmap);

        return true;
    }

    private static void ShadeAndComposite(byte[] body, byte[] dst, int fullW, int fullH, int fullStride,
                                           int padX, int padY, int pillW, int pillH, int pillStride,
                                           GlassShape.NormalMap nmap)
    {
        float[] normals = nmap.Normals;
        float[] sdf = nmap.Sdf;

        for (int y = 0; y < fullH; y++)
        {
            for (int x = 0; x < fullW; x++)
            {
                int pi = y * fullW + x;
                int dstIdx = y * fullStride + x * 4;
                float pixSdf = sdf[pi];

                if (pixSdf <= -ShadowRadius)
                {
                    // Way outside — fully transparent.
                    dst[dstIdx + 0] = 0;
                    dst[dstIdx + 1] = 0;
                    dst[dstIdx + 2] = 0;
                    dst[dstIdx + 3] = 0;
                    continue;
                }

                if (pixSdf <= 0)
                {
                    // Shadow halo. Smooth quadratic falloff from rim outward.
                    float t = -pixSdf / ShadowRadius;       // 0 at rim, 1 at outer shadow edge
                    if (t > 1f) t = 1f;
                    float falloff = 1f - t;
                    float a = falloff * falloff * ShadowOpacity;     // ease-out quadratic
                    byte alpha = (byte)(a * 255f);
                    // Premultiplied black shadow
                    dst[dstIdx + 0] = 0;
                    dst[dstIdx + 1] = 0;
                    dst[dstIdx + 2] = 0;
                    dst[dstIdx + 3] = alpha;
                    continue;
                }

                // Inside the pill. Sample the blurred body with refraction-driven offset.
                float nx = normals[pi * 3 + 0];
                float ny = normals[pi * 3 + 1];
                float nz = normals[pi * 3 + 2];

                int pillX = x - padX;
                int pillY = y - padY;
                int sx = pillX + (int)MathF.Round(nx * RefractionStrength);
                int sy = pillY + (int)MathF.Round(ny * RefractionStrength);
                if (sx < 0) sx = 0; else if (sx >= pillW) sx = pillW - 1;
                if (sy < 0) sy = 0; else if (sy >= pillH) sy = pillH - 1;
                int srcIdx = sy * pillStride + sx * 4;
                int B = body[srcIdx + 0];
                int G = body[srcIdx + 1];
                int R = body[srcIdx + 2];

                // Vibrancy: darken on bright backdrops so foreground stays legible.
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

                // Phong specular.
                float NdotH = nx * HalfVec.x + ny * HalfVec.y + nz * HalfVec.z;
                if (NdotH < 0f) NdotH = 0f;
                float spec = MathF.Pow(NdotH, SpecShininess) * SpecIntensity;

                // Schlick Fresnel rim.
                float NdotV = nz;
                if (NdotV < 0f) NdotV = 0f;
                float oneMinus = 1f - NdotV;
                float fresnel = FresnelF0 + (1f - FresnelF0) * MathF.Pow(oneMinus, FresnelExp);
                fresnel *= RimIntensity;

                int specAdd = (int)(spec * 255f);
                int rimAdd  = (int)(fresnel * 255f);

                int rOut = R + specAdd + rimAdd;
                int gOut = G + specAdd + rimAdd;
                int bOut = B + specAdd + (int)(rimAdd * 1.04f);
                if (rOut > 255) rOut = 255;
                if (gOut > 255) gOut = 255;
                if (bOut > 255) bOut = 255;

                // Anti-aliased rim alpha. WPF Bgra32 is STRAIGHT alpha — we leave the
                // RGB values as-is and let WPF blend with the backdrop based on alpha.
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

    private static void ApplyGlassWashAndSaturation(byte[] buf, int w, int h, int stride,
                                                     double washT, double saturation)
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

                B = (B * omt + 255 * it) >> 8;
                G = (G * omt + 255 * it) >> 8;
                R = (R * omt + 255 * it) >> 8;

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
                sumB += src[i + 0];
                sumG += src[i + 1];
                sumR += src[i + 2];
            }

            for (int x = 0; x < w; x++)
            {
                int oi = row + x * 4;
                dst[oi + 0] = (byte)(sumB / span);
                dst[oi + 1] = (byte)(sumG / span);
                dst[oi + 2] = (byte)(sumR / span);
                dst[oi + 3] = 255;

                int xOut = x - r;
                int xIn  = x + r + 1;
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
                sumB += src[i + 0];
                sumG += src[i + 1];
                sumR += src[i + 2];
            }

            for (int y = 0; y < h; y++)
            {
                int oi = y * stride + colOff;
                dst[oi + 0] = (byte)(sumB / span);
                dst[oi + 1] = (byte)(sumG / span);
                dst[oi + 2] = (byte)(sumR / span);
                dst[oi + 3] = 255;

                int yOut = y - r;
                int yIn  = y + r + 1;
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
}
