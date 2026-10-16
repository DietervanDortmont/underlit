using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
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

    /// <summary>v0.6.74: while dim is active, re-assert HWND_TOPMOST
    /// every 500 ms so shell UI like Windows Search (which uses a
    /// higher-priority topmost class) can't render above the overlay.
    /// Stopped when opacity returns to 0 so we don't burn cycles when
    /// there's nothing to dim.</summary>
    private DispatcherTimer? _topmostTimer;
    private const int TopmostReassertIntervalMs = 500;

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
        UpdateTopmostTimerState();
    }

    /// <summary>Per-monitor opacity override. Falls back to global opacity if not specified.</summary>
    public void SetOpacityPerMonitor(IDictionary<string, double> perMonitor, double fallback)
    {
        _currentOpacity = fallback;
        bool anyVisible = false;
        foreach (var kvp in _overlays)
        {
            var opacity = perMonitor.TryGetValue(kvp.Key, out var v) ? v : fallback;
            kvp.Value.SetDimOpacity(opacity);
            if (opacity > 0) anyVisible = true;
        }
        UpdateTopmostTimerState(anyVisible || _currentOpacity > 0);
    }

    /// <summary>v0.6.74: start the periodic topmost re-assertion timer
    /// when at least one overlay is visible; stop it when everything
    /// fades back to 0. Cheap when active (a single SWP per monitor
    /// every 500 ms), zero-cost when stopped.</summary>
    private void UpdateTopmostTimerState(bool? forceActive = null)
    {
        bool active = forceActive ?? (_currentOpacity > 0);
        if (active)
        {
            if (_topmostTimer == null)
            {
                _topmostTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(TopmostReassertIntervalMs),
                };
                _topmostTimer.Tick += (_, _) =>
                {
                    foreach (var w in _overlays.Values) w.ReassertTopmost();
                };
            }
            if (!_topmostTimer.IsEnabled) _topmostTimer.Start();
        }
        else
        {
            _topmostTimer?.Stop();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _topmostTimer?.Stop();
        _topmostTimer = null;
        foreach (var w in _overlays.Values)
        {
            try { w.Close(); } catch { /* ignore */ }
        }
        _overlays.Clear();
    }
}
