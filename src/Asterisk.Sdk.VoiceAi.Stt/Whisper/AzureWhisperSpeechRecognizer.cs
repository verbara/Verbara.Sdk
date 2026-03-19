using System.Text.Json;
using Asterisk.Sdk.Audio;
using Asterisk.Sdk.VoiceAi.Stt.Internal;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.VoiceAi.Stt.Whisper;

/// <summary>
/// Azure OpenAI Whisper REST STT provider. Uses api-key header authentication
/// and Azure-specific deployment URLs.
/// </summary>
public sealed class AzureWhisperSpeechRecognizer : SpeechRecognizer
{
    private readonly AzureWhisperOptions _options;
    private readonly HttpClient _http;

    /// <summary>Initializes a new instance for production use with DI-managed HttpClient.</summary>
    public AzureWhisperSpeechRecognizer(IOptions<AzureWhisperOptions> options, HttpClient http)
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
        var wavBytes = WhisperSpeechRecognizer.AddWavHeaderStatic(pcmData, format);

        var uri = new Uri($"{_options.Endpoint.ToString().TrimEnd('/')}/{_options.Deployment}" +
            $"/audio/transcriptions?api-version={_options.ApiVersion}");

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(wavBytes), "file", "audio.wav");
        form.Add(new StringContent("whisper-1"), "model");

        using var req = new HttpRequestMessage(HttpMethod.Post, uri);
        req.Headers.Add("api-key", _options.ApiKey);
        req.Content = form;

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize(json,
            VoiceAiSttJsonContext.Default.WhisperTranscriptionResponse);

        if (!string.IsNullOrEmpty(result?.Text))
            yield return new SpeechRecognitionResult(result.Text, 1.0f, true, TimeSpan.Zero);
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
