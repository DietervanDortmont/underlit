using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Underlit.Sys;

/// <summary>
/// Drives the Liquid Glass backdrop for a WPF window (v0.3 architecture).
///
/// In v0.3 the OSD window is 300×66 with AllowsTransparency=true. The visible
/// pill is the central 280×46; a 10-px margin around it carries the soft shadow
/// rendered into the bitmap's alpha channel.
///
///   1. We BitBlt only the PILL region (not the shadow padding) since the shadow
///      doesn't refract behind-content — it's purely synthetic.
///   2. GlassRenderer outputs a full 300×66 BGRA bitmap with pill, shadow, and
///      transparent corners.
///   3. We assign that to the GlassBackdropBrush, which fills the transparent
///      window via the GlassBackdrop Border (no CornerRadius — the alpha mask
///      defines the shape).
///
/// Live capture (v0.4): will switch the capture source to Windows.Graphics.Capture
/// so we can stream while the OSD is visible. For now this remains a per-show
/// snapshot — the pill itself is the right shape and the look is correct, just
/// not yet animated.
/// </summary>
public sealed class LiveGlassController : IDisposable
{
    /// <summary>Logical-DIP padding around the pill where the shadow lives.</summary>
    public const int PaddingDip = 10;
    public const int PillWidthDip = 280;
    public const int PillHeightDip = 46;
    public const int FullWidthDip = PillWidthDip + PaddingDip * 2;
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

    /// <summary>
    /// Capture pixels behind the pill region (NOT the shadow padding), run them
    /// through GlassRenderer, and paint into the target ImageBrush. Call BEFORE
    /// the window's Show() so the OSD itself isn't part of the BitBlt.
    /// </summary>
    public void RefreshNow()
    {
        if (_disposed) return;
        try
        {
            var src = PresentationSource.FromVisual(_window);
            double scale = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

            // Window position in physical pixels.
            int physWinX = (int)Math.Round(_window.Left * scale);
            int physWinY = (int)Math.Round(_window.Top  * scale);
            int physFullW = (int)Math.Round(FullWidthDip  * scale);
            int physFullH = (int)Math.Round(FullHeightDip * scale);
            int physPadX = (int)Math.Round(PaddingDip * scale);
            int physPadY = (int)Math.Round(PaddingDip * scale);
            int physPillW = physFullW - physPadX * 2;
            int physPillH = physFullH - physPadY * 2;

            if (physPillW <= 0 || physPillH <= 0) return;

            _scratch.Configure(physFullW, physFullH, physPadX, physPadY);

            // Capture only the pill region — the shadow doesn't refract behind-content.
            int physPillX = physWinX + physPadX;
            int physPillY = physWinY + physPadY;
            using var pillCapture = ScreenCapture.CaptureRegion(physPillX, physPillY, physPillW, physPillH);

            if (!GlassRenderer.Render(pillCapture, _scratch)) return;

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
