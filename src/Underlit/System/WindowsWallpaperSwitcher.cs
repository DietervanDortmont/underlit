using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Underlit.Sys;

/// <summary>
/// Sets the Windows desktop background — either to an image file or to a
/// solid colour. Used by the auto-theme switcher (v0.6.63) so the user
/// can have a different wallpaper for light vs dark mode.
///
/// <para>Mechanism:
/// <list type="bullet">
///   <item><b>Image</b>: <c>SystemParametersInfo(SPI_SETDESKWALLPAPER)</c>
///         with the file path. Windows handles JPG / PNG / BMP. Pictures
///         get the user's existing fit / fill / centre style.</item>
///   <item><b>Solid colour</b>: clear wallpaper to "" via SPI, then call
///         <c>SetSysColors</c> with <c>COLOR_BACKGROUND = 1</c>. Also
///         persist to <c>HKCU\Control Panel\Colors\Background</c> so the
///         change survives reboot.</item>
/// </list>
///
/// <para>All operations are best-effort: if any fail (file missing, registry
/// access blocked, etc.), they log and return false. Auto-theme will keep
/// trying on subsequent ticks so transient failures self-heal.</para>
/// </summary>
internal static class WindowsWallpaperSwitcher
{
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SystemParametersInfo(
        uint uiAction, uint uiParam, string pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetSysColors(int cElements, int[] lpaElements, uint[] lpaRgbValues);

    private const uint SPI_SETDESKWALLPAPER = 0x0014;
    private const uint SPIF_UPDATEINIFILE   = 0x01;
    private const uint SPIF_SENDCHANGE      = 0x02;
    private const int  COLOR_BACKGROUND     = 1;

    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int nIndex);

    /// <summary>Paint the desktop with the supplied image file. Returns
    /// false if the file doesn't exist or the API call fails.</summary>
    public static bool SetImage(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            Logger.Warn($"WindowsWallpaperSwitcher.SetImage: file not found at '{imagePath}'");
            return false;
        }
        // v0.7.2: this is called every 30 s. Setting the wallpaper broadcasts
        // WM_SETTINGCHANGE to every window (Explorer refreshes and jumps), so
        // only do it when the wallpaper really is something else.
        if (string.Equals(GetCurrentWallpaperPath(), imagePath, StringComparison.OrdinalIgnoreCase))
            return true;
        try
        {
            Logger.Info($"WindowsWallpaperSwitcher: setting wallpaper to '{imagePath}'");
            bool ok = SystemParametersInfo(
                SPI_SETDESKWALLPAPER, 0, imagePath,
                SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            if (!ok)
            {
                Logger.Warn($"WindowsWallpaperSwitcher.SetImage: SPI returned false for '{imagePath}'");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn("WindowsWallpaperSwitcher.SetImage threw", ex);
            return false;
        }
    }

    /// <summary>Replace the wallpaper with a solid colour. Order matters:
    /// (1) clear the existing wallpaper file via SPI, (2) flip the system
    /// colour, (3) persist the colour to the registry so it survives
    /// sign-out. Without (3), the colour resets to grey on next login.</summary>
    public static bool SetSolidColor(byte r, byte g, byte b)
    {
        bool anyOk = false;
        try
        {
            uint rgb = (uint)r | ((uint)g << 8) | ((uint)b << 16);

            // v0.7.2: called every 30 s. Both steps below broadcast to every
            // window (WM_SETTINGCHANGE / WM_SYSCOLORCHANGE) and made Explorer
            // and other apps redraw and lose their scroll position twice a
            // minute. Skip each step when it's already in the wanted state.
            bool wallpaperClear = string.IsNullOrEmpty(GetCurrentWallpaperPath());
            bool colourRight    = (GetSysColor(COLOR_BACKGROUND) & 0xFFFFFF) == rgb;
            if (wallpaperClear && colourRight) return true;
            Logger.Info($"WindowsWallpaperSwitcher: setting solid desktop colour {r},{g},{b}");

            // 1) Clear wallpaper - if a wallpaper file is set, the solid
            //    colour underneath is invisible.
            if (!wallpaperClear)
                SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, "",
                                      SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);

            // 2) Live-set the desktop colour. Note Windows' RGB encoding
            //    is little-endian: 0xBBGGRR.
            if (!colourRight)
                SetSysColors(1, new[] { COLOR_BACKGROUND }, new[] { rgb });

            // 3) Persist via registry so the colour survives reboot.
            //    HKCU\Control Panel\Colors\Background is the documented
            //    location; the value is a space-separated "R G B" string.
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Control Panel\Colors", writable: true);
            if (key != null)
            {
                key.SetValue("Background", $"{r} {g} {b}", RegistryValueKind.String);
            }
            anyOk = true;
        }
        catch (Exception ex)
        {
            Logger.Warn("WindowsWallpaperSwitcher.SetSolidColor threw", ex);
        }
        return anyOk;
    }

    /// <summary>Read the current wallpaper file path from the registry.
    /// Returns empty if there's no image (i.e. the desktop is showing a
    /// solid colour). Used for diagnostics.</summary>
    public static string GetCurrentWallpaperPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            return key?.GetValue("Wallpaper") as string ?? "";
        }
        catch
        {
            return "";
        }
    }
}
