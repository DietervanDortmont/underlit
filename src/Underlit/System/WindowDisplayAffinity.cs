using System;
using System.Runtime.InteropServices;

namespace Underlit.Sys;

/// <summary>
/// SetWindowDisplayAffinity wrapper.
///
/// <para><b>Win10 2004 / build 19041+</b>: WDA_EXCLUDEFROMCAPTURE is the
/// "show on monitor, hide from capture" flag. The Win32 docs spell it out:
/// the window is displayed only on a monitor — for clients of <i>monitor
/// capture</i>, the window does not appear in the captured frame at all
/// (the capture shows whatever's underneath, NOT a black hole).</para>
///
/// <para>Which clients honour it:</para>
/// <list type="bullet">
///   <item>Windows Graphics Capture (WGC) — properly excluded. Used by
///         Snipping Tool, Win+Shift+S, Game Bar, OBS, Teams/Loom screen
///         share, most modern recorders.</item>
///   <item>DXGI Output Duplication — properly excluded.</item>
///   <item>GDI BitBlt of the desktop DC + PrintWindow — flag has NO
///         EFFECT; the window appears as-is. Legacy code path. Older
///         versions of PrtScn-to-clipboard used this; modern Windows 11
///         routes PrtScn through WGC, so the flag now applies.</item>
/// </list>
///
/// <para>Used for two things in Underlit:</para>
/// <list type="number">
///   <item>The OSD pill — so live-glass WGC frames don't capture our own
///         pill (feedback loop), and so the user's screenshots don't
///         include our floating brightness indicator.</item>
///   <item>The per-monitor OverlayWindow dim layers — so the user's
///         screenshots aren't darkened by our black-tint overlay.</item>
/// </list>
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
