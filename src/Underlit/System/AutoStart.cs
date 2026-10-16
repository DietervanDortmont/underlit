using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace Underlit.Sys;

/// <summary>
/// Underlit auto-start at user login.
///
/// <para>v0.6.52: dual-mechanism, both pointing at the same EXE, but in
/// places that don't duplicate in Task Manager.</para>
///
/// <list type="number">
///   <item>
///     <b>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</b> — the canonical
///     per-user Run key. This is what the Inno Setup installer writes and what
///     Task Manager → Startup apps reads from. Visible to the user.
///   </item>
///   <item>
///     <b>Scheduled Task at <c>\Underlit\Autostart</c></b> — a Task Scheduler
///     entry with an "At log on" trigger for the current user. Tasks living in
///     a custom folder (not the root or <c>\Microsoft\Windows\</c>) are NOT
///     surfaced in Task Manager → Startup apps, so this doesn't appear as a
///     second entry next to the Run-key. The scheduler service processes this
///     trigger independently of the Explorer / Run-key path, so anti-virus or
///     reputation gates that silently swallow Run-key launches generally don't
///     touch it. The single-instance mutex in <c>App.xaml.cs</c> guarantees
///     only ONE Underlit ends up running even when both mechanisms fire.
///   </item>
/// </list>
///
/// <para>The classic failure mode this code defends against is "Task Manager →
/// Startup apps shows Underlit Enabled, but Underlit doesn't actually launch on
/// boot." Two common causes: a stale path in the registry (the Run value was
/// written when the EXE lived elsewhere; Task Manager just sees the entry and
/// reports Enabled without verifying the path), and Windows or AV silently
/// gating Run-key launches. The Scheduled Task backstop covers the second.</para>
///
/// <para>Defense: <see cref="EnsureValid"/> is called every time Underlit
/// launches. It (a) verifies that the registered Run-key path equals
/// <see cref="Environment.ProcessPath"/>, (b) verifies the file exists, (c)
/// rewrites the entry if either check fails, and (d) registers / refreshes the
/// Scheduled Task. So one manual launch after a reinstall self-heals
/// everything for the next reboot.</para>
///
/// <para>Dev-build guard (v0.6.51): if the running EXE is under a
/// <c>bin\Debug</c> or <c>bin\Release</c> path, we DON'T rewrite either
/// mechanism. That prevents <c>scripts/run.ps1</c> from clobbering the
/// installed copy's autostart entries every time the developer iterates.</para>
/// </summary>
internal static class AutoStart
{
    private const string RunKey         = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name           = "Underlit";
    private const string LnkName        = "Underlit.lnk";
    private const string TaskFolder     = "Underlit";
    private const string TaskName       = "Autostart";
    private static string TaskPath => $"\\{TaskFolder}\\{TaskName}";

    /// <summary>True if EITHER autostart mechanism is currently registered.
    /// Used by the host's reconciliation logic to inherit a pre-existing
    /// user choice when settings.json is freshly defaulted.</summary>
    public static bool IsEnabled()
    {
        return GetRegisteredCommand() is { Length: > 0 } || IsScheduledTaskRegistered();
    }

    /// <summary>The raw value stored under the Run key, or null if absent.
    /// May be stale (point to a nonexistent path) — pair with
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

    /// <summary>True iff the running EXE lives under a bin\Debug or bin\Release
    /// folder. We detect this so the dev iteration loop (scripts/run.ps1)
    /// doesn't accidentally rewrite the user's installed-build autostart entry
    /// to a transient bin path.</summary>
    public static bool IsDevBuild()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;
        string lower = exe.Replace('/', '\\').ToLowerInvariant();
        return lower.Contains(@"\bin\debug\") || lower.Contains(@"\bin\release\");
    }

    /// <summary>%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\Underlit.lnk
    /// — only used to remove stale shortcuts left over from v0.6.50.</summary>
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

    /// <summary>Multi-line diagnostic string surfaced in Settings → General
    /// so users can see exactly what Windows thinks of their autostart when
    /// troubleshooting.</summary>
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

        string approved = ReadStartupApprovedState();
        string devNote  = IsDevBuild()
            ? "yes (dev-build guard active, autostart writes are skipped)"
            : "no";

        string lnk = ShortcutPath() is { } p && File.Exists(p)
            ? $"present at {p} (will be cleaned up)"
            : "absent (good)";

        string task = IsScheduledTaskRegistered()
            ? $"registered at {TaskPath}"
            : "NOT registered";

        return $"Run-key value: {regValue}\n"
             + $"  -> file on disk: {regPathOnDisk}\n"
             + $"Current EXE:     {currentExe}\n"
             + $"Dev build:       {devNote}\n"
             + $"Legacy shortcut: {lnk}\n"
             + $"Scheduled task:  {task}\n"
             + $"Task Manager:    {approved}";
    }

    /// <summary>True iff the Run-key value matches <see cref="GetCurrentExePath"/>
    /// AND the file at that path exists.</summary>
    public static bool IsRegistryEntryValid()
    {
        string? reg = GetRegisteredCommand();
        string? cur = GetCurrentExePath();
        if (string.IsNullOrEmpty(reg) || string.IsNullOrEmpty(cur)) return false;
        if (!string.Equals(reg, cur, StringComparison.OrdinalIgnoreCase)) return false;
        string? unquoted = UnquotePath(reg);
        return !string.IsNullOrEmpty(unquoted) && File.Exists(unquoted);
    }

    /// <summary>Read the StartupApproved bit Task Manager writes when the
    /// user toggles the entry off. The first byte is 0x02 (or absent) when
    /// enabled and 0x03 when disabled.</summary>
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
                    0x03 => "DISABLED by user (toggle in Task Manager -> Startup apps)",
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

    // -------------------------------------------------------------------------
    // Scheduled Task mechanism (v0.6.52)
    // -------------------------------------------------------------------------

    /// <summary>True iff our Scheduled Task is registered. Implemented via
    /// schtasks.exe /Query so we don't take a NuGet dependency on
    /// Microsoft.Win32.TaskScheduler. Exit code 0 = task exists.</summary>
    public static bool IsScheduledTaskRegistered()
    {
        try
        {
            return RunSchtasks($"/Query /TN \"{TaskFolder}\\{TaskName}\"") == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Register (or overwrite) the autostart Scheduled Task pointing at the
    /// current EXE. Builds a Task Scheduler v1.2 XML descriptor and feeds it
    /// to <c>schtasks /Create /XML</c>, which is the most portable way to
    /// configure the full set of options we want (no AC-power gating, no
    /// 72-hour execution limit, runs interactively as the user, etc.).
    /// </summary>
    private static bool RegisterScheduledTask()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            Logger.Warn("AutoStart.RegisterScheduledTask: ProcessPath null/empty/missing");
            return false;
        }

        // UserDomainName is the actual NETBIOS domain for AD-joined boxes,
        // or the local computer name on home machines. Either way, the
        // "DOMAIN\user" form is what Task Scheduler expects in <UserId>.
        string user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        string workingDir = Path.GetDirectoryName(exe) ?? "";

        // Escape for embedding in XML. Path components rarely contain &<>"',
        // but better safe.
        static string E(string s) => System.Security.SecurityElement.Escape(s) ?? "";

        string xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n"
            + "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n"
            + "  <RegistrationInfo>\r\n"
            + $"    <Description>Underlit autostart - launches Underlit when {E(user)} signs in.</Description>\r\n"
            + "    <Author>Underlit</Author>\r\n"
            + "  </RegistrationInfo>\r\n"
            + "  <Triggers>\r\n"
            + "    <LogonTrigger>\r\n"
            + "      <Enabled>true</Enabled>\r\n"
            + $"      <UserId>{E(user)}</UserId>\r\n"
            + "    </LogonTrigger>\r\n"
            + "  </Triggers>\r\n"
            + "  <Principals>\r\n"
            + "    <Principal id=\"Author\">\r\n"
            + $"      <UserId>{E(user)}</UserId>\r\n"
            + "      <LogonType>InteractiveToken</LogonType>\r\n"
            + "      <RunLevel>LeastPrivilege</RunLevel>\r\n"
            + "    </Principal>\r\n"
            + "  </Principals>\r\n"
            + "  <Settings>\r\n"
            + "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n"
            + "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n"
            + "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n"
            + "    <AllowHardTerminate>false</AllowHardTerminate>\r\n"
            + "    <StartWhenAvailable>true</StartWhenAvailable>\r\n"
            + "    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\r\n"
            + "    <IdleSettings>\r\n"
            + "      <StopOnIdleEnd>false</StopOnIdleEnd>\r\n"
            + "      <RestartOnIdle>false</RestartOnIdle>\r\n"
            + "    </IdleSettings>\r\n"
            + "    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n"
            + "    <Enabled>true</Enabled>\r\n"
            + "    <Hidden>false</Hidden>\r\n"
            + "    <RunOnlyIfIdle>false</RunOnlyIfIdle>\r\n"
            + "    <WakeToRun>false</WakeToRun>\r\n"
            + "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n"
            + "    <Priority>7</Priority>\r\n"
            + "  </Settings>\r\n"
            + "  <Actions Context=\"Author\">\r\n"
            + "    <Exec>\r\n"
            + $"      <Command>{E(exe)}</Command>\r\n"
            + $"      <WorkingDirectory>{E(workingDir)}</WorkingDirectory>\r\n"
            + "    </Exec>\r\n"
            + "  </Actions>\r\n"
            + "</Task>";

        // schtasks expects the XML in UTF-16 (Unicode). Write to a temp
        // file, run /Create /XML, then clean up.
        string tempXml = Path.Combine(Path.GetTempPath(), $"underlit-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(tempXml, xml, Encoding.Unicode);
            int exit = RunSchtasks(
                $"/Create /TN \"{TaskFolder}\\{TaskName}\" /XML \"{tempXml}\" /F",
                out string err);
            if (exit != 0)
            {
                Logger.Warn($"AutoStart: schtasks /Create exit {exit}: {err.Trim()}");
                return false;
            }
            Logger.Info($"AutoStart: scheduled task registered at {TaskPath} -> {exe}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn("AutoStart: failed to register scheduled task", ex);
            return false;
        }
        finally
        {
            try { File.Delete(tempXml); } catch { /* ignore */ }
        }
    }

    /// <summary>Remove the autostart Scheduled Task. No-op if absent.</summary>
    private static bool UnregisterScheduledTask()
    {
        if (!IsScheduledTaskRegistered()) return false;
        try
        {
            int exit = RunSchtasks(
                $"/Delete /TN \"{TaskFolder}\\{TaskName}\" /F",
                out string err);
            if (exit != 0)
            {
                Logger.Warn($"AutoStart: schtasks /Delete exit {exit}: {err.Trim()}");
                return false;
            }
            Logger.Info("AutoStart: scheduled task removed");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn("AutoStart: failed to unregister scheduled task", ex);
            return false;
        }
    }

    private static int RunSchtasks(string args)
        => RunSchtasks(args, out _);

    private static int RunSchtasks(string args, out string stderr)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        if (p == null) { stderr = "Process.Start returned null"; return -1; }
        // We don't care about stdout for /Query - the exit code tells us
        // everything. /Create writes to stderr on failure.
        stderr = p.StandardError.ReadToEnd();
        _ = p.StandardOutput.ReadToEnd(); // drain to avoid pipe deadlock
        p.WaitForExit();
        return p.ExitCode;
    }

    // -------------------------------------------------------------------------
    // Combined Set / EnsureValid
    // -------------------------------------------------------------------------

    /// <summary>
    /// Idempotent enable/disable. Writes (or deletes) BOTH the Run-key entry
    /// and the Scheduled Task using the current
    /// <see cref="Environment.ProcessPath"/>, AND removes any leftover
    /// Startup-folder shortcut from v0.6.50. Always rewrites when enabled,
    /// so a stale path from a prior install location is corrected.
    /// </summary>
    public static void Set(bool enabled)
    {
        SetRunKey(enabled);
        RemoveLegacyShortcut();
        if (enabled) RegisterScheduledTask();
        else         UnregisterScheduledTask();
    }

    /// <summary>
    /// Self-heal. Cheap, runs at every launch, idempotent when nothing's
    /// wrong. Returns true iff anything was changed.
    /// </summary>
    public static bool EnsureValid(bool desired)
    {
        // Always sweep the leftover shortcut on every launch — cheap and
        // it's the one thing we DO want a dev build to clean up.
        bool removedLegacy = RemoveLegacyShortcut();

        if (IsDevBuild())
        {
            if (!desired)
            {
                bool changedDevDisable = false;
                if (GetRegisteredCommand() != null) { SetRunKey(false); changedDevDisable = true; }
                if (IsScheduledTaskRegistered())    { UnregisterScheduledTask(); changedDevDisable = true; }
                return changedDevDisable || removedLegacy;
            }
            // Dev build with autostart desired: do nothing. Whatever the
            // installer-built copy registered stays in place.
            Logger.Info("AutoStart: dev-build detected, leaving Run-key + scheduled task untouched (will not autostart from bin\\Debug).");
            return removedLegacy;
        }

        if (!desired)
        {
            bool changedDisable = false;
            if (GetRegisteredCommand() != null) { SetRunKey(false); changedDisable = true; }
            if (IsScheduledTaskRegistered())    { UnregisterScheduledTask(); changedDisable = true; }
            return changedDisable || removedLegacy;
        }

        bool changed = removedLegacy;
        if (!IsRegistryEntryValid())
        {
            Logger.Info($"AutoStart: Run-key entry was missing or stale (was: {GetRegisteredCommand() ?? "<absent>"}), refreshing");
            SetRunKey(true);
            changed = true;
        }
        // Always (re-)register the scheduled task. Cheap idempotent overwrite
        // via /F flag, ensures the <Command> path is the running EXE even
        // after an install-location change.
        if (RegisterScheduledTask()) changed = true;
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
                Logger.Warn("AutoStart.SetRunKey(true): Environment.ProcessPath was null/empty - registry not updated");
                return;
            }
            if (!File.Exists(exe))
            {
                Logger.Warn($"AutoStart.SetRunKey(true): about to register path that doesn't exist on disk: {exe}");
            }
            string value = $"\"{exe}\"";
            string? prior = k.GetValue(Name) as string;
            k.SetValue(Name, value);
            if (!string.Equals(prior, value, StringComparison.OrdinalIgnoreCase))
                Logger.Info($"AutoStart: Run-key path refreshed -> {value} (was: {prior ?? "<absent>"})");
        }
        else
        {
            string? prior = k.GetValue(Name) as string;
            try { k.DeleteValue(Name, throwOnMissingValue: false); } catch { /* ignore */ }
            if (prior != null) Logger.Info("AutoStart: Run-key entry removed");
        }
    }

    /// <summary>Remove any leftover Startup-folder shortcut from v0.6.50.
    /// Returns true iff a shortcut was found and deleted.</summary>
    private static bool RemoveLegacyShortcut()
    {
        try
        {
            string? path = ShortcutPath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            File.Delete(path);
            Logger.Info($"AutoStart: removed legacy Startup-folder shortcut at {path}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn("AutoStart: failed to remove legacy Startup-folder shortcut", ex);
            return false;
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
        if (s.StartsWith("\"") && s.IndexOf('"', 1) is int idx && idx > 0)
            s = s.Substring(1, idx - 1);
        return s;
    }
}
