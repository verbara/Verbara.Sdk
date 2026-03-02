using System.Text.Json;
using Asterisk.Sdk;
using Asterisk.Sdk.Ari.Client;

namespace Asterisk.Sdk.Ari.Resources;

/// <summary>ARI Channels resource - REST operations on channels.</summary>
public sealed class AriChannelsResource : IAriChannelsResource
{
    private readonly HttpClient _http;
    private readonly AriClientOptions _options;

    internal AriChannelsResource(HttpClient http, AriClientOptions options)
    {
        _http = http;
        _options = options;
    }

    public async ValueTask<AriChannel[]> ListAsync(CancellationToken cancellationToken = default)
    {
        var response = await _http.GetAsync("channels", cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriChannelArray) ?? [];
    }

    public async ValueTask<AriChannel> CreateAsync(string endpoint, string? app = null, CancellationToken cancellationToken = default)
    {
        var appName = app ?? _options.Application;
        var url = $"channels?endpoint={Uri.EscapeDataString(endpoint)}&app={Uri.EscapeDataString(appName)}";
        var response = await _http.PostAsync(url, null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriChannel)!;
    }

    public async ValueTask<AriChannel> GetAsync(string channelId, CancellationToken cancellationToken = default)
    {
        var response = await _http.GetAsync($"channels/{Uri.EscapeDataString(channelId)}", cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriChannel)!;
    }

    public async ValueTask HangupAsync(string channelId, CancellationToken cancellationToken = default)
    {
        var response = await _http.DeleteAsync($"channels/{Uri.EscapeDataString(channelId)}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask<AriChannel> OriginateAsync(string endpoint, string? extension = null, string? context = null, CancellationToken cancellationToken = default)
    {
        var url = $"channels?endpoint={Uri.EscapeDataString(endpoint)}&app={Uri.EscapeDataString(_options.Application)}";
        if (extension is not null) url += $"&extension={Uri.EscapeDataString(extension)}";
        if (context is not null) url += $"&context={Uri.EscapeDataString(context)}";

        var response = await _http.PostAsync(url, null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriChannel)!;
    }
}
