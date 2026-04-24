using System;
using System.Collections.Generic;
using System.Windows.Interop;
using Underlit.Display;

namespace Underlit.Input;

/// <summary>
/// Registers RegisterHotKey-based global hotkeys against a hidden message-only window
/// and dispatches WM_HOTKEY to named events.
///
/// Caller passes the engine-owned HwndSource (OSD window works well) — we attach a
/// WndProc hook to receive WM_HOTKEY. One HotkeyManager per HWND.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    public event Action<string>? Triggered;

    private readonly HwndSource _source;
    private readonly Dictionary<int, string> _idToName = new();
    private readonly Dictionary<string, int> _nameToId = new();
    private int _nextId = 0xB001;

    public HotkeyManager(HwndSource source)
    {
        _source = source;
        _source.AddHook(WndProc);
    }

    public bool Register(string name, Hotkey hk)
    {
        Unregister(name);
        int id = _nextId++;
        // NOTE: intentionally NOT setting MOD_NOREPEAT — when the user holds the
        // brightness/warmth hotkey down, we want Windows to keep firing WM_HOTKEY
        // at the system key-repeat rate so the level keeps stepping.
        uint mods = (uint)hk.Modifiers;
        if (!NativeMethods.RegisterHotKey(_source.Handle, id, mods, hk.VirtualKey))
        {
            Logger.Warn($"RegisterHotKey failed for {name} ({hk})");
            return false;
        }
        _idToName[id] = name;
        _nameToId[name] = id;
        return true;
    }

    public void Unregister(string name)
    {
        if (_nameToId.TryGetValue(name, out var id))
        {
            NativeMethods.UnregisterHotKey(_source.Handle, id);
            _idToName.Remove(id);
            _nameToId.Remove(name);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_idToName.TryGetValue(id, out var name))
            {
                Triggered?.Invoke(name);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _idToName.Keys)
        {
            try { NativeMethods.UnregisterHotKey(_source.Handle, id); } catch { /* ignore */ }
        }
        _idToName.Clear();
        _nameToId.Clear();
        try { _source.RemoveHook(WndProc); } catch { /* ignore */ }
    }
}
