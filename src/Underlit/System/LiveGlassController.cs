using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Underlit.Sys;

/// <summary>
/// Drives a live Liquid Glass backdrop for a WPF window.
///
/// Each tick (throttled to ~30 fps so we don't burn CPU at 240Hz monitor refresh):
///   1. BitBlt the screen pixels behind the window's current Left/Top/Width/Height.
///   2. Run them through GlassRenderer (blur + saturation + edge refraction + sheen)
///      using a per-controller scratch buffer (zero allocation per frame).
///   3. WritePixels into a persistent WriteableBitmap.
///   4. Assign the bitmap to the target ImageBrush ONCE — subsequent updates flow
///      through automatically because WriteableBitmap notifies WPF on AddDirtyRect.
///
/// Why this works without flicker / feedback:
///   We mark the host window with WDA_EXCLUDEFROMCAPTURE so the BitBlt of the
///   desktop region simply does not contain our window's pixels. This lets us
///   capture continuously while the window is visible — no cloak/uncloak needed.
///   On Win10 builds older than 2004 the affinity call is a no-op; we still try
///   to capture but the user might see a feedback ring. The OS minimum is enforced
///   by the project's TargetFramework so this rarely matters.
/// </summary>
public sealed class LiveGlassController : IDisposable
{
    private readonly Window _window;
    private readonly ImageBrush _targetBrush;
    private readonly GlassRenderer.Scratch _scratch = new();
    private readonly TimeSpan _frameInterval;

    private WriteableBitmap? _bitmap;
    private long _lastTickTicks;
    private bool _running;
    private bool _affinitySet;
    private bool _disposed;

    public LiveGlassController(Window window, ImageBrush targetBrush, int targetFps = 30)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _targetBrush = targetBrush ?? throw new ArgumentNullException(nameof(targetBrush));
        if (targetFps < 1) targetFps = 30;
        _frameInterval = TimeSpan.FromMilliseconds(1000.0 / targetFps);
    }

    public bool IsRunning => _running;

    /// <summary>Begin per-frame capture/render. Idempotent.</summary>
    public void Start()
    {
        if (_disposed || _running) return;
        EnsureExcludeFromCapture();
        CompositionTarget.Rendering += OnRendering;
        _running = true;

        // First frame inline — guarantees the OSD doesn't show a black or stale backdrop
        // on its initial fade-in. CompositionTarget.Rendering doesn't fire until the next
        // frame, which would be ~16ms after Show().
        TryRender();
    }

    /// <summary>Stop per-frame capture. Idempotent.</summary>
    public void Stop()
    {
        if (!_running) return;
        CompositionTarget.Rendering -= OnRendering;
        _running = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _bitmap = null;
    }

    private void EnsureExcludeFromCapture()
    {
        if (_affinitySet) return;
        var helper = new WindowInteropHelper(_window);
        IntPtr hwnd = helper.Handle;
        if (hwnd != IntPtr.Zero)
        {
            WindowDisplayAffinity.ExcludeFromCapture(hwnd);
            _affinitySet = true;
        }
        // If hwnd is still Zero (window not Show()n yet), we'll retry next tick.
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        long now = DateTime.UtcNow.Ticks;
        if (now - _lastTickTicks < _frameInterval.Ticks) return;
        _lastTickTicks = now;
        TryRender();
    }

    private void TryRender()
    {
        if (_disposed) return;
        try
        {
            EnsureExcludeFromCapture();

            // Compute physical-pixel rect from the window's current logical position.
            var src = PresentationSource.FromVisual(_window);
            double scale = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            int physW = Math.Max(1, (int)Math.Round(_window.Width  * scale));
            int physH = Math.Max(1, (int)Math.Round(_window.Height * scale));
            int physX = (int)Math.Round(_window.Left * scale);
            int physY = (int)Math.Round(_window.Top  * scale);

            using var captured = ScreenCapture.CaptureRegion(physX, physY, physW, physH);
            if (!GlassRenderer.Render(captured, _scratch))
                return;

            // Lazily (re)create the WriteableBitmap when size changes — usually just once per session.
            if (_bitmap == null || _bitmap.PixelWidth != physW || _bitmap.PixelHeight != physH)
            {
                _bitmap = new WriteableBitmap(physW, physH, 96, 96, PixelFormats.Bgra32, null);
                _targetBrush.ImageSource = _bitmap;
            }

            _bitmap.Lock();
            try
            {
                int rowBytes = _scratch.Stride;
                int totalBytes = rowBytes * _scratch.Height;
                Marshal.Copy(_scratch.Output, 0, _bitmap.BackBuffer, totalBytes);
                _bitmap.AddDirtyRect(new Int32Rect(0, 0, physW, physH));
            }
            finally
            {
                _bitmap.Unlock();
            }
        }
        catch (Exception ex)
        {
            // Best-effort. Log once, don't spam.
            Logger.Warn("LiveGlassController tick failed", ex);
        }
    }
}
