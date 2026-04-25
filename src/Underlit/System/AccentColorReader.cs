using System;
using System.Windows.Media;
using Microsoft.Win32;

namespace Underlit.Sys;

/// <summary>
/// Reads the user's Windows accent color and tracks changes.
///
/// Storage:
///   HKCU\Software\Microsoft\Windows\DWM\AccentColor
/// Encoding: DWORD in 0xAABBGGRR (alpha-blue-green-red, low byte = R).
/// Changes propagate via SystemEvents.UserPreferenceChanged (category General).
/// </summary>
public static class AccentColorReader
{
    public static event Action<Color>? AccentChanged;

    /// <summary>Windows' default fallback accent (used when the registry key is missing).</summary>
    public static readonly Color DefaultAccent = Color.FromRgb(0x00, 0x78, 0xD4);

    static AccentColorReader()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General)
            {
                AccentChanged?.Invoke(GetAccentColor());
            }
        };
    }

    public static Color GetAccentColor()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (k?.GetValue("AccentColor") is int dword)
            {
                byte r = (byte)(dword         & 0xFF);
                byte g = (byte)((dword >> 8)  & 0xFF);
                byte b = (byte)((dword >> 16) & 0xFF);
                return Color.FromRgb(r, g, b);
            }
        }
        catch { /* fall through */ }
        return DefaultAccent;
    }
}
