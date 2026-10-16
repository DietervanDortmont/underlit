using System;
using System.Collections.Generic;
using System.Linq;

namespace Underlit.Display;

/// <summary>
/// Unified hardware-brightness facade. For each display, we try the "best" available
/// hardware path:
///   - External monitor with DDC/CI     → DDC/CI (one I2C channel per monitor — independent)
///   - Internal laptop panel            → WMI    (Windows' single global laptop-brightness API)
///   - Anything else                    → not supported (engine falls back to gamma+overlay)
///
/// <para>v0.6.76: WMI is no longer keyed off <c>IsPrimary</c>. The old behaviour
/// broke as soon as the user made an external monitor their primary — WMI is a
/// single global laptop-panel API, not a per-display channel, so attributing it
/// to "whichever is primary" would route the laptop's brightness to the wrong
/// display name (and try DDC/CI on a laptop, which doesn't support it).
/// Instead, we now claim WMI for the FIRST display that lacks DDC/CI support,
/// since laptop panels are exactly the ones that don't enumerate on the
/// DDC/CI I2C bus. The result: each external monitor gets its own DDC/CI
/// channel, and the laptop gets the (singular) WMI cycle, regardless of which
/// is currently primary.</para>
/// </summary>
public sealed class HardwareBrightness : IDisposable
{
    private readonly DdcCiMonitorSet _ddc = new();
    private bool _wmiTried;
    private bool _wmiAvailable;
    private string? _wmiOwnerDeviceName;

    public void Register(IReadOnlyList<DisplayInfo> displays)
    {
        _ddc.Register(displays);
        if (!_wmiTried)
        {
            _wmiTried = true;
            _wmiAvailable = WmiBrightness.TryGet().HasValue;
        }

        // v0.6.76: pick the first display without DDC/CI support as the
        // WMI owner. Laptop panels don't expose DDC/CI on the I2C bus, so
        // "no DDC/CI registration" is the most reliable in-process signal
        // we have for "this is the internal panel". Other non-DDC displays
        // (rare — e.g. some KVMs or virtual monitors) would otherwise
        // fight for the same WMI cycle and override each other's writes,
        // so we restrict WMI to a single device name.
        _wmiOwnerDeviceName = null;
        if (_wmiAvailable)
        {
            foreach (var d in displays)
            {
                bool hasDdc = _ddc.Entries.TryGetValue(d.DeviceName, out var e) && e.Supported;
                if (!hasDdc)
                {
                    _wmiOwnerDeviceName = d.DeviceName;
                    Logger.Info($"HardwareBrightness: WMI assigned to {d.DeviceName} (no DDC/CI on this display — treated as internal panel)");
                    break;
                }
            }
            if (_wmiOwnerDeviceName == null)
            {
                Logger.Info("HardwareBrightness: WMI available but every display reports DDC/CI — leaving WMI unassigned to avoid double-writes");
            }
        }
        // Log the chosen path for every display so multi-monitor issues
        // are easier to diagnose from a log dump.
        foreach (var d in displays)
        {
            Logger.Info($"HardwareBrightness: {d.DeviceName} {(d.IsPrimary ? "(primary) " : "")}→ {PathFor(d)}");
        }
    }

    public enum Path { None, Wmi, DdcCi }

    public Path PathFor(DisplayInfo d)
    {
        // Prefer DDC/CI when available — it's per-monitor, so multiple
        // displays can be driven independently. WMI is a fallback for
        // the one display that doesn't have DDC/CI.
        if (_ddc.Entries.TryGetValue(d.DeviceName, out var e) && e.Supported) return Path.DdcCi;
        if (_wmiAvailable && d.DeviceName == _wmiOwnerDeviceName) return Path.Wmi;
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
