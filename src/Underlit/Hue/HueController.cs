using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Underlit.Core;
using Underlit.Settings;

namespace Underlit.Hue;

/// <summary>
/// Drives the user's Hue lights to follow the screen's warmth target plus an
/// optional kelvin offset, with an independent brightness slider.
///
/// <para>v0.6.53 - per-light push with gamut-aware fade-out and manual-off
/// coexistence. Group selections are expanded to their member lights, merged
/// with the user's per-light selections, deduped, then driven individually.
/// Per-light writes are rate-limited at 10 commands/sec on the bridge (vs
/// 1/sec for group writes), so per-light is actually faster than the old
/// per-group push - it's just chattier on the wire.</para>
///
/// <para>Three layers of behaviour per bulb, applied on every push tick:</para>
/// <list type="number">
///   <item><b>Manual-off respect (Phase C).</b> Before each push we GET the
///         bulb's current state.on. If the user turned it off via Google Home
///         / the Hue app / a wall switch, we don't push CT/bri AND we never
///         send <c>on:true</c> to bring it back. The exception is bulbs we
///         turned off ourselves via the fade-out below: those we DO bring
///         back when the schedule warmth comes back into range.</item>
///   <item><b>Gamut-aware fade-out (Phase B).</b> Each bulb's
///         <c>capabilities.control.ct.{min,max}</c> tells us its CT range.
///         If the schedule warmth is approaching the bulb's deepest-warm
///         limit (last 20% of its mireds range), we ramp brightness toward
///         zero. Past the limit we send <c>on:false</c> and remember we
///         did so. When the warmth recovers we re-enable + restore brightness.
///         This makes IKEA Tradfri tunables fade out gracefully through the
///         evening instead of clamping at 2200 K and looking wrong against
///         deep-amber Hue bulbs.</item>
///   <item><b>Push (Circadian or WhiteToRed).</b> Whatever's left after the
///         two layers above turns into a single <c>SetLightStateAsync</c> or
///         <c>SetLightColorAsync</c> call.</item>
/// </list>
///
/// <para>Coalesced writer: at most one write train in flight; intermediate
/// warmth/brightness changes during a fast slider drag are dropped, only the
/// latest pending values are written. Same single-worker / latest-pending
/// pattern as the brightness hardware writer.</para>
/// </summary>
public sealed class HueController : IDisposable
{
    private readonly UnderlitEngine _engine;
    private AppSettings _settings;
    private HueBridgeClient? _client;
    private string? _lastBridgeIp;
    private string? _lastBridgeUsername;

    private readonly object _lock = new();
    private bool _writeInFlight;
    /// <summary>v0.6.59: pending push batch. Each PushNow REPLACES the
    /// whole batch (latest-wins), and the worker drains the batch in
    /// one tick - pushing every slice with delays in between. Simpler
    /// and more obviously correct than the per-key dictionary I tried
    /// in 0.6.58 (which had a subtle bug where rapid simulation ticks
    /// could leave the worker idle if it drained the dict between
    /// PushNow calls).</summary>
    private List<GroupPush>? _pendingBatch;

    private sealed record GroupPush(
        string Id,
        int Kelvin,
        int Brightness,
        HashSet<string> LightIds,
        HashSet<string> GroupIds,
        HueColorRangeMode Range,
        List<HueGradientStop> Stops)
    {
        /// <summary>v0.7.0: true when this slice was sampled from the
        /// time-of-day stops instead of the screen warmth.</summary>
        public bool TimeMode { get; init; }
        public byte R { get; init; }
        public byte G { get; init; }
        public byte B { get; init; }
        /// <summary>0..100 from the stops, before the master brightness.</summary>
        public double StopBrightnessPercent { get; init; }
        public int NextStopMinute { get; init; }
    }

    /// <summary>Per-bulb shadow state, keyed by light id. Built lazily as
    /// we discover bulbs via the bridge. Holds capabilities (so we don't
    /// re-fetch them on every push) and tracking flags for the manual-off
    /// + auto-fade logic. ConcurrentDictionary because PushOneLight runs
    /// in parallel via Task.WhenAll and may insert on bulb-not-yet-seen.</summary>
    private readonly ConcurrentDictionary<string, LightShadow> _shadows = new();

    /// <summary>How long shadow capability data is good for before we
    /// re-fetch from the bridge. 5 minutes is short enough to pick up a
    /// freshly-paired bulb and long enough to avoid a thundering herd of
    /// /lights GETs on rapid hotkey presses.</summary>
    private static readonly TimeSpan CapabilitiesTtl = TimeSpan.FromMinutes(5);

    public HueController(UnderlitEngine engine, AppSettings settings)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _engine.LevelChanged += OnEngineLevelChanged;
        ReinitClientIfNeeded();

        // v0.7.0: in time-of-day mode the lamps change with the clock, not
        // with the screen warmth (which is flat all day), so we need our
        // own tick. Also catches external changes (Google etc.) within 30 s.
        var dispatcher = System.Windows.Application.Current?.Dispatcher
                         ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
        _clockTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _clockTimer.Tick += (_, _) =>
        {
            if (_settings.HueColorSource == HueColorSource.TimeOfDay && SimulatedNow == null)
                PushNow();
        };
        _clockTimer.Start();
    }

    private readonly System.Windows.Threading.DispatcherTimer _clockTimer;

    /// <summary>v0.7.0: set by the host while the 24 h simulator runs so
    /// time-of-day stops follow the simulated clock. Null = real time.</summary>
    public DateTime? SimulatedNow { get; set; }

    private DateTime Now => SimulatedNow ?? DateTime.Now;

    private volatile bool _pausedForPreview;
    /// <summary>v0.7.0: true while the colour-stop editor previews a colour
    /// on the bulbs. Nothing is pushed meanwhile; switching it back off
    /// takes every bulb back and re-applies the schedule.</summary>
    public bool PausedForPreview
    {
        get => _pausedForPreview;
        set
        {
            if (_pausedForPreview == value) return;
            _pausedForPreview = value;
            if (!value) ResumeAll();
        }
    }

    /// <summary>v0.7.0: raised (on a worker thread) when a bulb is
    /// changed by something other than Underlit and Underlit backs off.
    /// Argument is the bulb's name.</summary>
    public event Action<string>? LightTakenOver;

    /// <summary>v0.7.0: number of bulbs currently left alone because
    /// someone else changed them.</summary>
    public int OverriddenCount => _shadows.Values.Count(s => s.ManualOverride);

    /// <summary>v0.7.0: take back every bulb that was changed by Google /
    /// the Hue app / a switch. With <paramref name="turnOn"/> also switch
    /// selected bulbs that are off back on (the "Resume schedule" quick
    /// scene); without it, bulbs that are off stay off.</summary>
    public void ResumeAll(bool turnOn = false)
    {
        int released = 0;
        foreach (var sh in _shadows.Values)
        {
            if (sh.ManualOverride) released++;
            ClearOverride(sh, reason: null);
            if (turnOn) sh.ResumeRequested = true;
        }
        if (released > 0 || turnOn)
            Logger.Info($"Hue: resume requested (released {released} overridden bulb(s), turnOn={turnOn})");
        PushNow();
    }

    /// <summary>v0.6.62: kept for backward compat; no longer read.
    /// Profiles now ALWAYS use engine.TargetWarmth so they follow
    /// schedule + simulator alike with no special-casing.</summary>
    public bool IsSimulating { get; set; }

    /// <summary>Swap in new settings (called from UnderlitHost.ApplySettings).</summary>
    public void UpdateSettings(AppSettings newSettings)
    {
        _settings = newSettings ?? throw new ArgumentNullException(nameof(newSettings));
        ReinitClientIfNeeded();
        PushNow();
    }

    /// <summary>Push the current settings + engine warmth straight to the
    /// bridge. v0.6.58: builds N+1 push slices when colour groups exist:
    /// one per LightColorGroup (with its own gradient + time-offset
    /// computed kelvin) plus a "default" slice catching anything not
    /// claimed by any group.</summary>
    public void PushNow()
    {
        if (_client == null) return;
        if (_pausedForPreview) return;

        bool hasGroups = _settings.HueSelectedGroupIds.Count > 0;
        bool hasLights = _settings.HueSelectedLightIds.Count > 0;
        if (!hasGroups && !hasLights) return;

        if (_engine.Boosted && _settings.HueIgnoreBoost) return;

        int hueBri = Math.Clamp(_settings.HueBrightness, 1, 254);
        bool timeMode = _settings.HueColorSource == HueColorSource.TimeOfDay;
        DateTime now = Now;
        var defaultTimeStops = _settings.HueTimeStops;
        int screenKelvin = _engine.TargetWarmth;
        int defaultKelvin = Math.Clamp(screenKelvin + _settings.HueWarmthOffsetKelvin, 1500, 6500);
        var defaultStops = _settings.HueGradientStops
            .Select(s => new HueGradientStop { Kelvin = s.Kelvin, Color = s.Color })
            .ToList();

        var colorGroups = _settings.ColorGroups ?? new List<LightColorGroup>();

        // Track which lights/groups are claimed by colour profiles so
        // the default slice doesn't double-push them.
        var claimedLights = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var claimedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var grp in colorGroups)
        {
            if (!grp.Enabled) continue;
            foreach (var lid in grp.LightIds) claimedLights.Add(lid);
            foreach (var gid in grp.GroupIds) claimedGroups.Add(gid);
        }

        // Build the batch in fixed order: default slice first, then one
        // entry per enabled colour profile.
        var batch = new List<GroupPush>();

        var defaultLights = new HashSet<string>(_settings.HueSelectedLightIds, StringComparer.OrdinalIgnoreCase);
        defaultLights.ExceptWith(claimedLights);
        var defaultGroupSet = new HashSet<string>(_settings.HueSelectedGroupIds, StringComparer.OrdinalIgnoreCase);
        defaultGroupSet.ExceptWith(claimedGroups);
        if (defaultLights.Count > 0 || defaultGroupSet.Count > 0)
        {
            batch.Add(WithTimeSample(new GroupPush(
                "default", defaultKelvin, hueBri,
                defaultLights, defaultGroupSet,
                _settings.HueColorRange, defaultStops), timeMode, defaultTimeStops, now));
        }

        foreach (var grp in colorGroups)
        {
            if (!grp.Enabled) continue;
            if (grp.LightIds.Count == 0 && grp.GroupIds.Count == 0) continue;

            // v0.6.62: profile bulbs follow the engine's current target
            // warmth verbatim - same as the default slice. This is the
            // simplification step the user asked for: one source of
            // truth, no time-shifted second curve. Customisation moves
            // to the per-profile gradient (kelvin -> colour mapping).
            int grpKelvin = defaultKelvin;

            var grpStops = (grp.Gradient is { Count: > 0 } ? grp.Gradient : defaultStops)
                .Select(s => new HueGradientStop { Kelvin = s.Kelvin, Color = s.Color })
                .ToList();

            var grpTimeStops = grp.TimeStops is { Count: > 0 } ? grp.TimeStops : defaultTimeStops;
            batch.Add(WithTimeSample(new GroupPush(
                grp.Id, grpKelvin, hueBri,
                new HashSet<string>(grp.LightIds, StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(grp.GroupIds, StringComparer.OrdinalIgnoreCase),
                _settings.HueColorRange, grpStops), timeMode, grpTimeStops, now));
        }

        if (batch.Count == 0) return;
        EnqueueBatch(batch);
    }

    private GroupPush WithTimeSample(GroupPush p, bool timeMode, List<HueTimeStop> stops, DateTime now)
    {
        if (!timeMode) return p;
        var smp = TimeStopSampler.At(stops, _settings, now);
        return p with
        {
            TimeMode = true,
            R = smp.R, G = smp.G, B = smp.B,
            StopBrightnessPercent = smp.BrightnessPercent,
            NextStopMinute = smp.NextStopMinute,
        };
    }

    /// <summary>v0.6.59: replace the pending batch with a fresh one.
    /// Latest-wins: a flurry of schedule ticks overwrites previous
    /// batches before the worker has had a chance to drain them.</summary>
    private void EnqueueBatch(List<GroupPush> batch)
    {
        bool kickoff;
        lock (_lock)
        {
            _pendingBatch = batch;
            kickoff = !_writeInFlight;
            if (kickoff) _writeInFlight = true;
        }
        if (!kickoff) return;
        Task.Run(WriteWorker);
    }

    private async Task WriteWorker()
    {
        while (true)
        {
            List<GroupPush>? batch;
            HueBridgeClient? client;
            lock (_lock)
            {
                if (_pendingBatch == null || _pendingBatch.Count == 0)
                {
                    _writeInFlight = false;
                    return;
                }
                batch  = _pendingBatch;
                client = _client;
                _pendingBatch = null;
            }
            if (client == null)
            {
                // No bridge client - bail clean rather than busy-loop.
                lock (_lock) _writeInFlight = false;
                return;
            }

            // v0.7.1: every bulb belongs to exactly one slice. Before this a
            // bulb that sat in a colour group AND in a selected Hue room got
            // pushed twice per tick (group colour, then default colour).
            // Colour groups win in list order; Default gets what's left.
            Dictionary<string, HashSet<string>> finalLights;
            try { finalLights = await ResolveExclusiveLights(client, batch); }
            catch (Exception ex) { Logger.Warn("Hue: resolving light assignment failed", ex); finalLights = new(); }

            // Push each slice in turn, delaying between to honour the
            // bridge's per-light rate limit. Failures of one slice
            // mustn't kill the rest.
            for (int i = 0; i < batch.Count; i++)
            {
                var slice = batch[i];
                if (!finalLights.TryGetValue(slice.Id, out var lights) || lights.Count == 0) continue;
                try { await PushLights(client, slice, lights); }
                catch (Exception ex) { Logger.Warn($"Hue push failed for slice '{slice.Id}'", ex); }
                if (i + 1 < batch.Count) await Task.Delay(MinWriteIntervalMs);
            }
            // Delay before checking for a new batch so back-to-back
            // PushNow() calls don't slam the bridge.
            await Task.Delay(MinWriteIntervalMs);
        }
    }

    private void OnEngineLevelChanged(double brightness, int warmth)
    {
        // Only warmth changes make us push; screen brightness is independent
        // from Hue brightness (which has its own slider/hotkey).
        PushNow();
    }

    private void ReinitClientIfNeeded()
    {
        bool ipChanged   = _settings.HueBridgeIp       != _lastBridgeIp;
        bool userChanged = _settings.HueBridgeUsername != _lastBridgeUsername;
        if (!ipChanged && !userChanged && _client != null) return;

        _client?.Dispose();
        _client = null;
        _shadows.Clear(); // bridge swap = stale shadows
        _lastBridgeIp       = _settings.HueBridgeIp;
        _lastBridgeUsername = _settings.HueBridgeUsername;

        if (!string.IsNullOrEmpty(_settings.HueBridgeIp)
            && !string.IsNullOrEmpty(_settings.HueBridgeUsername))
        {
            _client = new HueBridgeClient(_settings.HueBridgeIp!, _settings.HueBridgeUsername);
        }
    }

    /// <summary>
    /// Minimum interval between consecutive write trains. Per-light commands
    /// can theoretically run at 10/sec per the Hue dev docs, but a write
    /// "train" can include ~10 lights in parallel, so spacing the trains
    /// at ~250 ms (4/sec) keeps the bridge happy on fan-outs to many
    /// bulbs without making slider drags feel laggy.
    /// </summary>
    private const int MinWriteIntervalMs = 250;

    /// <summary>Snappy 100 ms transitiontime so a hotkey or slider drag
    /// visibly tracks instead of feeling like a laggy fade.</summary>
    private const int SnappyTransitionDeciseconds = 1;

    // v0.6.55: FadeZoneFraction removed. The fade window is now derived
    // from the schedule's deepest-warm anchor + the user's
    // HueBulbFadeOnsetKelvin tunable, so CT-limited bulbs (e.g. IKEA
    // Tradfri) gradually dim from full bright at their CT limit down to
    // off at the schedule's deepest amber. See ComputeFadeFactor.

    /// <summary>
    /// One push tick. Resolves the slice's group + light selections to
    /// a final, deduped per-light list, walks each light through the
    /// manual-off / fade-out logic, and issues the single appropriate
    /// PUT. All lights are pushed in parallel.
    /// </summary>
    /// <summary>v0.7.1: expand each slice's Hue rooms to bulbs and give
    /// every bulb to exactly one slice: colour groups first (in order),
    /// then the default slice.</summary>
    private async Task<Dictionary<string, HashSet<string>>> ResolveExclusiveLights(HueBridgeClient client, List<GroupPush> batch)
    {
        var allLights = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sl in batch) { allLights.UnionWith(sl.LightIds); allGroups.UnionWith(sl.GroupIds); }
        await EnsureShadowsFresh(client, allLights, allGroups);

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var sl in batch.Where(b => b.Id != "default").Concat(batch.Where(b => b.Id == "default")))
        {
            var set = new HashSet<string>(sl.LightIds, StringComparer.OrdinalIgnoreCase);
            foreach (var gid in sl.GroupIds)
                if (_groupMembers.TryGetValue(gid, out var members))
                    foreach (var lid in members) set.Add(lid);
            set.ExceptWith(taken);
            taken.UnionWith(set);
            result[sl.Id] = set;
        }
        return result;
    }

    /// <summary>One push tick for one slice's (already resolved) bulbs,
    /// all in parallel.</summary>
    private async Task PushLights(HueBridgeClient client, GroupPush work, HashSet<string> lightIds)
    {
        var tasks = new List<Task>(lightIds.Count);
        foreach (var lightId in lightIds)
            tasks.Add(PushOneLight(client, lightId, work));
        await Task.WhenAll(tasks);
    }

    /// <summary>Per-light push logic. See <see cref="PushLights"/>
    /// summary for the high-level flow.</summary>
    private async Task PushOneLight(HueBridgeClient client, string lightId, GroupPush work)
    {
        if (!_shadows.TryGetValue(lightId, out var shadow))
        {
            // Unknown bulb (selected but not yet seen). Fetch on demand.
            var fetched = await client.GetLightAsync(lightId);
            if (fetched == null) return;
            // GetOrAdd handles the race where two parallel PushOneLight
            // calls both try to add the same light id - whoever wins, we
            // take their value.
            shadow = _shadows.GetOrAdd(lightId, _ => LightShadow.FromBridge(fetched));
        }

        // v0.6.55: skip smart plugs entirely. The schedule warmth+brightness
        // is meaningless for an on/off-only device, and pushing it makes
        // the bridge log harmless 4xx errors. Plugs are controlled via
        // routines / Quick Scenes instead.
        if (shadow.IsPlug) return;

        // Phase C: GET current state (cheap, ~30 ms LAN). Drives the
        // manual-off respect logic and (v0.7.0) external-change detection.
        var current = await client.GetLightAsync(lightId);
        if (current == null) return;

        bool currentOn   = current.On;
        bool currentReachable = current.Reachable;
        if (!currentReachable) return; // bulb offline / unplugged - don't touch

        // v0.7.0: "Resume schedule" quick scene - take the bulb back and
        // switch it on even if it is off right now.
        bool resume = shadow.ResumeRequested;
        if (resume)
        {
            shadow.ResumeRequested = false;
            ClearOverride(shadow, reason: null);
        }

        // Manual-off detection: bulb is off, we last saw it on (or never
        // sent), AND we didn't fade it off ourselves. -> the user
        // (or a routine, or Google) turned it off. Honour that.
        bool weTurnedItOff = shadow.AutoFadedOff;
        if (!currentOn && !weTurnedItOff && !resume)
        {
            // v0.7.0: switching a bulb off ends a manual override (unless
            // the user asked to hold overrides until an explicit Resume).
            if (shadow.ManualOverride && _settings.HueOverrideRelease != HueOverrideRelease.UntilResume)
                ClearOverride(shadow, "it was switched off");
            // Update shadow so we don't re-trigger this branch needlessly.
            shadow.LastKnownOn = false;
            shadow.LastSentOn  = null;
            shadow.Expected    = null;
            return;
        }

        // Manual-on after our auto-fade: user overrode our fade-off
        // (e.g. "Hey Google, turn on the lamp"). v0.7.0: they clearly
        // want it on, so hand the bulb to them - previously we pushed
        // again and the next fade tick switched it straight back off.
        if (currentOn && shadow.AutoFadedOff && !resume)
        {
            shadow.AutoFadedOff = false;
            shadow.LastKnownOn  = true;
            MarkOverride(shadow, work, "it was switched on while Underlit had faded it out");
            return;
        }

        // Switched on by someone else after a manual off: there's no
        // baseline to compare against, so adopt it into the schedule.
        if (currentOn && shadow.LastKnownOn == false)
            shadow.Expected = null;
        shadow.LastKnownOn = currentOn;

        // v0.7.0: bulb is being left alone because someone else changed it.
        if (shadow.ManualOverride)
        {
            if (shadow.OverrideUntil.HasValue && Now >= shadow.OverrideUntil.Value)
                ClearOverride(shadow, "the next colour stop was reached");
            else
                return;
        }

        // v0.7.0: external-change detection. Compare what the bulb reports
        // now with what it reported right after our last write. Skipped
        // while one of our own writes is still unconfirmed, so our own
        // fades and slider drags never count as "someone else".
        if (!resume && currentOn
            && shadow.Expected is { } expected
            && shadow.WriteSeq == shadow.ConfirmedSeq
            && Fingerprint.Differs(expected, current, out var what))
        {
            MarkOverride(shadow, work, what);
            return;
        }

        bool mayTurnOn = shadow.AutoFadedOff || resume;

        // v0.7.0: time-of-day mode. Colour + brightness come straight from
        // the timeline stops; no kelvin gradient, no CT fade-out.
        if (work.TimeMode)
        {
            await PushTimeMode(client, lightId, shadow, work, currentOn, mayTurnOn);
            return;
        }

        // Phase B: gamut-aware fade-out. For colour-or-CT bulbs in
        // WhiteToRed mode we're driving xy directly so the bulb's CT
        // range doesn't apply - just push the colour. For CT-only bulbs
        // (and CT mode on any bulb) we may need to reduce brightness or
        // turn off as warmth runs past the bulb's max-mireds limit.
        bool wantOff = false;
        double fadeFactor = 1.0;
        int desiredMireds = HueBridgeClient.KelvinToMireds(work.Kelvin);
        if (work.Range == HueColorRangeMode.WhiteToRed && shadow.HasColor)
        {
            // Full-colour bulb in colour mode: no CT-range fade.
            fadeFactor = 1.0;
        }
        else
        {
            // v0.6.55: fade factor is now derived from the schedule's
            // deepest-warm anchor, not a fixed % of the bulb's range.
            int scheduleDeepestMireds = ComputeScheduleDeepestMireds();
            fadeFactor = ComputeFadeFactor(desiredMireds, shadow,
                                            scheduleDeepestMireds,
                                            _settings.HueBulbFadeOnsetKelvin);
            if (fadeFactor <= 0.0) wantOff = true;
        }

        // -------------------------------------------------------
        // Decide what to send.
        // -------------------------------------------------------
        if (wantOff)
        {
            if (currentOn) await FadeOff(client, lightId, shadow);
            // else: already off (we faded it off earlier). No-op.
            return;
        }

        // Bulb should be on, possibly at reduced brightness due to fade.
        int actualBri = Math.Max(1, (int)Math.Round(work.Brightness * fadeFactor));

        if (work.Range == HueColorRangeMode.WhiteToRed && shadow.HasColor)
        {
            // v0.6.62: black gradient stops = "bulb off". Sample the
            // gradient in sRGB at this kelvin; if the resulting colour
            // is near-black, treat the gradient as instructing the bulb
            // to turn off rather than pushing a near-zero xy.
            var (gr, gg, gb) = SampleGradientRgb(work.Kelvin, work.Stops);
            int sumRgb = gr + gg + gb;
            if (sumRgb < 24) // ~8/255 average channel = essentially black
            {
                if (currentOn) await FadeOff(client, lightId, shadow);
                return;
            }
            (double x, double y) = GradientXY(work.Kelvin, work.Stops);
            bool ok = await client.SetLightColorAsync(
                lightId, x, y,
                brightness254: actualBri,
                transitionDeciseconds: SnappyTransitionDeciseconds);
            if (ok)
            {
                shadow.LastSentOn = true;
                shadow.AutoFadedOff = false;
                ScheduleConfirm(client, lightId, shadow, SnappyTransitionDeciseconds);
            }
            return;
        }

        // CT mode (or WhiteToRed on a CT-only bulb): clamp mireds to the
        // bulb's range so we never send out-of-range values.
        int clampedMireds = ClampMireds(desiredMireds, shadow);

        bool? sendOn = null;
        if (!currentOn && mayTurnOn)
        {
            // We previously faded it off and the bulb's still off. The
            // schedule warmth has come back into range, so revive it.
            sendOn = true;
        }
        // If currentOn is already true, sendOn stays null - we don't
        // touch the on state, just CT/bri. This is what makes Underlit
        // play nicely with manual on/off.

        bool ok2 = await client.SetLightStateAsync(
            lightId, on: sendOn,
            mireds: clampedMireds, brightness254: actualBri,
            transitionDeciseconds: SnappyTransitionDeciseconds);
        if (ok2)
        {
            shadow.LastSentBri = actualBri;
            shadow.LastSentCt  = clampedMireds;
            if (sendOn == true)
            {
                shadow.LastSentOn   = true;
                shadow.AutoFadedOff = false;
            }
            else if (currentOn)
            {
                shadow.LastSentOn = true;
            }
            ScheduleConfirm(client, lightId, shadow, SnappyTransitionDeciseconds);
        }
    }

    /// <summary>v0.7.0: transition for clock-driven time-of-day pushes.
    /// 1 s keeps the 30 s steps invisible without making edits feel laggy.</summary>
    private const int TimeModeTransitionDeciseconds = 10;

    private async Task PushTimeMode(HueBridgeClient client, string lightId, LightShadow shadow,
                                    GroupPush work, bool currentOn, bool mayTurnOn)
    {
        double master = Math.Clamp(_settings.HueBrightness, 1, 254) / 254.0;
        // v0.7.1: per-bulb mapping. Colour bulbs get the colour; white-only
        // bulbs get the nearest white and dim out as the colour moves past
        // what they can make (warm white -> orange), then switch off.
        var cmd = BulbColorMapper.For(
            shadow.HasColor, shadow.CtMinMireds.HasValue || shadow.CtMaxMireds.HasValue,
            shadow.CtMinMireds, shadow.CtMaxMireds,
            work.R, work.G, work.B, work.StopBrightnessPercent, master,
            _settings.HueWhiteBulbFollowPercent);

        if (cmd.Off)
        {
            if (currentOn) await FadeOff(client, lightId, shadow);
            return;
        }
        if (!currentOn && !mayTurnOn) return;

        bool ok = cmd.X.HasValue && cmd.Y.HasValue
            ? await client.SetLightColorAsync(lightId, cmd.X.Value, cmd.Y.Value, brightness254: cmd.Bri254,
                                              transitionDeciseconds: TimeModeTransitionDeciseconds)
            : await client.SetLightStateAsync(lightId, on: currentOn ? null : true,
                                              mireds: cmd.Mireds.HasValue ? ClampMireds(cmd.Mireds.Value, shadow) : null,
                                              brightness254: cmd.Bri254,
                                              transitionDeciseconds: TimeModeTransitionDeciseconds);
        if (ok)
        {
            shadow.LastSentOn   = true;
            shadow.LastSentBri  = cmd.Bri254;
            shadow.AutoFadedOff = false;
            ScheduleConfirm(client, lightId, shadow, TimeModeTransitionDeciseconds);
        }
    }

    /// <summary>Switch a bulb off because the schedule says so (fade-out,
    /// black gradient stop, "off" time stop). Remembered so we're allowed
    /// to bring it back later, unlike a manual off.</summary>
    private static async Task FadeOff(HueBridgeClient client, string lightId, LightShadow shadow)
    {
        bool ok = await client.SetLightStateAsync(
            lightId, on: false, mireds: null, brightness254: null,
            transitionDeciseconds: SnappyTransitionDeciseconds);
        if (ok)
        {
            shadow.LastSentOn   = false;
            shadow.AutoFadedOff = true;
            shadow.Expected     = null;
            Interlocked.Increment(ref shadow.WriteSeq);
            shadow.ConfirmedSeq = shadow.WriteSeq;
        }
    }

    // -------------------------------------------------------------------------
    // v0.7.0: external-change (Google Home / Hue app / switch) handling
    // -------------------------------------------------------------------------

    /// <summary>After each successful write, wait for the transition to
    /// finish and read the bulb back. That readback (not what we sent) is
    /// the baseline, because the bridge clamps colours to each bulb's
    /// gamut and rounds brightness. Only the latest write's readback counts.</summary>
    private static void ScheduleConfirm(HueBridgeClient client, string lightId, LightShadow shadow, int transitionDeciseconds)
    {
        int seq = Interlocked.Increment(ref shadow.WriteSeq);
        _ = Task.Run(async () =>
        {
            await Task.Delay(transitionDeciseconds * 100 + 1500);
            if (Volatile.Read(ref shadow.WriteSeq) != seq) return; // a newer write superseded us
            HueLight? rb = null;
            try { rb = await client.GetLightAsync(lightId); } catch { /* client disposed */ }
            if (Volatile.Read(ref shadow.WriteSeq) != seq) return;
            shadow.Expected = rb != null && rb.On ? Fingerprint.From(rb) : null;
            shadow.ConfirmedSeq = seq;
        });
    }

    private void MarkOverride(LightShadow shadow, GroupPush work, string what)
    {
        if (_settings.HueOverrideRelease == HueOverrideRelease.UntilNextStop)
            shadow.OverrideUntil = NextReleasePoint(work);
        else
            shadow.OverrideUntil = null;
        shadow.ManualOverride = true;
        shadow.Expected = null;
        Logger.Info($"Hue: '{shadow.Name}' was changed outside Underlit ({what}); leaving it alone"
                    + (shadow.OverrideUntil.HasValue ? $" until {shadow.OverrideUntil:HH:mm}" : "")
                    + $" [{_settings.HueOverrideRelease}]");
        try { LightTakenOver?.Invoke(shadow.Name); } catch { /* listener issues aren't ours */ }
    }

    private static void ClearOverride(LightShadow shadow, string? reason)
    {
        if (shadow.ManualOverride && reason != null)
            Logger.Info($"Hue: taking '{shadow.Name}' back because {reason}");
        shadow.ManualOverride = false;
        shadow.OverrideUntil  = null;
        shadow.Expected       = null;
    }

    /// <summary>When an UntilNextStop override ends: the next colour stop
    /// in time-of-day mode, otherwise the next of the schedule's four
    /// ramp points.</summary>
    private DateTime NextReleasePoint(GroupPush work)
    {
        var now = Now;
        int nowMin = now.Hour * 60 + now.Minute;
        IEnumerable<int> candidates = work.TimeMode
            ? new[] { work.NextStopMinute }
            : new[] { _settings.WakeupStart, _settings.WakeupEnd, _settings.BedtimeStart, _settings.BedtimeEnd }
                .Select(t => t.Hour * 60 + t.Minute);
        int best = int.MaxValue;
        foreach (var m in candidates)
        {
            int ahead = ((m - nowMin) % 1440 + 1440) % 1440;
            if (ahead == 0) ahead = 1440;
            best = Math.Min(best, ahead);
        }
        if (best == int.MaxValue) best = 1440;
        return now.Date.AddMinutes(nowMin + best);
    }

    /// <summary>What a bulb reported right after Underlit's last write.</summary>
    private sealed record Fingerprint(int? Bri, string? Mode, double? X, double? Y, int? Ct, int? Hue, int? Sat)
    {
        public static Fingerprint From(HueLight l) =>
            new(l.Brightness, l.ColorMode, l.X, l.Y, l.Mireds, l.HueValue, l.Saturation);

        /// <summary>Tolerances are generous on purpose: the bridge rounds,
        /// and a false "someone else changed it" is worse than missing a
        /// change of a couple of percent.</summary>
        public static bool Differs(Fingerprint e, HueLight now, out string what)
        {
            if (e.Bri.HasValue && now.Brightness.HasValue && Math.Abs(e.Bri.Value - now.Brightness.Value) > 6)
            { what = $"brightness {e.Bri} -> {now.Brightness}"; return true; }

            if (e.Mode != null && now.ColorMode != null && !string.Equals(e.Mode, now.ColorMode, StringComparison.OrdinalIgnoreCase))
            { what = $"colour mode {e.Mode} -> {now.ColorMode}"; return true; }

            switch (now.ColorMode)
            {
                case "xy" when e.X.HasValue && e.Y.HasValue && now.X.HasValue && now.Y.HasValue:
                    double d = Math.Sqrt(Math.Pow(e.X.Value - now.X.Value, 2) + Math.Pow(e.Y.Value - now.Y.Value, 2));
                    if (d > 0.015) { what = $"colour moved by {d:0.000} xy"; return true; }
                    break;
                case "ct" when e.Ct.HasValue && now.Mireds.HasValue:
                    if (Math.Abs(e.Ct.Value - now.Mireds.Value) > 8) { what = $"colour temperature {e.Ct} -> {now.Mireds} mired"; return true; }
                    break;
                case "hs" when e.Hue.HasValue && e.Sat.HasValue && now.HueValue.HasValue && now.Saturation.HasValue:
                    int dh = Math.Abs(e.Hue.Value - now.HueValue.Value);
                    dh = Math.Min(dh, 65536 - dh);
                    if (dh > 1000 || Math.Abs(e.Sat.Value - now.Saturation.Value) > 10) { what = "hue/saturation changed"; return true; }
                    break;
            }
            what = "";
            return false;
        }
    }

    /// <summary>v0.6.55: brightness scaling factor (0..1) based on the
    /// gap between the bulb's CT-max limit and the schedule's deepest
    /// warmth. CT-only bulbs that can't follow the schedule all the way
    /// down to the deepest amber don't just clamp at their warm end and
    /// stay full-bright (which made them look glaringly white-ish next
    /// to colour bulbs that DID reach the warm end) - they ramp brightness
    /// down from 1.0 at their CT limit to 0.0 at the schedule's deepest,
    /// so by the time the room is at its full bedtime amber the limited
    /// bulb is off entirely.
    ///
    /// <para>The fade actually starts <paramref name="fadeOnsetKelvin"/>
    /// degrees BEFORE the bulb's hard CT limit (default 200 K) so the
    /// dimming eases in instead of cliffing. Tuneable via
    /// <c>HueBulbFadeOnsetKelvin</c> in settings.</para>
    /// </summary>
    private static double ComputeFadeFactor(int desiredMireds, LightShadow shadow,
                                              int scheduleDeepestMireds, int fadeOnsetKelvin)
    {
        if (!shadow.CtMaxMireds.HasValue) return 1.0; // no CT info = full-range bulb (Hue Color etc.)
        int bulbMaxMireds = shadow.CtMaxMireds.Value;

        // Where the fade STARTS (cooler than the bulb's hard limit by
        // fadeOnsetKelvin K). Convert via mireds = 1e6 / K so the offset
        // in K maps correctly across the non-linear mireds axis.
        int bulbMaxK = (int)Math.Round(1_000_000.0 / bulbMaxMireds);
        int fadeStartK = bulbMaxK + Math.Max(0, fadeOnsetKelvin);
        int fadeStartMireds = (int)Math.Round(1_000_000.0 / fadeStartK);

        // If the schedule never goes warmer than where we'd start fading,
        // the bulb is always inside its happy range - no fade.
        if (scheduleDeepestMireds <= fadeStartMireds) return 1.0;

        if (desiredMireds <= fadeStartMireds)         return 1.0;
        if (desiredMireds >= scheduleDeepestMireds)   return 0.0;
        return 1.0 - (desiredMireds - fadeStartMireds)
                     / (double)(scheduleDeepestMireds - fadeStartMireds);
    }

    /// <summary>The deepest-warm point in the user's schedule, expressed
    /// in mireds. This is the "fully bedtime" anchor that CT-limited
    /// bulbs fade against.</summary>
    private int ComputeScheduleDeepestMireds()
    {
        // The four schedule anchors each carry their own kelvin. Deepest
        // warm = lowest kelvin = highest mireds.
        int deepestK = Math.Min(
            Math.Min(_settings.WakeupStartKelvin, _settings.WakeupEndKelvin),
            Math.Min(_settings.BedtimeStartKelvin, _settings.BedtimeEndKelvin));
        deepestK = Math.Clamp(deepestK, 1500, 6500);
        return HueBridgeClient.KelvinToMireds(deepestK);
    }

    private static int ClampMireds(int desiredMireds, LightShadow shadow)
    {
        int min = shadow.CtMinMireds ?? 153;
        int max = shadow.CtMaxMireds ?? 500;
        return Math.Clamp(desiredMireds, min, max);
    }

    // -------------------------------------------------------------------------
    // Shadow / capability cache
    // -------------------------------------------------------------------------

    private DateTime _shadowsLastFetched = DateTime.MinValue;
    private readonly Dictionary<string, List<string>> _groupMembers = new();

    /// <summary>Pull /lights and /groups if our cache is older than
    /// <see cref="CapabilitiesTtl"/> or empty. Populates <see cref="_shadows"/>
    /// (per-bulb capabilities) and <see cref="_groupMembers"/> (group id ->
    /// constituent light ids).</summary>
    private async Task EnsureShadowsFresh(HueBridgeClient client, HashSet<string> selectedLights, HashSet<string> selectedGroups)
    {
        bool stale = (DateTime.UtcNow - _shadowsLastFetched) > CapabilitiesTtl
                     || (_shadows.Count == 0 && (selectedLights.Count > 0 || selectedGroups.Count > 0));
        if (!stale) return;

        try
        {
            var lights = await client.GetLightsAsync();
            foreach (var l in lights)
            {
                if (_shadows.TryGetValue(l.Id, out var existing))
                {
                    // Refresh capabilities, keep tracking flags.
                    existing.UpdateCapabilities(l);
                }
                else
                {
                    _shadows[l.Id] = LightShadow.FromBridge(l);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Hue: GetLightsAsync failed during shadow refresh", ex);
        }

        try
        {
            var groups = await client.GetGroupsAsync();
            _groupMembers.Clear();
            foreach (var g in groups) _groupMembers[g.Id] = g.LightIds;
        }
        catch (Exception ex)
        {
            Logger.Warn("Hue: GetGroupsAsync failed during shadow refresh", ex);
        }

        _shadowsLastFetched = DateTime.UtcNow;
    }

    // -------------------------------------------------------------------------
    // Multi-stop xy gradient (from v0.6.49). Pure math, kept verbatim.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Multi-stop gradient. Sort the stops by kelvin, find the adjacent
    /// pair that brackets the requested kelvin, and lerp in CIE xy space
    /// between just those two endpoints.
    /// </summary>
    /// <summary>v0.6.62: sample the gradient at the given kelvin in
    /// sRGB. Mirrors GradientXY's bracket-and-lerp logic but produces
    /// (r, g, b) bytes from the stop colours so the controller can
    /// detect "this stop is black" before converting to xy (which is
    /// undefined for absolute black).</summary>
    private static (byte r, byte g, byte b) SampleGradientRgb(int kelvin, List<HueGradientStop> stops)
    {
        if (stops == null || stops.Count == 0) return (255, 255, 255);
        var sorted = stops.Where(s => s != null).OrderBy(s => s.Kelvin).ToList();
        if (sorted.Count == 1) return HexToRgb(sorted[0].Color);
        if (kelvin <= sorted[0].Kelvin)        return HexToRgb(sorted[0].Color);
        if (kelvin >= sorted[^1].Kelvin)       return HexToRgb(sorted[^1].Color);
        for (int i = 0; i < sorted.Count - 1; i++)
        {
            var a = sorted[i];
            var b = sorted[i + 1];
            if (kelvin >= a.Kelvin && kelvin <= b.Kelvin)
            {
                int span = b.Kelvin - a.Kelvin;
                double t = span <= 0 ? 0.0 : (kelvin - a.Kelvin) / (double)span;
                var (ra, ga, ba) = HexToRgb(a.Color);
                var (rb, gb, bb) = HexToRgb(b.Color);
                return (
                    (byte)Math.Round(ra + (rb - ra) * t),
                    (byte)Math.Round(ga + (gb - ga) * t),
                    (byte)Math.Round(ba + (bb - ba) * t));
            }
        }
        return HexToRgb(sorted[^1].Color);
    }

    private static (byte r, byte g, byte b) HexToRgb(string hex)
    {
        if (string.IsNullOrEmpty(hex) || hex[0] != '#') return (255, 255, 255);
        try
        {
            int off = hex.Length == 9 ? 3 : 1;
            byte r = Convert.ToByte(hex.Substring(off, 2), 16);
            byte g = Convert.ToByte(hex.Substring(off + 2, 2), 16);
            byte b = Convert.ToByte(hex.Substring(off + 4, 2), 16);
            return (r, g, b);
        }
        catch { return (255, 255, 255); }
    }

    private static (double x, double y) GradientXY(int kelvin, List<HueGradientStop> stops)
    {
        if (stops == null || stops.Count == 0)
            return (0.3128, 0.3290);

        var sorted = stops
            .Where(s => s != null)
            .OrderBy(s => s.Kelvin)
            .ToList();

        if (sorted.Count == 1)
            return HexToXy(sorted[0].Color, fallback: (0.3128, 0.3290));

        if (kelvin <= sorted[0].Kelvin)
            return HexToXy(sorted[0].Color, fallback: (0.6750, 0.3220));
        if (kelvin >= sorted[^1].Kelvin)
            return HexToXy(sorted[^1].Color, fallback: (0.3128, 0.3290));

        for (int i = 0; i < sorted.Count - 1; i++)
        {
            var a = sorted[i];
            var b = sorted[i + 1];
            if (kelvin >= a.Kelvin && kelvin <= b.Kelvin)
            {
                int span = b.Kelvin - a.Kelvin;
                double t = span <= 0 ? 0.0 : (kelvin - a.Kelvin) / (double)span;
                var (xa, ya) = HexToXy(a.Color, fallback: (0.6750, 0.3220));
                var (xb, yb) = HexToXy(b.Color, fallback: (0.3128, 0.3290));
                return (xa + (xb - xa) * t, ya + (yb - ya) * t);
            }
        }
        return HexToXy(sorted[^1].Color, fallback: (0.3128, 0.3290));
    }

    private static (double x, double y) HexToXy(string hex, (double x, double y) fallback)
    {
        if (string.IsNullOrEmpty(hex) || hex[0] != '#') return fallback;
        try
        {
            int off = hex.Length == 9 ? 3 : 1;
            byte r = Convert.ToByte(hex.Substring(off, 2), 16);
            byte g = Convert.ToByte(hex.Substring(off + 2, 2), 16);
            byte b = Convert.ToByte(hex.Substring(off + 4, 2), 16);
            return SrgbToXy(r, g, b);
        }
        catch
        {
            return fallback;
        }
    }

    private static (double x, double y) SrgbToXy(byte r, byte g, byte b)
    {
        double rL = SrgbDecode(r / 255.0);
        double gL = SrgbDecode(g / 255.0);
        double bL = SrgbDecode(b / 255.0);
        double X = 0.4124 * rL + 0.3576 * gL + 0.1805 * bL;
        double Y = 0.2126 * rL + 0.7152 * gL + 0.0722 * bL;
        double Z = 0.0193 * rL + 0.1192 * gL + 0.9505 * bL;
        double sum = X + Y + Z;
        if (sum < 1e-9) return (0.3128, 0.3290);
        return (X / sum, Y / sum);
    }

    private static double SrgbDecode(double c) =>
        c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    public void Dispose()
    {
        _engine.LevelChanged -= OnEngineLevelChanged;
        _clockTimer.Stop();
        _client?.Dispose();
        _client = null;
    }

    /// <summary>Per-bulb runtime state. Kept in HueController (not in
    /// AppSettings) because it's purely transient - no point persisting
    /// "last sent ct" across app restarts.</summary>
    private sealed class LightShadow
    {
        public string Id = "";
        public string Name = "";
        public bool   HasColor;
        public bool   IsPlug;
        public int?   CtMinMireds;
        public int?   CtMaxMireds;

        // Bridge-side state we last observed.
        public bool? LastKnownOn;

        // What we last asked the bulb to be at.
        public bool? LastSentOn;
        public int?  LastSentBri;
        public int?  LastSentCt;

        /// <summary>True iff we turned this bulb off ourselves due to the
        /// schedule running past its CT range. Used to know we're allowed
        /// to revive it (vs respecting a manual user-off).</summary>
        public bool AutoFadedOff;

        // ---- v0.7.0: external-change handling ----
        /// <summary>Bridge readback right after our last confirmed write.</summary>
        public Fingerprint? Expected;
        /// <summary>Bumped on every write; confirm readbacks only count if
        /// no newer write happened in between.</summary>
        public int WriteSeq;
        public int ConfirmedSeq;
        /// <summary>Someone else changed this bulb; don't push to it.</summary>
        public bool ManualOverride;
        /// <summary>For UntilNextStop: when to take it back.</summary>
        public DateTime? OverrideUntil;
        /// <summary>Set by ResumeAll(turnOn: true).</summary>
        public bool ResumeRequested;

        public static LightShadow FromBridge(HueLight l) => new()
        {
            Id = l.Id,
            Name = l.Name,
            HasColor = l.HasColor,
            IsPlug = l.IsPlug,
            CtMinMireds = l.CtMinMireds,
            CtMaxMireds = l.CtMaxMireds,
            LastKnownOn = l.On,
        };

        public void UpdateCapabilities(HueLight l)
        {
            Name = l.Name;
            HasColor = l.HasColor;
            IsPlug = l.IsPlug;
            CtMinMireds = l.CtMinMireds;
            CtMaxMireds = l.CtMaxMireds;
        }
    }
}
