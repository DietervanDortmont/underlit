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
/// Two modes:
///
///   • LEGACY (fallback): BitBlt the screen pixels behind the OSD before each Show().
///     The capture is frozen for the 1.3 s the OSD is visible. This is the bulletproof
///     path that works on every Windows configuration.
///
///   • LIVE (v0.6+): Windows.Graphics.Capture continuously captures the primary
///     monitor at ~30 fps. The OSD has WDA_EXCLUDEFROMCAPTURE set so it's
///     properly excluded from those frames (no feedback). On every render tick we
///     crop the latest monitor frame to the OSD's physical rect, run the renderer,
///     and WritePixels into the WriteableBitmap in place. Result: glass refracts
///     whatever is behind it in real time.
///
/// If WGC fails to initialise (older Windows, missing API, permissions) we silently
/// fall back to LEGACY so the OSD always works — this is alpha-stage code and we
/// don't want a single bug in WGC interop to brick the app.
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
    private readonly GlassRenderer.Scratch _scratch = new();

    private WriteableBitmap? _bitmap;
    private bool _disposed;

    public float LastPillLuminance { get; private set; } = 0.5f;

    // Live mode plumbing.
    private WgcCapture? _wgc;
    private DispatcherTimer? _liveTicker;
    private GlassParams? _liveParams;
    private bool _liveSupported;
    private bool _liveActive;

    public LiveGlassController(Window window, ImageBrush targetBrush)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _targetBrush = targetBrush ?? throw new ArgumentNullException(nameof(targetBrush));
    }

    /// <summary>
    /// Try to start the live-capture engine. Returns true if WGC initialised; false
    /// means the controller silently falls back to legacy capture-on-show behaviour.
    /// </summary>
    public bool TryEnableLive(IntPtr hwnd)
    {
        if (_disposed || _liveSupported) return _liveSupported;
        try
        {
            // Exclude the OSD from its own capture. WGC honours this flag (BitBlt
            // doesn't), so the captured monitor frames will show desktop pixels
            // behind the OSD instead of a black silhouette.
            WindowDisplayAffinity.ExcludeFromCapture(hwnd);

            IntPtr hMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTOPRIMARY);
            if (hMon == IntPtr.Zero) hMon = MonitorFromWindow(IntPtr.Zero, MONITOR_DEFAULTTOPRIMARY);

            _wgc = new WgcCapture();
            _wgc.StartForMonitor(hMon);
            _liveSupported = true;
            Logger.Info("LiveGlass: WGC capture started");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn("LiveGlass: WGC init failed, falling back to capture-on-show", ex);
            try { _wgc?.Dispose(); } catch { }
            _wgc = null;
            _liveSupported = false;
            return false;
        }
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
        if (_liveTicker == null)
        {
            _liveTicker = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(33),     // ~30 fps
            };
            _liveTicker.Tick += OnLiveTick;
        }
        if (!_liveActive)
        {
            _liveActive = true;
            _liveTicker.Start();
            // Render the first frame inline so the OSD doesn't fade in over a stale image.
            OnLiveTick(this, EventArgs.Empty);
        }
    }

    public void StopLiveTicker()
    {
        if (!_liveActive) return;
        _liveActive = false;
        _liveTicker?.Stop();
    }

    public void UpdateLiveParams(GlassParams parameters)
    {
        if (_liveActive) _liveParams = parameters.Clone();
    }

    private void OnLiveTick(object? sender, EventArgs e)
    {
        if (_disposed || !_liveActive || _wgc == null || _liveParams == null) return;
        try
        {
            byte[]? frameBytes;
            int frameW, frameH, frameStride;
            lock (_wgc.FrameLock)
            {
                frameBytes = _wgc.LatestFrame;
                frameW = _wgc.FrameWidth;
                frameH = _wgc.FrameHeight;
                frameStride = _wgc.FrameStride;
            }
            if (frameBytes == null || frameW <= 0 || frameH <= 0) return;

            using var crop = CropMonitorToOsd(frameBytes, frameW, frameH, frameStride);
            if (crop == null) return;
            RenderInto(crop, _liveParams);
        }
        catch (Exception ex)
        {
            Logger.Warn("Live tick failed", ex);
        }
    }

    /// <summary>
    /// Build a Bitmap of the area behind the OSD by copying that sub-rect out of the
    /// latest WGC monitor frame. Returns null if the OSD's rect is fully off-screen.
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

        // Output bitmap is the FULL window region (including the 10-px shadow padding
        // around the pill). The renderer needs that padding for refraction headroom.
        var bmp = new Bitmap(physFullW, physFullH, DPixelFormat.Format32bppArgb);
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
    /// In v0.6.2 the BitBlt-on-show fallback is gone — the OSD is intentionally
    /// LIVE-ONLY now. If WGC failed to initialise, this method is a no-op and the
    /// OSD's GlassBackdrop will simply be blank (so the failure is visible rather
    /// than masked by a static-capture path that "kicks in" without being asked).
    ///
    /// Diagnose any WGC failure by inspecting %LOCALAPPDATA%\Underlit\underlit.log
    /// — WgcCapture.StartForMonitor logs every step it succeeds at, so the last
    /// "WGC: ..." line tells you exactly where init died.
    /// </summary>
    public void RefreshNow(GlassParams parameters)
    {
        // Intentionally empty. See doc comment.
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

        if (_bitmap == null || _bitmap.PixelWidth != physFullW || _bitmap.PixelHeight != physFullH)
        {
            double bitmapDpi = 96.0 * scale;
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
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopLiveTicker();
        if (_liveTicker != null)
        {
            _liveTicker.Tick -= OnLiveTick;
            _liveTicker = null;
        }
        try { _wgc?.Dispose(); } catch { }
        _wgc = null;
        _bitmap = null;
    }

    private const int MONITOR_DEFAULTTOPRIMARY = 1;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);
}
