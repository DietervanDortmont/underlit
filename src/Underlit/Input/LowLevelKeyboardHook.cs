using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Underlit.Display;

namespace Underlit.Input;

/// <summary>
/// Global WH_KEYBOARD_LL hook stub.
///
/// Originally this hook tried to catch laptop Fn-brightness keys by matching
/// VKs 0xAF / 0xB0 — which we'd labelled VK_BRIGHTNESS_DOWN / VK_BRIGHTNESS_UP.
/// That was wrong: 0xAF is VK_VOLUME_UP and 0xB0 is VK_MEDIA_NEXT_TRACK in the
/// real Win32 VK table. There is no standard VK_BRIGHTNESS_* — modern laptop
/// brightness keys go through HID consumer-page input that never fires
/// WM_KEYDOWN at all. So the hook was silently stealing the user's volume-up
/// and media-next-track presses and remapping them to brightness changes.
///
/// As of v0.6.12 the hook installs but matches NO VK codes — it's a transparent
/// passthrough. Brightness can still be controlled via RegisterHotKey-style
/// hotkeys configured in Settings (Ctrl+Alt+Up/Down by default), which is the
/// only reliable cross-OEM mechanism.
///
/// Handler events fire on a dedicated hook thread — we marshal to the UI
/// dispatcher before invoking the subscriber.
/// </summary>
public sealed class LowLevelKeyboardHook : IDisposable
{
    // Events kept on the API surface for backwards compatibility with subscribers,
    // but are never raised after v0.6.12 (the hook is a passthrough — see class doc).
#pragma warning disable CS0067
    public event Action? BrightnessDown;
    public event Action? BrightnessUp;
#pragma warning restore CS0067

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
    /// Vestigial setting from the v0.5-era hook. The hook no longer matches any
    /// VK codes (see HookCallback) so this flag has no observable effect.
    /// Retained on the API surface for back-compat.
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
        // v0.6.12: hook is a transparent passthrough. The VK codes we used to match
        // (0xAF, 0xB0) were really VK_VOLUME_UP and VK_MEDIA_NEXT_TRACK — see the
        // class doc comment. Matching them caused the user's volume keys to dim the
        // screen, so the matching is gone. Real laptop brightness keys do NOT fire
        // WM_KEYDOWN at all; they need OEM-specific HID handling we don't ship.
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
