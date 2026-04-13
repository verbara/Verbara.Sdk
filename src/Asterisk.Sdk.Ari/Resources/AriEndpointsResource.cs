using System.Text.Json;
using Asterisk.Sdk;
using Asterisk.Sdk.Ari.Client;

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
        await response.EnsureAriSuccessAsync();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriEndpointArray) ?? [];
    }

    public async ValueTask<AriEndpoint> GetAsync(string tech, string resource, CancellationToken cancellationToken = default)
    {
        var url = $"endpoints/{Uri.EscapeDataString(tech)}/{Uri.EscapeDataString(resource)}";
        var response = await _http.GetAsync(url, cancellationToken);
        await response.EnsureAriSuccessAsync("endpoint", $"{tech}/{resource}");
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriEndpoint)!;
    }

    public async ValueTask<AriEndpoint[]> ListByTechAsync(string tech, CancellationToken cancellationToken = default)
    {
        var url = $"endpoints/{Uri.EscapeDataString(tech)}";
        var response = await _http.GetAsync(url, cancellationToken);
        await response.EnsureAriSuccessAsync("endpoint", tech);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriEndpointArray) ?? [];
    }

    public async ValueTask SendMessageAsync(string destination, string from, string? body = null, CancellationToken cancellationToken = default)
    {
        var url = $"endpoints/sendMessage?to={Uri.EscapeDataString(destination)}&from={Uri.EscapeDataString(from)}";
        if (body is not null) url += $"&body={Uri.EscapeDataString(body)}";
        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        var response = await _http.SendAsync(request, cancellationToken);
        await response.EnsureAriSuccessAsync();
    }

    public async ValueTask SendMessageToEndpointAsync(string tech, string resource, string from, string? body = null, CancellationToken cancellationToken = default)
    {
        var url = $"endpoints/{Uri.EscapeDataString(tech)}/{Uri.EscapeDataString(resource)}/sendMessage?from={Uri.EscapeDataString(from)}";
        if (body is not null) url += $"&body={Uri.EscapeDataString(body)}";
        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        var response = await _http.SendAsync(request, cancellationToken);
        await response.EnsureAriSuccessAsync();
    }
}
