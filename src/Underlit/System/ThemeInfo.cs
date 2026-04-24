using System;
using Microsoft.Win32;

namespace Underlit.Sys;

/// <summary>
/// Detects Windows' light/dark theme and notifies when it changes.
///
/// "AppsUseLightTheme" under HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize
/// is 0 for dark apps, 1 for light. Windows fires <see cref="SystemEvents.UserPreferenceChanged"/>
/// with category General whenever the user flips this.
/// </summary>
public static class ThemeInfo
{
    public static event Action<bool>? ThemeChanged; // argument: isDark

    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    static ThemeInfo()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General)
            {
                ThemeChanged?.Invoke(IsDarkMode());
            }
        };
    }

    public static bool IsDarkMode()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            if (k?.GetValue("AppsUseLightTheme") is int v) return v == 0;
        }
        catch { /* fall through */ }
        return true; // default to dark if we can't read
    }
}
