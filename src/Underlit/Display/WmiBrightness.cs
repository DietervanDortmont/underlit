using System;
using System.Management;

namespace Underlit.Display;

/// <summary>
/// Controls built-in laptop-panel brightness via WMI (WmiMonitorBrightness /
/// WmiMonitorBrightnessMethods) in the root\WMI namespace.
/// Only applies to the internal panel; external monitors go through DDC/CI.
/// </summary>
public static class WmiBrightness
{
    /// <summary>Returns 0–100, or null if no WMI-controllable monitor exists.</summary>
    public static int? TryGet()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject obj in searcher.Get())
            {
                return Convert.ToInt32(obj["CurrentBrightness"]);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("WMI brightness get failed", ex);
        }
        return null;
    }

    /// <summary>Set brightness 0–100. Returns true on success. Harmless no-op if no WMI monitor.</summary>
    public static bool TrySet(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject obj in searcher.Get())
            {
                // timeout=0 (apply for this session, not permanently)
                obj.InvokeMethod("WmiSetBrightness", new object[] { (uint)0, (byte)percent });
                return true;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("WMI brightness set failed", ex);
        }
        return false;
    }
}
