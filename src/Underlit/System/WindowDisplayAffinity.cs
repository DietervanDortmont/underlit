using System;
using System.Runtime.InteropServices;

namespace Underlit.Sys;

/// <summary>
/// Thin wrapper around SetWindowDisplayAffinity to mark a window as "exclude from capture".
///
/// Why we need this for Liquid Glass:
///   The OSD captures the screen pixels behind it via BitBlt every frame to produce its
///   live-blurred backdrop. If the OSD itself appeared in those captures, we'd get a
///   visible feedback loop (an "infinite hall of mirrors" inside the flyout). The classic
///   workaround is to cloak the window via DwmSetWindowAttribute(DWMWA_CLOAK), capture,
///   then uncloak — but cloak/uncloak each frame causes flicker on most hardware.
///
///   WDA_EXCLUDEFROMCAPTURE (Windows 10 build 19041 / 2004 onwards) tells the compositor
///   to omit this window from any screen-capture operation while still rendering it
///   normally on the user's display. No flicker, no feedback loop, no cloak dance.
/// </summary>
public static class WindowDisplayAffinity
{
    private const uint WDA_NONE              = 0x0;
    private const uint WDA_MONITOR           = 0x1;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11; // Win10 2004+

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    /// <summary>
    /// Excludes the window from screen capture. Returns true on success.
    /// On older Windows builds the API is silently a no-op — caller should treat the
    /// "false" return as "live capture won't be safe; consider falling back to capture-once".
    /// </summary>
    public static bool ExcludeFromCapture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            return SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
        }
        catch
        {
            return false;
        }
    }

    public static bool ResetAffinity(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try { return SetWindowDisplayAffinity(hwnd, WDA_NONE); }
        catch { return false; }
    }

    /// <summary>True on Windows 10 build 19041 (2004) and newer.</summary>
    public static bool IsExcludeFromCaptureSupported =>
        Environment.OSVersion.Version.Major >= 10 &&
        Environment.OSVersion.Version.Build >= 19041;
}
