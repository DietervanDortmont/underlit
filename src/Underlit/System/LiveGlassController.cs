using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Underlit.Sys;

/// <summary>
/// Drives the Liquid Glass backdrop for a WPF window (v0.3.1).
///
/// Pipeline:
///   1. BitBlt the FULL window region behind the OSD (300×66 physical). We capture
///      MORE than just the pill so refraction at the bevel can sample pixels
///      slightly outside the pill's shape — that's optically correct, since the
///      bevel acts as a lens pulling light from beyond its edge.
///   2. GlassRenderer outputs a 300×66 BGRA bitmap with the pill rendered in
///      the centre and fully-transparent corners (no shadow halo in v0.3.1).
///   3. Assign to GlassBackdropBrush — the AllowsTransparency=true window then
///      shows just the pill, no rectangular outline.
///
/// Live capture is still v0.4 work. This remains a per-show snapshot.
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

    public LiveGlassController(Window window, ImageBrush targetBrush)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _targetBrush = targetBrush ?? throw new ArgumentNullException(nameof(targetBrush));
    }

    public void RefreshNow()
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
            int physPadX = (int)Math.Round(PaddingDip * scale);
            int physPadY = (int)Math.Round(PaddingDip * scale);
            int bevelPx  = Math.Max(1, (int)Math.Round(GlassRenderer.BevelWidthDip * scale));

            if (physFullW <= 0 || physFullH <= 0) return;

            _scratch.Configure(physFullW, physFullH, physPadX, physPadY, bevelPx);

            // Capture the full window region (so refraction at the bevel has headroom).
            using var capture = ScreenCapture.CaptureRegion(physWinX, physWinY, physFullW, physFullH);

            if (!GlassRenderer.Render(capture, _scratch)) return;

            if (_bitmap == null || _bitmap.PixelWidth != physFullW || _bitmap.PixelHeight != physFullH)
            {
                _bitmap = new WriteableBitmap(physFullW, physFullH, 96, 96, PixelFormats.Bgra32, null);
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
