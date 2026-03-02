using System.Text.Json;
using Asterisk.Sdk;

namespace Asterisk.Sdk.Ari.Resources;

/// <summary>ARI Playbacks resource - REST operations on playbacks.</summary>
public sealed class AriPlaybacksResource : IAriPlaybacksResource
{
    private readonly HttpClient _http;

    internal AriPlaybacksResource(HttpClient http)
    {
        _http = http;
    }

    public async ValueTask<AriPlayback> GetAsync(string playbackId, CancellationToken cancellationToken = default)
    {
        var response = await _http.GetAsync($"playbacks/{Uri.EscapeDataString(playbackId)}", cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriPlayback)!;
    }

    public async ValueTask StopAsync(string playbackId, CancellationToken cancellationToken = default)
    {
        var response = await _http.DeleteAsync($"playbacks/{Uri.EscapeDataString(playbackId)}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask ControlAsync(string playbackId, string operation, CancellationToken cancellationToken = default)
    {
        var url = $"playbacks/{Uri.EscapeDataString(playbackId)}/control?operation={Uri.EscapeDataString(operation)}";
        var response = await _http.PostAsync(url, null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
