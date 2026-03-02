using System.Text.Json;
using Asterisk.Sdk;

namespace Asterisk.Sdk.Ari.Resources;

/// <summary>ARI Endpoints resource - REST operations on endpoints.</summary>
public sealed class AriEndpointsResource : IAriEndpointsResource
{
    private readonly HttpClient _http;

    internal AriEndpointsResource(HttpClient http)
    {
        _http = http;
    }

    public async ValueTask<AriEndpoint[]> ListAsync(CancellationToken cancellationToken = default)
    {
        var response = await _http.GetAsync("endpoints", cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriEndpointArray) ?? [];
    }

    public async ValueTask<AriEndpoint> GetAsync(string tech, string resource, CancellationToken cancellationToken = default)
    {
        var url = $"endpoints/{Uri.EscapeDataString(tech)}/{Uri.EscapeDataString(resource)}";
        var response = await _http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriEndpoint)!;
    }
}
