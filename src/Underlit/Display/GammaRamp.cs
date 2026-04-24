using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Underlit.Display;

/// <summary>
/// Applies per-monitor gamma ramps for brightness + color-temperature.
///
/// Windows limits how dark a gamma ramp is allowed to go; on most GPUs values
/// below roughly 0.35x get clamped. That's why we ALSO use an overlay window —
/// gamma takes the first bite, overlay handles the rest.
///
/// This class is deliberately thread-safe-ish: one lock guards DC cache. In practice
/// it's only touched from the engine's timer thread + display-change handler.
/// </summary>
public sealed class GammaRampApplier : IDisposable
{
    private readonly object _lock = new();
    // device-name -> HDC. Cached because CreateDC is not free and we update at ~60Hz during ramps.
    private readonly Dictionary<string, IntPtr> _dcByDevice = new(StringComparer.OrdinalIgnoreCase);
    // device-name -> original ramp captured at startup so we can restore cleanly.
    private readonly Dictionary<string, ushort[]> _originalRamps = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public void Register(IEnumerable<DisplayInfo> displays)
    {
        lock (_lock)
        {
            // Close stale DCs for monitors that disappeared.
            var currentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in displays) currentNames.Add(d.DeviceName);

            var toRemove = new List<string>();
            foreach (var kvp in _dcByDevice)
                if (!currentNames.Contains(kvp.Key)) toRemove.Add(kvp.Key);

            foreach (var name in toRemove)
            {
                if (_dcByDevice.TryGetValue(name, out var dc))
                {
                    TryRestoreRamp(dc, name);
                    NativeMethods.DeleteDC(dc);
                }
                _dcByDevice.Remove(name);
                _originalRamps.Remove(name);
            }

            // Open DCs for new monitors, capturing the original ramp.
            foreach (var d in displays)
            {
                if (string.IsNullOrEmpty(d.DeviceName)) continue;
                if (_dcByDevice.ContainsKey(d.DeviceName)) continue;

                var dc = NativeMethods.CreateDC("DISPLAY", d.DeviceName, null, IntPtr.Zero);
                if (dc == IntPtr.Zero)
                {
                    Logger.Warn($"CreateDC failed for {d.DeviceName}");
                    continue;
                }

                var orig = new ushort[3 * 256];
                if (NativeMethods.GetDeviceGammaRamp(dc, orig))
                {
                    _originalRamps[d.DeviceName] = orig;
                }
                else
                {
                    // Some GPUs (HDR mode, some Intel integrated) refuse gamma reads.
                    // We still keep the DC — writes may still work, and if they don't,
                    // overlay-only mode is fine.
                    Logger.Warn($"GetDeviceGammaRamp failed for {d.DeviceName}");
                }
                _dcByDevice[d.DeviceName] = dc;
            }
        }
    }

    /// <summary>
    /// Apply gamma to every registered monitor.
    /// brightness: 0..1 (1 = unmodified from original, 0 = fully dim via gamma — but Windows will clamp).
    /// warmthKelvin: 6500 = neutral, lower = warmer (more red / less blue).
    /// </summary>
    public void Apply(double brightness, int warmthKelvin)
    {
        brightness = Math.Clamp(brightness, 0.0, 1.0);
        warmthKelvin = Math.Clamp(warmthKelvin, 1500, 6500);

        var rgb = KelvinToRgbMultipliers(warmthKelvin);

        lock (_lock)
        {
            foreach (var kvp in _dcByDevice)
            {
                var ramp = BuildRamp(brightness, rgb.r, rgb.g, rgb.b);
                if (!NativeMethods.SetDeviceGammaRamp(kvp.Value, ramp))
                {
                    // Common when HDR is on; we silently accept — overlay picks up the slack.
                }
            }
        }
    }

    /// <summary>Reset all monitors to the ramp we captured at startup (or a linear ramp if we didn't have one).</summary>
    public void Restore()
    {
        lock (_lock)
        {
            foreach (var kvp in _dcByDevice)
            {
                TryRestoreRamp(kvp.Value, kvp.Key);
            }
        }
    }

    private void TryRestoreRamp(IntPtr dc, string device)
    {
        if (_originalRamps.TryGetValue(device, out var orig))
        {
            NativeMethods.SetDeviceGammaRamp(dc, orig);
        }
        else
        {
            NativeMethods.SetDeviceGammaRamp(dc, LinearRamp());
        }
    }

    private static ushort[] LinearRamp()
    {
        var r = new ushort[3 * 256];
        for (int i = 0; i < 256; i++)
        {
            ushort v = (ushort)(i * 257); // 0..65535
            r[i] = v; r[i + 256] = v; r[i + 512] = v;
        }
        return r;
    }

    /// <summary>
    /// Build a gamma ramp that dims by `brightness` and tints by per-channel multipliers.
    /// Each channel = clamp(input * brightness * channelMult).
    /// </summary>
    private static ushort[] BuildRamp(double brightness, double rMult, double gMult, double bMult)
    {
        var ramp = new ushort[3 * 256];
        for (int i = 0; i < 256; i++)
        {
            double baseVal = i * 257.0; // 0..65535
            ramp[i]       = ClampU16(baseVal * brightness * rMult);
            ramp[i + 256] = ClampU16(baseVal * brightness * gMult);
            ramp[i + 512] = ClampU16(baseVal * brightness * bMult);
        }
        return ramp;
    }

    private static ushort ClampU16(double v)
    {
        if (v < 0) return 0;
        if (v > 65535) return 65535;
        return (ushort)v;
    }

    /// <summary>
    /// Convert a color temperature in Kelvin (1500..6500) to per-channel gamma multipliers.
    /// Values ~0.5..1.0. Based on Tanner Helland's black-body approximation, tuned for
    /// the subset we care about (1500–6500K).
    /// </summary>
    public static (double r, double g, double b) KelvinToRgbMultipliers(int kelvin)
    {
        double temp = kelvin / 100.0;
        double r, g, b;

        // Red
        if (temp <= 66) r = 255;
        else
        {
            r = temp - 60;
            r = 329.698727446 * Math.Pow(r, -0.1332047592);
        }

        // Green
        if (temp <= 66) g = 99.4708025861 * Math.Log(temp) - 161.1195681661;
        else
        {
            g = temp - 60;
            g = 288.1221695283 * Math.Pow(g, -0.0755148492);
        }

        // Blue
        if (temp >= 66) b = 255;
        else if (temp <= 19) b = 0;
        else b = 138.5177312231 * Math.Log(temp - 10) - 305.0447927307;

        return (
            Math.Clamp(r, 0, 255) / 255.0,
            Math.Clamp(g, 0, 255) / 255.0,
            Math.Clamp(b, 0, 255) / 255.0
        );
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock)
        {
            foreach (var kvp in _dcByDevice)
            {
                try { TryRestoreRamp(kvp.Value, kvp.Key); } catch { /* ignore */ }
                NativeMethods.DeleteDC(kvp.Value);
            }
            _dcByDevice.Clear();
            _originalRamps.Clear();
        }
    }
}
