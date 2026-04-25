using System;
using System.Runtime.InteropServices;
using System.Windows.Media;
using Color = System.Windows.Media.Color; // disambiguate vs System.Drawing.Color (WinForms is referenced)

namespace Underlit.Sys;

/// <summary>
/// Backdrop helpers for the OSD window.
///
/// Two paths, picked at runtime depending on Windows version:
///
///   1. Modern DWM API — Windows 11 22H2 build 22621+:
///        DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE = DWMSBT_TRANSIENTWINDOW)
///      gives a TRUE live-updating acrylic backdrop (the same one the Quick Settings
///      flyout uses). Pairs with DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND for
///      proper anti-aliased rounded corners and an automatic DWM-provided shadow.
///      Requires the window to NOT have WS_EX_LAYERED (i.e. AllowsTransparency=False).
///
///   2. Legacy fallback — Windows 10 / pre-22H2 Windows 11:
///        SetWindowCompositionAttribute(WCA_ACCENT_POLICY, ACCENT_ENABLE_ACRYLICBLURBEHIND)
///      gives a frosted blur but the backdrop is captured at composite time and does
///      NOT update live as content moves behind the window. Best we can do without
///      the modern API.
/// </summary>
public static class Acrylic
{
    public static bool IsModernSupported =>
        Environment.OSVersion.Version.Major >= 10
        && Environment.OSVersion.Version.Build >= 22621;

    /// <summary>
    /// Enable a live-updating acrylic backdrop. Returns true if the modern path
    /// was used; false if the call fell back to legacy or did nothing.
    /// </summary>
    public static bool EnableLiveAcrylic(IntPtr hwnd, bool darkMode)
    {
        if (IsModernSupported)
        {
            // Honor the system's current dark/light setting on the window borders.
            int useDark = darkMode ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));

            // Round corners — DWM does this with proper anti-aliasing, no manual region.
            int corner = (int)DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

            // Acrylic transient backdrop. Updates live as desktop content changes.
            int backdrop = (int)DWM_SYSTEMBACKDROP_TYPE.DWMSBT_TRANSIENTWINDOW;
            int hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
            return hr == 0;
        }
        else
        {
            // Fallback: legacy acrylic. Not live-updating but better than nothing.
            ApplyAccentPolicy(hwnd, AccentState.AcrylicBlurBehind, Color.FromRgb(0x20, 0x20, 0x20), 0x40);
            return false;
        }
    }

    public static void Disable(IntPtr hwnd)
    {
        if (IsModernSupported)
        {
            int backdrop = (int)DWM_SYSTEMBACKDROP_TYPE.DWMSBT_NONE;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
            // Leave DWMWCP_ROUND in place — rounded corners are a feature regardless.
        }
        else
        {
            ApplyAccentPolicy(hwnd, AccentState.Disabled, Color.FromRgb(0, 0, 0), 0);
        }
    }

    // ---------------- Modern DWM API ----------------

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE      = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE     = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE          = 38;

    private enum DWM_WINDOW_CORNER_PREFERENCE
    {
        DWMWCP_DEFAULT    = 0,
        DWMWCP_DONOTROUND = 1,
        DWMWCP_ROUND      = 2,
        DWMWCP_ROUNDSMALL = 3,
    }

    private enum DWM_SYSTEMBACKDROP_TYPE
    {
        DWMSBT_AUTO              = 0,
        DWMSBT_NONE              = 1,
        DWMSBT_MAINWINDOW        = 2,  // Mica
        DWMSBT_TRANSIENTWINDOW   = 3,  // Acrylic — what we want for an OSD flyout
        DWMSBT_TABBEDWINDOW      = 4,  // Tabbed Mica
    }

    [DllImport("dwmapi.dll", SetLastError = false, PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // ---------------- Legacy fallback ----------------

    private enum AccentState : int
    {
        Disabled                  = 0,
        EnableGradient            = 1,
        EnableTransparentGradient = 2,
        BlurBehind                = 3,
        AcrylicBlurBehind         = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public uint GradientColor;  // ABGR
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    private const int WCA_ACCENT_POLICY = 19;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    private static void ApplyAccentPolicy(IntPtr hwnd, AccentState state, Color tint, byte tintOpacity)
    {
        uint abgr = ((uint)tintOpacity << 24)
                  | ((uint)tint.B      << 16)
                  | ((uint)tint.G      << 8)
                  | (uint)tint.R;

        var policy = new AccentPolicy
        {
            AccentState = state,
            AccentFlags = state == AccentState.AcrylicBlurBehind ? 2 : 0,
            GradientColor = abgr,
            AnimationId = 0
        };

        int size = Marshal.SizeOf(policy);
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, ptr, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = ptr,
                SizeOfData = size
            };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
