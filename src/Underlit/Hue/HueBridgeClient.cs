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
        // v0.6.49: 5 s timeout was too long when the bridge becomes
        // unreachable mid-session — a rapid hotkey press could stall
        // the worker for 5 s before the next command went out, leaving
        // the bulb visibly out of sync. 3 s is enough for a healthy
        // bridge over LAN and surfaces dropouts faster.
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
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

    /// <summary>
    /// Fetch the bridge's full lights list, including each bulb's
    /// capabilities (CT range, colour gamut) and current on/bri/ct state.
    /// Used by the per-light selection UI and the gamut-aware push logic
    /// in <see cref="HueController"/>.
    /// </summary>
    public async Task<List<HueLight>> GetLightsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(Username)) throw new InvalidOperationException("Bridge not paired - call TryPairAsync first");
        using var resp = await _http.GetAsync($"http://{BridgeIp}/api/{Username}/lights", ct);
        resp.EnsureSuccessStatusCode();
        string body = await resp.Content.ReadAsStringAsync(ct);

        var list = new List<HueLight>();
        using var doc = JsonDocument.Parse(body);
        // /lights returns an OBJECT keyed by light id (string).
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            list.Add(ParseLight(prop.Name, prop.Value));
        }
        return list;
    }

    /// <summary>Fetch a single light's current state. Cheaper than
    /// <see cref="GetLightsAsync"/> when you only need one bulb's
    /// on/off state (e.g. to coexist with Google Home / the Hue app's
    /// manual controls before pushing a schedule update).</summary>
    public async Task<HueLight?> GetLightAsync(string lightId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(Username)) throw new InvalidOperationException("Bridge not paired");
        try
        {
            using var resp = await _http.GetAsync($"http://{BridgeIp}/api/{Username}/lights/{lightId}", ct);
            if (!resp.IsSuccessStatusCode) return null;
            string body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            return ParseLight(lightId, doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    private static HueLight ParseLight(string id, JsonElement el)
    {
        string name = el.TryGetProperty("name", out var n) ? (n.GetString() ?? id) : id;
        string type = el.TryGetProperty("type", out var t) ? (t.GetString() ?? "") : "";
        string modelId = el.TryGetProperty("modelid", out var m) ? (m.GetString() ?? "") : "";
        string manufacturer = el.TryGetProperty("manufacturername", out var mn) ? (mn.GetString() ?? "") : "";

        bool stateOn = false;
        int? stateBri = null;
        int? stateCt = null;
        bool stateReachable = true;
        string? stateColorMode = null;
        double? stateX = null, stateY = null;
        int? stateHue = null, stateSat = null;
        if (el.TryGetProperty("state", out var st))
        {
            if (st.TryGetProperty("on",  out var o)  && o.ValueKind  == JsonValueKind.True)  stateOn = true;
            if (st.TryGetProperty("bri", out var b)  && b.ValueKind  == JsonValueKind.Number) stateBri = b.GetInt32();
            if (st.TryGetProperty("ct",  out var c)  && c.ValueKind  == JsonValueKind.Number) stateCt  = c.GetInt32();
            if (st.TryGetProperty("reachable", out var r) && r.ValueKind == JsonValueKind.False) stateReachable = false;
            // v0.7.0: full colour state so HueController can tell when
            // Google / the Hue app changed a bulb behind Underlit's back.
            if (st.TryGetProperty("colormode", out var cm) && cm.ValueKind == JsonValueKind.String) stateColorMode = cm.GetString();
            if (st.TryGetProperty("xy", out var xy) && xy.ValueKind == JsonValueKind.Array && xy.GetArrayLength() == 2)
            {
                stateX = xy[0].GetDouble();
                stateY = xy[1].GetDouble();
            }
            if (st.TryGetProperty("hue", out var hu) && hu.ValueKind == JsonValueKind.Number) stateHue = hu.GetInt32();
            if (st.TryGetProperty("sat", out var sa) && sa.ValueKind == JsonValueKind.Number) stateSat = sa.GetInt32();
        }

        // capabilities.control.ct.{min,max} - mireds range. Absent on
        // colour-only bulbs; whites and tunable-whites have it. IKEA
        // Tradfri tunables typically report 250..454 mireds (4000..2200 K).
        int? ctMin = null, ctMax = null;
        if (el.TryGetProperty("capabilities", out var cap)
            && cap.TryGetProperty("control", out var ctrl)
            && ctrl.TryGetProperty("ct", out var ctEl))
        {
            if (ctEl.TryGetProperty("min", out var mi) && mi.ValueKind == JsonValueKind.Number) ctMin = mi.GetInt32();
            if (ctEl.TryGetProperty("max", out var ma) && ma.ValueKind == JsonValueKind.Number) ctMax = ma.GetInt32();
        }
        // hasColor: capabilities.control.colorgamut OR colorgamuttype !=
        // null. We don't need the gamut itself - just whether the bulb
        // can do non-CT colours, so the WhiteToRed mode applies.
        bool hasColor = false;
        if (el.TryGetProperty("capabilities", out var cap2)
            && cap2.TryGetProperty("control", out var ctrl2))
        {
            hasColor = ctrl2.TryGetProperty("colorgamut", out _)
                    || ctrl2.TryGetProperty("colorgamuttype", out _);
        }

        return new HueLight
        {
            Id = id,
            Name = name,
            Type = type,
            ModelId = modelId,
            Manufacturer = manufacturer,
            CtMinMireds = ctMin,
            CtMaxMireds = ctMax,
            HasColor = hasColor,
            On = stateOn,
            Brightness = stateBri,
            Mireds = stateCt,
            Reachable = stateReachable,
            ColorMode = stateColorMode,
            X = stateX,
            Y = stateY,
            HueValue = stateHue,
            Saturation = stateSat,
        };
    }

    /// <summary>Set a single light's state. Mirrors
    /// <see cref="SetGroupStateAsync"/> but writes to /lights/{id}/state.
    /// /lights endpoints are rate-limited at 10 commands/sec by the bridge
    /// (vs 1/sec for /groups), so per-light pushes are actually faster
    /// than per-group ones.</summary>
    public async Task<bool> SetLightStateAsync(
        string lightId, bool? on, int? mireds, int? brightness254,
        int? transitionDeciseconds = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(Username)) throw new InvalidOperationException("Bridge not paired");

        var state = new Dictionary<string, object>();
        if (on.HasValue)            state["on"]  = on.Value;
        if (mireds.HasValue)        state["ct"]  = Math.Clamp(mireds.Value, 153, 500);
        if (brightness254.HasValue) state["bri"] = Math.Clamp(brightness254.Value, 1, 254);
        if (transitionDeciseconds.HasValue)
            state["transitiontime"] = Math.Max(0, transitionDeciseconds.Value);

        return await PutLightState(lightId, state, ct);
    }

    /// <summary>Set a single light's colour via xy chromaticity.</summary>
    public async Task<bool> SetLightColorAsync(
        string lightId, double x, double y, int? brightness254,
        int? transitionDeciseconds = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(Username)) throw new InvalidOperationException("Bridge not paired");

        var state = new Dictionary<string, object>
        {
            { "on", true },
            { "xy", new double[] { Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1) } },
        };
        if (brightness254.HasValue) state["bri"] = Math.Clamp(brightness254.Value, 1, 254);
        if (transitionDeciseconds.HasValue)
            state["transitiontime"] = Math.Max(0, transitionDeciseconds.Value);

        return await PutLightState(lightId, state, ct);
    }

    private async Task<bool> PutLightState(string lightId, Dictionary<string, object> state, CancellationToken ct)
    {
        try
        {
            string body = JsonSerializer.Serialize(state);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PutAsync(
                $"http://{BridgeIp}/api/{Username}/lights/{lightId}/state", content, ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<HueGroup>> GetGroupsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(Username)) throw new InvalidOperationException("Bridge not paired - call TryPairAsync first");
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
            var lightIds = new List<string>();
            if (prop.Value.TryGetProperty("lights", out var lights) && lights.ValueKind == JsonValueKind.Array)
            {
                foreach (var lid in lights.EnumerateArray())
                    if (lid.ValueKind == JsonValueKind.String) lightIds.Add(lid.GetString()!);
            }
            list.Add(new HueGroup { Id = id, Name = name, Type = type, LightIds = lightIds });
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
        int? transitionDeciseconds = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(Username)) throw new InvalidOperationException("Bridge not paired");

        var state = new Dictionary<string, object>();
        if (on.HasValue)            state["on"]  = on.Value;
        if (mireds.HasValue)        state["ct"]  = Math.Clamp(mireds.Value, 153, 500);
        if (brightness254.HasValue) state["bri"] = Math.Clamp(brightness254.Value, 1, 254);
        // Hue's default transitiontime is 4 (400 ms) which feels laggy for
        // interactive slider drags. Callers can pass 0–1 for snappy feedback
        // or omit to use the bridge default.
        if (transitionDeciseconds.HasValue)
            state["transitiontime"] = Math.Max(0, transitionDeciseconds.Value);

        return await PutGroupAction(groupId, state, ct);
    }

    /// <summary>
    /// Set the colour of a Hue group via CIE xy chromaticity. Use this when the
    /// user has chosen a non-circadian colour range (e.g. white to red) where
    /// we drive the bulb out of its colour-temperature range. Coordinates are
    /// CIE 1931 xy, both 0..1.
    /// </summary>
    public async Task<bool> SetGroupColorAsync(
        string groupId, double x, double y, int? brightness254,
        int? transitionDeciseconds = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(Username)) throw new InvalidOperationException("Bridge not paired");

        var state = new Dictionary<string, object>
        {
            { "on", true },
            { "xy", new double[] { Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1) } },
        };
        if (brightness254.HasValue) state["bri"] = Math.Clamp(brightness254.Value, 1, 254);
        if (transitionDeciseconds.HasValue)
            state["transitiontime"] = Math.Max(0, transitionDeciseconds.Value);

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

    /// <summary>v0.6.55: read the bridge's stored scenes (the ones the
    /// user has set up in the Hue app or via Hue accessories). Used to
    /// pin existing scenes to Underlit's Quick Scenes row so the user
    /// doesn't have to re-create them.</summary>
    public async Task<List<HueScene>> GetScenesAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(Username)) throw new InvalidOperationException("Bridge not paired");
        using var resp = await _http.GetAsync($"http://{BridgeIp}/api/{Username}/scenes", ct);
        resp.EnsureSuccessStatusCode();
        string body = await resp.Content.ReadAsStringAsync(ct);

        var list = new List<HueScene>();
        using var doc = JsonDocument.Parse(body);
        // /scenes returns an OBJECT keyed by scene id.
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            string id = prop.Name;
            string name = prop.Value.TryGetProperty("name", out var n) ? (n.GetString() ?? id) : id;
            string type = prop.Value.TryGetProperty("type", out var t) ? (t.GetString() ?? "") : "";
            string? group = prop.Value.TryGetProperty("group", out var g) && g.ValueKind == JsonValueKind.String
                ? g.GetString() : null;
            // recycle = true means it's a transient scene Hue created
            // for "Last on state" tracking; we hide those from the user.
            bool recycle = prop.Value.TryGetProperty("recycle", out var r) && r.ValueKind == JsonValueKind.True;
            list.Add(new HueScene { Id = id, Name = name, Type = type, GroupId = group, Recycle = recycle });
        }
        return list;
    }

    /// <summary>Tell the bridge to play a stored scene. The scene's own
    /// metadata determines which lights it affects, so we just PUT to
    /// /groups/0/action (the virtual "all lights" group) with the scene
    /// id - the bridge then dispatches to the scene's member lights.</summary>
    public async Task<bool> ActivateSceneAsync(string sceneId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(Username)) throw new InvalidOperationException("Bridge not paired");
        try
        {
            string body = JsonSerializer.Serialize(new Dictionary<string, object> { { "scene", sceneId } });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PutAsync(
                $"http://{BridgeIp}/api/{Username}/groups/0/action", content, ct);
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
    /// <summary>v0.6.53: ids of the individual lights in this group.
    /// Used by HueController to expand group selections into per-light
    /// pushes so per-bulb gamut adaptation works for grouped lights too.</summary>
    public List<string> LightIds { get; set; } = new();
    public int LightCount => LightIds.Count;
    public override string ToString() =>
        LightCount > 0 ? $"{Name} ({LightCount} lights - {Type})" : $"{Name} ({Type})";
}

/// <summary>
/// One Hue Bridge light. v0.6.53.
///
/// Underlit uses three pieces of per-light data at runtime:
///   - <see cref="CtMinMireds"/> / <see cref="CtMaxMireds"/>: the bulb's
///     hardware colour-temperature range, in mireds. IKEA Tradfri tunables
///     typically report 250..454 mireds (4000..2200 K). Hue tunables go
///     wider, ~153..500. Used by the gamut-aware push logic so we can
///     gracefully fade out bulbs that can't reach the schedule's deepest
///     warmth instead of clamping at the bulb's max-warmth point.
///   - <see cref="HasColor"/>: whether the bulb supports xy chromaticity
///     (i.e. full colour, not just CT). Needed to know if WhiteToRed mode
///     applies to it.
///   - <see cref="On"/>: the bulb's current on/off state. Read before each
///     push so manual on/off via Google Home / the Hue app / a wall switch
///     is respected (Underlit never re-enables a bulb the user turned off).
/// </summary>
public sealed class HueLight
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string Manufacturer { get; set; } = "";

    /// <summary>Mireds the bulb supports; null when the bulb has no
    /// CT control (e.g. on/off-only bulbs, pure colour bulbs without a
    /// white axis).</summary>
    public int? CtMinMireds { get; set; }
    public int? CtMaxMireds { get; set; }

    public bool HasColor { get; set; }

    // Live state at the moment GetLightsAsync / GetLightAsync was called.
    public bool On { get; set; }
    public int? Brightness { get; set; }
    public int? Mireds { get; set; }
    public bool Reachable { get; set; } = true;
    /// <summary>v0.7.0: "xy", "ct" or "hs" (null for white-only bulbs).</summary>
    public string? ColorMode { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
    public int? HueValue { get; set; }
    public int? Saturation { get; set; }

    /// <summary>v0.6.55: true iff this entity is a smart plug rather
    /// than a bulb. Hue's own SmartPlug and IKEA Tradfri plug-in units
    /// report type="On/off plug-in unit". We surface plugs in their own
    /// settings section so the schedule push doesn't try to send them
    /// CT/bri (which they ignore) and so the user can wire routines
    /// (e.g. "turn on the SAD lamp at wake time") to them.</summary>
    public bool IsPlug =>
        Type.Equals("On/off plug-in unit", StringComparison.OrdinalIgnoreCase)
        || Type.IndexOf("plug", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Compact human label used in the Lights settings list.
    /// Shows the bulb's CT range (or "colour"/"on-off"/"plug" for
    /// non-CT bulbs) so the user can spot which IKEA bulbs will fade
    /// out and which Hue bulbs will go full red.</summary>
    public string DisplayLabel
    {
        get
        {
            string suffix;
            if (IsPlug)
            {
                suffix = "smart plug";
            }
            else if (CtMinMireds.HasValue && CtMaxMireds.HasValue)
            {
                int minK = (int)Math.Round(1_000_000.0 / CtMaxMireds.Value);
                int maxK = (int)Math.Round(1_000_000.0 / CtMinMireds.Value);
                suffix = HasColor
                    ? $"colour + {minK}-{maxK} K"
                    : $"{minK}-{maxK} K";
            }
            else
            {
                suffix = HasColor ? "colour" : "on/off";
            }
            string status = Reachable ? "" : " - unreachable";
            return $"{Name} ({suffix}{status})";
        }
    }
    public override string ToString() => DisplayLabel;
}

/// <summary>v0.6.55: a stored scene on the Hue Bridge. Activated via
/// <see cref="HueBridgeClient.ActivateSceneAsync"/>. Underlit lists
/// them under "From Hue app" in the Quick Scenes card so the user can
/// re-use scenes they've already curated in the official Hue app.</summary>
public sealed class HueScene
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string? GroupId { get; set; }
    /// <summary>Recycle = true means it's a transient bridge-managed
    /// scene (e.g. "Last on state" trackers). We filter these out of
    /// the user-facing list.</summary>
    public bool Recycle { get; set; }
}
