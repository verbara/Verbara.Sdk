using System.Net;
using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.Tts.Speechmatics;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tts.Tests.Speechmatics;

public class SpeechmaticsSpeechSynthesizerTests : IAsyncDisposable
{
    private readonly SpeechmaticsFakeServer _server;
    private readonly HttpClient _http;

    public SpeechmaticsSpeechSynthesizerTests()
    {
        _server = new SpeechmaticsFakeServer();
        _server.Start();
        _http = new HttpClient();
    }

    private SpeechmaticsSpeechSynthesizer BuildSynthesizer(Action<SpeechmaticsOptions>? configure = null)
    {
        var opts = new SpeechmaticsOptions
        {
            ApiKey = "test-key",
            Voice = "eleanor",
            Language = "en",
            SampleRate = 16000,
        };
        configure?.Invoke(opts);
        return new SpeechmaticsSpeechSynthesizer(Options.Create(opts), _http, _server.Origin);
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldPostJson_WithTextLanguageAndAuth()
    {
        var synth = BuildSynthesizer(o =>
        {
            o.Voice = "eleanor";
            o.Language = "es";
        });

        await synth.SynthesizeAsync("hola mundo", AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.ReceivedRequestJson.Should().NotBeNullOrEmpty();
        var json = _server.ReceivedRequestJson!;
        json.Should().Contain("\"text\":\"hola mundo\"");
        json.Should().Contain("\"language\":\"es\"");
        json.Should().Contain("\"sample_rate\":8000"); // AudioFormat.Slin16Mono8kHz → 8000 Hz
        _server.ReceivedAuthorization.Should().Be("Bearer test-key");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldSelectVoiceByPathSegment_NotByBodyField()
    {
        var synth = BuildSynthesizer(o => o.Voice = "eleanor");

        await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        // The vendor selects the voice by path segment; /generate answers 404. Asserting the
        // segment is what makes the route a tested property rather than an assumption.
        _server.ReceivedMethod.Should().Be("POST");
        _server.ReceivedPath.Should().Be("/generate/eleanor");
        _server.ReceivedVoice.Should().Be("eleanor");
        _server.UnmatchedRequestCount.Should().Be(0);

        // And it is NOT also sent in the body: which one wins when they disagree was never measured.
        _server.ReceivedRequestJson.Should().NotContain("\"voice\"");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldNotPostToBareGenerate_WhenVoiceIsConfigured()
    {
        // Regression guard for the shipped defect: every request the client made went to
        // /generate with the voice in the body, which the live API answers 404. The old fake
        // never inspected the path, so three green tests certified a route that never worked.
        var synth = BuildSynthesizer(o => o.Voice = "eleanor");

        await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.ReceivedPath.Should().NotBe("/generate");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldEscapeVoice_WhenItContainsUriReservedCharacters()
    {
        var synth = BuildSynthesizer(o => o.Voice = "a b/c");

        var act = async () => await synth
            .SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz)
            .ToListAsync();

        // Escaped, the slash stays inside one segment and the route still matches.
        await act.Should().NotThrowAsync();
        _server.ReceivedVoice.Should().Be("a b/c");
        _server.UnmatchedRequestCount.Should().Be(0);
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldYieldAudioBytes_WhenResponseIsSuccess()
    {
        // 10 KB payload forces at least 2 chunks (ChunkSize = 8192).
        _server.ResponseAudio = new byte[10_000];
        for (var i = 0; i < _server.ResponseAudio.Length; i++)
            _server.ResponseAudio[i] = (byte)(i & 0xFF);

        var synth = BuildSynthesizer();
        var chunks = await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        chunks.Should().NotBeEmpty();
        var totalBytes = chunks.Sum(c => c.Length);
        totalBytes.Should().Be(10_000);
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldThrow_WhenResponseIsErrorStatus()
    {
        _server.ResponseStatus = HttpStatusCode.Unauthorized;

        var synth = BuildSynthesizer();

        var act = async () => await synth
            .SynthesizeAsync("fail", AudioFormat.Slin16Mono8kHz)
            .ToListAsync();

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        _http.Dispose();
        await _server.DisposeAsync();
    }
}
