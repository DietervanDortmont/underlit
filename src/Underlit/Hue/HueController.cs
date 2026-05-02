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

    /// <summary>
    /// Minimum interval between consecutive bridge writes, in ms. Prevents
    /// the worker loop from saturating the Hue Bridge with HTTP traffic
    /// when the user holds a hotkey or drags the slider fast. Each write
    /// itself is parallelised across groups, so this is the rate-limit on
    /// the LATEST-VALUE writes, not on individual groups. Hue Bridge v1
    /// docs recommend ≤ 10 commands/s/light; ~150 ms keeps us well under.
    /// </summary>
    private const int MinWriteIntervalMs = 150;

    /// <summary>Snappy 100 ms transitiontime on the bridge so a hotkey or
    /// slider drag visibly tracks instead of feeling like a laggy fade.</summary>
    private const int SnappyTransitionDeciseconds = 1;

    private void EnqueueWrite(int kelvin, int bri, List<string> groups, HueColorRangeMode range)
    {
        bool kickoff;
        lock (_lock)
        {
            // Latest-wins: any pending value still in the queue is replaced
            // by the freshest one. The worker only ever picks up the most
            // recent state, so a flurry of hotkey presses converges to the
            // last value rather than playing each intermediate frame.
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

                // Map kelvin → bridge state per the chosen colour range.
                await PushColourState(client, work);

                // Rate-limit: pause briefly before checking for the next
                // pending value. If the user spammed a hotkey during this
                // pause, _pending now holds only the very latest state,
                // and the next iteration sends that single value instead
                // of replaying everything that was queued.
                await Task.Delay(MinWriteIntervalMs);
            }
        });
    }

    /// <summary>
    /// Translate the (kelvin, brightness, range) state into the right
    /// bridge command for the user's selected colour range, and push it
    /// to every selected group in parallel. Three modes:
    /// <list type="bullet">
    ///   <item><b>Circadian</b> — colour temperature in mireds, native
    ///         to white-tunable lights. Most accurate "follow the sun".</item>
    ///   <item><b>WarmWhiteOnly</b> — same as Circadian but kelvin
    ///         clamped to 2700..5000 K, never goes deep amber.</item>
    ///   <item><b>WhiteToRed</b> — interpolate xy chromaticity between
    ///         CIE D65 white and a deep red as kelvin drops from 6500 to
    ///         1500 K. Drives full-colour bulbs out of their CT range.</item>
    /// </list>
    /// </summary>
    private static async Task PushColourState(
        HueBridgeClient client,
        (int Kelvin, int Brightness, List<string> Groups, HueColorRangeMode Range) work)
    {
        var writes = new List<Task>(work.Groups.Count);

        if (work.Range == HueColorRangeMode.WhiteToRed)
        {
            (double x, double y) = WhiteToRedXY(work.Kelvin);
            foreach (var groupId in work.Groups)
            {
                writes.Add(WriteGroupColorSafeAsync(client, groupId, x, y,
                                                      work.Brightness,
                                                      SnappyTransitionDeciseconds));
            }
        }
        else
        {
            int kelvin = work.Kelvin;
            if (work.Range == HueColorRangeMode.WarmWhiteOnly)
            {
                kelvin = Math.Clamp(kelvin, 2700, 5000);
            }
            int mireds = HueBridgeClient.KelvinToMireds(kelvin);
            foreach (var groupId in work.Groups)
            {
                writes.Add(WriteGroupCtSafeAsync(client, groupId, mireds,
                                                   work.Brightness,
                                                   SnappyTransitionDeciseconds));
            }
        }

        await Task.WhenAll(writes);
    }

    /// <summary>
    /// Lerp CIE 1931 xy between D65 white (kelvin 6500) and deep red
    /// (kelvin 1500). Linear interpolation in xy is good enough for an
    /// ambient effect; perceptual gradient roughly matches what users
    /// expect from "warm goes red".
    /// </summary>
    private static (double x, double y) WhiteToRedXY(int kelvin)
    {
        double t = (Math.Clamp(kelvin, 1500, 6500) - 1500) / 5000.0;  // 0=red, 1=white
        // D65 white point — the bulb's "neutral" colour.
        const double xWhite = 0.3128, yWhite = 0.3290;
        // Deep red, near-monochromatic ~700 nm. Inside Hue's sRGB-ish gamut
        // so the bridge accepts it without major re-mapping.
        const double xRed   = 0.6750, yRed   = 0.3220;
        double x = xRed + (xWhite - xRed) * t;
        double y = yRed + (yWhite - yRed) * t;
        return (x, y);
    }

    private static async Task WriteGroupCtSafeAsync(HueBridgeClient client, string groupId,
                                                      int mireds, int brightness,
                                                      int transitionDeciseconds)
    {
        try
        {
            await client.SetGroupStateAsync(groupId, on: null, mireds: mireds,
                                              brightness254: brightness,
                                              transitionDeciseconds: transitionDeciseconds);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Hue CT write failed for group {groupId}", ex);
        }
    }

    private static async Task WriteGroupColorSafeAsync(HueBridgeClient client, string groupId,
                                                         double x, double y, int brightness,
                                                         int transitionDeciseconds)
    {
        try
        {
            await client.SetGroupColorAsync(groupId, x, y,
                                              brightness254: brightness,
                                              transitionDeciseconds: transitionDeciseconds);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Hue colour write failed for group {groupId}", ex);
        }
    }

    public void Dispose()
    {
        _engine.LevelChanged -= OnEngineLevelChanged;
        _client?.Dispose();
        _client = null;
    }
}
