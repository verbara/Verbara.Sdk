using System.Text.Json;
using Asterisk.Sdk;
using Asterisk.Sdk.Ari.Client;

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
        await response.EnsureAriSuccessAsync();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriApplicationArray) ?? [];
    }

    public async ValueTask<AriApplication> GetAsync(string applicationName, CancellationToken cancellationToken = default)
    {
        var response = await _http.GetAsync($"applications/{Uri.EscapeDataString(applicationName)}", cancellationToken);
        await response.EnsureAriSuccessAsync("application", applicationName);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriApplication)!;
    }

    public async ValueTask<AriApplication> SubscribeAsync(string applicationName, string eventSource, CancellationToken cancellationToken = default)
    {
        var url = $"applications/{Uri.EscapeDataString(applicationName)}/subscription?eventSource={Uri.EscapeDataString(eventSource)}";
        var response = await _http.PostAsync(url, null, cancellationToken);
        await response.EnsureAriSuccessAsync("application", applicationName);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriApplication)!;
    }

    public async ValueTask<AriApplication> UnsubscribeAsync(string applicationName, string eventSource, CancellationToken cancellationToken = default)
    {
        var url = $"applications/{Uri.EscapeDataString(applicationName)}/subscription?eventSource={Uri.EscapeDataString(eventSource)}";
        var response = await _http.DeleteAsync(url, cancellationToken);
        await response.EnsureAriSuccessAsync("application", applicationName);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriApplication)!;
    }

    public async ValueTask<AriApplication> SetEventFilterAsync(string applicationName, CancellationToken cancellationToken = default)
    {
        var url = $"applications/{Uri.EscapeDataString(applicationName)}/eventFilter";
        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        var response = await _http.SendAsync(request, cancellationToken);
        await response.EnsureAriSuccessAsync("application", applicationName);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriApplication)!;
    }
}
