using System.Text.Json;
using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Ari.Client;

namespace Asterisk.NetAot.Ari.Resources;

/// <summary>ARI Bridges resource - REST operations on bridges.</summary>
public sealed class AriBridgesResource : IAriBridgesResource
{
    private readonly HttpClient _http;
    private readonly AriClientOptions _options;

    internal AriBridgesResource(HttpClient http, AriClientOptions options)
    {
        _http = http;
        _options = options;
    }

    public async ValueTask<AriBridge> CreateAsync(string? type = null, string? name = null, CancellationToken cancellationToken = default)
    {
        var query = "";
        if (type is not null) query += $"type={type}&";
        if (name is not null) query += $"name={name}&";

        var response = await _http.PostAsync($"bridges?{query.TrimEnd('&')}", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriBridge)!;
    }

    public async ValueTask<AriBridge> GetAsync(string bridgeId, CancellationToken cancellationToken = default)
    {
        var response = await _http.GetAsync($"bridges/{bridgeId}", cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriBridge)!;
    }

    public async ValueTask DestroyAsync(string bridgeId, CancellationToken cancellationToken = default)
    {
        var response = await _http.DeleteAsync($"bridges/{bridgeId}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask AddChannelAsync(string bridgeId, string channelId, CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsync($"bridges/{bridgeId}/addChannel?channel={channelId}", null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask RemoveChannelAsync(string bridgeId, string channelId, CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsync($"bridges/{bridgeId}/removeChannel?channel={channelId}", null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
