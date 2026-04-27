using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Underlit.Sys;

/// <summary>
/// Drives the Liquid Glass backdrop for a WPF window (v0.3.2).
///
/// Pipeline:
///   1. BitBlt the FULL window region behind the OSD.
///   2. GlassRenderer with current GlassParams (light angle, intensity, refraction,
///      depth, dispersion, frost) outputs a 300×66 BGRA bitmap.
///   3. Update the persistent WriteableBitmap.
///
/// Live capture is still v0.4 work.
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

    /// <summary>
    /// Average luminance (0..1) of the captured pixels behind the icon zone of the
    /// pill, populated by RefreshNow(). Used by the OSD to set adaptive icon colour
    /// — dark icons on bright backdrops, light icons on dark backdrops.
    /// </summary>
    public float LastPillLuminance { get; private set; } = 0.5f;

    public LiveGlassController(Window window, ImageBrush targetBrush)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _targetBrush = targetBrush ?? throw new ArgumentNullException(nameof(targetBrush));
    }

    /// <summary>
    /// Capture and render with the given parameters.
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
            int physPadX = (int)Math.Round(PaddingDip * scale);
            int physPadY = (int)Math.Round(PaddingDip * scale);

            if (physFullW <= 0 || physFullH <= 0) return;

            int physPillH = physFullH - physPadY * 2;
            int cornerRadiusPx = parameters.CornerRadiusPx(physPillH);
            _scratch.Configure(physFullW, physFullH, physPadX, physPadY,
                                cornerRadiusPx, parameters.SquircleExponent(),
                                parameters.BevelWidthFraction(),
                                parameters.BodyCurvatureFraction());

            using var capture = ScreenCapture.CaptureRegion(physWinX, physWinY, physFullW, physFullH);

            if (!GlassRenderer.Render(capture, _scratch, parameters)) return;
            LastPillLuminance = GlassRenderer.LastPillLuminance;

            if (_bitmap == null || _bitmap.PixelWidth != physFullW || _bitmap.PixelHeight != physFullH)
            {
                // CRITICAL: bitmap DPI must match the screen DPI so that natural-DIP-size
                // of the bitmap equals the Grid's DIP size — eliminates the bilinear
                // resample that was blurring the pill rim and creating a 2-px shadow
                // halo (the "layer 2" the user kept flagging at fractional DPI).
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
