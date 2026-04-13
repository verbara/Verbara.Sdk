using System.Text.Json;
using Asterisk.Sdk;
using Asterisk.Sdk.Ari.Client;

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
        await response.EnsureAriSuccessAsync();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriDeviceStateArray) ?? [];
    }

    public async ValueTask<AriDeviceState> GetAsync(string deviceName, CancellationToken cancellationToken = default)
    {
        var url = $"deviceStates/{Uri.EscapeDataString(deviceName)}";
        var response = await _http.GetAsync(url, cancellationToken);
        await response.EnsureAriSuccessAsync("deviceState", deviceName);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriDeviceState)!;
    }

    public async ValueTask UpdateAsync(string deviceName, string deviceState, CancellationToken cancellationToken = default)
    {
        var url = $"deviceStates/{Uri.EscapeDataString(deviceName)}?deviceState={Uri.EscapeDataString(deviceState)}";
        var response = await _http.PutAsync(url, null, cancellationToken);
        await response.EnsureAriSuccessAsync("deviceState", deviceName);
    }

    public async ValueTask DeleteAsync(string deviceName, CancellationToken cancellationToken = default)
    {
        var url = $"deviceStates/{Uri.EscapeDataString(deviceName)}";
        var response = await _http.DeleteAsync(url, cancellationToken);
        await response.EnsureAriSuccessAsync("deviceState", deviceName);
    }
}
