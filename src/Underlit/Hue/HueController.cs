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
    private (int Kelvin, int Brightness, List<string> Groups, HueColorRangeMode Range,
             string GradientCool, string GradientWarm)? _pending;

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

        EnqueueWrite(hueKelvin, hueBri,
                     _settings.HueSelectedGroupIds.ToList(),
                     _settings.HueColorRange,
                     _settings.HueGradientCoolColor ?? "#FFFFFFFF",
                     _settings.HueGradientWarmColor ?? "#FFB00010");
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

    private void EnqueueWrite(int kelvin, int bri, List<string> groups, HueColorRangeMode range,
                                string gradientCool, string gradientWarm)
    {
        bool kickoff;
        lock (_lock)
        {
            // Latest-wins: any pending value still in the queue is replaced
            // by the freshest one. The worker only ever picks up the most
            // recent state, so a flurry of hotkey presses converges to the
            // last value rather than playing each intermediate frame.
            _pending = (kelvin, bri, groups, range, gradientCool, gradientWarm);
            kickoff = !_writeInFlight;
            if (kickoff) _writeInFlight = true;
        }
        if (!kickoff) return;

        Task.Run(async () =>
        {
            while (true)
            {
                (int Kelvin, int Brightness, List<string> Groups, HueColorRangeMode Range,
                 string GradientCool, string GradientWarm) work;
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
        (int Kelvin, int Brightness, List<string> Groups, HueColorRangeMode Range,
         string GradientCool, string GradientWarm) work)
    {
        var writes = new List<Task>(work.Groups.Count);

        if (work.Range == HueColorRangeMode.WhiteToRed)
        {
            (double x, double y) = GradientXY(work.Kelvin, work.GradientCool, work.GradientWarm);
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
    /// v0.6.45: gradient between user-pickable cool and warm endpoints.
    /// Both endpoints are sRGB hex strings ("#AARRGGBB"). At kelvin=6500
    /// the bulb sits at the cool endpoint; at kelvin=1500 it sits at
    /// the warm endpoint; linear lerp in CIE xy in between. Hue Bridge
    /// accepts xy directly so we don't fight the bulb's colour gamut.
    /// </summary>
    private static (double x, double y) GradientXY(int kelvin, string coolHex, string warmHex)
    {
        double t = (Math.Clamp(kelvin, 1500, 6500) - 1500) / 5000.0;  // 0 = warm, 1 = cool
        var (xc, yc) = HexToXy(coolHex, fallback: (0.3128, 0.3290));  // D65 default
        var (xw, yw) = HexToXy(warmHex, fallback: (0.6750, 0.3220));  // deep red default
        double x = xw + (xc - xw) * t;
        double y = yw + (yc - yw) * t;
        return (x, y);
    }

    /// <summary>Parse a "#AARRGGBB" hex string and return its CIE 1931
    /// xy chromaticity. Falls back if the parse fails.</summary>
    private static (double x, double y) HexToXy(string hex, (double x, double y) fallback)
    {
        if (string.IsNullOrEmpty(hex) || hex[0] != '#') return fallback;
        try
        {
            // Skip alpha if present.
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

    /// <summary>sRGB to CIE 1931 xy via the standard sRGB → linear → XYZ
    /// pipeline. Used to map the user's picked colour swatch to a Hue
    /// Bridge xy command.</summary>
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
