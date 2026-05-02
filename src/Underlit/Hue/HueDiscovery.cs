using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Underlit.Hue;

/// <summary>One Hue Bridge found on the network. <see cref="Ip"/> is the LAN IP
/// the bridge announced; <see cref="Id"/> is its unique serial number (the
/// bridge ID printed on the device label).</summary>
public sealed class HueDiscoveredBridge
{
    public string Id { get; set; } = "";
    public string Ip { get; set; } = "";
    public override string ToString() => string.IsNullOrEmpty(Id) ? Ip : $"{Ip}  ·  {Id[..Math.Min(6, Id.Length)]}…";
}

/// <summary>
/// Hue Bridge discovery — currently via Philips' cloud endpoint at
/// <c>discovery.meethue.com</c>, which returns the IP of every bridge that has
/// recently announced itself from the same WAN address as the requester. Fast
/// and reliable on a normal home network; gracefully returns an empty list if
/// you're offline / firewalled / on a network where bridges haven't reported.
///
/// UPnP/SSDP-based LAN discovery is a viable fallback (and would let us
/// discover bridges with no internet) but adds a chunk of socket plumbing —
/// deferred to a follow-up if/when users hit the empty-list case.
/// </summary>
public static class HueDiscovery
{
    private const string CloudEndpoint = "https://discovery.meethue.com";

    public static async Task<List<HueDiscoveredBridge>> DiscoverViaCloudAsync(CancellationToken ct = default)
    {
        var result = new List<HueDiscoveredBridge>();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            using var resp = await http.GetAsync(CloudEndpoint, ct);
            if (!resp.IsSuccessStatusCode) return result;
            string body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

            foreach (var elem in doc.RootElement.EnumerateArray())
            {
                string id = elem.TryGetProperty("id", out var idEl) ? (idEl.GetString() ?? "") : "";
                string ip = elem.TryGetProperty("internalipaddress", out var ipEl) ? (ipEl.GetString() ?? "") : "";
                if (!string.IsNullOrEmpty(ip))
                    result.Add(new HueDiscoveredBridge { Id = id, Ip = ip });
            }
        }
        catch
        {
            // Network errors → empty list. The Lights settings page lets the
            // user fall back to a manual IP entry, which works offline.
        }
        return result;
    }
}
