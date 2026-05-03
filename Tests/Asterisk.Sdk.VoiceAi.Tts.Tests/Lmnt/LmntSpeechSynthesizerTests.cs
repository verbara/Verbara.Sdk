using System.Net;
using Asterisk.Sdk.Audio;
using Asterisk.Sdk.VoiceAi.Tts.DependencyInjection;
using Asterisk.Sdk.VoiceAi.Tts.Lmnt;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Asterisk.Sdk.VoiceAi.Tts.Tests.Lmnt;

// ─────────────────────────────────────────────────────────────────────────────
// Options tests
// ─────────────────────────────────────────────────────────────────────────────

public class LmntTtsOptionsTests
{
    [Fact]
    public void LmntTtsOptions_ShouldDefaultTransportToWebSocket()
    {
        var opts = new LmntTtsOptions();
        opts.Transport.Should().Be(LmntTransport.WebSocket);
    }

    [Fact]
    public void LmntTtsOptions_ShouldDefaultVoiceToLeah()
    {
        var opts = new LmntTtsOptions();
        opts.Voice.Should().Be(LmntVoices.Leah);
    }

    [Fact]
    public void LmntTtsOptions_ShouldDefaultFormatToRaw()
    {
        var opts = new LmntTtsOptions();
        opts.Format.Should().Be("raw");
    }

    [Fact]
    public void LmntTtsOptions_ShouldDefaultSampleRateTo16000()
    {
        var opts = new LmntTtsOptions();
        opts.SampleRate.Should().Be(16000);
    }

    [Fact]
    public void LmntTtsOptions_ShouldDefaultSpeedTo1()
    {
        var opts = new LmntTtsOptions();
        opts.Speed.Should().Be(1.0);
    }

    [Fact]
    public void LmntTtsOptionsValidator_ShouldFail_WhenApiKeyEmpty()
    {
        var validator = new LmntTtsOptionsValidator();
        var result = validator.Validate(null, new LmntTtsOptions { ApiKey = string.Empty });
        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void LmntTtsOptionsValidator_ShouldSucceed_WhenApiKeyProvided()
    {
        var validator = new LmntTtsOptionsValidator();
        var result = validator.Validate(null, new LmntTtsOptions { ApiKey = "lmnt-key" });
        result.Succeeded.Should().BeTrue();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// WebSocket transport tests
// ─────────────────────────────────────────────────────────────────────────────

public class LmntSpeechSynthesizerWsTests : IAsyncDisposable
{
    private readonly LmntWsFakeServer _server;

    public LmntSpeechSynthesizerWsTests()
    {
        _server = new LmntWsFakeServer();
        _server.Start();
    }

    private LmntSpeechSynthesizer BuildSynthesizer(Action<LmntTtsOptions>? configure = null)
    {
        var opts = new LmntTtsOptions
        {
            ApiKey = "test-lmnt-key",
            Voice = LmntVoices.Leah,
            Format = "raw",
            Transport = LmntTransport.WebSocket,
        };
        configure?.Invoke(opts);
        return new LmntSpeechSynthesizer(Options.Create(opts), fakeWsPort: _server.Port);
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldSendInitMessage_WithApiKeyVoiceAndFormat()
    {
        var synth = BuildSynthesizer();
        await synth.SynthesizeAsync("hello world", AudioFormat.Slin16Mono16kHz).ToListAsync();

        _server.ReceivedJsonMessages.Should().NotBeEmpty();
        var init = _server.ReceivedJsonMessages[0];

        // The first message must contain X-API-Key (auth embedded in body), voice, and format.
        init.Should().Contain("\"X-API-Key\"");
        init.Should().Contain("test-lmnt-key");
        init.Should().Contain("\"voice\"");
        init.Should().Contain(LmntVoices.Leah);
        init.Should().Contain("\"format\"");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldSendTextMessage_WithCorrectText()
    {
        var synth = BuildSynthesizer();
        await synth.SynthesizeAsync("hello lmnt", AudioFormat.Slin16Mono16kHz).ToListAsync();

        // At least one message after init should contain the synthesized text.
        var textMessages = _server.ReceivedJsonMessages.Skip(1).ToList();
        textMessages.Should().NotBeEmpty();
        textMessages.Any(m => m.Contains("hello lmnt")).Should().BeTrue();
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldYieldBinaryAudioFrames_WhenServerSendsFrames()
    {
        var synth = BuildSynthesizer();
        var frames = await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono16kHz).ToListAsync();

        frames.Should().HaveCount(2);
        frames.All(f => f.Length == 320).Should().BeTrue();
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldTerminate_WhenServerSendsFinish()
    {
        _server.SendFinishTerminator = true;
        var synth = BuildSynthesizer();

        var frames = await synth.SynthesizeAsync("test", AudioFormat.Slin16Mono16kHz).ToListAsync();

        frames.Should().HaveCount(2);
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldComplete_WhenServerAborts()
    {
        _server.AbortAfterSend = true;
        var synth = BuildSynthesizer();

        var act = async () => await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono16kHz)
            .ToListAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldAbort_WhenCancelled()
    {
        // Strategy: server holds the socket open indefinitely (no frames, no finish, no close).
        // Cancel only after the fake server confirms receipt of the init message,
        // guaranteeing ReadAllAsync(ct) is blocked inside the channel reader.
        // Polling on an observable signal avoids wall-clock flakiness on slow CI runners
        // (mirrors the Deepgram STT deflake from issue #32).
        _server.AudioFramesToSend.Clear();
        _server.SendFinishTerminator = false;

        using var cts = new CancellationTokenSource();
        var synth = BuildSynthesizer();

        // Fire-and-forget: cancel the moment the server received the first message.
        var cancelTrigger = Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (_server.ReceivedJsonMessages.Count == 0)
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException(
                        "LMNT fake server did not receive any message within 5 s; synthesizer never started.");
                await Task.Delay(5).ConfigureAwait(false);
            }
            await cts.CancelAsync().ConfigureAwait(false);
        });

        // Use ToListAsync(cts.Token): ensures the token is checked on each iteration
        // so the OCE propagates even if the channel reader completes first.
        var act = async () => await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono16kHz, cts.Token)
            .ToListAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await cancelTrigger; // surface any helper-side timeout
    }

    [Fact]
    public async Task SynthesizeAsync_WsInit_ShouldIncludeFlushAndEof_InSubsequentMessages()
    {
        var synth = BuildSynthesizer();
        await synth.SynthesizeAsync("flush test", AudioFormat.Slin16Mono16kHz).ToListAsync();

        // The synthesizer must send flush and eof after the text message.
        var allMessages = string.Concat(_server.ReceivedJsonMessages);
        allMessages.Should().Contain("\"flush\"");
        allMessages.Should().Contain("\"eof\"");
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _server.DisposeAsync();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// HTTP transport tests
// ─────────────────────────────────────────────────────────────────────────────

public class LmntSpeechSynthesizerHttpTests : IAsyncDisposable
{
    private readonly LmntHttpFakeServer _server;
    private readonly HttpClient _http;

    public LmntSpeechSynthesizerHttpTests()
    {
        _server = new LmntHttpFakeServer();
        _server.Start();
        _http = new HttpClient();
    }

    private LmntSpeechSynthesizer BuildSynthesizer(Action<LmntTtsOptions>? configure = null)
    {
        var opts = new LmntTtsOptions
        {
            ApiKey = "test-lmnt-key",
            Voice = LmntVoices.Leah,
            Transport = LmntTransport.Http,
        };
        configure?.Invoke(opts);
        return new LmntSpeechSynthesizer(Options.Create(opts), _http, _server.BaseUri);
    }

    [Fact]
    public async Task SynthesizeAsync_Http_ShouldPostToGenerateEndpoint_WithApiKeyHeader()
    {
        var synth = BuildSynthesizer();
        await synth.SynthesizeAsync("hello http", AudioFormat.Slin16Mono16kHz).ToListAsync();

        _server.ReceivedApiKey.Should().Be("test-lmnt-key");
    }

    [Fact]
    public async Task SynthesizeAsync_Http_ShouldSendLmntVersionHeader()
    {
        var synth = BuildSynthesizer();
        await synth.SynthesizeAsync("hello", AudioFormat.Slin16Mono16kHz).ToListAsync();

        _server.ReceivedLmntVersion.Should().Be("1.0");
    }

    [Fact]
    public async Task SynthesizeAsync_Http_ShouldIncludeVoiceAndTextInBody()
    {
        var synth = BuildSynthesizer();
        await synth.SynthesizeAsync("form body test", AudioFormat.Slin16Mono16kHz).ToListAsync();

        _server.ReceivedRequestBody.Should().NotBeNullOrEmpty();
        _server.ReceivedRequestBody!.Should().Contain("voice=");
        _server.ReceivedRequestBody.Should().Contain("text=");
    }

    [Fact]
    public async Task SynthesizeAsync_Http_ShouldYieldAudioBytes_WhenResponseIsSuccess()
    {
        _server.ResponseAudio = new byte[10_000];
        for (var i = 0; i < _server.ResponseAudio.Length; i++)
            _server.ResponseAudio[i] = (byte)(i & 0xFF);

        var synth = BuildSynthesizer();
        var chunks = await synth.SynthesizeAsync("large", AudioFormat.Slin16Mono16kHz).ToListAsync();

        chunks.Should().NotBeEmpty();
        chunks.Sum(c => c.Length).Should().Be(10_000);
    }

    [Fact]
    public async Task SynthesizeAsync_Http_ShouldThrow_WhenResponseIsErrorStatus()
    {
        _server.ResponseStatus = HttpStatusCode.Unauthorized;

        var synth = BuildSynthesizer();

        var act = async () => await synth
            .SynthesizeAsync("fail", AudioFormat.Slin16Mono16kHz)
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

// ─────────────────────────────────────────────────────────────────────────────
// DI / integration-level tests
// ─────────────────────────────────────────────────────────────────────────────

public class LmntDiTests
{
    [Fact]
    public async Task AddLmntSpeechSynthesizer_ShouldRegisterAsSpeechSynthesizer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLmntSpeechSynthesizer(o => o.ApiKey = "lmnt-key");

        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<SpeechSynthesizer>().Should().BeOfType<LmntSpeechSynthesizer>();
    }

    [Fact]
    public async Task AddLmntSpeechSynthesizer_ShouldApplyOptions_WhenConfigured()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLmntSpeechSynthesizer(o =>
        {
            o.ApiKey = "my-key";
            o.Voice = LmntVoices.Amy;
            o.Transport = LmntTransport.Http;
        });

        await using var provider = services.BuildServiceProvider();
        var opts = provider.GetRequiredService<IOptions<LmntTtsOptions>>().Value;
        opts.Voice.Should().Be(LmntVoices.Amy);
        opts.Transport.Should().Be(LmntTransport.Http);
    }
}
