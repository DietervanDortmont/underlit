using System;
using System.Collections.Generic;

namespace Underlit.Display;

/// <summary>
/// Enumerates all active monitors. A fresh enumeration should be done whenever
/// Windows fires a display-change event (hot-plug, DPI change, resolution change).
/// </summary>
public static class DisplayManager
{
    public static List<DisplayInfo> Enumerate()
    {
        var list = new List<DisplayInfo>();

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref NativeMethods.RECT _, IntPtr _) =>
        {
            var info = new NativeMethods.MONITORINFOEX
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>()
            };
            if (NativeMethods.GetMonitorInfo(hMon, ref info))
            {
                var r = info.rcMonitor;
                list.Add(new DisplayInfo
                {
                    HMonitor = hMon,
                    DeviceName = info.szDevice ?? "",
                    Bounds = new MonitorBounds(r.left, r.top, r.Width, r.Height),
                    IsPrimary = (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0
                });
            }
            return true; // continue enumeration
        }, IntPtr.Zero);

        return list;
    }
}
