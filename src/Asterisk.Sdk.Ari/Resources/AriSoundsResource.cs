using System.Text.Json;
using Asterisk.Sdk;

namespace Asterisk.Sdk.Ari.Resources;

/// <summary>ARI Sounds resource - REST operations on sounds.</summary>
public sealed class AriSoundsResource : IAriSoundsResource
{
    private readonly HttpClient _http;

    internal AriSoundsResource(HttpClient http)
    {
        _http = http;
    }

    public async ValueTask<AriSound[]> ListAsync(CancellationToken cancellationToken = default)
    {
        var response = await _http.GetAsync("sounds", cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriSoundArray) ?? [];
    }

    public async ValueTask<AriSound> GetAsync(string soundId, CancellationToken cancellationToken = default)
    {
        var response = await _http.GetAsync($"sounds/{Uri.EscapeDataString(soundId)}", cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriSound)!;
    }
}
