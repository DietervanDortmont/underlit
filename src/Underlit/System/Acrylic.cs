using System;
using System.Runtime.InteropServices;
using System.Windows.Media;
using Color = System.Windows.Media.Color; // disambiguate vs System.Drawing.Color

namespace Underlit.Sys;

/// <summary>
/// DWM backdrop helpers. Two paths:
///
///   1. Modern (Win11 22H2+, build 22621):
///        DwmExtendFrameIntoClientArea(margins=-1,-1,-1,-1) opens the whole client
///        area to DWM compositing. Then DwmSetWindowAttribute sets DWMWA_SYSTEMBACKDROP_TYPE
///        to DWMSBT_TRANSIENTWINDOW (live acrylic) and DWMWA_WINDOW_CORNER_PREFERENCE
///        to DWMWCP_ROUND for native rounded corners. DWM redraws the backdrop live as
///        windows move beneath.
///
///   2. Legacy fallback (Win10 / pre-22H2 Win11):
///        SetWindowCompositionAttribute(WCA_ACCENT_POLICY, ACCENT_ENABLE_ACRYLICBLURBEHIND)
///        gives a frosted blur but the backdrop is captured at composite time and does
///        NOT update live as content moves behind the window.
///
/// Important caveat: the modern path requires the window to NOT be layered, i.e.
/// AllowsTransparency=False in WPF.
/// </summary>
public static class Acrylic
{
    public static bool IsModernSupported =>
        Environment.OSVersion.Version.Major >= 10
        && Environment.OSVersion.Version.Build >= 22621;

    public enum Backdrop
    {
        None              = 1,
        Acrylic           = 3,   // DWMSBT_TRANSIENTWINDOW — frosted, live
        Mica              = 2,   // DWMSBT_MAINWINDOW — solid Mica (less blur)
    }

    /// <summary>
    /// Apply a backdrop. Returns true if the modern DWM path was used; false on legacy fallback or no-op.
    /// </summary>
    public static bool Apply(IntPtr hwnd, Backdrop kind, bool darkMode)
    {
        if (hwnd == IntPtr.Zero) return false;

        if (IsModernSupported)
        {
            // Tell DWM to render its frame across the entire client area. Without this,
            // the backdrop never shows on a non-layered WPF window.
            var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);

            // Dark window borders match dark backdrop tint.
            int useDark = darkMode ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));

            // Native anti-aliased rounded corners.
            int corner = (int)DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

            // System backdrop type. DWMSBT_TRANSIENTWINDOW is the acrylic one Windows
            // uses for transient flyouts; updates live as desktop content changes.
            int backdrop = (int)kind;
            int hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
            return hr == 0;
        }
        else if (kind == Backdrop.None)
        {
            ApplyAccentPolicy(hwnd, AccentState.Disabled, Color.FromRgb(0, 0, 0), 0);
            return false;
        }
        else
        {
            // Legacy: cached acrylic blur.
            ApplyAccentPolicy(hwnd, AccentState.AcrylicBlurBehind, Color.FromRgb(0x20, 0x20, 0x20), 0x40);
            return false;
        }
    }

    /// <summary>
    /// Disable Windows' automatic 8-px rounded corners + the soft window shadow that
    /// goes with them. Required for AllowsTransparency=true windows where the visible
    /// shape comes from our own bitmap's alpha channel — otherwise DWM paints a faint
    /// 300×66 rounded-rect outline behind whatever we draw, which the user rightly
    /// flagged as "the rectangle that remains".
    /// </summary>
    public static void DisableSystemRounding(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsModernSupported) return;
        int corner = (int)DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }

    // ---------------- Modern DWM API ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left, Right, Top, Bottom;
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE  = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE      = 38;

    private enum DWM_WINDOW_CORNER_PREFERENCE
    {
        DWMWCP_DEFAULT    = 0,
        DWMWCP_DONOTROUND = 1,
        DWMWCP_ROUND      = 2,
        DWMWCP_ROUNDSMALL = 3,
    }

    [DllImport("dwmapi.dll", SetLastError = false, PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("dwmapi.dll", SetLastError = false, PreserveSig = true)]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    // ---------------- Legacy fallback ----------------

    private enum AccentState : int
    {
        Disabled                  = 0,
        AcrylicBlurBehind         = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public uint GradientColor;
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
