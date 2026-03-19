using System.Runtime.CompilerServices;
using System.Security;
using System.Text;
using Asterisk.Sdk.Audio;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.VoiceAi.Tts.Azure;

/// <summary>
/// Azure Cognitive Services TTS provider over REST. Sends SSML and streams
/// raw PCM audio back in chunked frames.
/// </summary>
public sealed class AzureTtsSpeechSynthesizer : SpeechSynthesizer
{
    private readonly AzureTtsOptions _options;
    private readonly HttpClient _http;
    private readonly int _chunkSize;

    /// <summary>Initializes a new instance for production use.</summary>
    public AzureTtsSpeechSynthesizer(IOptions<AzureTtsOptions> options, HttpClient http)
    {
        _options = options.Value;
        _http = http;
        _chunkSize = 4096;
    }

    /// <summary>Initializes a new instance for testing with a custom chunk size.</summary>
    internal AzureTtsSpeechSynthesizer(
        IOptions<AzureTtsOptions> options,
        HttpClient http,
        int chunkSize)
    {
        _options = options.Value;
        _http = http;
        _chunkSize = chunkSize;
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

        var uri = new Uri(
            $"https://{_options.Region}.tts.speech.microsoft.com/cognitiveservices/v1");

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
