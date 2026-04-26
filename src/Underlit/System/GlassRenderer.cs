using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace Underlit.Sys;

/// <summary>
/// Real-deal Liquid Glass renderer. Given a captured screenshot of the pixels
/// behind the OSD's location, produces a frosted, lensed image that we render
/// behind the rest of the UI.
///
/// Pipeline (all on CPU, BGRA32):
///   1. Pre-tint    — pull the colour towards white slightly for a glass body wash.
///   2. Saturation  — boost saturation ~1.15x. Apple's glass amplifies behind-content
///                    colour for the "wet" living feel.
///   3. Box blur    — 3 passes of separable running-sum box blur. With three boxes
///                    of the right radii this approximates a true Gaussian to ~3% error
///                    (Wells-style), which is plenty for our look.
///   4. Refraction  — for every pixel within `edgeWidth` of the window border, sample
///                    the *blurred* image at an offset toward the centre. Magnitude
///                    falls off smoothly from `maxOffset` at the edge to 0. This is
///                    the visual signature of a curved glass rim — light bends inward.
///   5. Edge sheen  — add a faint white wash on the very outermost ring to suggest
///                    the bright crown of refraction Apple's glass shows.
///
/// Designed for live use at 30fps on a 280×46 region (~13k pixels) so allocations
/// happen ONCE — caller passes in scratch byte[] buffers and we use them in place.
/// </summary>
public static class GlassRenderer
{
    // ---- Visual constants tuned to match iOS Control Center / macOS Tahoe Liquid Glass ----
    //
    // I reverse-engineered these from the iOS 26 Control Center reference shots:
    //   - The blur is HEAVY — clearly more than 20 px gaussian on a 4× retina capture, so
    //     ~22 px on our 1× capture works out roughly the same perceptually.
    //   - The behind-content colour is amplified by ~30% — saturation > 1.
    //   - There is almost no white wash on the body. The blur itself plus the rim sheen
    //     do the "glassy" work; pulling the body toward white kills the see-through feel.
    //   - The rim has a clear bright crown, brighter at the top, that's about 3 px wide.
    //   - Edge refraction (light bending inward at the curved rim) is visible roughly
    //     20 px deep with a peak displacement of ~12 px — that's the "lensed pill" look.
    public const double GlassWashStrength = 0.06;   // very subtle white wash
    public const double SaturationBoost   = 1.32;   // pop the colour underneath
    public const int    BlurRadius        = 22;     // heavier gaussian — frostier
    public const int    BlurPasses        = 3;      // 3 box passes ≈ gaussian
    public const int    EdgeWidth         = 20;     // deeper rim refraction zone
    public const int    MaxOffset         = 12;     // peak refraction displacement
    public const double EdgeSheenStrength = 0.55;   // bright crown on the rim
    public const int    EdgeSheenPx       = 3;      // outer ring thickness for the sheen

    /// <summary>
    /// Buffers owned by the caller — let us avoid GC churn at 30fps.
    /// All four arrays must be at least `stride * height` bytes long.
    /// </summary>
    public sealed class Scratch
    {
        public byte[] Buffer1 = Array.Empty<byte>();
        public byte[] Buffer2 = Array.Empty<byte>();
        public byte[] Output  = Array.Empty<byte>();
        public int Stride;
        public int Width;
        public int Height;

        public void EnsureCapacity(int width, int height)
        {
            int stride = width * 4;
            int size = stride * height;
            if (Buffer1.Length < size) Buffer1 = new byte[size];
            if (Buffer2.Length < size) Buffer2 = new byte[size];
            if (Output.Length  < size) Output  = new byte[size];
            Stride = stride;
            Width  = width;
            Height = height;
        }
    }

    /// <summary>
    /// Render `input` (a captured screen region) as Liquid Glass into the scratch.Output buffer.
    /// Caller can wrap scratch.Output as a WriteableBitmap and assign it as a brush.
    ///
    /// Returns true on success. False means input had bad dimensions; output buffer is unchanged.
    /// </summary>
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

        // -------- 1. Read pixels into the working buffer A --------
        var rect = new Rectangle(0, 0, w, h);
        // We need stride to match LockBits's stride; LockBits uses rowsize = w*4 padded
        // to 4-byte alignment, which for 32bpp is exactly w*4. So scratch.Stride matches.
        var data = input.LockBits(rect, ImageLockMode.ReadOnly, DPixelFormat.Format32bppArgb);
        try
        {
            // Copy each row in case the source bitmap stride differs from ours (rare for 32bpp).
            if (data.Stride == stride)
            {
                Marshal.Copy(data.Scan0, a, 0, stride * h);
            }
            else
            {
                int srcStride = data.Stride;
                IntPtr scan = data.Scan0;
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(IntPtr.Add(scan, y * srcStride), a, y * stride, stride);
                }
            }
        }
        finally
        {
            input.UnlockBits(data);
        }

        // -------- 2. Pre-tint and saturation boost --------
        ApplyGlassWashAndSaturation(a, w, h, stride, GlassWashStrength, SaturationBoost);

        // -------- 3. Box blur, 3 passes ≈ gaussian (a → b → a → b → a → b) --------
        for (int pass = 0; pass < BlurPasses; pass++)
        {
            BoxBlurHorizontal(a, b, w, h, stride, BlurRadius);
            BoxBlurVertical  (b, a, w, h, stride, BlurRadius);
        }
        // After loop, blurred result is in `a`.

        // -------- 4. Edge refraction: write into `outBuf` from blurred `a` --------
        // Interior pixels copy through; edge pixels sample at displaced positions.
        Buffer.BlockCopy(a, 0, outBuf, 0, stride * h);
        ApplyEdgeRefraction(a, outBuf, w, h, stride, EdgeWidth, MaxOffset);

        // -------- 5. Bright crown on the outermost rim --------
        ApplyEdgeSheen(outBuf, w, h, stride, EdgeSheenPx, EdgeSheenStrength);

        return true;
    }

    // -------- Glass wash + saturation boost in one pass --------
    private static void ApplyGlassWashAndSaturation(byte[] buf, int w, int h, int stride,
                                                     double washT, double saturation)
    {
        int it  = (int)(washT * 256);
        int omt = 256 - it;
        // saturation as fixed-point ×256
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

                // 1) Saturation: lerp from grey towards full colour by `saturation`.
                // grey = luminance approximation (Rec.709-ish via simple .299/.587/.114).
                int grey = (R * 77 + G * 150 + B * 29) >> 8; // /256
                R = grey + (((R - grey) * satFixed) >> 8);
                G = grey + (((G - grey) * satFixed) >> 8);
                B = grey + (((B - grey) * satFixed) >> 8);
                if (R < 0) R = 0; else if (R > 255) R = 255;
                if (G < 0) G = 0; else if (G > 255) G = 255;
                if (B < 0) B = 0; else if (B > 255) B = 255;

                // 2) Glass wash toward white.
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

    // -------- Box blur, separable, running-sum O(N) per row --------
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

    /// <summary>
    /// Edge refraction. Pixels within `edgeWidth` of any border are replaced by samples
    /// from the blurred buffer at an offset toward the window's interior. The displacement
    /// peaks at `maxOffset` right at the rim and decays quadratically — the optical
    /// signature of a curved glass edge bending light inward.
    /// </summary>
    private static void ApplyEdgeRefraction(byte[] src, byte[] dst, int w, int h, int stride,
                                             int edgeWidth, int maxOffset)
    {
        for (int y = 0; y < h; y++)
        {
            int distTop    = y;
            int distBottom = h - 1 - y;
            for (int x = 0; x < w; x++)
            {
                int distLeft  = x;
                int distRight = w - 1 - x;

                double horizFactor = 0;
                double vertFactor  = 0;

                if (distLeft   < edgeWidth) horizFactor += Falloff(distLeft,   edgeWidth);
                if (distRight  < edgeWidth) horizFactor -= Falloff(distRight,  edgeWidth);
                if (distTop    < edgeWidth) vertFactor  += Falloff(distTop,    edgeWidth);
                if (distBottom < edgeWidth) vertFactor  -= Falloff(distBottom, edgeWidth);

                if (horizFactor == 0 && vertFactor == 0) continue;

                int sx = x + (int)Math.Round(horizFactor * maxOffset);
                int sy = y + (int)Math.Round(vertFactor  * maxOffset);
                if (sx < 0) sx = 0; else if (sx >= w) sx = w - 1;
                if (sy < 0) sy = 0; else if (sy >= h) sy = h - 1;

                int srcIdx = sy * stride + sx * 4;
                int dstIdx = y  * stride + x  * 4;
                dst[dstIdx + 0] = src[srcIdx + 0];
                dst[dstIdx + 1] = src[srcIdx + 1];
                dst[dstIdx + 2] = src[srcIdx + 2];
                dst[dstIdx + 3] = 255;
            }
        }
    }

    private static double Falloff(int dist, int edgeWidth)
    {
        if (dist >= edgeWidth) return 0;
        double t = 1.0 - (double)dist / edgeWidth;
        return t * t;
    }

    private static void ApplyEdgeSheen(byte[] buf, int w, int h, int stride, int edgePx, double strength)
    {
        if (strength <= 0 || edgePx <= 0) return;

        for (int y = 0; y < h; y++)
        {
            int distTop    = y;
            int distBottom = h - 1 - y;
            for (int x = 0; x < w; x++)
            {
                int distLeft  = x;
                int distRight = w - 1 - x;
                int minDist = Math.Min(Math.Min(distLeft, distRight), Math.Min(distTop, distBottom));
                if (minDist >= edgePx) continue;

                double t = (1.0 - (double)minDist / edgePx) * strength;
                int it = (int)(t * 256);
                int omt = 256 - it;

                int i = y * stride + x * 4;
                buf[i + 0] = (byte)((buf[i + 0] * omt + 255 * it) >> 8);
                buf[i + 1] = (byte)((buf[i + 1] * omt + 255 * it) >> 8);
                buf[i + 2] = (byte)((buf[i + 2] * omt + 255 * it) >> 8);
                buf[i + 3] = 255;
            }
        }
    }
}
