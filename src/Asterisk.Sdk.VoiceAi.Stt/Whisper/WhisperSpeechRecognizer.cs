using System.Net.Http.Headers;
using System.Text.Json;
using Asterisk.Sdk.Audio;
using Asterisk.Sdk.VoiceAi.Stt.Internal;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.VoiceAi.Stt.Whisper;

/// <summary>
/// OpenAI Whisper REST STT provider. Drains all audio frames into a WAV buffer,
/// then sends a single multipart POST request to the Whisper API.
/// </summary>
public sealed class WhisperSpeechRecognizer : SpeechRecognizer
{
    private readonly WhisperOptions _options;
    private readonly HttpClient _http;

    /// <summary>Initializes a new instance for production use.</summary>
    public WhisperSpeechRecognizer(IOptions<WhisperOptions> options)
        : this(options, new HttpClient()) { }

    /// <summary>Initializes a new instance for testing with a custom HttpClient.</summary>
    internal WhisperSpeechRecognizer(IOptions<WhisperOptions> options, HttpClient http)
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
        var wavBytes = AddWavHeaderStatic(pcmData, format);

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(wavBytes), "file", "audio.wav");
        form.Add(new StringContent(_options.Model), "model");
        form.Add(new StringContent(_options.Language), "language");

        using var req = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
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
        var ms = new MemoryStream();
        await foreach (var frame in frames.WithCancellation(ct).ConfigureAwait(false))
            ms.Write(frame.Span);
        return ms.ToArray();
    }

    internal static byte[] AddWavHeaderStatic(byte[] pcmData, AudioFormat format)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        int sampleRate = format.SampleRate;
        short channels = (short)format.Channels;
        short bitsPerSample = (short)format.BitsPerSample;
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        short blockAlign = (short)(channels * bitsPerSample / 8);

        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + pcmData.Length);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1);
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write(bitsPerSample);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(pcmData.Length);
        bw.Write(pcmData);
        return ms.ToArray();
    }
}
