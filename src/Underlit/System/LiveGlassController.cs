using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace Underlit.Sys;

/// <summary>
/// Drives the Liquid Glass backdrop for a WPF window.
///
/// Two modes (selectable per-user via Settings → Liquid Glass → "Live capture"):
///
///   • STATIC (no yellow border): BitBlt the screen pixels behind the OSD's about-
///     to-show position once per Show(). Capture is frozen for the 1.3 s the OSD is
///     visible. No yellow border on any Windows build.
///
///   • LIVE (v0.6+): Windows.Graphics.Capture continuously captures the primary
///     monitor at ~30 fps. The OSD has WDA_EXCLUDEFROMCAPTURE set so it's
///     properly excluded from those frames (no feedback). On every render tick we
///     crop the latest monitor frame to the OSD's physical rect, run the renderer,
///     and WritePixels into the WriteableBitmap in place. Result: glass refracts
///     whatever is behind it in real time. On Win11 22H2/23H2 this shows a brief
///     yellow capture-border indicator while the OSD is visible.
///
/// If LIVE is requested but WGC fails to initialise (older Windows, missing API,
/// permissions) we silently fall back to STATIC so the OSD always works.
/// </summary>
public sealed class LiveGlassController : IDisposable
{
    public const int PaddingDip   = 10;
    public const int PillWidthDip = 280;
    public const int PillHeightDip = 46;
    public const int FullWidthDip  = PillWidthDip  + PaddingDip * 2;
    public const int FullHeightDip = PillHeightDip + PaddingDip * 2;

    private readonly Window _window;
    private readonly ImageBrush _targetBrush;
    /// <summary>v0.6.30: separate brush for the highlights overlay layer (rim +
    /// specular). Sits ABOVE the brightness bar fill in the OSD's z-order so
    /// the bar can never cover the rim/spec. Optional — if the caller passes
    /// null, the renderer's highlight buffer is simply not surfaced and the
    /// behaviour falls back to the v0.6.29 "all-in-one" body bitmap. New
    /// XAML always passes a brush, so null path is just legacy safety.</summary>
    private readonly ImageBrush? _highlightsBrush;
    private readonly GlassRenderer.Scratch _scratch = new();

    private WriteableBitmap? _bitmap;
    private WriteableBitmap? _highlightsBitmap;
    private bool _disposed;

    public float LastPillLuminance { get; private set; } = 0.5f;

    // Live mode plumbing — back to WGC after the Magnification-API detour.
    // MagCapture (left in the codebase) couldn't be made invisible AND keep capturing
    // simultaneously: cloaked or alpha=0 hosts cause DWM to skip rendering, so the
    // magnifier surface stays empty (logged as centre pixel BGRA = (240,240,240)).
    // The visible-host alternative shows a 450×99 preview rectangle, which is uglier
    // than the yellow border. WGC's border is at least time-limited to the OSD.
    private WgcCapture? _wgc;
    private GlassParams? _liveParams;
    private bool _liveSupported;
    private bool _liveActive;
    private long _lastRenderedFrameId = -1;
    /// <summary>
    /// True between BeginInvoke schedule and dispatch. Prevents the dispatcher
    /// queue from filling up if WGC delivers frames faster than the UI can render
    /// (the dropped frame is fine — the next FrameArrived will pick up the latest
    /// pixels via FrameId).
    /// </summary>
    private volatile bool _renderQueued;
    // Pre-allocated bitmap for the cropped pill region — re-used every frame
    // to avoid per-frame GC pressure (was a measurable spike before v0.6.4).
    private Bitmap? _cropBmp;
    private int _cropBmpW;
    private int _cropBmpH;

    public LiveGlassController(Window window, ImageBrush targetBrush, ImageBrush? highlightsBrush = null)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _targetBrush = targetBrush ?? throw new ArgumentNullException(nameof(targetBrush));
        _highlightsBrush = highlightsBrush;
    }

    /// <summary>
    /// Try to start the live-capture engine. Returns true if WGC initialised; false
    /// means the controller silently falls back to static capture-on-show behaviour.
    /// </summary>
    public bool TryEnableLive(IntPtr hwnd)
    {
        if (_disposed || _liveSupported) return _liveSupported;
        try
        {
            // Exclude the OSD from WGC frames so the captured monitor doesn't see
            // the OSD itself (would otherwise create a feedback loop / black hole).
            WindowDisplayAffinity.ExcludeFromCapture(hwnd);

            // WGC captures the entire monitor; we crop to the OSD rect each frame.
            IntPtr hMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTOPRIMARY);
            if (hMon == IntPtr.Zero)
                throw new InvalidOperationException("MonitorFromWindow returned NULL");

            _wgc = new WgcCapture();
            _wgc.StartForMonitor(hMon);

            _liveSupported = true;
            Logger.Info("LiveGlass: WGC initialised (yellow capture border will appear while OSD is visible)");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn("LiveGlass: WGC init failed — OSD will show empty glass until this is fixed", ex);
            try { _wgc?.Dispose(); } catch { }
            _wgc = null;
            _liveSupported = false;
            return false;
        }
    }

    /// <summary>
    /// Tear down the live-capture engine if it was initialised. Safe to call when
    /// nothing is running. After this returns, RefreshNow() (the static BitBlt
    /// path) is the only way to update the glass — no yellow border can appear.
    /// </summary>
    public void DisableLive()
    {
        if (_liveActive)
        {
            _liveActive = false;
            if (_wgc != null) _wgc.FrameArrived -= OnWgcFrameArrived;
        }
        if (_wgc != null)
        {
            try { _wgc.Dispose(); } catch { }
            _wgc = null;
        }
        _renderQueued = false;
        // Reset the OSD's display affinity — we don't need WDA_EXCLUDEFROMCAPTURE
        // anymore, and leaving it on would still apply if any other app used WGC.
        // (No-op if hwnd is unknown here — caller can re-set as needed.)
        _liveSupported = false;
        Logger.Info("LiveGlass: WGC torn down — static BitBlt path active, no yellow border");
    }

    /// <summary>
    /// Begin per-frame rendering while the OSD is visible. Caller invokes this in
    /// Flash() (see OsdWindow). Stops on its own when StopLiveTicker() is called.
    /// No-op if live mode isn't supported.
    /// </summary>
    public void StartLiveTicker(GlassParams parameters)
    {
        if (_disposed || !_liveSupported || _wgc == null) return;
        _liveParams = parameters.Clone();
        if (!_liveActive)
        {
            _liveActive = true;
            _lastRenderedFrameId = -1;
            // Subscribe BEFORE StartCapture so we don't miss the first FrameArrived.
            _wgc.FrameArrived += OnWgcFrameArrived;
            _wgc.StartCapture();
        }
    }

    public void StopLiveTicker()
    {
        if (!_liveActive) return;
        _liveActive = false;
        if (_wgc != null) _wgc.FrameArrived -= OnWgcFrameArrived;
        _wgc?.StopCapture();
        _renderQueued = false;
    }

    public void UpdateLiveParams(GlassParams parameters)
    {
        if (_liveActive) _liveParams = parameters.Clone();
    }

    /// <summary>
    /// Called on a WGC worker thread whenever a fresh frame is available. We schedule
    /// the actual render on the UI dispatcher at <see cref="DispatcherPriority.Render"/>
    /// so it runs after any pending Normal-priority work — that way the OSD's auto-hide
    /// DispatcherTimer (now Normal) always wins, even when frames pour in at 60+ fps.
    ///
    /// Coalescing: if a render is already queued, we skip — the queued render will pick
    /// up whatever the LATEST FrameId is when it runs. So we never let the UI thread
    /// fall behind the WGC producer thread.
    /// </summary>
    private void OnWgcFrameArrived()
    {
        if (_disposed || !_liveActive) return;
        if (_renderQueued) return;
        _renderQueued = true;
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Render, (Action)RenderLatestFrame);
    }

    private void RenderLatestFrame()
    {
        _renderQueued = false;
        if (_disposed || !_liveActive || _wgc == null || _liveParams == null) return;
        try
        {
            // Snapshot the frame state under FrameLock, then crop to the OSD rect.
            byte[]? frameBytes;
            int frameW, frameH, frameStride;
            long frameId;
            lock (_wgc.FrameLock)
            {
                if (_wgc.FrameId == _lastRenderedFrameId) return;
                frameBytes = _wgc.LatestFrame;
                frameW = _wgc.FrameWidth;
                frameH = _wgc.FrameHeight;
                frameStride = _wgc.FrameStride;
                frameId = _wgc.FrameId;
            }
            if (frameBytes == null) return;

            var bmp = CropMonitorToOsd(frameBytes, frameW, frameH, frameStride);
            if (bmp == null) return;
            RenderInto(bmp, _liveParams);
            _lastRenderedFrameId = frameId;
        }
        catch (Exception ex)
        {
            Logger.Warn("Live render failed", ex);
        }
    }

    /// <summary>
    /// Crop the full-monitor WGC frame to the OSD's physical pixel rect, copying
    /// into a pre-allocated reusable Bitmap to avoid per-frame GC pressure.
    /// </summary>
    private Bitmap? CropMonitorToOsd(byte[] frame, int frameW, int frameH, int frameStride)
    {
        var src = PresentationSource.FromVisual(_window);
        double scale = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        int physWinX = (int)Math.Round(_window.Left * scale);
        int physWinY = (int)Math.Round(_window.Top  * scale);
        int physFullW = (int)Math.Round(FullWidthDip  * scale);
        int physFullH = (int)Math.Round(FullHeightDip * scale);

        if (physFullW <= 0 || physFullH <= 0) return null;
        if (physWinX >= frameW || physWinY >= frameH) return null;
        if (physWinX + physFullW <= 0 || physWinY + physFullH <= 0) return null;

        // Pre-allocated reusable crop bitmap — saves a per-frame Bitmap allocation
        // and the GC spike pause that came with it.
        if (_cropBmp == null || _cropBmpW != physFullW || _cropBmpH != physFullH)
        {
            _cropBmp?.Dispose();
            _cropBmp = new Bitmap(physFullW, physFullH, DPixelFormat.Format32bppArgb);
            _cropBmpW = physFullW;
            _cropBmpH = physFullH;
        }
        var bmp = _cropBmp;
        var data = bmp.LockBits(new Rectangle(0, 0, physFullW, physFullH),
                                  ImageLockMode.WriteOnly, DPixelFormat.Format32bppArgb);
        try
        {
            // Per-row copy with clipping to the monitor frame bounds.
            for (int y = 0; y < physFullH; y++)
            {
                int srcY = physWinY + y;
                int dstRowOffset = y * data.Stride;
                if (srcY < 0 || srcY >= frameH)
                {
                    // Off-monitor row → zero-fill (will composite as background).
                    for (int xRow = 0; xRow < data.Stride; xRow++)
                        Marshal.WriteByte(data.Scan0, dstRowOffset + xRow, 0);
                    continue;
                }
                int xStart = Math.Max(0, physWinX);
                int xEnd   = Math.Min(frameW, physWinX + physFullW);
                int copyW  = xEnd - xStart;
                if (copyW <= 0) continue;
                int leftPad = xStart - physWinX;
                int srcOffset = srcY * frameStride + xStart * 4;
                Marshal.Copy(frame, srcOffset,
                             IntPtr.Add(data.Scan0, dstRowOffset + leftPad * 4),
                             copyW * 4);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }

    /// <summary>
    /// Static (one-shot) refresh: BitBlt the screen pixels at the OSD's about-to-show
    /// position into a Bitmap, run the renderer once. The capture is frozen for the
    /// 1.3 s the OSD is visible. Used when "Live capture" is OFF in settings, AND as
    /// a fallback when WGC failed to init.
    ///
    /// MUST be called BEFORE the OSD's Show() so the OSD itself isn't in the BitBlt.
    /// </summary>
    public void RefreshNow(GlassParams parameters)
    {
        if (_disposed) return;
        try
        {
            var src = PresentationSource.FromVisual(_window);
            double scale = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            int physWinX = (int)Math.Round(_window.Left * scale);
            int physWinY = (int)Math.Round(_window.Top  * scale);
            int physFullW = (int)Math.Round(FullWidthDip  * scale);
            int physFullH = (int)Math.Round(FullHeightDip * scale);
            if (physFullW <= 0 || physFullH <= 0) return;

            // Allocate a fresh Bitmap each call (it's once per Show — cheap), then
            // render through the same RenderInto path the live mode uses.
            using var capture = ScreenCapture.CaptureRegion(physWinX, physWinY, physFullW, physFullH);
            RenderInto(capture, parameters);
        }
        catch (Exception ex)
        {
            Logger.Warn("Static glass refresh failed", ex);
        }
    }

    /// <summary>Run the renderer over a captured bitmap and update the WriteableBitmap.</summary>
    private void RenderInto(Bitmap capture, GlassParams parameters)
    {
        var src = PresentationSource.FromVisual(_window);
        double scale = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        int physFullW = capture.Width;
        int physFullH = capture.Height;
        int physPadX  = (int)Math.Round(PaddingDip * scale);
        int physPadY  = (int)Math.Round(PaddingDip * scale);
        int physPillH = physFullH - physPadY * 2;
        int cornerRadiusPx = parameters.CornerRadiusPx(physPillH);
        _scratch.Configure(physFullW, physFullH, physPadX, physPadY, cornerRadiusPx);

        if (!GlassRenderer.Render(capture, _scratch, parameters)) return;
        LastPillLuminance = GlassRenderer.LastPillLuminance;

        double bitmapDpi = 96.0 * scale;
        if (_bitmap == null || _bitmap.PixelWidth != physFullW || _bitmap.PixelHeight != physFullH)
        {
            _bitmap = new WriteableBitmap(physFullW, physFullH, bitmapDpi, bitmapDpi,
                                            PixelFormats.Bgra32, null);
        }

        _bitmap.Lock();
        try
        {
            int totalBytes = _scratch.FullStride * _scratch.FullH;
            Marshal.Copy(_scratch.Output, 0, _bitmap.BackBuffer, totalBytes);
            _bitmap.AddDirtyRect(new Int32Rect(0, 0, physFullW, physFullH));
        }
        finally
        {
            _bitmap.Unlock();
        }
        _targetBrush.ImageSource = _bitmap;

        // Highlights overlay — second WriteableBitmap fed from
        // scratch.OutputHighlights. Sits ABOVE the brightness bar fill so the
        // bar can't cover the rim/spec.
        if (_highlightsBrush != null)
        {
            if (_highlightsBitmap == null
                || _highlightsBitmap.PixelWidth != physFullW
                || _highlightsBitmap.PixelHeight != physFullH)
            {
                _highlightsBitmap = new WriteableBitmap(physFullW, physFullH, bitmapDpi, bitmapDpi,
                                                          PixelFormats.Bgra32, null);
            }
            _highlightsBitmap.Lock();
            try
            {
                int totalBytes = _scratch.FullStride * _scratch.FullH;
                Marshal.Copy(_scratch.OutputHighlights, 0, _highlightsBitmap.BackBuffer, totalBytes);
                _highlightsBitmap.AddDirtyRect(new Int32Rect(0, 0, physFullW, physFullH));
            }
            finally
            {
                _highlightsBitmap.Unlock();
            }
            _highlightsBrush.ImageSource = _highlightsBitmap;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopLiveTicker();
        try { _wgc?.Dispose(); } catch { }
        _wgc = null;
        try { _cropBmp?.Dispose(); } catch { }
        _cropBmp = null;
        _bitmap = null;
        _highlightsBitmap = null;
    }

    private const int MONITOR_DEFAULTTOPRIMARY = 1;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);
}
