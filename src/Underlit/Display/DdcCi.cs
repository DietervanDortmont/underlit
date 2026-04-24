using System;
using System.Collections.Generic;

namespace Underlit.Display;

/// <summary>
/// DDC/CI wrapper. Lets us set/get hardware brightness on external monitors
/// that implement VCP feature 0x10 (Luminance).
///
/// NOT all monitors support this. Many do but lie about the range; some throttle
/// writes (don't update faster than ~5Hz). The engine should debounce.
/// </summary>
public sealed class DdcCiMonitorSet : IDisposable
{
    public sealed class Entry
    {
        public string DeviceName { get; init; } = "";
        public IntPtr Physical { get; init; }
        public uint Min { get; set; }
        public uint Max { get; set; }
        public bool Supported { get; set; }
    }

    // device-name -> array of physical monitors backing it (usually 1)
    private readonly Dictionary<string, NativeMethods.PHYSICAL_MONITOR[]> _physPerDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public IReadOnlyDictionary<string, Entry> Entries => _entries;

    public void Register(IEnumerable<DisplayInfo> displays)
    {
        Clear();

        foreach (var d in displays)
        {
            if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(d.HMonitor, out uint count) || count == 0)
                continue;

            var arr = new NativeMethods.PHYSICAL_MONITOR[count];
            if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(d.HMonitor, count, arr))
                continue;

            _physPerDevice[d.DeviceName] = arr;

            // Use the first physical monitor. Multi-PM mappings are rare (video walls).
            var phys = arr[0];
            if (NativeMethods.GetMonitorBrightness(phys.hPhysicalMonitor, out uint min, out uint cur, out uint max))
            {
                _entries[d.DeviceName] = new Entry
                {
                    DeviceName = d.DeviceName,
                    Physical = phys.hPhysicalMonitor,
                    Min = min,
                    Max = max,
                    Supported = max > min
                };
            }
            else
            {
                _entries[d.DeviceName] = new Entry { DeviceName = d.DeviceName, Physical = phys.hPhysicalMonitor, Supported = false };
            }
        }
    }

    /// <summary>Percent 0..100 clamped into the monitor's declared range.</summary>
    public bool TrySet(string deviceName, int percent)
    {
        if (!_entries.TryGetValue(deviceName, out var e) || !e.Supported) return false;
        percent = Math.Clamp(percent, 0, 100);
        var span = e.Max - e.Min;
        var value = (uint)(e.Min + Math.Round(span * percent / 100.0));
        try
        {
            return NativeMethods.SetMonitorBrightness(e.Physical, value);
        }
        catch (Exception ex)
        {
            Logger.Warn($"DDC/CI set failed for {deviceName}", ex);
            return false;
        }
    }

    private void Clear()
    {
        foreach (var kvp in _physPerDevice)
        {
            try { NativeMethods.DestroyPhysicalMonitors((uint)kvp.Value.Length, kvp.Value); } catch { /* ignore */ }
        }
        _physPerDevice.Clear();
        _entries.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
    }
}
