using System;
using System.IO;
using Microsoft.Win32;

namespace Underlit.Sys;

/// <summary>
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run entry. Per-user, no admin.
///
/// Common failure mode: the registry value contains a stale path because the EXE
/// was moved (uninstall + reinstall to a different folder, build-output drift).
/// Task Manager → Startup apps shows the entry as "Enabled" because the value
/// exists, but boot-time launch silently fails because the file isn't there.
///
/// To self-heal, callers should invoke <see cref="Set"/> at startup whenever the
/// user has auto-start enabled — that rewrites the value to the CURRENT
/// <see cref="Environment.ProcessPath"/>, so any path drift is corrected on the
/// next launch the user does manually.
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

    /// <summary>The raw value stored under the Run key, or null if absent. May be
    /// stale (point to a nonexistent path) — callers can use this for diagnostics
    /// and pair it with <see cref="GetCurrentExePath"/> to detect drift.</summary>
    public static string? GetRegisteredCommand()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(Name) as string;
    }

    /// <summary>The path of the running EXE, quoted exactly as we'd write it to
    /// the registry — so callers can do a string-equality check against
    /// <see cref="GetRegisteredCommand"/> to detect stale entries.</summary>
    public static string? GetCurrentExePath()
    {
        var exe = Environment.ProcessPath;
        return string.IsNullOrEmpty(exe) ? null : $"\"{exe}\"";
    }

    /// <summary>
    /// Write or remove the Run entry. When <paramref name="enabled"/> is true,
    /// the value is unconditionally rewritten to the current process path —
    /// callers should invoke this at startup to repair any stale path that may
    /// have drifted (uninstall/reinstall, EXE moved between folders, etc.).
    /// </summary>
    public static void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                     ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (k == null)
        {
            Logger.Warn("AutoStart.Set: failed to open HKCU Run key");
            return;
        }

        if (enabled)
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                Logger.Warn("AutoStart.Set(true): Environment.ProcessPath was null/empty — registry not updated");
                return;
            }
            if (!File.Exists(exe))
            {
                // Belt-and-braces: log a warning if we're somehow about to register a
                // path that doesn't exist on disk. Shouldn't happen — Environment.ProcessPath
                // is the running EXE — but worth flagging if it ever does.
                Logger.Warn($"AutoStart.Set(true): about to register path that doesn't exist on disk: {exe}");
            }
            string value = $"\"{exe}\"";
            string? prior = k.GetValue(Name) as string;
            k.SetValue(Name, value);
            if (!string.Equals(prior, value, StringComparison.OrdinalIgnoreCase))
                Logger.Info($"AutoStart: registry path refreshed → {value} (was: {prior ?? "<absent>"})");
        }
        else
        {
            string? prior = k.GetValue(Name) as string;
            try { k.DeleteValue(Name, throwOnMissingValue: false); } catch { /* ignore */ }
            if (prior != null) Logger.Info("AutoStart: registry entry removed");
        }
    }
}
