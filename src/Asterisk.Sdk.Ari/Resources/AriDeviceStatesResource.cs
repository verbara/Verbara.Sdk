using System.Text.Json;
using Asterisk.Sdk;

namespace Asterisk.Sdk.Ari.Resources;

/// <summary>ARI DeviceStates resource - REST operations on device states.</summary>
public sealed class AriDeviceStatesResource : IAriDeviceStatesResource
{
    private readonly HttpClient _http;

    internal AriDeviceStatesResource(HttpClient http)
    {
        _http = http;
    }

    public async ValueTask<AriDeviceState[]> ListAsync(CancellationToken cancellationToken = default)
    {
        var response = await _http.GetAsync("deviceStates", cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriDeviceStateArray) ?? [];
    }

    public async ValueTask<AriDeviceState> GetAsync(string deviceName, CancellationToken cancellationToken = default)
    {
        var url = $"deviceStates/{Uri.EscapeDataString(deviceName)}";
        var response = await _http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriDeviceState)!;
    }

    public async ValueTask UpdateAsync(string deviceName, string deviceState, CancellationToken cancellationToken = default)
    {
        var url = $"deviceStates/{Uri.EscapeDataString(deviceName)}?deviceState={Uri.EscapeDataString(deviceState)}";
        var response = await _http.PutAsync(url, null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask DeleteAsync(string deviceName, CancellationToken cancellationToken = default)
    {
        var url = $"deviceStates/{Uri.EscapeDataString(deviceName)}";
        var response = await _http.DeleteAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
