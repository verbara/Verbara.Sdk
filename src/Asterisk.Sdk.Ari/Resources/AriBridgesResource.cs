using System.Text.Json;
using Asterisk.Sdk;
using Asterisk.Sdk.Ari.Client;

namespace Asterisk.Sdk.Ari.Resources;

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

    public async ValueTask<AriBridge[]> ListAsync(CancellationToken cancellationToken = default)
    {
        var response = await _http.GetAsync("bridges", cancellationToken);
        await response.EnsureAriSuccessAsync();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriBridgeArray) ?? [];
    }

    public async ValueTask<AriBridge> CreateAsync(string? type = null, string? name = null, CancellationToken cancellationToken = default)
    {
        var query = "";
        if (type is not null) query += $"type={Uri.EscapeDataString(type)}&";
        if (name is not null) query += $"name={Uri.EscapeDataString(name)}&";

        var response = await _http.PostAsync($"bridges?{query.TrimEnd('&')}", null, cancellationToken);
        await response.EnsureAriSuccessAsync();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriBridge)!;
    }

    public async ValueTask<AriBridge> GetAsync(string bridgeId, CancellationToken cancellationToken = default)
    {
        var response = await _http.GetAsync($"bridges/{Uri.EscapeDataString(bridgeId)}", cancellationToken);
        await response.EnsureAriSuccessAsync();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriBridge)!;
    }

    public async ValueTask DestroyAsync(string bridgeId, CancellationToken cancellationToken = default)
    {
        var response = await _http.DeleteAsync($"bridges/{Uri.EscapeDataString(bridgeId)}", cancellationToken);
        await response.EnsureAriSuccessAsync();
    }

    public async ValueTask AddChannelAsync(string bridgeId, string channelId, CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsync(
            $"bridges/{Uri.EscapeDataString(bridgeId)}/addChannel?channel={Uri.EscapeDataString(channelId)}",
            null, cancellationToken);
        await response.EnsureAriSuccessAsync();
    }

    public async ValueTask RemoveChannelAsync(string bridgeId, string channelId, CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsync(
            $"bridges/{Uri.EscapeDataString(bridgeId)}/removeChannel?channel={Uri.EscapeDataString(channelId)}",
            null, cancellationToken);
        await response.EnsureAriSuccessAsync();
    }
}
