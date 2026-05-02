using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Underlit.Hue;

/// <summary>
/// Minimal Philips Hue Bridge v1 HTTP client.
///
/// Why v1 over v2: v2 needs HTTPS with the bridge's self-signed certificate +
/// SSE for live updates. v1 is plain HTTP, every published Hue tutorial maps to
/// it directly, and Underlit only needs request/response (set group state on
/// schedule transitions; no streaming). We can move to v2 later if real-time
/// state-back-from-the-bridge becomes important.
///
/// Usage:
///   • Construct with the bridge IP. If you already have a username (from a
///     previous successful pair), pass it in.
///   • To pair, call <see cref="TryPairAsync"/> AFTER instructing the user to
///     press the link button on the bridge. Hue gives you a 30-second window
///     during which the bridge will accept the pairing POST; outside that
///     window it returns an "link button not pressed" error.
///   • Once <see cref="Username"/> is non-null, call <see cref="GetGroupsAsync"/>
///     and <see cref="SetGroupStateAsync"/> / <see cref="SetGroupColorAsync"/>
///     to drive lights.
///
/// Threading: every public method is async and uses <see cref="HttpClient"/>
/// internally. Safe to call from any thread; callers should marshal results
/// back to the UI thread themselves.
/// </summary>
public sealed class HueBridgeClient : IDisposable
{
    private readonly HttpClient _http;

    public string BridgeIp { get; }
    public string? Username { get; private set; }

    public HueBridgeClient(string bridgeIp, string? username = null)
    {
        BridgeIp = bridgeIp ?? throw new ArgumentNullException(nameof(bridgeIp));
        Username = string.IsNullOrEmpty(username) ? null : username;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    /// <summary>
    /// Attempt to pair with the bridge. Returns success only if the user pressed
    /// the bridge's link button within the last ~30 seconds.
    ///
    /// <paramref name="deviceType"/> is what shows up in the Hue app's "Linked
    /// apps" list — convention is "AppName#description" (max 40 chars total).
    /// </summary>
    public async Task<HuePairResult> TryPairAsync(string deviceType, CancellationToken ct = default)
    {
        try
        {
            string body = JsonSerializer.Serialize(new { devicetype = deviceType });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync($"http://{BridgeIp}/api", content, ct);
            string respBody = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                return new HuePairResult(false, null, $"Bridge returned HTTP {(int)resp.StatusCode}");

            // Hue v1 always returns a JSON array. The first element is either
            // {"success":{"username":"..."}} or {"error":{"description":"..."}}.
            using var doc = JsonDocument.Parse(respBody);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return new HuePairResult(false, null, "Unexpected bridge response shape");

            var first = doc.RootElement[0];
            if (first.TryGetProperty("success", out var success)
                && success.TryGetProperty("username", out var user))
            {
                Username = user.GetString();
                return new HuePairResult(true, Username, null);
            }
            if (first.TryGetProperty("error", out var error)
                && error.TryGetProperty("description", out var desc))
            {
                return new HuePairResult(false, null, desc.GetString());
            }
            return new HuePairResult(false, null, "Unknown bridge response");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new HuePairResult(false, null, "Bridge unreachable (timed out)");
        }
        catch (HttpRequestException ex)
        {
            return new HuePairResult(false, null, $"Network error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new HuePairResult(false, null, ex.Message);
        }
    }

    /// <summary>
    /// Quick "is the bridge reachable AND does our username still work" check.
    /// Returns true if a request to /api/{username}/config succeeds. Useful for
    /// the Lights settings page status badge.
    /// </summary>
    public async Task<bool> IsReachableAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(Username)) return false;
        try
        {
            using var resp = await _http.GetAsync($"http://{BridgeIp}/api/{Username}/config", ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<List<HueGroup>> GetGroupsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(Username)) throw new InvalidOperationException("Bridge not paired — call TryPairAsync first");
        using var resp = await _http.GetAsync($"http://{BridgeIp}/api/{Username}/groups", ct);
        resp.EnsureSuccessStatusCode();
        string body = await resp.Content.ReadAsStringAsync(ct);

        var list = new List<HueGroup>();
        using var doc = JsonDocument.Parse(body);
        // The /groups endpoint returns an OBJECT keyed by group id, not an array.
        // Each group has at least a "name" and "type". Lights array is the
        // concrete light ids in the group; we don't need the per-light state here.
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            string id = prop.Name;
            string name = prop.Value.TryGetProperty("name", out var n) ? (n.GetString() ?? id) : id;
            string type = prop.Value.TryGetProperty("type", out var t) ? (t.GetString() ?? "Group") : "Group";
            int lightCount = 0;
            if (prop.Value.TryGetProperty("lights", out var lights) && lights.ValueKind == JsonValueKind.Array)
                lightCount = lights.GetArrayLength();
            list.Add(new HueGroup { Id = id, Name = name, Type = type, LightCount = lightCount });
        }
        return list;
    }

    /// <summary>
    /// Set the on/off + brightness + colour-temperature of an entire Hue group.
    /// Pass null for any field you don't want to change. <paramref name="mireds"/>
    /// is colour temperature in mireds (153–500 covers 6500 K → 2000 K). Use
    /// <see cref="KelvinToMireds"/> to convert from kelvin.
    /// </summary>
    public async Task<bool> SetGroupStateAsync(
        string groupId, bool? on, int? mireds, int? brightness254,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(Username)) throw new InvalidOperationException("Bridge not paired");

        var state = new Dictionary<string, object>();
        if (on.HasValue)            state["on"]  = on.Value;
        if (mireds.HasValue)        state["ct"]  = Math.Clamp(mireds.Value, 153, 500);
        if (brightness254.HasValue) state["bri"] = Math.Clamp(brightness254.Value, 1, 254);

        return await PutGroupAction(groupId, state, ct);
    }

    /// <summary>
    /// Set the colour of a Hue group via CIE xy chromaticity. Use this when the
    /// user has chosen a non-circadian colour range (e.g. white→red) where we
    /// drive the bulb out of its colour-temperature range. Coordinates are CIE
    /// 1931 xy, both 0..1.
    /// </summary>
    public async Task<bool> SetGroupColorAsync(
        string groupId, double x, double y, int? brightness254,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(Username)) throw new InvalidOperationException("Bridge not paired");

        var state = new Dictionary<string, object>
        {
            { "on", true },
            { "xy", new double[] { Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1) } },
        };
        if (brightness254.HasValue) state["bri"] = Math.Clamp(brightness254.Value, 1, 254);

        return await PutGroupAction(groupId, state, ct);
    }

    private async Task<bool> PutGroupAction(string groupId, Dictionary<string, object> state, CancellationToken ct)
    {
        try
        {
            string body = JsonSerializer.Serialize(state);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PutAsync(
                $"http://{BridgeIp}/api/{Username}/groups/{groupId}/action", content, ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Convert a colour temperature in kelvin to Hue mireds, clamped
    /// to the bulb's supported range (153 mireds = 6500K to 500 mireds = 2000K).</summary>
    public static int KelvinToMireds(int kelvin)
    {
        if (kelvin < 1) kelvin = 1;
        int mireds = (int)Math.Round(1_000_000.0 / kelvin);
        return Math.Clamp(mireds, 153, 500);
    }

    public void Dispose() => _http.Dispose();
}

public sealed record HuePairResult(bool Success, string? Username, string? Error);

public sealed class HueGroup
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public int LightCount { get; set; }
    public override string ToString() =>
        LightCount > 0 ? $"{Name} ({LightCount} lights · {Type})" : $"{Name} ({Type})";
}
