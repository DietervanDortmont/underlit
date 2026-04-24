using System;
using System.Windows;
using System.Windows.Interop;
using Underlit.Display;

namespace Underlit.UI;

/// <summary>
/// One per monitor. Transparent, click-through, topmost, no-activate.
/// Opacity is driven by the engine (0 = invisible, 0.92 = nearly fully dim).
///
/// Positioning uses SetWindowPos in physical pixels (not WPF's DIP Left/Top/Width/Height)
/// because PerMonitorV2 + mixed-DPI makes DIP math frankly fiddly. Using pixel coords
/// we match exactly what EnumDisplayMonitors gave us, which is what we want for full coverage.
/// </summary>
public partial class OverlayWindow : Window
{
    public string DeviceName { get; }
    public IntPtr HMonitor { get; private set; }
    private MonitorBounds _bounds;

    public OverlayWindow(DisplayInfo display)
    {
        InitializeComponent();
        DeviceName = display.DeviceName;
        HMonitor = display.HMonitor;
        _bounds = display.Bounds;

        // WPF still needs some Left/Top to create the window — we set it to the
        // monitor origin in pixels-treated-as-DIPs; SetWindowPos below will correct
        // any DPI-induced wrongness.
        Left   = display.Bounds.Left;
        Top    = display.Bounds.Top;
        Width  = Math.Max(1, display.Bounds.Width);
        Height = Math.Max(1, display.Bounds.Height);

        SourceInitialized += (_, _) => ApplyClickThroughStyles();
    }

    public void ApplyBounds(DisplayInfo display)
    {
        HMonitor = display.HMonitor;
        _bounds = display.Bounds;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(
                hwnd, NativeMethods.HWND_TOPMOST,
                _bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }
    }

    private void ApplyClickThroughStyles()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        ex |= NativeMethods.WS_EX_LAYERED
            | NativeMethods.WS_EX_TRANSPARENT   // click-through
            | NativeMethods.WS_EX_TOOLWINDOW    // don't show in alt-tab
            | NativeMethods.WS_EX_NOACTIVATE    // never becomes focus
            | NativeMethods.WS_EX_TOPMOST;
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex);

        // Position + size in physical pixels, regardless of DPI.
        NativeMethods.SetWindowPos(
            hwnd, NativeMethods.HWND_TOPMOST,
            _bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

        // Also force no-activate on show.
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNOACTIVATE);
    }

    public void SetDimOpacity(double opacity)
    {
        // Never go fully opaque — an all-black screen is dangerous (user can't see
        // to turn it off). Cap at 0.92.
        Opacity = Math.Clamp(opacity, 0.0, 0.92);
    }
}
