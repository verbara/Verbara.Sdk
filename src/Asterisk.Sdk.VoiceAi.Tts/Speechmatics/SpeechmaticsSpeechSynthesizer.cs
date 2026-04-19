using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Asterisk.Sdk.Audio;
using Asterisk.Sdk.VoiceAi.Tts.Internal;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.VoiceAi.Tts.Speechmatics;

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
    private readonly string? _fakeBaseUri;
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

    /// <summary>Initializes a new instance for testing with a fake server.</summary>
    internal SpeechmaticsSpeechSynthesizer(
        IOptions<SpeechmaticsOptions> options,
        HttpClient http,
        string fakeBaseUri)
    {
        _options = options.Value;
        _http = http;
        _fakeBaseUri = fakeBaseUri;
        _ownsHttpClient = false;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
        string text,
        AudioFormat outputFormat,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var sampleRate = outputFormat.SampleRate > 0 ? outputFormat.SampleRate : _options.SampleRate;
        var payload = new SpeechmaticsTtsRequest
        {
            Text = text,
            Voice = _options.Voice,
            Language = _options.Language,
            SampleRate = sampleRate,
        };

        var json = JsonSerializer.Serialize(payload, VoiceAiTtsJsonContext.Default.SpeechmaticsTtsRequest);

        var uri = _fakeBaseUri ?? _options.BaseUri;
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
        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { yield break; }

            if (read == 0) yield break;

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
