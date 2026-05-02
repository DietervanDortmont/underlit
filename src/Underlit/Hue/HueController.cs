using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Underlit.Core;
using Underlit.Settings;

namespace Underlit.Hue;

/// <summary>
/// Drives the user's Hue lights to follow the screen's warmth target plus an
/// optional kelvin offset, with an independent brightness slider.
///
/// Wiring:
///   • Subscribes to <see cref="UnderlitEngine.LevelChanged"/>. Every time the
///     screen warmth target changes, recomputes the corresponding Hue mireds
///     value (kelvin + offset → clamp to 1500..6500 → KelvinToMireds) and
///     queues a write to every group in <see cref="AppSettings.HueSelectedGroupIds"/>.
///   • Boost ignored: when <see cref="UnderlitEngine.Boosted"/> is true and
///     <see cref="AppSettings.HueIgnoreBoost"/> is also true, no write happens
///     — the lights stay where they were instead of flashing to full neutral.
///   • Coalesced writer: at most one HTTP request train in flight; intermediate
///     warmth/brightness changes during a fast slider drag are dropped, only
///     the latest pending values are written. Same single-worker / latest-
///     pending pattern as the brightness hardware writer.
///
/// External entry points (for hotkey / slider changes that need to push without
/// waiting for the engine to fire LevelChanged): <see cref="PushNow"/>.
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
    private (int Kelvin, int Brightness, List<string> Groups, HueColorRangeMode Range)? _pending;

    public HueController(UnderlitEngine engine, AppSettings settings)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _engine.LevelChanged += OnEngineLevelChanged;
        ReinitClientIfNeeded();
    }

    /// <summary>Swap in new settings (called from UnderlitHost.ApplySettings).
    /// Re-creates the bridge client if the IP/username changed; otherwise just
    /// updates the local reference and re-pushes so any new offset / brightness
    /// / colour-range value takes effect immediately.</summary>
    public void UpdateSettings(AppSettings newSettings)
    {
        _settings = newSettings ?? throw new ArgumentNullException(nameof(newSettings));
        ReinitClientIfNeeded();
        PushNow();
    }

    /// <summary>Push the current settings + engine warmth straight to the bridge,
    /// bypassing the wait for the next engine LevelChanged. Called after a Hue
    /// hotkey adjustment, after Hue settings change in the Settings window, etc.</summary>
    public void PushNow()
    {
        if (_client == null) return;
        if (_settings.HueSelectedGroupIds.Count == 0) return;
        if (_engine.Boosted && _settings.HueIgnoreBoost) return;

        int screenKelvin = _engine.CurrentWarmth;
        int hueKelvin = Math.Clamp(screenKelvin + _settings.HueWarmthOffsetKelvin, 1500, 6500);
        int hueBri    = Math.Clamp(_settings.HueBrightness, 1, 254);

        EnqueueWrite(hueKelvin, hueBri, _settings.HueSelectedGroupIds.ToList(), _settings.HueColorRange);
    }

    private void OnEngineLevelChanged(double brightness, int warmth)
    {
        // The brightness arg is the screen brightness — Hue brightness is
        // independent (driven by the user's HueBrightness slider/hotkey), so
        // we ignore it. Only warmth changes make us push. We re-read CurrentWarmth
        // through PushNow for consistency with hotkey-triggered pushes.
        PushNow();
    }

    private void ReinitClientIfNeeded()
    {
        bool ipChanged   = _settings.HueBridgeIp       != _lastBridgeIp;
        bool userChanged = _settings.HueBridgeUsername != _lastBridgeUsername;
        if (!ipChanged && !userChanged && _client != null) return;

        _client?.Dispose();
        _client = null;
        _lastBridgeIp       = _settings.HueBridgeIp;
        _lastBridgeUsername = _settings.HueBridgeUsername;

        if (!string.IsNullOrEmpty(_settings.HueBridgeIp)
            && !string.IsNullOrEmpty(_settings.HueBridgeUsername))
        {
            _client = new HueBridgeClient(_settings.HueBridgeIp!, _settings.HueBridgeUsername);
        }
    }

    private void EnqueueWrite(int kelvin, int bri, List<string> groups, HueColorRangeMode range)
    {
        bool kickoff;
        lock (_lock)
        {
            _pending = (kelvin, bri, groups, range);
            kickoff = !_writeInFlight;
            if (kickoff) _writeInFlight = true;
        }
        if (!kickoff) return;

        Task.Run(async () =>
        {
            while (true)
            {
                (int Kelvin, int Brightness, List<string> Groups, HueColorRangeMode Range) work;
                HueBridgeClient? client;
                lock (_lock)
                {
                    if (_pending is null)
                    {
                        _writeInFlight = false;
                        return;
                    }
                    work   = _pending.Value;
                    client = _client;
                    _pending = null;
                }
                if (client == null) continue;

                int effectiveKelvin = work.Kelvin;
                if (work.Range == HueColorRangeMode.WarmWhiteOnly)
                {
                    // Tighten the band to a calmer evening palette.
                    effectiveKelvin = Math.Clamp(effectiveKelvin, 2700, 5000);
                }
                int mireds = HueBridgeClient.KelvinToMireds(effectiveKelvin);

                // Snappy 100 ms transition (vs the bridge default 400 ms) so
                // slider drags + hotkey taps feel responsive instead of laggy.
                // Schedule transitions are gentle on their own (warmth shifts
                // by a few K per minute), so 100 ms is fine there too.
                const int snappyTransition = 1;

                // v0.6.31: write all selected groups in parallel instead of
                // sequentially. With N groups the previous code took N × HTTP
                // round-trips before the next pending value could be picked
                // up, which made fast slider drags update the bulb roughly
                // every Nx100ms instead of every 100ms. Parallel writes
                // collapse that to ~1 round-trip regardless of group count.
                var writes = new List<Task>(work.Groups.Count);
                foreach (var groupId in work.Groups)
                {
                    writes.Add(WriteGroupSafeAsync(client, groupId, mireds,
                                                     work.Brightness, snappyTransition));
                }
                await Task.WhenAll(writes);
            }
        });
    }

    /// <summary>
    /// PUT one group's new state, swallowing failures (transient network
    /// hiccups, bridge command-rate limits, etc.) so a single bad group
    /// doesn't block the others when we're firing in parallel.
    /// </summary>
    private static async Task WriteGroupSafeAsync(HueBridgeClient client, string groupId,
                                                    int mireds, int brightness, int transitionDeciseconds)
    {
        try
        {
            await client.SetGroupStateAsync(groupId, on: null, mireds: mireds,
                                              brightness254: brightness,
                                              transitionDeciseconds: transitionDeciseconds);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Hue write failed for group {groupId}", ex);
        }
    }

    public void Dispose()
    {
        _engine.LevelChanged -= OnEngineLevelChanged;
        _client?.Dispose();
        _client = null;
    }
}
