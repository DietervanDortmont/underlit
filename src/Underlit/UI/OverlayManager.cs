using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Underlit.Display;

namespace Underlit.UI;

/// <summary>
/// Owns one OverlayWindow per monitor. Rebuilds when displays change.
/// All calls marshalled to the UI thread by the caller (engine).
/// </summary>
public sealed class OverlayManager : IDisposable
{
    private readonly Dictionary<string, OverlayWindow> _overlays = new(StringComparer.OrdinalIgnoreCase);
    private double _currentOpacity;
    private bool _disposed;

    public void Sync(IReadOnlyList<DisplayInfo> displays)
    {
        if (_disposed) return;

        var currentNames = new HashSet<string>(displays.Select(d => d.DeviceName), StringComparer.OrdinalIgnoreCase);

        // Remove overlays for monitors that vanished.
        var stale = _overlays.Keys.Where(k => !currentNames.Contains(k)).ToList();
        foreach (var name in stale)
        {
            try { _overlays[name].Close(); } catch { /* ignore */ }
            _overlays.Remove(name);
        }

        // Add/update overlays for current monitors.
        foreach (var d in displays)
        {
            if (_overlays.TryGetValue(d.DeviceName, out var existing))
            {
                existing.ApplyBounds(d);
                existing.SetDimOpacity(_currentOpacity);
            }
            else
            {
                var w = new OverlayWindow(d);
                w.Show();
                w.SetDimOpacity(_currentOpacity);
                _overlays[d.DeviceName] = w;
            }
        }
    }

    /// <summary>Set opacity for all overlays (0 = invisible, 0.92 = almost black).</summary>
    public void SetOpacity(double opacity)
    {
        _currentOpacity = opacity;
        foreach (var w in _overlays.Values)
            w.SetDimOpacity(opacity);
    }

    /// <summary>Per-monitor opacity override. Falls back to global opacity if not specified.</summary>
    public void SetOpacityPerMonitor(IDictionary<string, double> perMonitor, double fallback)
    {
        _currentOpacity = fallback;
        foreach (var kvp in _overlays)
        {
            var opacity = perMonitor.TryGetValue(kvp.Key, out var v) ? v : fallback;
            kvp.Value.SetDimOpacity(opacity);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var w in _overlays.Values)
        {
            try { w.Close(); } catch { /* ignore */ }
        }
        _overlays.Clear();
    }
}
