using System;
using Microsoft.Win32;

namespace Underlit.Sys;

/// <summary>
/// Reads the user's Windows "Transparency effects" toggle and tracks changes.
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\EnableTransparency (DWORD 0/1).
/// </summary>
public static class TransparencyPreference
{
    public static event Action<bool>? Changed;

    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    static TransparencyPreference()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General)
            {
                Changed?.Invoke(IsEnabled());
            }
        };
    }

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            if (k?.GetValue("EnableTransparency") is int v) return v != 0;
        }
        catch { /* fall through */ }
        return true; // default to enabled if we can't read
    }
}
