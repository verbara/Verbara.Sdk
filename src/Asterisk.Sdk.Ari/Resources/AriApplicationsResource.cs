using System.Text.Json;
using Asterisk.Sdk;

namespace Asterisk.Sdk.Ari.Resources;

/// <summary>ARI Applications resource - REST operations on Stasis applications.</summary>
public sealed class AriApplicationsResource : IAriApplicationsResource
{
    private readonly HttpClient _http;

    internal AriApplicationsResource(HttpClient http)
    {
        _http = http;
    }

    public async ValueTask<AriApplication[]> ListAsync(CancellationToken cancellationToken = default)
    {
        var response = await _http.GetAsync("applications", cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriApplicationArray) ?? [];
    }

    public async ValueTask<AriApplication> GetAsync(string applicationName, CancellationToken cancellationToken = default)
    {
        var response = await _http.GetAsync($"applications/{Uri.EscapeDataString(applicationName)}", cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriApplication)!;
    }
}
