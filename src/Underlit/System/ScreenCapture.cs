using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace Underlit.Sys;

/// <summary>
/// BitBlt-based screen capture. We use Graphics.CopyFromScreen which under the hood
/// is a BitBlt on the desktop DC into a memory DC. Cheap on a single 280x48 region.
///
/// Used by the LiquidGlass OSD to grab the actual pixels currently behind the window
/// so we can blur and refract them locally — the only way to get true Apple-style
/// glass on Windows since DWM doesn't expose its own blurred backdrop pixels.
/// </summary>
public static class ScreenCapture
{
    /// <summary>
    /// Captures a rectangle of the screen in physical (device) pixels.
    /// Returns a freshly-allocated 32bpp ARGB Bitmap which the caller owns and must Dispose.
    /// </summary>
    public static Bitmap CaptureRegion(int physX, int physY, int physW, int physH)
    {
        if (physW <= 0) physW = 1;
        if (physH <= 0) physH = 1;

        var bmp = new Bitmap(physW, physH, PixelFormat.Format32bppArgb);
        try
        {
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(physX, physY, 0, 0, new Size(physW, physH), CopyPixelOperation.SourceCopy);
        }
        catch
        {
            // Capture can fail under fast user-switch / lock screen / RDP transitions.
            // Caller will treat a black bitmap as "no glass available" and fall back.
        }
        return bmp;
    }
}
