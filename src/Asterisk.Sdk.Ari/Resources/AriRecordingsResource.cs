using System.Text.Json;
using Asterisk.Sdk;
using Asterisk.Sdk.Ari.Client;

namespace Asterisk.Sdk.Ari.Resources;

/// <summary>ARI Recordings resource - REST operations on live and stored recordings.</summary>
public sealed class AriRecordingsResource : IAriRecordingsResource
{
    private readonly HttpClient _http;

    internal AriRecordingsResource(HttpClient http)
    {
        _http = http;
    }

    public async ValueTask<AriLiveRecording> GetLiveAsync(string recordingName, CancellationToken cancellationToken = default)
    {
        var response = await _http.GetAsync($"recordings/live/{Uri.EscapeDataString(recordingName)}", cancellationToken);
        await response.EnsureAriSuccessAsync();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriLiveRecording)!;
    }

    public async ValueTask StopAsync(string recordingName, CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsync(
            $"recordings/live/{Uri.EscapeDataString(recordingName)}/stop", null, cancellationToken);
        await response.EnsureAriSuccessAsync();
    }

    public async ValueTask DeleteStoredAsync(string recordingName, CancellationToken cancellationToken = default)
    {
        var response = await _http.DeleteAsync(
            $"recordings/stored/{Uri.EscapeDataString(recordingName)}", cancellationToken);
        await response.EnsureAriSuccessAsync();
    }
}
