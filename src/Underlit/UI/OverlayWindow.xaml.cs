using System;
using System.Windows;
using System.Windows.Interop;
using Underlit.Display;
using Underlit.Sys;

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

        // v0.6.72: hide the dim overlay from screenshots / recordings.
        // WDA_EXCLUDEFROMCAPTURE keeps the overlay visible on the monitor
        // (the user's extended-dim brightness still applies) but every
        // modern screen-capture client — Snipping Tool, Win+Shift+S, Game
        // Bar, OBS, Teams/Loom share — captures the desktop content
        // UNDER the overlay rather than the dimmed pixels. Means the
        // user no longer has to disable dim before grabbing a clean
        // screenshot.
        WindowDisplayAffinity.ExcludeFromCapture(hwnd);
    }

    public void SetDimOpacity(double opacity)
    {
        // Never go fully opaque — an all-black screen is dangerous (user can't see
        // to turn it off). Cap at 0.92.
        Opacity = Math.Clamp(opacity, 0.0, 0.92);
    }

    /// <summary>v0.6.74: re-assert HWND_TOPMOST. Some shell UI elements —
    /// notably Windows Search (Win key) and the Action Center — register
    /// themselves at a slightly higher topmost tier than normal user
    /// topmost windows and end up rendering above our dim overlay. The
    /// user sees the rest of the desktop dimmed but Search at full
    /// brightness, which looks like a bug.
    ///
    /// <para>The fix used by f.lux, Iris, Lights Out, and similar apps
    /// is exactly this: every ~500 ms while dim is active, call
    /// SetWindowPos with HWND_TOPMOST so we get re-inserted at the top
    /// of the topmost z-order group. Cheap (it's a single SWP call
    /// per monitor) and idempotent — if we're already on top the call
    /// is a no-op.</para></summary>
    public void ReassertTopmost()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(
            hwnd, NativeMethods.HWND_TOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE
            | NativeMethods.SWP_NOSIZE
            | NativeMethods.SWP_NOACTIVATE);
    }
}
