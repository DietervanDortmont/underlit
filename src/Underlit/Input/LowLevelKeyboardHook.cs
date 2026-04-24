using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Underlit.Display;

namespace Underlit.Input;

/// <summary>
/// Global WH_KEYBOARD_LL hook. Catches brightness keys (VK 0xAF/0xB0) that modern
/// Windows laptops surface when you press Fn+F-brightness.
///
/// Not every OEM surfaces these. Some (especially older Dell, some HP) route
/// brightness keys through vendor HID devices and never hit the system hook.
/// For those, the user binds a custom RegisterHotKey-style hotkey instead.
///
/// Handler events fire on a dedicated hook thread — we marshal to the UI
/// dispatcher before invoking the subscriber.
/// </summary>
public sealed class LowLevelKeyboardHook : IDisposable
{
    public event Action? BrightnessDown;
    public event Action? BrightnessUp;

    private IntPtr _hook;
    private NativeMethods.HookProc? _proc; // keep a strong reference so it isn't GC'd
    private readonly Dispatcher _uiDispatcher;
    private bool _swallowNativeKey;

    public LowLevelKeyboardHook(Dispatcher uiDispatcher, bool swallowNativeKey)
    {
        _uiDispatcher = uiDispatcher;
        _swallowNativeKey = swallowNativeKey;
    }

    /// <summary>
    /// If true, we return a non-zero value from the hook for VK_BRIGHTNESS_* events,
    /// which prevents Windows' default brightness behavior from also firing.
    /// We only want this when our "below-min" or "above-max" dimming is in charge.
    /// </summary>
    public bool SwallowNativeKey
    {
        get => _swallowNativeKey;
        set => _swallowNativeKey = value;
    }

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _proc = HookCallback;
        var hMod = NativeMethods.GetModuleHandle(null);
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, hMod, 0);
        if (_hook == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            Logger.Warn($"SetWindowsHookEx failed, err={err}");
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);

        int msg = wParam.ToInt32();
        if (msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN)
        {
            var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            if (data.vkCode == NativeMethods.VK_BRIGHTNESS_DOWN)
            {
                _uiDispatcher.BeginInvoke((Action)(() => BrightnessDown?.Invoke()), DispatcherPriority.Input);
                if (_swallowNativeKey) return (IntPtr)1;
            }
            else if (data.vkCode == NativeMethods.VK_BRIGHTNESS_UP)
            {
                _uiDispatcher.BeginInvoke((Action)(() => BrightnessUp?.Invoke()), DispatcherPriority.Input);
                if (_swallowNativeKey) return (IntPtr)1;
            }
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        _proc = null;
    }
}
