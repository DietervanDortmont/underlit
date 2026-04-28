using System;
using System.Runtime.InteropServices;

namespace Underlit.Sys;

/// <summary>
/// SetWindowDisplayAffinity wrapper. Used by the WGC live-capture path so the OSD
/// excludes itself from frames while WGC captures the monitor — the only flag/API
/// combination on Windows that gives proper "desktop minus our window" capture.
///
/// Note: WDA_EXCLUDEFROMCAPTURE behaves differently per capture method:
///   • BitBlt of desktop DC      → window appears BLACK in capture (don't use)
///   • DXGI Output Duplication   → window appears BLACK in capture (don't use)
///   • Windows.Graphics.Capture  → window is properly EXCLUDED (this is what we want)
///
/// So this flag is only safe to use in combination with WGC, not BitBlt.
/// </summary>
public static class WindowDisplayAffinity
{
    private const uint WDA_NONE              = 0x0;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11; // Win10 2004 / build 19041+

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    public static bool ExcludeFromCapture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try { return SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE); }
        catch { return false; }
    }

    public static bool ResetAffinity(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try { return SetWindowDisplayAffinity(hwnd, WDA_NONE); }
        catch { return false; }
    }
}
