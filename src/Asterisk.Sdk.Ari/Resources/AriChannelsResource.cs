using System.Text.Json;
using Asterisk.Sdk;
using Asterisk.Sdk.Ari.Client;

namespace Asterisk.Sdk.Ari.Resources;

/// <summary>ARI Channels resource - REST operations on channels.</summary>
public sealed class AriChannelsResource : IAriChannelsResource
{
    private readonly HttpClient _http;
    private readonly AriClientOptions _options;

    internal AriChannelsResource(HttpClient http, AriClientOptions options)
    {
        _http = http;
        _options = options;
    }

    public async ValueTask<AriChannel[]> ListAsync(CancellationToken cancellationToken = default)
    {
        var response = await _http.GetAsync("channels", cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriChannelArray) ?? [];
    }

    public async ValueTask<AriChannel> CreateAsync(string endpoint, string? app = null, CancellationToken cancellationToken = default)
    {
        var appName = app ?? _options.Application;
        var url = $"channels?endpoint={Uri.EscapeDataString(endpoint)}&app={Uri.EscapeDataString(appName)}";
        var response = await _http.PostAsync(url, null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriChannel)!;
    }

    public async ValueTask<AriChannel> GetAsync(string channelId, CancellationToken cancellationToken = default)
    {
        var response = await _http.GetAsync($"channels/{Uri.EscapeDataString(channelId)}", cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriChannel)!;
    }

    public async ValueTask HangupAsync(string channelId, CancellationToken cancellationToken = default)
    {
        var response = await _http.DeleteAsync($"channels/{Uri.EscapeDataString(channelId)}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask<AriChannel> OriginateAsync(string endpoint, string? extension = null, string? context = null, CancellationToken cancellationToken = default)
    {
        var url = $"channels?endpoint={Uri.EscapeDataString(endpoint)}&app={Uri.EscapeDataString(_options.Application)}";
        if (extension is not null) url += $"&extension={Uri.EscapeDataString(extension)}";
        if (context is not null) url += $"&context={Uri.EscapeDataString(context)}";

        var response = await _http.PostAsync(url, null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriChannel)!;
    }

    public async ValueTask RingAsync(string channelId, CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsync($"channels/{Uri.EscapeDataString(channelId)}/ring", null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask ProgressAsync(string channelId, CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsync($"channels/{Uri.EscapeDataString(channelId)}/progress", null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask AnswerAsync(string channelId, CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsync($"channels/{Uri.EscapeDataString(channelId)}/answer", null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask<AriChannel> CreateExternalMediaAsync(string app, string externalHost, string format,
        string? encapsulation = null, string? transport = null, string? connectionType = null,
        string? direction = null, string? data = null, CancellationToken cancellationToken = default)
    {
        var url = $"channels/externalMedia?app={Uri.EscapeDataString(app)}&external_host={Uri.EscapeDataString(externalHost)}&format={Uri.EscapeDataString(format)}";
        if (encapsulation is not null) url += $"&encapsulation={Uri.EscapeDataString(encapsulation)}";
        if (transport is not null) url += $"&transport={Uri.EscapeDataString(transport)}";
        if (connectionType is not null) url += $"&connection_type={Uri.EscapeDataString(connectionType)}";
        if (direction is not null) url += $"&direction={Uri.EscapeDataString(direction)}";
        if (data is not null) url += $"&data={Uri.EscapeDataString(data)}";

        var response = await _http.PostAsync(url, null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriChannel)!;
    }

    public async ValueTask<AriVariable> GetVariableAsync(string channelId, string variable, CancellationToken cancellationToken = default)
    {
        var response = await _http.GetAsync($"channels/{Uri.EscapeDataString(channelId)}/variable?variable={Uri.EscapeDataString(variable)}", cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriVariable)!;
    }

    public async ValueTask SetVariableAsync(string channelId, string variable, string value, CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsync($"channels/{Uri.EscapeDataString(channelId)}/variable?variable={Uri.EscapeDataString(variable)}&value={Uri.EscapeDataString(value)}", null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask HoldAsync(string channelId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"channels/{Uri.EscapeDataString(channelId)}/hold");
        var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask UnholdAsync(string channelId, CancellationToken cancellationToken = default)
    {
        var response = await _http.DeleteAsync($"channels/{Uri.EscapeDataString(channelId)}/hold", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask MuteAsync(string channelId, string? direction = null, CancellationToken cancellationToken = default)
    {
        var url = $"channels/{Uri.EscapeDataString(channelId)}/mute";
        if (direction is not null) url += $"?direction={Uri.EscapeDataString(direction)}";
        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask UnmuteAsync(string channelId, string? direction = null, CancellationToken cancellationToken = default)
    {
        var url = $"channels/{Uri.EscapeDataString(channelId)}/mute";
        if (direction is not null) url += $"?direction={Uri.EscapeDataString(direction)}";
        var response = await _http.DeleteAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask SendDtmfAsync(string channelId, string dtmf, int? before = null, int? between = null, int? duration = null, int? after = null, CancellationToken cancellationToken = default)
    {
        var url = $"channels/{Uri.EscapeDataString(channelId)}/dtmf?dtmf={Uri.EscapeDataString(dtmf)}";
        if (before is not null) url += $"&before={before}";
        if (between is not null) url += $"&between={between}";
        if (duration is not null) url += $"&duration={duration}";
        if (after is not null) url += $"&after={after}";
        var response = await _http.PostAsync(url, null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask<AriPlayback> PlayAsync(string channelId, string media, string? lang = null, int? offsetms = null, int? skipms = null, string? playbackId = null, CancellationToken cancellationToken = default)
    {
        var url = $"channels/{Uri.EscapeDataString(channelId)}/play?media={Uri.EscapeDataString(media)}";
        if (lang is not null) url += $"&lang={Uri.EscapeDataString(lang)}";
        if (offsetms is not null) url += $"&offsetms={offsetms}";
        if (skipms is not null) url += $"&skipms={skipms}";
        if (playbackId is not null) url += $"&playbackId={Uri.EscapeDataString(playbackId)}";
        var response = await _http.PostAsync(url, null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriPlayback)!;
    }

    public async ValueTask<AriLiveRecording> RecordAsync(string channelId, string name, string format, int? maxDurationSeconds = null, int? maxSilenceSeconds = null, string? ifExists = null, bool? beep = null, string? terminateOn = null, CancellationToken cancellationToken = default)
    {
        var url = $"channels/{Uri.EscapeDataString(channelId)}/record?name={Uri.EscapeDataString(name)}&format={Uri.EscapeDataString(format)}";
        if (maxDurationSeconds is not null) url += $"&maxDurationSeconds={maxDurationSeconds}";
        if (maxSilenceSeconds is not null) url += $"&maxSilenceSeconds={maxSilenceSeconds}";
        if (ifExists is not null) url += $"&ifExists={Uri.EscapeDataString(ifExists)}";
        if (beep is not null) url += $"&beep={beep.Value.ToString().ToLowerInvariant()}";
        if (terminateOn is not null) url += $"&terminateOn={Uri.EscapeDataString(terminateOn)}";
        var response = await _http.PostAsync(url, null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriLiveRecording)!;
    }

    public async ValueTask<AriChannel> SnoopAsync(string channelId, string app, string? spy = null, string? whisper = null, string? snoopId = null, CancellationToken cancellationToken = default)
    {
        var url = $"channels/{Uri.EscapeDataString(channelId)}/snoop?app={Uri.EscapeDataString(app)}";
        if (spy is not null) url += $"&spy={Uri.EscapeDataString(spy)}";
        if (whisper is not null) url += $"&whisper={Uri.EscapeDataString(whisper)}";
        if (snoopId is not null) url += $"&snoopId={Uri.EscapeDataString(snoopId)}";
        var response = await _http.PostAsync(url, null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriChannel)!;
    }

    public async ValueTask RedirectAsync(string channelId, string endpoint, CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsync($"channels/{Uri.EscapeDataString(channelId)}/redirect?endpoint={Uri.EscapeDataString(endpoint)}", null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask ContinueAsync(string channelId, string? context = null, string? extension = null, int? priority = null, string? label = null, CancellationToken cancellationToken = default)
    {
        var url = $"channels/{Uri.EscapeDataString(channelId)}/continue";
        var sep = '?';
        if (context is not null) { url += $"{sep}context={Uri.EscapeDataString(context)}"; sep = '&'; }
        if (extension is not null) { url += $"{sep}extension={Uri.EscapeDataString(extension)}"; sep = '&'; }
        if (priority is not null) { url += $"{sep}priority={priority}"; sep = '&'; }
        if (label is not null) { url += $"{sep}label={Uri.EscapeDataString(label)}"; }
        var response = await _http.PostAsync(url, null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask<AriChannel> CreateWithoutDialAsync(string endpoint, string app, string? channelId = null, string? otherChannelId = null, string? originator = null, IReadOnlyDictionary<string, string>? variables = null, CancellationToken cancellationToken = default)
    {
        var url = $"channels/create?endpoint={Uri.EscapeDataString(endpoint)}&app={Uri.EscapeDataString(app)}";
        if (channelId is not null) url += $"&channelId={Uri.EscapeDataString(channelId)}";
        if (otherChannelId is not null) url += $"&otherChannelId={Uri.EscapeDataString(otherChannelId)}";
        if (originator is not null) url += $"&originator={Uri.EscapeDataString(originator)}";
        if (variables is not null)
        {
            foreach (var kv in variables)
                url += $"&variables[{Uri.EscapeDataString(kv.Key)}]={Uri.EscapeDataString(kv.Value)}";
        }
        var response = await _http.PostAsync(url, null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriChannel)!;
    }
}
