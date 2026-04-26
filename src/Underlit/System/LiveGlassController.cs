using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Underlit.Sys;

/// <summary>
/// Drives the Liquid Glass backdrop for a WPF window.
///
/// Strategy notes (v0.2.3 — pragmatic fallback after the live-loop attempt):
///
///   The "real" Apple Liquid Glass needs *true* live blur of the desktop behind
///   the window. On Windows, BitBlt of the desktop DC is the cheap option — but
///   to exclude OUR window from those captures (so we don't get a feedback loop)
///   the only flag that exists is WDA_EXCLUDEFROMCAPTURE, which is an anti-screen-
///   capture (DRM) feature: it makes the window appear as a *black rectangle* in
///   any capture, not "see-through". So a per-frame BitBlt loop captures black,
///   blurs black, and the OSD stays dark. It's a dead end.
///
///   The proper fix is Windows.Graphics.Capture or DXGI Output Duplication, both
///   of which can return the desktop minus our window via the DWM compositor.
///   That's a real interop project and is deferred.
///
///   For now we capture ONCE per Show() — when the OSD goes from hidden to
///   visible. The window isn't on screen yet so we don't need to exclude it.
///   This freezes the glass during the 1.3s display, but every press starts
///   fresh and the look is right. Switching modes back-and-forth no longer
///   leaves stale state because Start() re-asserts the brush.ImageSource.
/// </summary>
public sealed class LiveGlassController : IDisposable
{
    private readonly Window _window;
    private readonly ImageBrush _targetBrush;
    private readonly GlassRenderer.Scratch _scratch = new();

    private WriteableBitmap? _bitmap;
    private bool _disposed;

    public LiveGlassController(Window window, ImageBrush targetBrush, int targetFps = 30)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _targetBrush = targetBrush ?? throw new ArgumentNullException(nameof(targetBrush));
        // targetFps reserved for the future Windows.Graphics.Capture path.
    }

    /// <summary>
    /// Capture the screen pixels currently behind the window's location and paint them
    /// through GlassRenderer (blur + saturation + edge refraction + sheen) into the
    /// target ImageBrush.
    ///
    /// Call this BEFORE the window's Show() so the OSD itself isn't part of the BitBlt.
    /// Each call always re-asserts brush.ImageSource — important after a mode-flip
    /// nulled it out.
    /// </summary>
    public void RefreshNow()
    {
        if (_disposed) return;
        try
        {
            var src = PresentationSource.FromVisual(_window);
            double scale = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            int physW = Math.Max(1, (int)Math.Round(_window.Width  * scale));
            int physH = Math.Max(1, (int)Math.Round(_window.Height * scale));
            int physX = (int)Math.Round(_window.Left * scale);
            int physY = (int)Math.Round(_window.Top  * scale);

            using var captured = ScreenCapture.CaptureRegion(physX, physY, physW, physH);
            if (!GlassRenderer.Render(captured, _scratch)) return;

            if (_bitmap == null || _bitmap.PixelWidth != physW || _bitmap.PixelHeight != physH)
            {
                _bitmap = new WriteableBitmap(physW, physH, 96, 96, PixelFormats.Bgra32, null);
            }

            _bitmap.Lock();
            try
            {
                int totalBytes = _scratch.Stride * _scratch.Height;
                Marshal.Copy(_scratch.Output, 0, _bitmap.BackBuffer, totalBytes);
                _bitmap.AddDirtyRect(new Int32Rect(0, 0, physW, physH));
            }
            finally
            {
                _bitmap.Unlock();
            }

            // Always re-assert — ApplyVisuals nulls this out on mode flips. If we don't
            // re-assert here, the OSD shows the bare TintLayer (which looks dark) no
            // matter how many times we re-capture.
            _targetBrush.ImageSource = _bitmap;
        }
        catch (Exception ex)
        {
            Logger.Warn("Glass capture failed", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bitmap = null;
    }
}
