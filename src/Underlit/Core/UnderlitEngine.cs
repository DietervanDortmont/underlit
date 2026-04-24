using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Underlit.Display;
using Underlit.Settings;
using Underlit.UI;

namespace Underlit.Core;

/// <summary>
/// Brightness model — TWO separate state variables that obey one invariant:
///   at most one of them is nonzero at any time.
///
///   _hardwareLevel : 0..100, a cached mirror of Windows' native WMI brightness.
///   _extendedDim   : 0..100, Underlit's own software dimming on top.
///
/// Pressing brightness-down drives _hardwareLevel first (exactly like the Fn key).
/// Only once _hardwareLevel reaches 0 does further pressing activate _extendedDim.
/// Pressing brightness-up reverses: _extendedDim unwinds first, then _hardwareLevel.
///
/// This replaces an earlier signed-level model whose synthetic -100..+100 could drift
/// from Windows' real brightness — so you could end up with a fully-dim screen even
/// though the Windows slider showed 100%.
///
/// Warmth is entirely independent (1500 K..6500 K on the gamma ramp).
///
/// The `CurrentBrightness` property exposes a signed -100..+100 value derived from
/// (_hardwareLevel, _extendedDim) purely for display/OSD purposes.
/// </summary>
public sealed class UnderlitEngine : IDisposable
{
    // ---- Inputs ----
    private AppSettings _settings;
    private readonly Dispatcher _dispatcher;

    // ---- Outputs / state ----
    public event Action<double, int>? LevelChanged; // (signed -100..+100, warmth K)

    /// <summary>Cached Windows hardware brightness, 0..100.</summary>
    public int HardwareLevel => _hardwareLevel;

    /// <summary>Underlit's own software dim on top, 0..100. Only nonzero when hardware is at 0.</summary>
    public int ExtendedDim => _extendedDim;

    /// <summary>Signed form for display: hardware in positive range, -extendedDim in negative.</summary>
    public double CurrentBrightness => _extendedDim > 0 ? -(double)_extendedDim : _hardwareLevel;

    public int CurrentWarmth { get; private set; } = 6500;
    public bool Paused { get; private set; }
    public bool Boosted => _savedHardware is not null;

    // ---- Dependencies ----
    private readonly GammaRampApplier _gamma;
    private readonly OverlayManager _overlays;
    private readonly HardwareBrightness _hardware;

    // ---- Core state ----
    private int _hardwareLevel = 100;
    private int _extendedDim = 0;
    private int _targetWarmth = 6500;

    // ---- Rendering state (what's actually on screen right now) ----
    // Software dim rampss smoothly; hardware snaps via WMI.
    private double _currentSignedLevel = 100;
    private double _targetSignedLevel = 100;
    private int _currentWarmthRendered = 6500;

    private DateTime _rampStart;
    private double _rampStartLevel;
    private int _rampStartWarmth;
    private DispatcherTimer? _rampTimer;

    // ---- Boost save/restore ----
    private int? _savedHardware;
    private int? _savedExtendedDim;
    private int? _savedWarmth;

    // ---- Displays ----
    private List<DisplayInfo> _displays = new();
    public IReadOnlyList<DisplayInfo> Displays => _displays;

    // Debounce: last-value we've sent via WMI/DDC-CI. Also the anchor for
    // "is this WMI read an external change or our own echo?" in the sync poller.
    private readonly Dictionary<string, int> _lastAppliedHardwarePct = new(StringComparer.OrdinalIgnoreCase);

    public UnderlitEngine(AppSettings settings, Dispatcher dispatcher,
                       GammaRampApplier gamma, OverlayManager overlays, HardwareBrightness hardware)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        _gamma = gamma;
        _overlays = overlays;
        _hardware = hardware;

        // Initialise from settings' persisted signed level.
        if (_settings.BrightnessLevel >= 0)
        {
            _hardwareLevel = (int)Math.Clamp(_settings.BrightnessLevel, 0, 100);
            _extendedDim = 0;
        }
        else
        {
            _hardwareLevel = 0;
            _extendedDim = (int)Math.Clamp(-_settings.BrightnessLevel, 0, 100);
        }
        _targetWarmth = Math.Clamp(_settings.WarmthKelvin, 1500, 6500);
        CurrentWarmth = _targetWarmth;
        _currentWarmthRendered = _targetWarmth;
        _currentSignedLevel = DerivedSigned();
        _targetSignedLevel = _currentSignedLevel;
    }

    public void RefreshDisplays(List<DisplayInfo> displays)
    {
        _displays = displays;
        _gamma.Register(displays);
        _hardware.Register(displays);
        _overlays.Sync(displays);
        ApplyNow();
    }

    public void UpdateSettings(AppSettings settings)
    {
        _settings = settings;
        _targetWarmth = Math.Clamp(_targetWarmth, 1500, 6500);
        ApplyNow();
    }

    // ---- User commands ----

    /// <summary>Step brightness. delta &lt; 0 dims, delta &gt; 0 brightens.</summary>
    public void StepBrightness(double delta)
    {
        int step = (int)Math.Round(Math.Abs(delta));
        if (step <= 0) return;

        if (delta < 0)
        {
            // Brightness DOWN: extended-dim in, or hardware down, or enter extended.
            if (_extendedDim > 0)
            {
                _extendedDim = Math.Clamp(_extendedDim + step, 0, 100);
            }
            else if (_hardwareLevel > 0)
            {
                _hardwareLevel = Math.Clamp(_hardwareLevel - step, 0, 100);
            }
            else
            {
                _extendedDim = Math.Clamp(step, 0, 100);
            }
        }
        else
        {
            // Brightness UP: extended-dim out, or hardware up.
            if (_extendedDim > 0)
            {
                _extendedDim = Math.Clamp(_extendedDim - step, 0, 100);
            }
            else if (_hardwareLevel < 100)
            {
                _hardwareLevel = Math.Clamp(_hardwareLevel + step, 0, 100);
            }
        }

        CommitBrightnessChange();
    }

    public void StepWarmth(int deltaKelvin) => SetWarmth(_targetWarmth + deltaKelvin);

    public void SetWarmth(int kelvin)
    {
        _targetWarmth = Math.Clamp(kelvin, 1500, 6500);
        _settings.WarmthKelvin = _targetWarmth;
        StartRamp();
        LevelChanged?.Invoke(CurrentBrightness, _targetWarmth);
    }

    /// <summary>Toggle — jumps to max and back.</summary>
    public void Boost()
    {
        if (_savedHardware is null)
        {
            _savedHardware = _hardwareLevel;
            _savedExtendedDim = _extendedDim;
            _savedWarmth = _targetWarmth;
            _hardwareLevel = 100;
            _extendedDim = 0;
            _targetWarmth = 6500;
        }
        else
        {
            _hardwareLevel = _savedHardware.Value;
            _extendedDim = _savedExtendedDim!.Value;
            _targetWarmth = _savedWarmth!.Value;
            _savedHardware = null;
            _savedExtendedDim = null;
            _savedWarmth = null;
        }
        CommitBrightnessChange();
        // Warmth ramp too:
        LevelChanged?.Invoke(CurrentBrightness, _targetWarmth);
    }

    public void TogglePaused()
    {
        Paused = !Paused;
        ApplyNow();
        LevelChanged?.Invoke(CurrentBrightness, _targetWarmth);
    }

    /// <summary>
    /// Called by BrightnessSyncPoller with Windows' current WMI brightness.
    /// If the reading is different from our last-written value, it's an external change
    /// (user moved the Quick Settings slider, etc). We adopt the external value and,
    /// if it went above 0 while extended-dim was active, cancel the extended-dim
    /// (external action wins — the user clearly wants a brighter screen).
    /// </summary>
    public void SyncFromExternalHardware(int hwPct)
    {
        hwPct = Math.Clamp(hwPct, 0, 100);

        var primaryId = _displays.FirstOrDefault(d => d.IsPrimary)?.StableId;
        if (primaryId != null
            && _lastAppliedHardwarePct.TryGetValue(primaryId, out var last)
            && Math.Abs(last - hwPct) <= 1)
        {
            return; // matches our echo — nothing external
        }

        _hardwareLevel = hwPct;
        if (hwPct > 0 && _extendedDim > 0) _extendedDim = 0;

        // We didn't write this value — Windows did — so mark it as already applied
        // so our next WMI write doesn't think we need to re-send.
        if (primaryId != null) _lastAppliedHardwarePct[primaryId] = hwPct;

        _settings.BrightnessLevel = DerivedSigned();
        _targetSignedLevel = DerivedSigned();
        _currentSignedLevel = _targetSignedLevel; // snap, don't ramp for external changes
        ApplySoftwareNow();
        LevelChanged?.Invoke(CurrentBrightness, _targetWarmth);
    }

    /// <summary>Scheduler baseline — warmth only now.</summary>
    public void ApplyScheduleBaselineWarmth(int warmth) => SetWarmth(warmth);

    // ---- Internal ----

    private double DerivedSigned() => _extendedDim > 0 ? -(double)_extendedDim : _hardwareLevel;

    private void CommitBrightnessChange()
    {
        _settings.BrightnessLevel = DerivedSigned();
        WriteHardwareAsync(_hardwareLevel);
        _targetSignedLevel = DerivedSigned();
        StartRamp();
        LevelChanged?.Invoke(CurrentBrightness, _targetWarmth);
    }

    private void StartRamp()
    {
        if (!_settings.SmoothRamping || _settings.RampDurationMs <= 10)
        {
            _currentSignedLevel = _targetSignedLevel;
            _currentWarmthRendered = _targetWarmth;
            ApplySoftwareNow();
            return;
        }

        _rampStartLevel = _currentSignedLevel;
        _rampStartWarmth = _currentWarmthRendered;
        _rampStart = DateTime.UtcNow;

        if (_rampTimer == null)
        {
            _rampTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16),
                DispatcherPriority.Render, OnRampTick, _dispatcher);
        }
        _rampTimer.Start();
    }

    private void OnRampTick(object? sender, EventArgs e)
    {
        var elapsed = (DateTime.UtcNow - _rampStart).TotalMilliseconds;
        var t = Math.Clamp(elapsed / Math.Max(1, _settings.RampDurationMs), 0, 1);
        t = 1 - (1 - t) * (1 - t); // ease-out quad

        _currentSignedLevel = _rampStartLevel + (_targetSignedLevel - _rampStartLevel) * t;
        _currentWarmthRendered = (int)(_rampStartWarmth + (_targetWarmth - _rampStartWarmth) * t);

        ApplySoftwareNow();

        if (t >= 1.0) _rampTimer?.Stop();
    }

    /// <summary>Applies everything (hardware + software) using current target values.</summary>
    private void ApplyNow()
    {
        WriteHardwareAsync(_hardwareLevel);
        _targetSignedLevel = DerivedSigned();
        _currentSignedLevel = _targetSignedLevel;
        _currentWarmthRendered = _targetWarmth;
        ApplySoftwareNow();
    }

    /// <summary>Only software layers (gamma + overlay), driven by _currentSignedLevel.</summary>
    private void ApplySoftwareNow()
    {
        CurrentWarmth = _currentWarmthRendered;

        if (Paused)
        {
            _overlays.SetOpacity(0);
            _gamma.Apply(1.0, 6500);
            return;
        }

        double level = _currentSignedLevel;
        double gamma, overlay;

        if (level >= 0)
        {
            // Positive range — hardware is doing the work; software stays neutral.
            gamma = 1.0;
            overlay = 0;
        }
        else
        {
            // Negative range — split descent: first half mostly via gamma (1.0 → 0.55),
            // second half brings overlay in (0 → 0.88) for the final darkening.
            double descent = Math.Clamp(-level / 100.0, 0, 1);
            gamma = 1.0 - descent * 0.45;
            overlay = descent < 0.5 ? 0 : (descent - 0.5) * 2 * 0.88;
        }

        _gamma.Apply(gamma, _currentWarmthRendered);
        ApplyOverlayToAll(overlay);
    }

    private void WriteHardwareAsync(int percent)
    {
        var snapshot = _displays.ToList();
        var perMon = new Dictionary<string, double>(
            _settings.PerMonitor.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.BrightnessOffset,
                StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        var lastSnap = new Dictionary<string, int>(_lastAppliedHardwarePct, StringComparer.OrdinalIgnoreCase);

        Task.Run(() =>
        {
            foreach (var d in snapshot)
            {
                double adj = percent;
                if (perMon.TryGetValue(d.StableId, out var off))
                    adj = Math.Clamp(percent + off, 0, 100);
                int target = (int)Math.Round(adj);

                if (lastSnap.TryGetValue(d.StableId, out int last) && last == target)
                    continue;

                if (_hardware.TrySet(d, target))
                {
                    _dispatcher.BeginInvoke((Action)(() =>
                        _lastAppliedHardwarePct[d.StableId] = target), DispatcherPriority.Background);
                }
            }
        });
    }

    private void ApplyOverlayToAll(double globalOpacity)
    {
        if (_settings.PerMonitor.Count == 0)
        {
            _overlays.SetOpacity(globalOpacity);
            return;
        }
        var perMon = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in _displays)
        {
            if (_settings.PerMonitor.TryGetValue(d.StableId, out var pm) && pm.BrightnessOffset != 0)
            {
                double deltaOpacity = -pm.BrightnessOffset / 100.0 * 0.88;
                perMon[d.StableId] = Math.Clamp(globalOpacity + deltaOpacity, 0, 0.92);
            }
        }
        _overlays.SetOpacityPerMonitor(perMon, globalOpacity);
    }

    public void Dispose()
    {
        _rampTimer?.Stop();
    }
}
