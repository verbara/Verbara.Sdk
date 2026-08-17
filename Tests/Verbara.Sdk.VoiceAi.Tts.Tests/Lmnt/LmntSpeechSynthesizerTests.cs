using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Verbara.Sdk.Audio;
using Verbara.Sdk.TestInfrastructure.WebSocket;
using Verbara.Sdk.VoiceAi.Tts.DependencyInjection;
using Verbara.Sdk.VoiceAi.Tts.Lmnt;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tts.Tests.Lmnt;

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
    public void LmntTtsOptions_ShouldDefaultFormatToPcmS16Le()
    {
        // Not "raw". Measured 2026-08-15: `raw` is 16-bit PCM over WebSocket but an MP3 frame
        // stream over HTTP, so the old default handed HTTP callers MP3 bytes labelled Slin16.
        // pcm_s16le is the only value that is int16 PCM on both transports.
        var opts = new LmntTtsOptions();
        opts.Format.Should().Be("pcm_s16le");
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

/// <summary>
/// Transport: WebSocket (LMNT's default). Deliberately NOT migrated to the WireMock substrate —
/// WireMock.NET matches HTTP/1.1 requests and cannot hold the duplex session these tests drive
/// (ADR-0041 D2), so <c>LmntWsFakeServer</c> on <c>WebSocketTestServer</c> stays. LMNT ships both
/// transports, and D3 splits the provider by transport rather than by suite: the HTTP class further
/// down this file migrates (§4.6), this one does not. Fidelity here comes from recorded frames (D4).
/// </summary>
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
            Transport = LmntTransport.WebSocket,
        };
        // Format is deliberately left at its default so the suite exercises what ships.
        configure?.Invoke(opts);
        return new LmntSpeechSynthesizer(Options.Create(opts), fakeWsPort: _server.Port);
    }

    /// <summary>The audio the fake replays, read from the same tree the fake reads.</summary>
    private static byte[] RecordedAudio => LmntWsFakeServer.ReadFrameBytes(LmntWsFakeServer.AudioChunk);

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
    public async Task SynthesizeAsync_ShouldOmitModelField_WhenModelIsNotConfigured()
    {
        // The defect this guards is total, not cosmetic. Model is null by default, and the init
        // message used to serialize it as `"model": null`. The live endpoint validates that field
        // against a literal set, rejects an explicit null with 1002 protocol error, and sends zero
        // audio frames — so LMNT's DEFAULT transport, in its DEFAULT configuration, never produced
        // a single byte. No fake caught it because this one replies with audio regardless of what
        // the init message says.
        var synth = BuildSynthesizer();
        await synth.SynthesizeAsync("hello", AudioFormat.Slin16Mono16kHz).ToListAsync();

        var init = _server.ReceivedJsonMessages[0];
        init.Should().NotContain("\"model\"");
        init.Should().NotContain("null");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldSendModelField_WhenModelIsConfigured()
    {
        // The other half: omitting null must not degrade into omitting the field altogether.
        var synth = BuildSynthesizer(o => o.Model = "blizzard");
        await synth.SynthesizeAsync("hello", AudioFormat.Slin16Mono16kHz).ToListAsync();

        var init = _server.ReceivedJsonMessages[0];
        init.Should().Contain("\"model\":\"blizzard\"");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldNotHalfCloseTheSocket_AfterSendingEof()
    {
        // Third total defect on the same transport, and the one the other two hid. After `eof` the
        // synthesizer used to call CloseOutputAsync. Measured against the live endpoint on
        // 2026-08-15, that yields **zero** audio bytes and ConnectionClosedPrematurely: the server
        // reads the Close frame as "abandon the request". The control run differed in that single
        // step and received 30 688 B — 0.959 s of 16 kHz PCM — then closed NormalClosure itself.
        //
        // `eof` is already the end-of-input signal; the half-close was a second, contradictory one.
        //
        // This asserts on what the client SENT rather than on how a server reacts, because this
        // fake cannot reproduce the vendor's reaction without racing the send. Reading the flag
        // after the stream completes is ordered by causality, not luck: the stream cannot complete
        // until the server closes, the server closes only after sending audio, and a client Close
        // would necessarily precede that audio.
        var synth = BuildSynthesizer();
        var audio = await synth.SynthesizeAsync("hello", AudioFormat.Slin16Mono16kHz).ToListAsync();

        _server.ClientSentCloseFrame.Should().BeFalse(
            "a Close frame after eof makes the live endpoint tear the session down with zero audio");
        audio.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldSendTextMessage_WithCorrectText()
    {
        var synth = BuildSynthesizer();
        await synth.SynthesizeAsync("hello lmnt", AudioFormat.Slin16Mono16kHz).ToListAsync();

        // At least one message after init should contain the synthesized text.
        var textMessages = _server.ReceivedJsonMessages.Skip(1).ToList();
        textMessages.Should().NotBeEmpty();
        textMessages.Any(m => m.Contains("hello lmnt", StringComparison.Ordinal)).Should().BeTrue();
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldYieldBinaryAudioFrames_WhenServerSendsFrames()
    {
        // The point of the recording: a real waveform of a length that is NOT chunk-aligned
        // traverses the frame path, so a partial final frame reaches the consumer. Two 320-byte
        // arrays of zeros — exact multiples — could never produce one.
        //
        // Note what is NOT asserted: that the final frame is exactly `length % 320`. Frame count
        // and boundaries are the transport's business, not the client's contract; what the client
        // owes is every byte, in order, with nothing invented and nothing dropped.
        var expected = RecordedAudio;
        var synth = BuildSynthesizer();

        var frames = await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono16kHz).ToListAsync();

        (expected.Length % LmntWsFakeServer.AudioFrameSize).Should()
            .NotBe(0, "the recording must not be chunk-aligned");
        frames.Should().HaveCountGreaterThan(1, "the recording must actually be chunked");
        frames.Should().OnlyContain(f => f.Length > 0 && f.Length <= LmntWsFakeServer.AudioFrameSize);
        frames.Should().Contain(f => f.Length != LmntWsFakeServer.AudioFrameSize,
            "a partial frame must reach the consumer");
        frames.SelectMany(f => f.ToArray()).Should().Equal(expected,
            "streaming must not alter a single byte of the recorded audio");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldTerminate_WhenServerSendsFinish()
    {
        // Read lmnt-ws/finish-frame.provenance.json before trusting this: `finish` is documented as
        // a CLIENT-to-server message, and LMNT's published server-message set (ready, timestamps,
        // flush_complete, reset_complete, error) does not include it. The SDK terminates on it, so
        // the fake must send it; the discrepancy is recorded in the sidecar rather than papered over.
        _server.SendFinishTerminator = true;
        var synth = BuildSynthesizer();

        var frames = await synth.SynthesizeAsync("test", AudioFormat.Slin16Mono16kHz).ToListAsync();

        frames.SelectMany(f => f.ToArray()).Should().Equal(RecordedAudio);
    }

    [Fact]
    public void RecordedFixtures_ShouldCarryDocumentedFieldsAndExactByteLength_WhenReadFromRecordingsTree()
    {
        // Fixture-integrity fence. The fake is only as good as what is on disk: swap the audio or
        // re-save the JSON and the suite would keep passing while quietly testing something smaller.
        var audio = RecordedAudio;
        audio.Should().HaveCount(1808, "the sidecar records this exact length");
        (audio.Length % LmntWsFakeServer.AudioFrameSize).Should().NotBe(0);

        using var finish = JsonDocument.Parse(LmntWsFakeServer.ReadFrame(LmntWsFakeServer.FinishFrame));
        finish.RootElement.GetProperty("type").GetString().Should().Be("finish",
            "LmntSpeechSynthesizer terminates on exactly this value");
    }

    [Fact]
    public void RecordedFixtures_ShouldMatchTheirDocumentedGeneratorParameters_WhenRegeneratedLocally()
    {
        // The "commit a small generator" half of the source-audio rule (protocol guide §6): the
        // committed bytes are reproducible from three numbers in the sidecar, not magic.
        var regenerated = SyntheticPcm.Triangle(
            LmntWsFakeServer.AudioSampleCount,
            LmntWsFakeServer.AudioPeriodSamples,
            LmntWsFakeServer.AudioAmplitude);

        regenerated.Should().Equal(RecordedAudio);
    }

    /// <summary>
    /// Door 3 (<c>ADR-0050</c> E2c), and the inverse of the assertion this test used to make. It
    /// asserted <c>NotThrowAsync</c> — a socket killed mid-session ended the stream as though the
    /// vendor had finished speaking, so a truncated synthesis was indistinguishable from a complete
    /// one. The audio already yielded still reaches the caller; what changes is that the stream now
    /// ends by raising rather than by completing.
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_ShouldThrowTransportFailure_WhenServerAbortsMidSession()
    {
        _server.AbortAfterSend = true;
        var synth = BuildSynthesizer();

        var act = async () => await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono16kHz)
            .ToListAsync();

        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.Transport);
        failure.Code.Should().BeNull("a dead socket carries no vendor code");
        failure.InnerException.Should().BeOfType<WebSocketException>();
    }

    /// <summary>
    /// The fourth door, and the one no receive-loop test can reach: a session that never opens.
    /// <c>ADR-0050</c> E7 wraps the bare <see cref="WebSocketException"/> so the caller reads one type
    /// whether this vendor validates at the upgrade or in band — this one embeds the credential in the
    /// first message rather than in a header, so its upgrade cannot reject a bad key at all and the
    /// failure necessarily arrives later. A refused connection carries no HTTP answer, hence no code.
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_ShouldThrowHandshakeFailure_WhenNothingAcceptsTheUpgrade()
    {
        // The port is the seam on this surface (there is no BaseUri option), so it is passed directly
        // rather than through BuildSynthesizer — the point of this test is that it never reaches the fake.
        var synth = new LmntSpeechSynthesizer(
            Options.Create(new LmntTtsOptions
            {
                ApiKey = "test-lmnt-key",
                Voice = LmntVoices.Leah,
                Transport = LmntTransport.WebSocket,
            }),
            fakeWsPort: ClosedPort.Reserve());

        var act = async () => await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono16kHz)
            .ToListAsync();

        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.Handshake);
        failure.Code.Should().BeNull("a refused connection produced no HTTP answer to report");
        failure.InnerException.Should().BeAssignableTo<WebSocketException>();
    }

    /// <summary>
    /// The same door reached from the other side: the server crashes (RST) the instant it receives the
    /// client's first frame, so the client's remaining request sends (text/flush/eof) write to a
    /// half-dead socket and no audio ever arrives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This test also used to assert <c>NotThrowAsync</c>: a mid-send crash produced an empty stream
    /// and no exception at all. What it must not assert now is the exact type, and that is a finding
    /// rather than a shortcut. Which of the two failures the caller sees depends on an interleaving
    /// the client does not control — the loop raises <see cref="SpeechProviderFailureException"/> when
    /// the read faults, but if the client's own send observes the reset first the socket is already
    /// <c>Aborted</c> by the time the loop re-checks its condition, and the session ends through the
    /// zero-output rule instead. Both are failures and neither is silent, which is the contract
    /// (<c>ADR-0050</c> E3: one base type callers can catch).
    /// </para>
    /// <para>
    /// Looped 50× because the interleaving is timing-sensitive: a single pass would prove nothing
    /// about either arm.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SynthesizeAsync_ShouldReportAFailure_WhenServerAbortsMidSend()
    {
        _server.AbortOnFirstReceive = true;

        for (var i = 0; i < 50; i++)
        {
            var synth = BuildSynthesizer();

            var act = async () => await synth
                .SynthesizeAsync("test", AudioFormat.Slin16Mono16kHz)
                .ToListAsync();

            await act.Should().ThrowAsync<SpeechProviderException>();
        }
    }

    /// <summary>
    /// Door 1 (<c>ADR-0050</c> E2a) with the frame the live endpoint was measured sending for a
    /// rejected credential: <c>{"error":"Invalid API key"}</c> (§3.7a). The fake closes
    /// <em>normally</em> afterwards, so the frame is the only failure signal in the session and an
    /// exception here cannot have come from the close code.
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_ShouldThrowErrorFrameFailure_WhenCredentialIsRejectedInBand()
    {
        _server.ErrorFrameJson = """{"error":"Invalid API key"}""";
        var synth = BuildSynthesizer();

        var act = async () => await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono16kHz)
            .ToListAsync();

        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.ErrorFrame);
        failure.Provider.Should().Be("LMNT");
        failure.Message.Should().Contain("Invalid API key", "the vendor's reason is the whole evidence");
    }

    /// <summary>
    /// Door 2 (<c>ADR-0050</c> E2b): the vendor ends the session with a code and says nothing else —
    /// which is exactly how this one was measured rejecting a credential, closing <c>1002</c>. No
    /// audio and no <c>finish</c>, so the close code is the only signal the client can read.
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_ShouldThrowCloseCodeFailure_WhenServerClosesAbnormally()
    {
        _server.AudioFramesToSend.Clear();
        _server.SendFinishTerminator = false;
        _server.CloseStatus = WebSocketCloseStatus.ProtocolError;   // 1002, as measured
        _server.CloseStatusDescription = "Invalid API key";
        var synth = BuildSynthesizer();

        var act = async () => await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono16kHz)
            .ToListAsync();

        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.CloseCode);
        failure.Code.Should().Be("1002");
    }

    /// <summary>
    /// D2 (<c>ADR-0050</c> E5): the vendor finishes the session properly — <c>finish</c> then a normal
    /// close — having sent no audio at all. Nothing failed on the wire, so this is not a
    /// <see cref="SpeechProviderFailureException"/>; it is still an empty synthesis, and a caller that
    /// asked for speech and received none must be told.
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_ShouldThrowEmptyResult_WhenSessionEndsCleanlyWithNoAudio()
    {
        _server.AudioFramesToSend.Clear();
        var synth = BuildSynthesizer();

        var act = async () => await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono16kHz)
            .ToListAsync();

        var empty = (await act.Should().ThrowAsync<SpeechProviderEmptyResultException>()).Which;
        empty.Should().NotBeOfType<SpeechProviderFailureException>(
            "nothing failed on the wire — the session was clean and produced nothing");
        empty.Provider.Should().Be("LMNT");
    }

    /// <summary>
    /// The other half of E5: text carrying no speech is not asked of the provider at all, so the zero
    /// audio that follows is not a failure. Asserted through the fake seeing no session, since "did
    /// not throw" alone would also pass if a session had opened and been lucky.
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_ShouldYieldNothingWithoutConnecting_WhenTextIsWhitespace()
    {
        var synth = BuildSynthesizer();

        var frames = await synth.SynthesizeAsync(" \t ", AudioFormat.Slin16Mono16kHz).ToListAsync();

        frames.Should().BeEmpty();
        _server.ReceivedJsonMessages.Should().BeEmpty("no session should have been opened at all");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldAbort_WhenCancelled()
    {
        // Strategy: server holds the socket open indefinitely (no frames, no finish, no close).
        // Cancel only after the fake server confirms receipt of the init message,
        // guaranteeing ReadAllAsync(ct) is blocked inside the channel reader.
        // Polling on an observable signal avoids wall-clock flakiness on slow CI runners
        // (mirrors the Deepgram STT deflake from issue #32).
        //
        // HoldOpenUntilDisposed is what actually enforces "no close" — the strategy above was, until
        // now, only supplied by the fake's fixed 30 ms answer delay winning a race against the 5 ms
        // cancel poll. That is not a guarantee; it is a coincidence that held on this machine.
        _server.AudioFramesToSend.Clear();
        _server.HoldOpenUntilDisposed = true;

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
        return new LmntSpeechSynthesizer(Options.Create(opts), _http, _server.Origin);
    }

    [Fact]
    public async Task SynthesizeAsync_Http_ShouldSendApiKeyHeader()
    {
        var synth = BuildSynthesizer();
        await synth.SynthesizeAsync("hello http", AudioFormat.Slin16Mono16kHz).ToListAsync();

        _server.ReceivedApiKey.Should().Be("test-lmnt-key");
    }

    [Fact]
    public async Task SynthesizeAsync_Http_ShouldPostToBytesRoute_NotGenerate()
    {
        // Regression guard for the shipped defect. Every HTTP synthesis this client ever made went
        // to /v1/ai/speech/generate, which the live API answers 404 {"detail":"Not Found"} —
        // byte-identically to a path that does not exist. The old fake answered 200 on any path, so
        // the suite certified a route that had never once worked.
        var synth = BuildSynthesizer();

        await synth.SynthesizeAsync("hello http", AudioFormat.Slin16Mono16kHz).ToListAsync();

        _server.ReceivedMethod.Should().Be("POST");
        _server.ReceivedPath.Should().Be(LmntHttpFakeServer.SynthesisPath);
        _server.ReceivedPath.Should().NotBe("/v1/ai/speech/generate");
        _server.UnmatchedRequestCount.Should().Be(0);
    }

    [Fact]
    public async Task SynthesizeAsync_Http_ShouldThrow_WhenClientPostsToAnUnknownRoute()
    {
        // Proves the guard above can actually fail: point the client at an origin carrying a path
        // and the fake refuses it exactly as the live API does. Without this, a fake that silently
        // stopped matching would make the route assertions vacuous again.
        var opts = new LmntTtsOptions
        {
            ApiKey = "test-lmnt-key",
            Transport = LmntTransport.Http,
        };
        var synth = new LmntSpeechSynthesizer(
            Options.Create(opts), _http, _server.Origin + "/wrong-prefix");

        var act = async () => await synth
            .SynthesizeAsync("hello", AudioFormat.Slin16Mono16kHz)
            .ToListAsync();

        await act.Should().ThrowAsync<HttpRequestException>();
        _server.UnmatchedRequestCount.Should().Be(1);
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

    /// <summary>
    /// D2 (<c>ADR-0050</c> E5) on the other transport: <c>200 OK</c> with an empty body. The contract
    /// is declared on <c>SpeechSynthesizer.SynthesizeAsync</c>, so it cannot hold over WebSocket and
    /// not here.
    /// </summary>
    /// <remarks>
    /// Note what is deliberately <em>not</em> wrapped: a non-2xx status keeps raising
    /// <see cref="HttpRequestException"/> (the test above). It is already a typed exception, it already
    /// carries the vendor's status, and it is not one of the three silent doors this change closes —
    /// wrapping it would be a behaviour change with no defect behind it.
    /// </remarks>
    [Fact]
    public async Task SynthesizeAsync_Http_ShouldThrowEmptyResult_WhenTheResponseSucceedsWithNoAudio()
    {
        _server.ResponseAudio = [];

        var synth = BuildSynthesizer();

        var act = async () => await synth
            .SynthesizeAsync("silence", AudioFormat.Slin16Mono16kHz)
            .ToListAsync();

        var empty = (await act.Should().ThrowAsync<SpeechProviderEmptyResultException>()).Which;
        empty.Should().NotBeOfType<SpeechProviderFailureException>(
            "the request succeeded — the response merely carried no audio");
        empty.Provider.Should().Be("LMNT");
    }

    /// <summary>
    /// The E5 guard sits before the transport split, so this transport owes the same promise: text
    /// carrying no speech is never sent to the provider, and the zero audio that follows is not a
    /// failure. Asserted through the fake seeing no request at all.
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_Http_ShouldYieldNothingWithoutRequesting_WhenTextIsWhitespace()
    {
        var synth = BuildSynthesizer();

        var chunks = await synth.SynthesizeAsync("   ", AudioFormat.Slin16Mono16kHz).ToListAsync();

        chunks.Should().BeEmpty();
        _server.ReceivedPath.Should().BeNull("no request should have been issued at all");
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
