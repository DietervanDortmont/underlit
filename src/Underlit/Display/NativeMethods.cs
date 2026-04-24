using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Underlit.Display;

/// <summary>
/// P/Invoke signatures for Windows display APIs.
/// Kept deliberately small and focused — one source of truth so we don't
/// double-declare DllImports across files.
/// </summary>
internal static class NativeMethods
{
    // ---------- Structures ----------

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int left, top, right, bottom;
        public int Width => right - left;
        public int Height => bottom - top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    public const uint MONITORINFOF_PRIMARY = 0x00000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    // Window styles/extended styles used by the overlay and OSD windows.
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_LAYERED     = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW  = 0x00000080;
    public const int WS_EX_NOACTIVATE  = 0x08000000;
    public const int WS_EX_TOPMOST     = 0x00000008;

    // SetWindowPos flags
    public const uint SWP_NOSIZE      = 0x0001;
    public const uint SWP_NOMOVE      = 0x0002;
    public const uint SWP_NOACTIVATE  = 0x0010;
    public const uint SWP_NOOWNERZORDER = 0x0200;
    public const uint SWP_SHOWWINDOW  = 0x0040;
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    // ---------- Monitor enumeration ----------

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    // ---------- Device context / gamma ----------

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateDC(string lpszDriver, string lpszDevice, string? lpszOutput, IntPtr lpInitData);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);

    /// <summary>
    /// ramp is a ushort[3 * 256] — R, G, B channels back-to-back.
    /// Stored as 0..65535 (high byte = gamma-corrected output).
    /// </summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool SetDeviceGammaRamp(IntPtr hdc, [In] ushort[] ramp);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool GetDeviceGammaRamp(IntPtr hdc, [Out] ushort[] ramp);

    // ---------- DDC/CI (physical monitor brightness) ----------

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint pdwMinimumBrightness, out uint pdwCurrentBrightness, out uint pdwMaximumBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness);

    // ---------- Window helpers ----------

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    public const int SW_SHOWNOACTIVATE = 4;

    // ---------- Hotkeys ----------

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const uint MOD_ALT     = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT   = 0x0004;
    public const uint MOD_WIN     = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    public const int WM_HOTKEY = 0x0312;

    // ---------- Low-level keyboard hook ----------

    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN    = 0x0100;
    public const int WM_KEYUP      = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP   = 0x0105;

    // Brightness virtual-key codes, VK_BRIGHTNESS_DOWN / UP.
    // Not defined in WinUser.h headers under these names for all SDKs — hard-code the values.
    public const uint VK_BRIGHTNESS_DOWN = 0xAF;
    public const uint VK_BRIGHTNESS_UP   = 0xB0;

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    // ---------- Foreground window / process name (for exclusion list) ----------

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
