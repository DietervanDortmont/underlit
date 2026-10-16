using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Underlit.Sys;

/// <summary>
/// Forces the Windows 10/11 app + system theme to light or dark.
///
/// <para>v0.6.56. Underlit's optional auto-switcher uses this to follow
/// the user's circadian curve (warm = dark theme, cool = light theme)
/// without depending on third-party tools like Auto Dark Mode that
/// occasionally fail at startup.</para>
///
/// <para>Mechanism: write the two DWORDs the Windows shell reads from
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize</c>:
/// <list type="bullet">
///   <item><b>AppsUseLightTheme</b> - controls Win32 / UWP apps that
///         honour the system theme (Settings, Notepad, File Explorer
///         on recent builds).</item>
///   <item><b>SystemUsesLightTheme</b> - controls the taskbar, Start
///         menu, action centre, and other shell chrome.</item>
/// </list>
/// 0 = dark, 1 = light. After writing, broadcast WM_SETTINGCHANGE with
/// the "ImmersiveColorSet" parameter so the shell picks up the change
/// without requiring a sign-out.</para>
///
/// <para>"Aggressive" defence: callers should re-apply periodically
/// (every ~30 s is plenty) AND re-apply on app startup. That way any
/// other tool or Windows quirk that briefly flips the value gets
/// overridden quickly.</para>
/// </summary>
internal static class WindowsThemeSwitcher
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public enum WindowsTheme { Dark = 0, Light = 1 }

    /// <summary>Read the current theme. Defaults to Dark on read errors
    /// (matches Windows' factory default for fresh user profiles).</summary>
    public static WindowsTheme GetCurrent()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            if (k?.GetValue("AppsUseLightTheme") is int v)
                return v == 1 ? WindowsTheme.Light : WindowsTheme.Dark;
        }
        catch { /* fall through to default */ }
        return WindowsTheme.Dark;
    }

    /// <summary>Set the Windows theme. Idempotent - if the registry
    /// already matches, only the broadcast fires (so observers re-pick
    /// up the value if they missed an earlier broadcast). Returns true
    /// iff the registry was modified.</summary>
    public static bool Set(WindowsTheme theme)
    {
        bool changed = false;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(PersonalizeKey, writable: true)
                         ?? Registry.CurrentUser.CreateSubKey(PersonalizeKey, writable: true);
            if (k == null) return false;

            int desired = theme == WindowsTheme.Light ? 1 : 0;
            if ((k.GetValue("AppsUseLightTheme")   as int?) != desired)
            {
                k.SetValue("AppsUseLightTheme", desired, RegistryValueKind.DWord);
                changed = true;
            }
            if ((k.GetValue("SystemUsesLightTheme") as int?) != desired)
            {
                k.SetValue("SystemUsesLightTheme", desired, RegistryValueKind.DWord);
                changed = true;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("WindowsThemeSwitcher.Set: registry write failed", ex);
            return false;
        }

        // Always broadcast - cheap and ensures observers refresh even
        // when we didn't write anything new.
        try { BroadcastThemeChange(); }
        catch (Exception ex) { Logger.Warn("WindowsThemeSwitcher: broadcast failed", ex); }

        if (changed)
            Logger.Info($"WindowsThemeSwitcher: set theme to {theme}");
        return changed;
    }

    // --- WM_SETTINGCHANGE broadcast ---
    // The shell listens for "ImmersiveColorSet" on this message and
    // immediately re-reads the personalization registry values. Without
    // this, Explorer / Settings often won't visibly update until the
    // next sign-in.

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
        uint flags, uint timeoutMs, out IntPtr result);

    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint SMTO_ABORTIFHUNG  = 0x0002;

    private static void BroadcastThemeChange()
    {
        SendMessageTimeout(
            HWND_BROADCAST, WM_SETTINGCHANGE,
            IntPtr.Zero, "ImmersiveColorSet",
            SMTO_ABORTIFHUNG, 1000, out _);
    }
}
