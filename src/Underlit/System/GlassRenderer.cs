using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace Underlit.Sys;

/// <summary>
/// Curved-glass renderer for a flat-puck-with-bevel shape (v0.3.1).
///
/// Geometric model is in GlassShape: pill SDF + per-pixel surface normal where
/// the body is flat-top and only a small bevel ring at the rim has curvature.
///
/// Per-pixel pipeline:
///   1. Sample the (blurred) backdrop with refraction offset (zero in the body,
///      proportional to normal.xy in the bevel).
///   2. Add Phong specular — bright wherever the bevel normal aligns with the
///      light's half-vector. On a flat top this is always 0, on the bevel it
///      peaks in a localised crescent. Opposite corner = dark. ✓ matches Apple.
///   3. Add Schlick Fresnel rim — only the bevel has non-vertical normals so
///      Fresnel is automatically a thin sharp band right at the rim, dying
///      off as you move into the flat top.
///   4. Vibrancy: darken on bright backdrops so foreground icons stay legible.
///   5. Anti-aliased alpha at the rim.
///
/// NO SHADOW HALO in v0.3.1 — the user asked for the OSD to "float" without
/// shadow under it. Pixels outside the pill are fully transparent.
/// </summary>
public static class GlassRenderer
{
    // ---- Constants (not user-tunable) ----
    public const double SaturationBoost   = 1.10;
    public const double GlassWashStrength = 0.06;
    public const int    BlurPasses        = 3;
    // Wider bevel = refraction visible deeper into the body (Apple's pucks have
    // visible lens warping for ~⅓ the inset, not just the rim).
    public const int    BevelWidthDip     = 10;

    public const float  SpecShininess = 25f;
    public const float  SpecIntensity = 1.6f;
    public const float  FresnelF0     = 0.04f;
    public const float  FresnelExp    = 5.0f;
    public const float  RimIntensity  = 0.55f;

    public const float  VibrancyStartLum = 0.78f;
    public const float  VibrancyMaxDark  = 0.15f;

    public const float  EdgeAaWidth    = 1.2f;

    public sealed class Scratch
    {
        // All buffers are full-window-sized (300×66 typical) — refraction at the bevel
        // can sample pixels in the padding zone (just outside the visible pill).
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
        public int BevelPx;

        public GlassShape.NormalMap? NormalMap;

        public void Configure(int fullW, int fullH, int padX, int padY, int bevelPx,
                              double bevelMaxSlope, int cornerRadiusPx)
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
            BevelPx = bevelPx;

            if (NormalMap == null
                || NormalMap.Width != fullW
                || NormalMap.Height != fullH
                || NormalMap.PadX != padX
                || NormalMap.PadY != padY
                || NormalMap.PillW != pillW
                || NormalMap.PillH != pillH
                || NormalMap.BevelPx != bevelPx
                || NormalMap.CornerRadiusPx != cornerRadiusPx
                || Math.Abs(NormalMap.BevelMaxSlope - bevelMaxSlope) > 1e-6)
            {
                NormalMap = GlassShape.ComputePill(fullW, fullH, padX, padY, pillW, pillH,
                                                     bevelPx, bevelMaxSlope, cornerRadiusPx);
            }
        }
    }

    /// <summary>
    /// Average linear-ish luminance of the captured pill region BEFORE we apply blur,
    /// expressed as 0..1. Computed as a side-effect of Render() so the caller can use
    /// it for adaptive icon contrast (light backdrop → dark icon, vice versa). Apple
    /// calls this "vibrancy" — the foreground inverts to stay legible.
    /// </summary>
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
        var nmap = scratch.NormalMap!;

        // 1. Read full-window capture into A.
        var rect = new Rectangle(0, 0, fullW, fullH);
        var data = fullCapture.LockBits(rect, ImageLockMode.ReadOnly, DPixelFormat.Format32bppArgb);
        try
        {
            if (data.Stride == fullStride)
            {
                Marshal.Copy(data.Scan0, a, 0, fullStride * fullH);
            }
            else
            {
                for (int y = 0; y < fullH; y++)
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), a, y * fullStride, fullStride);
            }
        }
        finally { fullCapture.UnlockBits(data); }

        // Compute average luminance of the captured pixels behind the pill (before
        // any saturation / blur / shading). Used for adaptive icon contrast.
        LastPillLuminance = ComputePillLuminance(a, fullW, fullH, fullStride, nmap);

        // 2 + 3. Saturation + light wash.
        ApplyGlassWashAndSaturation(a, fullW, fullH, fullStride, GlassWashStrength, SaturationBoost);

        // 4. Box blur ×3 → A. Frost is the user-tunable blur radius.
        int blurRadius = Math.Max(0, (int)Math.Round(p.Frost));
        if (blurRadius > 0)
        {
            for (int pass = 0; pass < BlurPasses; pass++)
            {
                BoxBlurHorizontal(a, b, fullW, fullH, fullStride, blurRadius);
                BoxBlurVertical  (b, a, fullW, fullH, fullStride, blurRadius);
            }
        }

        // 5. Composite — needs runtime-derived light direction etc.
        var light = p.LightDirection();
        var halfVec = NormalizeVec(light.x + 0f, light.y + 0f, light.z + 1f);
        float intensityMul = (float)p.IntensityMul();
        float refraction = (float)p.Refraction;
        float dispersion = (float)p.Dispersion;
        ShadeAndComposite(a, scratch.Output, fullW, fullH, fullStride, nmap,
                           halfVec, intensityMul, refraction, dispersion);

        return true;
    }

    private static void ShadeAndComposite(byte[] body, byte[] dst, int fullW, int fullH, int fullStride,
                                           GlassShape.NormalMap nmap,
                                           (float x, float y, float z) halfVec,
                                           float intensityMul, float refraction, float dispersion)
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

                if (pixSdf <= 0)
                {
                    // Outside the pill — fully transparent. No shadow.
                    dst[dstIdx + 0] = 0;
                    dst[dstIdx + 1] = 0;
                    dst[dstIdx + 2] = 0;
                    dst[dstIdx + 3] = 0;
                    continue;
                }

                float nx = normals[pi * 3 + 0];
                float ny = normals[pi * 3 + 1];
                float nz = normals[pi * 3 + 2];

                // Refraction sample — body buffer has full window coords.
                // With dispersion, R/G/B sample at slightly different offsets, mimicking
                // chromatic aberration of real glass: red bends least, blue bends most.
                int B, G, R;
                if (dispersion > 0.01f && (nx != 0 || ny != 0))
                {
                    // Per-channel offset scale: R = refraction - dispersion/2, B = refraction + dispersion/2
                    float refrR = refraction - dispersion * 0.5f;
                    float refrG = refraction;
                    float refrB = refraction + dispersion * 0.5f;
                    int rxR = x + (int)MathF.Round(nx * refrR);
                    int ryR = y + (int)MathF.Round(ny * refrR);
                    int rxG = x + (int)MathF.Round(nx * refrG);
                    int ryG = y + (int)MathF.Round(ny * refrG);
                    int rxB = x + (int)MathF.Round(nx * refrB);
                    int ryB = y + (int)MathF.Round(ny * refrB);
                    if (rxR < 0) rxR = 0; else if (rxR >= fullW) rxR = fullW - 1;
                    if (ryR < 0) ryR = 0; else if (ryR >= fullH) ryR = fullH - 1;
                    if (rxG < 0) rxG = 0; else if (rxG >= fullW) rxG = fullW - 1;
                    if (ryG < 0) ryG = 0; else if (ryG >= fullH) ryG = fullH - 1;
                    if (rxB < 0) rxB = 0; else if (rxB >= fullW) rxB = fullW - 1;
                    if (ryB < 0) ryB = 0; else if (ryB >= fullH) ryB = fullH - 1;
                    R = body[ryR * fullStride + rxR * 4 + 2];
                    G = body[ryG * fullStride + rxG * 4 + 1];
                    B = body[ryB * fullStride + rxB * 4 + 0];
                }
                else
                {
                    int sx = x + (int)MathF.Round(nx * refraction);
                    int sy = y + (int)MathF.Round(ny * refraction);
                    if (sx < 0) sx = 0; else if (sx >= fullW) sx = fullW - 1;
                    if (sy < 0) sy = 0; else if (sy >= fullH) sy = fullH - 1;
                    int srcIdx = sy * fullStride + sx * 4;
                    B = body[srcIdx + 0];
                    G = body[srcIdx + 1];
                    R = body[srcIdx + 2];
                }

                // Vibrancy
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

                // Phong specular — peaks where bevel normal aligns with H. Light angle
                // (and hence halfVec) is runtime-tunable.
                float NdotH = nx * halfVec.x + ny * halfVec.y + nz * halfVec.z;
                if (NdotH < 0f) NdotH = 0f;
                float spec = MathF.Pow(NdotH, SpecShininess) * SpecIntensity * intensityMul;

                // Schlick Fresnel rim — flat top has N·V = 1 → Fresnel = F0 (essentially 0).
                // Bevel ring has N·V < 1 → Fresnel rises rapidly. Auto-thin rim.
                float NdotV = nz;
                if (NdotV < 0f) NdotV = 0f;
                float oneMinus = 1f - NdotV;
                float fresnel = FresnelF0 + (1f - FresnelF0) * MathF.Pow(oneMinus, FresnelExp);
                fresnel *= RimIntensity * intensityMul;

                int specAdd = (int)(spec * 255f);
                int rimAdd  = (int)(fresnel * 255f);

                int rOut = R + specAdd + rimAdd;
                int gOut = G + specAdd + rimAdd;
                int bOut = B + specAdd + (int)(rimAdd * 1.04f);
                if (rOut > 255) rOut = 255;
                if (gOut > 255) gOut = 255;
                if (bOut > 255) bOut = 255;

                // Subtract the body Phong leak — we don't want the flat top to glow.
                // (Already small due to high shininess, but make it strictly zero.)
                if (nz > 0.999f)
                {
                    rOut = R; gOut = G; bOut = B;
                }

                // Anti-aliased rim alpha.
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

    /// <summary>
    /// Average luminance across just the LEFT THIRD of the pill region (where the
    /// status icon sits). Returns 0..1. We average only the icon zone so a busy
    /// rainbow of content over the slider doesn't pull the icon's adaptive colour
    /// off — the icon adapts to what's under the icon.
    /// </summary>
    private static float ComputePillLuminance(byte[] body, int fullW, int fullH, int fullStride,
                                                GlassShape.NormalMap nmap)
    {
        float[] sdf = nmap.Sdf;
        int leftThirdEnd = nmap.PadX + nmap.PillW / 3;

        long sumLum = 0;
        int count = 0;
        for (int y = 0; y < fullH; y++)
        {
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
        }
        return count > 0 ? (sumLum / (float)count) / 255f : 0.5f;
    }
}
