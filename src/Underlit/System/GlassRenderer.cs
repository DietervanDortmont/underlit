using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace Underlit.Sys;

/// <summary>
/// Curved-glass renderer. Models the OSD as a hemispherical-pill lens and produces
/// the look of light passing through it from a fixed 45° top-left key light.
///
/// Pipeline (BGRA32, all CPU):
///   1.  Read captured pixels into buffer A.
///   2.  Saturation boost — Apple's glass amplifies behind-content colour ~30%.
///   3.  Subtle white wash — a touch of "frostiness" so the body isn't aggressively
///       see-through. This matches the user's note that real Apple glass *does*
///       have a tiny amount of frost.
///   4.  Box blur ×3 ≈ gaussian (radius 18) — the body frosting.
///   5.  Per-pixel pass using GlassShape's normal map:
///        a. Refraction: sample the BLURRED buffer at (x,y) + (nx,ny) * thickness.
///           This is real curved-lens refraction — pixels in the centre sample at
///           themselves (zero displacement), rim pixels sample from far inside.
///        b. Phong specular: pow(N·H, shininess) where H = halfway(L, V) for a
///           45° top-left key light. Bright crescent on the upper-left curve.
///        c. Schlick Fresnel rim: F0 + (1-F0) * (1 - N·V)^5. Bright ring at
///           grazing angles.
///        d. Vibrancy: behind a very bright backdrop the glass body is too pale
///           for icons/text to show — pull the body a notch toward neutral grey
///           when its luminance crosses a threshold so foreground stays legible.
///        e. Composite: result = body + spec + fresnel * rim_color.
///
/// Tuning notes — values were eyeballed against the iOS 26 Control Center
/// reference shots the user shared. The bright pill in the middle of those shots
/// has clearly more energy on the upper-left rim than anywhere else, the body is
/// roughly 80% transparent with visible refraction, and the spec is concentrated
/// in a ~30° crescent — not a soft glow.
/// </summary>
public static class GlassRenderer
{
    // ---- Body / blur ----
    public const double SaturationBoost   = 1.32;
    public const double GlassWashStrength = 0.06;
    public const int    BlurRadius        = 18;
    public const int    BlurPasses        = 3;

    // ---- Refraction (curved-lens displacement) ----
    public const float  RefractionStrength = 18f;     // peak displacement in physical pixels at the rim

    // ---- Lighting ----
    // Light direction (from surface toward source). Top-left, slightly forward.
    private static readonly (float x, float y, float z) LightDir = NormalizeVec(-0.55f, -0.70f, 0.45f);
    // View direction: viewer is straight on (out of the screen toward us).
    private static readonly (float x, float y, float z) ViewDir  = (0f, 0f, 1f);
    // Half vector for Blinn-Phong.
    private static readonly (float x, float y, float z) HalfVec  = NormalizeVec(LightDir.x + ViewDir.x,
                                                                                  LightDir.y + ViewDir.y,
                                                                                  LightDir.z + ViewDir.z);
    public const float  SpecShininess  = 50f;          // 30=soft glow, 100=tiny pinpoint. 50 = crisp crescent.
    public const float  SpecIntensity  = 1.10f;        // additive spec colour multiplier
    public const float  FresnelF0      = 0.04f;        // base reflectivity of glass
    public const float  FresnelExp     = 5.0f;         // Schlick exponent
    public const float  RimIntensity   = 0.85f;        // how much rim brightening to add

    // ---- Vibrancy (contrast restoration over very bright backgrounds) ----
    public const float  VibrancyStartLum = 0.78f;      // luminance above which we start darkening
    public const float  VibrancyMaxDark  = 0.18f;      // peak darkening factor (0 = none, 1 = full black)

    public sealed class Scratch
    {
        public byte[] Buffer1 = Array.Empty<byte>();
        public byte[] Buffer2 = Array.Empty<byte>();
        public byte[] Output  = Array.Empty<byte>();
        public int Stride;
        public int Width;
        public int Height;
        public GlassShape.NormalMap? NormalMap;

        public void EnsureCapacity(int width, int height)
        {
            int stride = width * 4;
            int size = stride * height;
            if (Buffer1.Length < size) Buffer1 = new byte[size];
            if (Buffer2.Length < size) Buffer2 = new byte[size];
            if (Output.Length  < size) Output  = new byte[size];
            Stride = stride;
            // Recompute the normal map only when dimensions change — it's pure geometry,
            // independent of the captured content.
            if (Width != width || Height != height || NormalMap == null)
            {
                Width = width;
                Height = height;
                NormalMap = GlassShape.ComputePill(width, height);
            }
        }
    }

    public static bool Render(Bitmap input, Scratch scratch)
    {
        int w = input.Width;
        int h = input.Height;
        if (w <= 0 || h <= 0) return false;

        scratch.EnsureCapacity(w, h);
        int stride = scratch.Stride;
        byte[] a = scratch.Buffer1;
        byte[] b = scratch.Buffer2;
        byte[] outBuf = scratch.Output;
        var nmap = scratch.NormalMap!;

        // 1. Read pixels into A.
        var rect = new Rectangle(0, 0, w, h);
        var data = input.LockBits(rect, ImageLockMode.ReadOnly, DPixelFormat.Format32bppArgb);
        try
        {
            if (data.Stride == stride)
            {
                Marshal.Copy(data.Scan0, a, 0, stride * h);
            }
            else
            {
                for (int y = 0; y < h; y++)
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), a, y * stride, stride);
            }
        }
        finally { input.UnlockBits(data); }

        // 2 + 3. Saturation boost + light white wash.
        ApplyGlassWashAndSaturation(a, w, h, stride, GlassWashStrength, SaturationBoost);

        // 4. Box blur ×3 ≈ gaussian. After loop the blurred result is in A.
        for (int pass = 0; pass < BlurPasses; pass++)
        {
            BoxBlurHorizontal(a, b, w, h, stride, BlurRadius);
            BoxBlurVertical  (b, a, w, h, stride, BlurRadius);
        }

        // 5. Per-pixel curved-glass shading: refraction + spec + Fresnel + vibrancy.
        ShadeAndComposite(a, outBuf, w, h, stride, nmap);

        return true;
    }

    /// <summary>
    /// Per-pixel curved-glass shader. Reads the blurred body from `body`, samples it
    /// with refraction-driven offsets, adds Phong specular and Schlick Fresnel from
    /// the precomputed normal map, and writes into `dst`.
    /// </summary>
    private static void ShadeAndComposite(byte[] body, byte[] dst, int w, int h, int stride,
                                          GlassShape.NormalMap nmap)
    {
        float[] normals = nmap.Normals;
        bool[] inside = nmap.Inside;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int pi = y * w + x;
                int dstIdx = y * stride + x * 4;

                if (!inside[pi])
                {
                    // Outside the pill — fully transparent. (DWM corner clipping will hide this anyway.)
                    dst[dstIdx + 0] = 0;
                    dst[dstIdx + 1] = 0;
                    dst[dstIdx + 2] = 0;
                    dst[dstIdx + 3] = 0;
                    continue;
                }

                float nx = normals[pi * 3 + 0];
                float ny = normals[pi * 3 + 1];
                float nz = normals[pi * 3 + 2];

                // ---- (a) Refraction sampling ----
                // For a glass surface viewed straight-on, the apparent shift of pixels
                // behind the surface is roughly proportional to the normal's xy component
                // (Snell's law linearised at small angles). RefractionStrength scales it.
                int sx = x + (int)MathF.Round(nx * RefractionStrength);
                int sy = y + (int)MathF.Round(ny * RefractionStrength);
                if (sx < 0) sx = 0; else if (sx >= w) sx = w - 1;
                if (sy < 0) sy = 0; else if (sy >= h) sy = h - 1;
                int srcIdx = sy * stride + sx * 4;
                int B = body[srcIdx + 0];
                int G = body[srcIdx + 1];
                int R = body[srcIdx + 2];

                // ---- (d) Vibrancy: if backdrop is very bright, darken slightly so
                // overlay icons/text remain legible. Apple does this on a per-element
                // basis ("vibrancy" effect). We approximate with a luminance-aware tint.
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

                // ---- (b) Phong specular ----
                float NdotH = nx * HalfVec.x + ny * HalfVec.y + nz * HalfVec.z;
                if (NdotH < 0f) NdotH = 0f;
                float spec = MathF.Pow(NdotH, SpecShininess) * SpecIntensity;

                // ---- (c) Schlick Fresnel ----
                float NdotV = nz; // ViewDir = (0,0,1)
                if (NdotV < 0f) NdotV = 0f;
                float oneMinus = 1f - NdotV;
                float fresnel = FresnelF0 + (1f - FresnelF0) * MathF.Pow(oneMinus, FresnelExp);
                fresnel *= RimIntensity;

                // ---- (e) Composite: body + additive spec + additive rim ----
                // Rim adds a cool-white wash; spec adds a warm-white pinpoint highlight.
                int specAdd = (int)(spec * 255f);
                int rimAdd  = (int)(fresnel * 255f);

                int rOut = R + specAdd + rimAdd;
                int gOut = G + specAdd + rimAdd;
                int bOut = B + specAdd + (int)(rimAdd * 1.04f); // tiny blue bias on rim
                if (rOut > 255) rOut = 255;
                if (gOut > 255) gOut = 255;
                if (bOut > 255) bOut = 255;

                // Alpha is full inside — the captured-image-on-DWM hybrid is for v0.3.
                dst[dstIdx + 0] = (byte)bOut;
                dst[dstIdx + 1] = (byte)gOut;
                dst[dstIdx + 2] = (byte)rOut;
                dst[dstIdx + 3] = 255;
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
