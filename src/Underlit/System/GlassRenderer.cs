using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace Underlit.Sys;

/// <summary>
/// Real-deal Liquid Glass renderer. Given a captured screenshot of the pixels
/// behind the OSD's location, produces a frosted, lensed image that we render
/// behind the rest of the UI.
///
/// Pipeline (all on CPU, BGRA32):
///   1. Pre-tint    — pull the colour towards white slightly for a glass body wash.
///   2. Box blur    — 3 passes of separable running-sum box blur. With three boxes
///                    of the right radii this approximates a true Gaussian to ~3% error
///                    (Wells-style), which is plenty for our look.
///   3. Refraction  — for every pixel within `edgeWidth` of the window border, sample
///                    the *blurred* image at an offset toward the centre. Magnitude
///                    falls off smoothly from `maxOffset` at the edge to 0. This is
///                    the visual signature of a curved glass rim — light bends inward.
///   4. Edge sheen  — add a faint white wash on the very outermost ring to suggest
///                    the bright crown of refraction Apple's glass shows.
///
/// 280×48 = ~13.5k pixels — even with 3 blur passes per channel and the displacement
/// pass it runs in well under a frame on any modern CPU.
/// </summary>
public static class GlassRenderer
{
    private const double GlassWashStrength = 0.18;   // 0..1 — how much we pull pixels towards white
    private const int    BlurRadius        = 14;     // total blur radius in physical pixels
    private const int    BlurPasses        = 3;      // 3 box passes ≈ gaussian
    private const int    EdgeWidth         = 16;     // displacement zone in physical pixels
    private const int    MaxOffset         = 10;     // peak refraction displacement in physical pixels
    private const double EdgeSheenStrength = 0.35;   // 0..1 white-wash strength right on the rim

    /// <summary>
    /// Render `input` (a captured screen region) as Liquid Glass. Caller owns `input`
    /// and must Dispose it. Returns a frozen, UI-thread-safe BitmapSource.
    /// </summary>
    public static BitmapSource Render(Bitmap input)
    {
        int w = input.Width;
        int h = input.Height;
        if (w <= 0 || h <= 0)
            return CreateBlank(1, 1);

        // -------- 1. Read pixels into a managed BGRA byte buffer --------
        var rect  = new Rectangle(0, 0, w, h);
        var data  = input.LockBits(rect, ImageLockMode.ReadOnly, DPixelFormat.Format32bppArgb);
        int stride = data.Stride;
        byte[] src = new byte[stride * h];
        Marshal.Copy(data.Scan0, src, 0, src.Length);
        input.UnlockBits(data);

        // -------- 2. Pre-tint (push everything towards white for the "glass wash") --------
        ApplyGlassWash(src, w, h, stride, GlassWashStrength);

        // -------- 3. Box blur, 3 passes ≈ gaussian --------
        byte[] tmp = new byte[src.Length];
        for (int pass = 0; pass < BlurPasses; pass++)
        {
            BoxBlurHorizontal(src, tmp, w, h, stride, BlurRadius);
            BoxBlurVertical  (tmp, src, w, h, stride, BlurRadius);
        }

        // -------- 4. Edge refraction: sample-with-offset near borders --------
        byte[] refracted = new byte[src.Length];
        Buffer.BlockCopy(src, 0, refracted, 0, src.Length);
        ApplyEdgeRefraction(src, refracted, w, h, stride, EdgeWidth, MaxOffset);

        // -------- 5. Add bright crown on the very outermost rim --------
        ApplyEdgeSheen(refracted, w, h, stride, edgePx: 2, EdgeSheenStrength);

        // -------- 6. Hand back as a frozen WPF BitmapSource --------
        var bs = BitmapSource.Create(
            w, h,
            96, 96,
            PixelFormats.Bgra32,
            null,
            refracted,
            stride);
        bs.Freeze();
        return bs;
    }

    private static BitmapSource CreateBlank(int w, int h)
    {
        var bs = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        bs.Freeze();
        return bs;
    }

    // -------- Glass wash: pixel = lerp(pixel, #FFFFFF, t) --------
    private static void ApplyGlassWash(byte[] buf, int w, int h, int stride, double t)
    {
        if (t <= 0) return;
        int it = (int)(t * 256);
        int omt = 256 - it;

        for (int y = 0; y < h; y++)
        {
            int row = y * stride;
            for (int x = 0; x < w; x++)
            {
                int i = row + x * 4;
                buf[i + 0] = (byte)((buf[i + 0] * omt + 255 * it) >> 8); // B
                buf[i + 1] = (byte)((buf[i + 1] * omt + 255 * it) >> 8); // G
                buf[i + 2] = (byte)((buf[i + 2] * omt + 255 * it) >> 8); // R
                buf[i + 3] = 255;                                          // A
            }
        }
    }

    // -------- Box blur, separable, running-sum O(N) per row --------
    // Reads from `src`, writes to `dst`. Horizontal pass only.
    private static void BoxBlurHorizontal(byte[] src, byte[] dst, int w, int h, int stride, int r)
    {
        int span = r * 2 + 1;
        for (int y = 0; y < h; y++)
        {
            int row = y * stride;
            int sumB = 0, sumG = 0, sumR = 0;

            // prime the window with the first 'r+1' pixels (clamping at left edge by repeating x=0)
            for (int x = -r; x <= r; x++)
            {
                int sx = Math.Clamp(x, 0, w - 1);
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

                // slide window
                int xOut = x - r;
                int xIn  = x + r + 1;
                int sxOut = Math.Clamp(xOut, 0, w - 1);
                int sxIn  = Math.Clamp(xIn,  0, w - 1);
                int io = row + sxOut * 4;
                int ii = row + sxIn  * 4;
                sumB += src[ii + 0] - src[io + 0];
                sumG += src[ii + 1] - src[io + 1];
                sumR += src[ii + 2] - src[io + 2];
            }
        }
    }

    // -------- Box blur vertical pass --------
    private static void BoxBlurVertical(byte[] src, byte[] dst, int w, int h, int stride, int r)
    {
        int span = r * 2 + 1;
        for (int x = 0; x < w; x++)
        {
            int colOff = x * 4;
            int sumB = 0, sumG = 0, sumR = 0;

            for (int y = -r; y <= r; y++)
            {
                int sy = Math.Clamp(y, 0, h - 1);
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
                int syOut = Math.Clamp(yOut, 0, h - 1);
                int syIn  = Math.Clamp(yIn,  0, h - 1);
                int io = syOut * stride + colOff;
                int ii = syIn  * stride + colOff;
                sumB += src[ii + 0] - src[io + 0];
                sumG += src[ii + 1] - src[io + 1];
                sumR += src[ii + 2] - src[io + 2];
            }
        }
    }

    /// <summary>
    /// Edge refraction. For each pixel within `edgeWidth` of any border, we sample
    /// the blurred source at a point displaced inward by an amount that peaks at the
    /// border (`maxOffset`) and falls off smoothly to zero at `edgeWidth` deep.
    ///
    /// The displacement vector points from the nearest edge toward the window's
    /// interior — exactly what a curved glass rim does to light passing through it.
    /// </summary>
    private static void ApplyEdgeRefraction(byte[] src, byte[] dst, int w, int h, int stride,
                                             int edgeWidth, int maxOffset)
    {
        // We read from `src` (the blurred buffer) and write into `dst`. Interior pixels
        // are already copied through. We only modify pixels with minDist < edgeWidth.
        for (int y = 0; y < h; y++)
        {
            int dxLeft  = y; // unused — placeholder so I remember y is row
            for (int x = 0; x < w; x++)
            {
                int distLeft   = x;
                int distRight  = w - 1 - x;
                int distTop    = y;
                int distBottom = h - 1 - y;

                // distance to each edge separately so we can compute the displacement vector
                // pointing away from edges proportional to nearness.
                double horizFactor = 0;   // negative = sample further right; positive = further left? we'll interpret as "toward centre"
                double vertFactor  = 0;

                if (distLeft   < edgeWidth) horizFactor += Falloff(distLeft,   edgeWidth);   // need to sample further INTO image -> +x
                if (distRight  < edgeWidth) horizFactor -= Falloff(distRight,  edgeWidth);   // sample toward centre -> -x
                if (distTop    < edgeWidth) vertFactor  += Falloff(distTop,    edgeWidth);   // sample further down -> +y
                if (distBottom < edgeWidth) vertFactor  -= Falloff(distBottom, edgeWidth);   // sample further up -> -y

                if (horizFactor == 0 && vertFactor == 0) continue;  // interior — leave as-is

                int sx = Math.Clamp((int)Math.Round(x + horizFactor * maxOffset), 0, w - 1);
                int sy = Math.Clamp((int)Math.Round(y + vertFactor  * maxOffset), 0, h - 1);

                int srcIdx = sy * stride + sx * 4;
                int dstIdx = y  * stride + x  * 4;

                dst[dstIdx + 0] = src[srcIdx + 0];
                dst[dstIdx + 1] = src[srcIdx + 1];
                dst[dstIdx + 2] = src[srcIdx + 2];
                dst[dstIdx + 3] = 255;
            }
        }
    }

    /// <summary>
    /// Quadratic falloff: 1 right at the edge, 0 at edgeWidth deep into the image.
    /// </summary>
    private static double Falloff(int dist, int edgeWidth)
    {
        if (dist >= edgeWidth) return 0;
        double t = 1.0 - (double)dist / edgeWidth;
        return t * t;
    }

    /// <summary>
    /// Adds a bright white sheen to the outermost `edgePx` ring — simulates the bright
    /// halo a glass edge produces when light catches it.
    /// </summary>
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
