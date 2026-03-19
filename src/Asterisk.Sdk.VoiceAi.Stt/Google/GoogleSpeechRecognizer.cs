using System.Text.Json;
using Asterisk.Sdk.Audio;
using Asterisk.Sdk.VoiceAi.Stt.Internal;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.VoiceAi.Stt.Google;

/// <summary>
/// Google Cloud Speech-to-Text v1 REST STT provider. Sends base64-encoded audio
/// in a JSON request body and returns the best transcript.
/// </summary>
public sealed class GoogleSpeechRecognizer : SpeechRecognizer
{
    private readonly GoogleSpeechOptions _options;
    private readonly HttpClient _http;

    /// <summary>Initializes a new instance for production use with DI-managed HttpClient.</summary>
    public GoogleSpeechRecognizer(IOptions<GoogleSpeechOptions> options, HttpClient http)
    {
        _options = options.Value;
        _http = http;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<SpeechRecognitionResult> StreamAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audioFrames,
        AudioFormat format,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var pcmData = await DrainFramesAsync(audioFrames, ct).ConfigureAwait(false);
        var base64Audio = Convert.ToBase64String(pcmData);

        var request = new GoogleSpeechRequest
        {
            Config = new GoogleSpeechConfig
            {
                Encoding = "LINEAR16",
                SampleRateHertz = format.SampleRate,
                LanguageCode = _options.LanguageCode,
                Model = _options.Model
            },
            Audio = new GoogleSpeechAudio { Content = base64Audio }
        };

        var json = JsonSerializer.Serialize(request, VoiceAiSttJsonContext.Default.GoogleSpeechRequest);
        var uri = new Uri($"https://speech.googleapis.com/v1/speech:recognize?key={_options.ApiKey}");

        using var req = new HttpRequestMessage(HttpMethod.Post, uri);
        req.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var responseJson = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize(responseJson,
            VoiceAiSttJsonContext.Default.GoogleSpeechResponse);

        var alt = result?.Results?.FirstOrDefault()?.Alternatives?.FirstOrDefault();
        if (alt is not null)
            yield return new SpeechRecognitionResult(alt.Transcript, alt.Confidence, true, TimeSpan.Zero);
    }

    private static async Task<byte[]> DrainFramesAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> frames, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await foreach (var frame in frames.WithCancellation(ct).ConfigureAwait(false))
            ms.Write(frame.Span);
        return ms.ToArray();
    }
}
