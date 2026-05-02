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

    /// <summary>
    /// v0.6.48: the user's INTENDED warmth — i.e. what the next ramp is
    /// settling toward. Different from <see cref="CurrentWarmth"/>, which
    /// tracks the live mid-ramp gamma value. The OSD warmth bar should
    /// read TargetWarmth so a hotkey press snaps the bar to the new
    /// value immediately while the screen smoothly catches up.
    /// </summary>
    public int TargetWarmth => _targetWarmth;

    /// <summary>Same idea as <see cref="TargetWarmth"/> but for brightness.
    /// Negative values represent extended-dim below the OS minimum.</summary>
    public double TargetBrightness => _targetSignedLevel;

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
        // v0.6.40: refresh gamma/overlay using the CURRENT in-flight warmth
        // and brightness instead of snapping to target. The old ApplyNow
        // here aborted any active warmth ramp by setting
        // _currentWarmthRendered = _targetWarmth, which interfered with
        // schedule-graph drag previews when a settings push happened
        // mid-drag.
        ApplySoftwareNow();
    }

    /// <summary>
    /// v0.6.48: re-assert the current target on the screen. Called after
    /// the system wakes from sleep, where Windows blanks the gamma ramp
    /// back to neutral. We snap to the current target (no ramp) so the
    /// screen returns to what Underlit thinks should be on screen
    /// without a 5 second visible fade on every wake.
    /// </summary>
    public void ReapplyAfterResume()
    {
        _currentSignedLevel = _targetSignedLevel;
        _currentWarmthRendered = _targetWarmth;
        WriteHardwareAsync(_hardwareLevel);
        ApplySoftwareNow();
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

    /// <summary>
    /// Absolute brightness setter — used by the OSD's mouse-drag handler to jump
    /// the slider to wherever the user dropped it. Same engine state model as
    /// StepBrightness: positive maps to hardware level, negative to extended dim.
    /// </summary>
    public void SetSignedBrightness(double signedLevel)
    {
        int level = (int)Math.Round(Math.Clamp(signedLevel, -100, 100));
        if (level >= 0)
        {
            _hardwareLevel = level;
            _extendedDim   = 0;
        }
        else
        {
            _hardwareLevel = 0;
            _extendedDim   = -level;
        }
        CommitBrightnessChange();
    }

    /// <summary>
    /// User's persistent kelvin offset on top of the schedule baseline.
    /// v0.6.43: when the schedule is enabled and the user nudges warmth
    /// via hotkey, we accumulate the delta here instead of overwriting
    /// <c>_targetWarmth</c> outright. The next scheduler tick takes
    /// <c>scheduleBaseline + _userWarmthOffset</c>, so the user's nudge
    /// survives instead of being reset back to the schedule's value
    /// every 30 s. Reset to 0 when the user toggles the schedule off
    /// (manual mode owns warmth) or explicitly resets.
    /// </summary>
    private int _userWarmthOffset;

    /// <summary>The most recent schedule baseline (without user offset),
    /// captured by <see cref="ApplyScheduleBaselineWarmth"/>. Used by
    /// <see cref="StepWarmth"/> so a hotkey press in scheduled mode
    /// recomputes the displayed warmth as <c>baseline + offset</c>.</summary>
    private int _scheduleBaseline = 6500;

    public void StepWarmth(int deltaKelvin)
    {
        if (_settings.ScheduleEnabled)
        {
            // Schedule mode: hotkey shifts the OFFSET, not the absolute
            // warmth. Compute new total = baseline + new offset, clamped
            // to the legal range, then back-solve the offset that
            // actually fits so we don't accumulate offsets we couldn't
            // apply (would surprise the user later when the baseline
            // moved).
            int proposed = Math.Clamp(_scheduleBaseline + _userWarmthOffset + deltaKelvin, 1500, 6500);
            _userWarmthOffset = proposed - _scheduleBaseline;
            SetWarmth(proposed);
        }
        else
        {
            // Manual mode: hotkey moves the absolute warmth, no offset
            // tracking (the user OWNS the warmth value).
            SetWarmth(_targetWarmth + deltaKelvin);
        }
    }

    public void SetWarmth(int kelvin)
    {
        _targetWarmth = Math.Clamp(kelvin, 1500, 6500);
        _settings.WarmthKelvin = _targetWarmth;
        StartRamp();
        LevelChanged?.Invoke(CurrentBrightness, _targetWarmth);
    }

    /// <summary>Reset the persistent user warmth offset to zero. Called
    /// when the user disables the schedule (manual mode owns warmth) or
    /// from a Settings reset.</summary>
    public void ResetUserWarmthOffset() => _userWarmthOffset = 0;

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
    /// <summary>
    /// Scheduler tick callback. v0.6.43: respects
    /// <see cref="_userWarmthOffset"/> so a hotkey nudge in scheduled
    /// mode persists instead of being snapped back to the curve's
    /// current-time value every 30 seconds. The screen settles to
    /// <c>scheduleBaseline + userOffset</c>.
    /// </summary>
    public void ApplyScheduleBaselineWarmth(int warmth)
    {
        _scheduleBaseline = warmth;
        int target = Math.Clamp(_scheduleBaseline + _userWarmthOffset, 1500, 6500);
        // v0.6.45: schedule baseline transitions use a long, dedicated
        // ramp so the eye doesn't notice the per-tick warmth shift. The
        // user's RampDurationMs setting is used for hotkey-driven
        // transitions where they ARE expected to see the change.
        _targetWarmth = target;
        _settings.WarmthKelvin = _targetWarmth;
        StartRampWithDuration(ScheduleRampDurationMs);
        LevelChanged?.Invoke(CurrentBrightness, _targetWarmth);
    }

    /// <summary>Long, gentle ramp duration used by schedule baseline
    /// transitions. Five seconds is shorter than the 30 s scheduler tick
    /// (so the screen settles long before the next tick) but long enough
    /// that the colour shift reads as gradual rather than a discrete
    /// jump every 30 s.</summary>
    private const int ScheduleRampDurationMs = 5000;

    /// <summary>Start a ramp using a caller-specified duration instead
    /// of <see cref="AppSettings.RampDurationMs"/>. Used for schedule
    /// baseline transitions, which want a long fade independent of the
    /// user's hotkey ramp setting.</summary>
    private void StartRampWithDuration(int durationMs)
    {
        if (durationMs <= 10)
        {
            _currentSignedLevel = _targetSignedLevel;
            _currentWarmthRendered = _targetWarmth;
            ApplySoftwareNow();
            return;
        }
        _rampStartLevel  = _currentSignedLevel;
        _rampStartWarmth = _currentWarmthRendered;
        _rampStart       = DateTime.UtcNow;
        _isPreviewRamping = false;
        _explicitRampDurationMs = durationMs;
        if (_rampTimer == null)
        {
            _rampTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(50),
                DispatcherPriority.Render, OnRampTick, _dispatcher);
        }
        _rampTimer.Start();
    }

    /// <summary>If non-zero, OnRampTick uses this duration instead of
    /// <see cref="AppSettings.RampDurationMs"/>. Set by
    /// <see cref="StartRampWithDuration"/>; cleared once the ramp
    /// completes.</summary>
    private int _explicitRampDurationMs;

    /// <summary>Fixed ramp duration used by <see cref="PreviewWarmth"/>,
    /// independent of the user's <see cref="AppSettings.RampDurationMs"/>
    /// setting. Short enough to feel responsive while dragging a graph
    /// anchor, long enough to mask the gamma jump from "current screen
    /// warmth" to "dot's kelvin" on the first preview of a drag — that
    /// jump was the source of the flicker users saw on ramp-anchor drags
    /// in v0.6.34, where preview snapped instantaneously.</summary>
    private const int PreviewRampMs = 90;

    /// <summary>
    /// Transient warmth preview — used by the schedule-graph drag handler so
    /// the screen tracks the dragged point's kelvin in real time without
    /// committing the value to <see cref="AppSettings.WarmthKelvin"/>. Pair
    /// with <see cref="EndWarmthPreview"/> on mouse-up; the scheduler's next
    /// tick (≤30 s) will reassert the schedule's actual current-time warmth.
    ///
    /// v0.6.36: smooth-ramps to the new target over <see cref="PreviewRampMs"/>
    /// instead of snapping. v0.6.34's snap eliminated the multi-restart
    /// jitter the original implementation had, but introduced a different
    /// flicker: the very first preview of a drag teleported the screen
    /// from whatever warmth the schedule was holding (e.g. 6500 K mid-day)
    /// to the dragged anchor's kelvin (e.g. 2700 K for a deep-night
    /// anchor) in one frame. That single huge gamma jump was the
    /// "flicker around 3000 K" users were reporting. A short fixed-
    /// duration ramp smooths the first jump AND tracks subsequent
    /// drag-moves without piling up — each new call updates
    /// <see cref="_targetWarmth"/>, the ramp tick keeps interpolating
    /// from current rendered toward whatever the latest target is.
    /// </summary>
    public void PreviewWarmth(int kelvin)
    {
        int newTarget = Math.Clamp(kelvin, 1500, 6500);
        if (newTarget == _targetWarmth) return;
        _targetWarmth = newTarget;

        // Restart the ramp from CURRENT rendered (not the target), so a
        // stream of preview calls during a fast drag interpolates smoothly
        // through whatever the screen is currently showing — no per-call
        // snap, no per-call jump back to a stale start point.
        _rampStartWarmth = _currentWarmthRendered;
        _rampStartLevel  = _currentSignedLevel;
        _rampStart       = DateTime.UtcNow;
        _isPreviewRamping = true;

        if (_rampTimer == null)
        {
            _rampTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16),
                DispatcherPriority.Render, OnRampTick, _dispatcher);
        }
        _rampTimer.Start();

        LevelChanged?.Invoke(CurrentBrightness, _targetWarmth);
    }

    /// <summary>True while a <see cref="PreviewWarmth"/>-driven ramp is
    /// running. Causes <see cref="OnRampTick"/> to use
    /// <see cref="PreviewRampMs"/> as its duration instead of the user's
    /// <see cref="AppSettings.RampDurationMs"/> — preview tracking has to
    /// stay tight even when the user has set a long schedule-ramp duration.</summary>
    private bool _isPreviewRamping;

    /// <summary>Stop preview-driving warmth and resume from the saved manual
    /// warmth in settings. The scheduler will overwrite that on its next tick
    /// if it's enabled.</summary>
    public void EndWarmthPreview()
    {
        _isPreviewRamping = false;  // back to user's normal ramp duration
        _targetWarmth = Math.Clamp(_settings.WarmthKelvin, 1500, 6500);
        StartRamp();
        LevelChanged?.Invoke(CurrentBrightness, _targetWarmth);
    }

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
        // Three duration sources, in priority:
        //  1. Schedule baseline ramp (long, ~5 s) sets _explicitRampDurationMs.
        //  2. Drag preview (short, ~90 ms) sets _isPreviewRamping.
        //  3. Default — user's hotkey RampDurationMs setting.
        int durationMs;
        if (_explicitRampDurationMs > 0)
            durationMs = _explicitRampDurationMs;
        else if (_isPreviewRamping)
            durationMs = PreviewRampMs;
        else
            durationMs = Math.Max(1, _settings.RampDurationMs);
        var t = Math.Clamp(elapsed / durationMs, 0, 1);
        t = 1 - (1 - t) * (1 - t); // ease-out quad

        _currentSignedLevel = _rampStartLevel + (_targetSignedLevel - _rampStartLevel) * t;
        _currentWarmthRendered = (int)(_rampStartWarmth + (_targetWarmth - _rampStartWarmth) * t);

        ApplySoftwareNow();

        if (t >= 1.0)
        {
            _rampTimer?.Stop();
            _isPreviewRamping = false;
            _explicitRampDurationMs = 0;
        }
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

    // Coalesced hardware-write state.
    //
    // Background — when the OSD's mouse-drag fired a fresh Task.Run for every
    // mouse-move, a half-second of fast scrubbing queued ~30 tasks all blocking
    // on WMI/DDC writes (each ~50–100 ms). The user released the mouse and then
    // watched the hardware brightness slowly walk through the drag history for
    // a few seconds because all those queued tasks were still draining.
    //
    // Fix: at most one worker in flight. Callers update _pendingHwWrite with
    // the latest desired write (percent + the per-call snapshots that the
    // worker needs). The worker loops, picks up the latest pending value,
    // writes hardware, then re-checks. Intermediate values during a fast drag
    // get dropped — only the LAST one the user ended on is committed.
    private readonly object _hwLock = new();
    private bool _hwWriteInFlight;
    private (int Percent,
             List<DisplayInfo> Displays,
             Dictionary<string, double> PerMonOffsets,
             Dictionary<string, int> LastAppliedSnapshot)? _pendingHwWrite;

    private void WriteHardwareAsync(int percent)
    {
        // Snapshot the inputs at the call site so the worker doesn't read
        // _displays / _settings / _lastAppliedHardwarePct from another thread.
        var snapshot = _displays.ToList();
        var perMon = new Dictionary<string, double>(
            _settings.PerMonitor.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.BrightnessOffset,
                StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        var lastSnap = new Dictionary<string, int>(_lastAppliedHardwarePct, StringComparer.OrdinalIgnoreCase);

        bool kickoff;
        lock (_hwLock)
        {
            // Replace any previously-pending write — we don't care about
            // intermediate values during a fast drag.
            _pendingHwWrite = (percent, snapshot, perMon, lastSnap);
            kickoff = !_hwWriteInFlight;
            if (kickoff) _hwWriteInFlight = true;
        }
        if (!kickoff) return;  // existing worker will pick up the latest pending value

        Task.Run(() =>
        {
            while (true)
            {
                (int Percent,
                 List<DisplayInfo> Displays,
                 Dictionary<string, double> PerMonOffsets,
                 Dictionary<string, int> LastAppliedSnapshot) work;

                lock (_hwLock)
                {
                    if (_pendingHwWrite is null)
                    {
                        _hwWriteInFlight = false;
                        return;
                    }
                    work = _pendingHwWrite.Value;
                    _pendingHwWrite = null;
                }

                foreach (var d in work.Displays)
                {
                    double adj = work.Percent;
                    if (work.PerMonOffsets.TryGetValue(d.StableId, out var off))
                        adj = Math.Clamp(work.Percent + off, 0, 100);
                    int target = (int)Math.Round(adj);

                    if (work.LastAppliedSnapshot.TryGetValue(d.StableId, out int last) && last == target)
                        continue;

                    if (_hardware.TrySet(d, target))
                    {
                        _dispatcher.BeginInvoke((Action)(() =>
                            _lastAppliedHardwarePct[d.StableId] = target), DispatcherPriority.Background);
                    }
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
