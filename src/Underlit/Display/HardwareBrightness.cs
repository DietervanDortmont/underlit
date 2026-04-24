using System;
using System.Collections.Generic;
using System.Linq;

namespace Underlit.Display;

/// <summary>
/// Unified hardware-brightness facade. For each display, we try the "best" available
/// hardware path:
///   - Internal panel on a laptop       → WMI
///   - External monitor with DDC/CI     → DDC/CI
///   - Anything else                    → not supported (engine falls back to gamma+overlay)
/// </summary>
public sealed class HardwareBrightness : IDisposable
{
    private readonly DdcCiMonitorSet _ddc = new();
    private bool _wmiTried;
    private bool _wmiAvailable;

    public void Register(IReadOnlyList<DisplayInfo> displays)
    {
        _ddc.Register(displays);
        if (!_wmiTried)
        {
            _wmiTried = true;
            _wmiAvailable = WmiBrightness.TryGet().HasValue;
        }
    }

    public enum Path { None, Wmi, DdcCi }

    public Path PathFor(DisplayInfo d)
    {
        if (d.IsPrimary && _wmiAvailable) return Path.Wmi;
        if (_ddc.Entries.TryGetValue(d.DeviceName, out var e) && e.Supported) return Path.DdcCi;
        return Path.None;
    }

    /// <summary>
    /// Set hardware brightness 0..100. If no hardware path exists for this display,
    /// returns false so the engine knows to stay in software-only mode.
    /// </summary>
    public bool TrySet(DisplayInfo d, int percent)
    {
        switch (PathFor(d))
        {
            case Path.Wmi:   return WmiBrightness.TrySet(percent);
            case Path.DdcCi: return _ddc.TrySet(d.DeviceName, percent);
            default:         return false;
        }
    }

    public bool AnyDisplayHasHardwarePath(IEnumerable<DisplayInfo> displays)
        => displays.Any(d => PathFor(d) != Path.None);

    public void Dispose() => _ddc.Dispose();
}
