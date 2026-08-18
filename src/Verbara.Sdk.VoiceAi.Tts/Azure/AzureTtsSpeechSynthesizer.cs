using System.Runtime.CompilerServices;
using System.Security;
using System.Text;
using Verbara.Sdk.Audio;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.VoiceAi.Tts.Azure;

/// <summary>
/// Azure Cognitive Services TTS provider over REST. Sends SSML and streams
/// raw PCM audio back in chunked frames.
/// </summary>
public sealed class AzureTtsSpeechSynthesizer : SpeechSynthesizer
{
    /// <summary>Route this provider posts to, appended to whichever origin is in effect.</summary>
    private const string SynthesisPath = "/cognitiveservices/v1";

    private readonly AzureTtsOptions _options;
    private readonly HttpClient _http;
    private readonly int _chunkSize;
    private readonly string? _fakeOrigin;

    /// <inheritdoc />
    public override string ProviderName => "Azure";

    /// <summary>Initializes a new instance for production use.</summary>
    public AzureTtsSpeechSynthesizer(IOptions<AzureTtsOptions> options, HttpClient http)
    {
        _options = options.Value;
        _http = http;
        _chunkSize = 4096;
    }

    /// <summary>
    /// Initializes a new instance for testing with a custom chunk size and, optionally, a fake
    /// server origin.
    /// </summary>
    /// <param name="options">Provider options, as in the production constructor.</param>
    /// <param name="http">Client the test owns and disposes.</param>
    /// <param name="chunkSize">Frame size the response stream is sliced into.</param>
    /// <param name="fakeOrigin">
    /// Scheme, host and port of an in-process test server — the IPv4 loopback literal, never
    /// <c>localhost</c> (ADR-0044). Only the <em>origin</em> is substituted: <see cref="SynthesisPath"/>
    /// is still appended by this class, so a test server matching strictly on the path is asserting
    /// the route this provider really builds rather than one the test handed it.
    /// </param>
    internal AzureTtsSpeechSynthesizer(
        IOptions<AzureTtsOptions> options,
        HttpClient http,
        int chunkSize,
        string? fakeOrigin = null)
    {
        _options = options.Value;
        _http = http;
        _chunkSize = chunkSize;
        _fakeOrigin = fakeOrigin;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
        string text,
        AudioFormat outputFormat,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var escapedText = SecurityElement.Escape(text) ?? string.Empty;
        var ssml = $"""
            <speak version='1.0' xml:lang='{_options.Language}'>
                <voice name='{_options.VoiceName}'>{escapedText}</voice>
            </speak>
            """;

        // Map the requested AudioFormat sample rate to Azure output format string,
        // falling back to the configured option for unknown rates.
        var outputFormatStr = outputFormat.SampleRate switch
        {
            8000 => AzureTtsOutputFormat.Raw8Khz16BitMonoPcm,
            16000 => AzureTtsOutputFormat.Raw16Khz16BitMonoPcm,
            24000 => AzureTtsOutputFormat.Raw24Khz16BitMonoPcm,
            48000 => AzureTtsOutputFormat.Raw48Khz16BitMonoPcm,
            _ => _options.OutputFormat
        };

        var origin = _fakeOrigin
            // endpoint-allow: REGION-TEMPLATED — the origin is interpolated from _options.Region and
            // AzureTtsOptions exposes no endpoint property, so no single constant can express it.
            // This surface passed both halves of its probe; the exemption is about shape, not doubt.
            ?? $"https://{_options.Region}.tts.speech.microsoft.com";
        var uri = new Uri(new Uri(origin), SynthesisPath);

        using var req = new HttpRequestMessage(HttpMethod.Post, uri);
        req.Headers.Add("Ocp-Apim-Subscription-Key", _options.ApiKey);
        req.Headers.Add("X-Microsoft-OutputFormat", outputFormatStr);
        req.Content = new StringContent(ssml, Encoding.UTF8, "application/ssml+xml");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buf = new byte[_chunkSize];
        int read;
        while ((read = await stream.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            var chunk = new byte[read];
            buf.AsSpan(0, read).CopyTo(chunk);
            yield return chunk.AsMemory();
        }
    }
}
