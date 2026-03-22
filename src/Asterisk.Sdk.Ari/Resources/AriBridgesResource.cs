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

    public async ValueTask<AriPlayback> PlayAsync(string bridgeId, string media, string? lang = null, int? offsetms = null, int? skipms = null, string? playbackId = null, string? format = null, CancellationToken cancellationToken = default)
    {
        var query = $"media={Uri.EscapeDataString(media)}";
        if (lang is not null) query += $"&lang={Uri.EscapeDataString(lang)}";
        if (offsetms.HasValue) query += $"&offsetms={offsetms.Value}";
        if (skipms.HasValue) query += $"&skipms={skipms.Value}";
        if (playbackId is not null) query += $"&playbackId={Uri.EscapeDataString(playbackId)}";
        if (format is not null) query += $"&format={Uri.EscapeDataString(format)}";

        var response = await _http.PostAsync(
            $"bridges/{Uri.EscapeDataString(bridgeId)}/play?{query}", null, cancellationToken);
        await response.EnsureAriSuccessAsync();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriPlayback)!;
    }

    public async ValueTask<AriLiveRecording> RecordAsync(string bridgeId, string name, string recordingFormat, int? maxDurationSeconds = null, int? maxSilenceSeconds = null, string? ifExists = null, bool? beep = null, string? terminateOn = null, string? format = null, CancellationToken cancellationToken = default)
    {
        var query = $"name={Uri.EscapeDataString(name)}&format={Uri.EscapeDataString(recordingFormat)}";
        if (maxDurationSeconds.HasValue) query += $"&maxDurationSeconds={maxDurationSeconds.Value}";
        if (maxSilenceSeconds.HasValue) query += $"&maxSilenceSeconds={maxSilenceSeconds.Value}";
        if (ifExists is not null) query += $"&ifExists={Uri.EscapeDataString(ifExists)}";
        if (beep.HasValue) query += $"&beep={beep.Value.ToString().ToLowerInvariant()}";
        if (terminateOn is not null) query += $"&terminateOn={Uri.EscapeDataString(terminateOn)}";
        if (format is not null) query += $"&announcerFormat={Uri.EscapeDataString(format)}";

        var response = await _http.PostAsync(
            $"bridges/{Uri.EscapeDataString(bridgeId)}/record?{query}", null, cancellationToken);
        await response.EnsureAriSuccessAsync();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriLiveRecording)!;
    }

    public async ValueTask<AriBridge> CreateWithIdAsync(string bridgeId, string? type = null, string? name = null, CancellationToken cancellationToken = default)
    {
        var query = "";
        if (type is not null) query += $"type={Uri.EscapeDataString(type)}&";
        if (name is not null) query += $"name={Uri.EscapeDataString(name)}&";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"bridges/{Uri.EscapeDataString(bridgeId)}?{query.TrimEnd('&')}");
        var response = await _http.SendAsync(request, cancellationToken);
        await response.EnsureAriSuccessAsync();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriBridge)!;
    }

    public async ValueTask SetVideoSourceAsync(string bridgeId, string channelId, CancellationToken cancellationToken = default)
    {
        var url = $"bridges/{Uri.EscapeDataString(bridgeId)}/videoSource/{Uri.EscapeDataString(channelId)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        var response = await _http.SendAsync(request, cancellationToken);
        await response.EnsureAriSuccessAsync();
    }

    public async ValueTask ClearVideoSourceAsync(string bridgeId, CancellationToken cancellationToken = default)
    {
        var url = $"bridges/{Uri.EscapeDataString(bridgeId)}/videoSource";
        var response = await _http.DeleteAsync(url, cancellationToken);
        await response.EnsureAriSuccessAsync();
    }

    public async ValueTask StartMohAsync(string bridgeId, string? mohClass = null, CancellationToken cancellationToken = default)
    {
        var url = $"bridges/{Uri.EscapeDataString(bridgeId)}/moh";
        if (mohClass is not null) url += $"?mohClass={Uri.EscapeDataString(mohClass)}";
        var response = await _http.PostAsync(url, null, cancellationToken);
        await response.EnsureAriSuccessAsync();
    }

    public async ValueTask StopMohAsync(string bridgeId, CancellationToken cancellationToken = default)
    {
        var url = $"bridges/{Uri.EscapeDataString(bridgeId)}/moh";
        var response = await _http.DeleteAsync(url, cancellationToken);
        await response.EnsureAriSuccessAsync();
    }
}
