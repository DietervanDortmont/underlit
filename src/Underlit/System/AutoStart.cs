using System;
using System.IO;
using Microsoft.Win32;

namespace Underlit.Sys;

/// <summary>
/// Underlit auto-start at user login.
///
/// <para>v0.6.50: registers via TWO mechanisms simultaneously:</para>
/// <list type="number">
///   <item>
///     <b>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</b> — the canonical
///     per-user Run key that Task Manager → Startup apps reads from. Per-user,
///     no admin, the same mechanism every other modern tray app uses.
///   </item>
///   <item>
///     <b>%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\Underlit.lnk</b>
///     — a shortcut in the user's Startup folder. Belt-and-braces: when the
///     Run key value gets pushed past Windows' Startup-app inhibit policy or
///     the Run key has gone stale, the shortcut still fires. The single-instance
///     mutex in <c>App.xaml.cs</c> ensures only one Underlit launches if both
///     mechanisms fire simultaneously.
///   </item>
/// </list>
///
/// <para>The classic failure mode this code defends against is "Task Manager →
/// Startup apps shows Underlit Enabled, but Underlit doesn't actually launch on
/// boot." The most common cause is a stale path in the registry: the Run value
/// was written when the EXE lived at one location, then the EXE was reinstalled
/// or moved. Task Manager reads the registry value, sees a non-empty string,
/// and reports "Enabled" without verifying the path. Boot tries to launch the
/// stale path, fails silently, and the user sees no app.</para>
///
/// <para>Defense: <see cref="EnsureValid"/> is called every time Underlit
/// launches manually. It (a) verifies that the registered path equals
/// <see cref="Environment.ProcessPath"/>, (b) verifies the file exists, and
/// (c) rewrites the entry if either check fails. So one manual launch after
/// a reinstall self-heals everything for the next reboot.</para>
/// </summary>
internal static class AutoStart
{
    private const string RunKey  = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name    = "Underlit";
    private const string LnkName = "Underlit.lnk";

    /// <summary>True if EITHER mechanism currently registers Underlit. Used by
    /// the host's reconciliation logic to inherit a pre-existing user choice
    /// when settings.json is freshly defaulted.</summary>
    public static bool IsEnabled()
    {
        return GetRegisteredCommand() is { Length: > 0 } || ShortcutPath() is { } p && File.Exists(p);
    }

    /// <summary>The raw value stored under the Run key, or null if absent. May
    /// be stale (point to a nonexistent path) — pair with
    /// <see cref="GetCurrentExePath"/> to detect drift.</summary>
    public static string? GetRegisteredCommand()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(Name) as string;
    }

    /// <summary>The path of the running EXE, quoted exactly as we'd write it
    /// to the registry — so callers can do a string-equality check against
    /// <see cref="GetRegisteredCommand"/> to detect stale entries.</summary>
    public static string? GetCurrentExePath()
    {
        var exe = Environment.ProcessPath;
        return string.IsNullOrEmpty(exe) ? null : $"\"{exe}\"";
    }

    /// <summary>%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\Underlit.lnk
    /// — the per-user Startup folder shortcut path. Returns null if the
    /// %APPDATA% folder can't be resolved (rare; only on broken profiles).</summary>
    public static string? ShortcutPath()
    {
        try
        {
            string startupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            if (string.IsNullOrEmpty(startupDir)) return null;
            return Path.Combine(startupDir, LnkName);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Multi-line diagnostic string showing every piece of the
    /// auto-start state: settings flag, registry value, file existence,
    /// shortcut state, current EXE path, and StartupApproved disable bit.
    /// Surfaced in Settings → General so users can see exactly what Windows
    /// thinks of their auto-start when troubleshooting.</summary>
    public static string GetDiagnostics()
    {
        string regValue = GetRegisteredCommand() ?? "(absent)";
        string currentExe = GetCurrentExePath() ?? "(unknown)";
        string regPathOnDisk = "(n/a)";
        try
        {
            string? unquoted = UnquotePath(GetRegisteredCommand());
            if (!string.IsNullOrEmpty(unquoted))
                regPathOnDisk = File.Exists(unquoted) ? "exists" : "MISSING";
        }
        catch { regPathOnDisk = "(error)"; }

        string lnkPath = ShortcutPath() ?? "(unknown)";
        string lnkState = string.IsNullOrEmpty(ShortcutPath()) ? "(unavailable)"
            : (File.Exists(ShortcutPath()!) ? "present" : "absent");

        string approved = ReadStartupApprovedState();

        return $"Run-key value: {regValue}\n"
             + $"  → file on disk: {regPathOnDisk}\n"
             + $"Current EXE:     {currentExe}\n"
             + $"Startup folder:  {lnkState} ({lnkPath})\n"
             + $"Task Manager:    {approved}";
    }

    /// <summary>True iff the Run-key value matches <see cref="GetCurrentExePath"/>
    /// AND the file at that path exists. False positives (Run value stale or
    /// path missing) are exactly what cause the "Task Manager Enabled but
    /// nothing launches on boot" failure mode.</summary>
    public static bool IsRegistryEntryValid()
    {
        string? reg = GetRegisteredCommand();
        string? cur = GetCurrentExePath();
        if (string.IsNullOrEmpty(reg) || string.IsNullOrEmpty(cur)) return false;
        if (!string.Equals(reg, cur, StringComparison.OrdinalIgnoreCase)) return false;
        string? unquoted = UnquotePath(reg);
        return !string.IsNullOrEmpty(unquoted) && File.Exists(unquoted);
    }

    /// <summary>True iff the Startup-folder shortcut exists at the canonical
    /// path. We don't try to resolve the .lnk's target with COM here — too
    /// fragile for a one-line check. The set path always uses the running
    /// EXE, so a present shortcut is treated as valid.</summary>
    public static bool IsShortcutValid()
    {
        string? p = ShortcutPath();
        return !string.IsNullOrEmpty(p) && File.Exists(p);
    }

    /// <summary>Read the StartupApproved bit Task Manager writes when the
    /// user toggles the entry off. The first byte is 0x02 (or absent) when
    /// enabled and 0x03 when disabled. Returned as a human-readable string
    /// for the diagnostic panel.</summary>
    private static string ReadStartupApprovedState()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
            if (k?.GetValue(Name) is byte[] bytes && bytes.Length >= 1)
            {
                return bytes[0] switch
                {
                    0x02 => "enabled",
                    0x03 => "DISABLED by user (toggle in Task Manager → Startup apps)",
                    _    => $"unknown ({bytes[0]:X2})",
                };
            }
            return "enabled (no StartupApproved override)";
        }
        catch
        {
            return "(error reading)";
        }
    }

    /// <summary>
    /// Idempotent enable/disable. Writes (or deletes) BOTH the Run-key entry
    /// and the Startup-folder shortcut. Always rewrites to the current
    /// <see cref="Environment.ProcessPath"/> so a stale path from a prior
    /// install location is corrected.
    /// </summary>
    public static void Set(bool enabled)
    {
        // 1) HKCU Run key.
        SetRunKey(enabled);

        // 2) Startup-folder shortcut.
        SetStartupShortcut(enabled);
    }

    /// <summary>
    /// Self-heal: if the user has auto-start enabled and either mechanism is
    /// missing or stale, rewrite both. Cheap, runs at every launch, idempotent
    /// when nothing's wrong. Returns true iff anything was changed.
    /// </summary>
    public static bool EnsureValid(bool desired)
    {
        if (!desired)
        {
            bool changedDisable = false;
            if (GetRegisteredCommand() != null) { SetRunKey(false); changedDisable = true; }
            if (IsShortcutValid())              { SetStartupShortcut(false); changedDisable = true; }
            return changedDisable;
        }

        bool changed = false;
        if (!IsRegistryEntryValid())
        {
            Logger.Info($"AutoStart: Run-key entry was missing or stale (was: {GetRegisteredCommand() ?? "<absent>"}), refreshing");
            SetRunKey(true);
            changed = true;
        }
        if (!IsShortcutValid())
        {
            Logger.Info("AutoStart: Startup-folder shortcut was missing, creating");
            SetStartupShortcut(true);
            changed = true;
        }
        return changed;
    }

    private static void SetRunKey(bool enabled)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                     ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (k == null)
        {
            Logger.Warn("AutoStart.SetRunKey: failed to open HKCU Run key");
            return;
        }

        if (enabled)
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                Logger.Warn("AutoStart.SetRunKey(true): Environment.ProcessPath was null/empty — registry not updated");
                return;
            }
            if (!File.Exists(exe))
            {
                // Belt-and-braces: log a warning if we're somehow about to register a
                // path that doesn't exist on disk. Shouldn't happen — Environment.ProcessPath
                // is the running EXE — but worth flagging if it ever does.
                Logger.Warn($"AutoStart.SetRunKey(true): about to register path that doesn't exist on disk: {exe}");
            }
            string value = $"\"{exe}\"";
            string? prior = k.GetValue(Name) as string;
            k.SetValue(Name, value);
            if (!string.Equals(prior, value, StringComparison.OrdinalIgnoreCase))
                Logger.Info($"AutoStart: Run-key path refreshed → {value} (was: {prior ?? "<absent>"})");
        }
        else
        {
            string? prior = k.GetValue(Name) as string;
            try { k.DeleteValue(Name, throwOnMissingValue: false); } catch { /* ignore */ }
            if (prior != null) Logger.Info("AutoStart: Run-key entry removed");
        }
    }

    /// <summary>Create or remove a .lnk shortcut at
    /// %APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\Underlit.lnk
    /// pointing at the current EXE. Uses the Windows Script Host COM
    /// (WshShell), which is the only no-extra-dependency way to write a
    /// Windows shortcut from .NET. Failures are logged but don't throw —
    /// the Run-key entry is the primary mechanism, the shortcut is just
    /// belt-and-braces.</summary>
    private static void SetStartupShortcut(bool enabled)
    {
        string? path = ShortcutPath();
        if (string.IsNullOrEmpty(path)) return;

        if (!enabled)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    Logger.Info("AutoStart: Startup-folder shortcut removed");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("AutoStart: failed to delete Startup shortcut", ex);
            }
            return;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            Logger.Warn("AutoStart.SetStartupShortcut(true): ProcessPath was null/empty — shortcut not created");
            return;
        }

        try
        {
            // WScript.Shell is the standard, dependency-free way to author
            // a .lnk on Windows from .NET. Runtime-bound via the COM ProgID
            // so we don't need an interop assembly.
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                Logger.Warn("AutoStart.SetStartupShortcut: WScript.Shell COM not available");
                return;
            }
            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell == null) return;
            try
            {
                dynamic shortcut = shell.CreateShortcut(path);
                shortcut.TargetPath        = exe;
                shortcut.WorkingDirectory  = Path.GetDirectoryName(exe) ?? "";
                shortcut.WindowStyle       = 7;          // Minimised. Underlit is a tray app.
                shortcut.Description       = "Underlit — circadian brightness and warmth.";
                shortcut.IconLocation      = exe + ",0"; // First icon resource of the EXE.
                shortcut.Save();
                Logger.Info($"AutoStart: Startup-folder shortcut written → {path}");
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("AutoStart: failed to create Startup shortcut", ex);
        }
    }

    /// <summary>Strip surrounding double-quotes from a Run-key value so we
    /// can File.Exists() check the actual filesystem path.</summary>
    private static string? UnquotePath(string? cmd)
    {
        if (string.IsNullOrEmpty(cmd)) return null;
        string s = cmd.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            s = s.Substring(1, s.Length - 2);
        // Run-key values can also have args after the EXE path. We only
        // care about the EXE itself for the existence check.
        if (s.StartsWith("\"") && s.IndexOf('"', 1) is int idx && idx > 0)
            s = s.Substring(1, idx - 1);
        return s;
    }
}
