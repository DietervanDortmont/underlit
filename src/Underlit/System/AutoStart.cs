using System;
using Microsoft.Win32;

namespace Underlit.Sys;

/// <summary>
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run entry. Per-user, no admin.
/// </summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name   = "Underlit";

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(Name) is string s && !string.IsNullOrWhiteSpace(s);
    }

    public static void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                     ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (k == null) return;

        if (enabled)
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            k.SetValue(Name, $"\"{exe}\"");
        }
        else
        {
            try { k.DeleteValue(Name, throwOnMissingValue: false); } catch { /* ignore */ }
        }
    }
}
