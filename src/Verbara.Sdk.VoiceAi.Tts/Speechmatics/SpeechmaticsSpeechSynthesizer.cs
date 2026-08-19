using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.Tts.Internal;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.VoiceAi.Tts.Speechmatics;

/// <summary>
/// Speechmatics REST TTS provider. POSTs a JSON request body and streams the
/// audio response back as <see cref="ReadOnlyMemory{T}"/> chunks.
/// </summary>
/// <remarks>
/// Unlike streaming WebSocket providers (Cartesia, ElevenLabs), Speechmatics TTS
/// is a single HTTPS request/response. This provider reads the response body in
/// ~8 KB chunks so the caller sees deterministic streaming without buffering the
/// full audio in memory.
/// </remarks>
public sealed class SpeechmaticsSpeechSynthesizer : SpeechSynthesizer
{
    private const int ChunkSize = 8192;

    private readonly SpeechmaticsOptions _options;
    private readonly HttpClient _http;
    private readonly string? _fakeOrigin;
    private readonly bool _ownsHttpClient;

    /// <inheritdoc />
    public override string ProviderName => "Speechmatics";

    /// <summary>Initializes a new instance for production use.</summary>
    public SpeechmaticsSpeechSynthesizer(IOptions<SpeechmaticsOptions> options)
    {
        _options = options.Value;
        // Construct HttpClient internally (AOT-clean, no factory reflection).
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds),
        };
        _ownsHttpClient = true;
    }

    /// <summary>
    /// Initializes a new instance for testing against a fake server. <paramref name="fakeOrigin"/>
    /// is an <b>origin</b>, not a complete endpoint: the production path is appended to it exactly
    /// as it is in production, so the fake sees the route the vendor sees.
    /// </summary>
    internal SpeechmaticsSpeechSynthesizer(
        IOptions<SpeechmaticsOptions> options,
        HttpClient http,
        string fakeOrigin)
    {
        _options = options.Value;
        _http = http;
        _fakeOrigin = fakeOrigin;
        _ownsHttpClient = false;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
        string text,
        AudioFormat outputFormat,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Nothing is asked of the provider for text that carries no speech, so the zero audio that
        // follows is not a provider failure and must not be reported as one (ADR-0050 E5). Not
        // theoretical on this vendor: measured 2026-08-18 against the live route, an empty or
        // whitespace `text` is answered `200 audio/wav` with 7 724 bytes, of which 3 817 of the
        // 3 840 samples are non-zero — 0.24 s of audible audio, not silence. Without this guard a
        // caller that was promised nothing paid for a request and had that audio pushed into its
        // stream. The same probe returned the same body for punctuation-only text, which this guard
        // deliberately does NOT catch: the contract's words are "empty or whitespace", and widening
        // it to "text a human would not read aloud" is a judgement no measurement supports.
        if (string.IsNullOrWhiteSpace(text)) yield break;

        var sampleRate = outputFormat.SampleRate > 0 ? outputFormat.SampleRate : _options.SampleRate;
        var payload = new SpeechmaticsTtsRequest
        {
            Text = text,
            Language = _options.Language,
            SampleRate = sampleRate,
        };

        var json = JsonSerializer.Serialize(payload, VoiceAiTtsJsonContext.Default.SpeechmaticsTtsRequest);

        // The API selects the voice by path segment: /generate/{voice} returns 200 audio/wav while
        // /generate returns 404. The voice is deliberately NOT also sent in the body: a probe on
        // 2026-08-16 showed the vendor returns identical output when both agree, but which one wins
        // when they disagree was not measured, and an unmeasured precedence is not something to
        // depend on.
        var origin = (_fakeOrigin ?? _options.BaseUri).TrimEnd('/');
        var uri = $"{origin}/generate/{Uri.EscapeDataString(_options.Voice)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buf = new byte[ChunkSize];
        var yieldedAudio = false;
        while (true)
        {
            // ADR-0052 F1. Cancellation stays what ADR-0050 E6 said it was — not a provider
            // failure, not counted, not wrapped — but it may not end the caller's sequence
            // silently: a truncated stream is indistinguishable from a whole one, which is the
            // shape ADR-0050 exists to retire. The token propagates to the caller's next
            // MoveNextAsync instead of being converted into a short result.
            var read = await stream.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);

            if (read == 0)
            {
                // ADR-0050 E5 on this transport too. The contract is declared on
                // SpeechSynthesizer.SynthesizeAsync, so it cannot hold over WebSocket and not over
                // HTTP; a 200 that carries no audio is exactly the silent zero-audio result E5
                // names, and without this a truncated synthesis completes indistinguishably from a
                // whole one.
                //
                // The guard counts bytes, not samples, and that boundary is deliberate. The live
                // route was probed on 2026-08-18 and never returned an empty body: the smallest
                // response seen was a 44-byte RIFF header followed by 7 680 bytes of data. So this
                // arm closes a gap in a contract this package publishes rather than a failure this
                // vendor is currently observed to produce. A header-only response carrying zero
                // samples would still pass here — catching that would mean parsing the container,
                // which this provider deliberately does not do (it yields the vendor's bytes
                // unexamined), and no measurement yet says the vendor emits one.
                if (!yieldedAudio)
                {
                    throw new SpeechProviderEmptyResultException(
                        ProviderName,
                        $"{ProviderName} answered the synthesis request with no audio and no error status.");
                }

                yield break;
            }

            yieldedAudio = true;
            var chunk = new byte[read];
            buf.AsSpan(0, read).CopyTo(chunk);
            yield return chunk.AsMemory();
        }
    }

    /// <inheritdoc />
    public override ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        if (_ownsHttpClient) _http.Dispose();
        return base.DisposeAsync();
    }
}
