using System.Text.Json;
using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.Stt.Internal;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.VoiceAi.Stt.Google;

/// <summary>
/// Google Cloud Speech-to-Text v1 REST STT provider. Sends base64-encoded audio
/// in a JSON request body and returns the best transcript.
/// </summary>
public sealed class GoogleSpeechRecognizer : SpeechRecognizer
{
    /// <summary>Route this provider posts to, appended to whichever origin is in effect.</summary>
    private const string RecognizePath = "/v1/speech:recognize";

    /// <summary>Origin the provider dials in production.</summary>
    private const string ProductionOrigin = "https://speech.googleapis.com";

    private readonly GoogleSpeechOptions _options;
    private readonly HttpClient _http;
    private readonly string? _fakeOrigin;

    /// <inheritdoc />
    public override string ProviderName => "Google";

    /// <summary>Initializes a new instance for production use with DI-managed HttpClient.</summary>
    public GoogleSpeechRecognizer(IOptions<GoogleSpeechOptions> options, HttpClient http)
    {
        _options = options.Value;
        _http = http;
    }

    /// <summary>Initializes a new instance for testing with a fake server origin.</summary>
    /// <param name="options">Provider options, as in the production constructor.</param>
    /// <param name="http">Client the test owns and disposes.</param>
    /// <param name="fakeOrigin">
    /// Scheme, host and port of an in-process test server — the IPv4 loopback literal, never
    /// <c>localhost</c> (ADR-0044). Only the <em>origin</em> is substituted: <see cref="RecognizePath"/>
    /// and the <c>key</c> query parameter are still appended by this class, so a test server matching
    /// strictly on path and query is asserting the request this provider really builds rather than
    /// one the test handed it.
    /// </param>
    internal GoogleSpeechRecognizer(
        IOptions<GoogleSpeechOptions> options,
        HttpClient http,
        string fakeOrigin)
    {
        _options = options.Value;
        _http = http;
        _fakeOrigin = fakeOrigin;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<SpeechRecognitionResult> StreamAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audioFrames,
        AudioFormat format,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Deterministic cancellation contract (test-determinism fence): observe the token
        // at iterator entry so a pre-cancelled token throws before any provider request is
        // issued, independent of scheduling/mock latency.
        ct.ThrowIfCancellationRequested();

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
        var origin = _fakeOrigin ?? ProductionOrigin;
        var uri = new Uri($"{origin}{RecognizePath}?key={_options.ApiKey}");

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
