using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Verbara.Sdk.Audio;
using Verbara.Sdk.TestInfrastructure.Http;
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
/// down this file has migrated (§4.6), this one has not. Fidelity here comes from recorded frames (D4).
/// </summary>
public class LmntSpeechSynthesizerWsTests : IAsyncDisposable
{
    private readonly LmntWsFakeServer _server;

    /// <summary>
    /// Ceiling on the wait for the client's first frame. Generous on purpose: if it expires the
    /// protocol assumption is wrong — the synthesizer never sent anything — rather than the runner
    /// having been slow. No assertion depends on its length.
    /// </summary>
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(10);

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

    /// <summary>
    /// The precedence <c>streaming-session-lifecycle</c> states: a requested cancellation outranks
    /// the empty-input shortcut. This surface already answered correctly before the rule was
    /// written down, its token check sitting ahead of the blank-text branch, — the assertion is here so the next synthesizer added to this
    /// package inherits it rather than the convention.
    /// </summary>
    /// <remarks>
    /// The token goes to the subject and <see cref="CancellationToken.None"/> to the enumerator, so
    /// the exception asserted came out of <c>SynthesizeAsync</c> and not out of the consumer
    /// standing in for it (ADR-0052 F3).
    /// </remarks>
    [Fact]
    public async Task SynthesizeAsync_ShouldThrowOperationCanceled_WhenTextIsWhitespaceAndTokenAlreadyCancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var synth = BuildSynthesizer();

        var act = async () => await synth
            .SynthesizeAsync(" \t ", AudioFormat.Slin16Mono16kHz, cts.Token)
            .ToListAsync(CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _server.ReceivedJsonMessages.Should().BeEmpty("the cancellation is observed before any session is opened");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldAbort_WhenCancelled()
    {
        // Strategy: server holds the socket open indefinitely (no frames, no finish, no close), and
        // the token fires once the fake has recorded the client's first frame — so ReadAllAsync(ct)
        // is demonstrably blocked inside the channel reader when it does.
        //
        // The wait is on the fake's own protocol signal, not on a clock. This used to poll
        // ReceivedJsonMessages every 5 ms against a 5 s DateTime deadline, which asserts through a
        // timer the protocol does not guarantee; FirstMessageReceived is the frame itself.
        //
        // HoldOpenUntilDisposed is set because a cancellation test must observe a socket that never
        // completes on its own. Do NOT read that as evidence the flag is doing the work: swapping it
        // for `await receiveTask` — the Class B trap — was measured 10/10 green here, because this
        // synthesizer no longer half-closes after EOF and so nothing ends the receive loop either
        // way. See LmntWsFakeServer.HoldOpenUntilDisposed's remarks. What the assertion below *can*
        // state is the condition at the moment of the cancel: the socket was live.
        _server.AudioFramesToSend.Clear();
        _server.HoldOpenUntilDisposed = true;

        using var cts = new CancellationTokenSource();
        var synth = BuildSynthesizer();

        // Fire-and-forget: cancel the moment the server records the client's first frame. The state
        // is captured before the cancel and asserted after it, so a surprising state fails the test
        // instead of hanging it by skipping the cancel.
        var cancelTrigger = Task.Run(async () =>
        {
            await _server.FirstMessageReceived.WaitAsync(FirstFrameTimeout).ConfigureAwait(false);
            var stateAtCancel = _server.SocketState;
            await cts.CancelAsync().ConfigureAwait(false);
            return stateAtCancel;
        });

        var act = async () => await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono16kHz, cts.Token)
            // ADR-0052 F3: the consumer holds no token. Passing the cancelled one to ToListAsync
            // makes the enumerator throw on our behalf, and the assertion then cannot tell a
            // propagated throw from a silent `yield break` in the subject.
            .ToListAsync(CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();

        // Surfaces a helper-side timeout, and names what actually held at the cancel.
        (await cancelTrigger).Should().Be(
            WebSocketState.Open,
            "cancellation has to be observed on a live socket, or the stream ended on the server's "
            + "close instead and the test would be crediting cancellation with someone else's work");
    }

    /// <summary>
    /// Cancellation observed while the server still has audio to give — the second half of this
    /// surface's cancellation coverage, not a replacement for the first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sibling above is the only pre-existing test in the whole suite that cancels on a live
    /// socket, and it stays: it holds the session open with <em>no</em> frames at all and fires
    /// once the fake has recorded the client's first message, so what it witnesses is a caller
    /// blocked on a silent provider. This one is the other condition — the provider is mid-answer
    /// and the caller has already been handed audio — which is the shape §3.3 of
    /// <c>voiceai-midstream-cancellation-coverage</c> prescribes and the one the sweep found
    /// nowhere on a WebSocket surface. Neither subsumes the other.
    /// </para>
    /// <para>
    /// It also needs none of the sibling's machinery: no <c>HoldOpenUntilDisposed</c>, no
    /// <c>FirstMessageReceived</c> wait, no fire-and-forget trigger task. The gate is the hold, and
    /// the caller's own first iteration is the trigger. The gate counts the fake's
    /// <em>outbound</em> messages; this surface sends no greeting, so a hold at 1 means the first
    /// binary audio frame was delivered and the second was not — the recorded audio chunks into
    /// six, so the <c>finish</c> terminator behind them is never reached.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SynthesizeAsync_ShouldAbort_WhenCancelledMidDelivery()
    {
        var gate = new OutboundFrameGate(1);
        _server.OutboundGate = gate;

        using var cts = new CancellationTokenSource();
        var synth = BuildSynthesizer();

        WebSocketState? stateAtCancel = null;
        var observed = 0;

        var act = async () =>
        {
            // ADR-0052 F3: the token goes to SynthesizeAsync and nowhere else. The consumer holds
            // none, so a throw here is the synthesizer's own.
            await foreach (var chunk in synth.SynthesizeAsync("test", AudioFormat.Slin16Mono16kHz, cts.Token))
            {
                observed++;
                stateAtCancel = _server.SocketState;
                await cts.CancelAsync();
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();

        observed.Should().Be(1, "the cancel must land after audio reached the caller, not before");
        stateAtCancel.Should().Be(
            WebSocketState.Open,
            "cancellation has to be observed on a live socket, or the stream ended on the server's "
            + "close instead and the test would be crediting cancellation with someone else's work");
        gate.Held.IsCompleted.Should().BeTrue("the fake must still have had a frame to send");
        gate.Delivered.Should().Be(1, "exactly the first audio frame was delivered");
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

/// <summary>
/// Transport: HTTP — migrated to the shared WireMock substrate (ADR-0041 D1, §4.6), replacing the
/// <c>HttpListener</c> fake. Matching is strict, so a request sent to the wrong route or without the
/// vendor's <c>X-API-Key</c> header fails to match rather than receiving the canned body.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The fixture is a pair, and only one half is the vendor's.</strong> LMNT is
/// <c>not-cleared</c> for committing Output (<c>docs/guides/provider-recording-protocol.md</c> §7),
/// so what was captured on 2026-08-17 is the response <em>envelope</em> —
/// <c>lmnt-http/synthesize-short-en-us.json</c>: real status, real header names, the vendor's own
/// declared media type, real content length and real read boundaries, with the audio counted and
/// discarded rather than written. The body served under it is
/// <c>lmnt-http/body-pcm-s16le-16khz.raw</c>, built locally by <see cref="SyntheticPcm"/>. The stub
/// takes its status and media type from the envelope, so the client meets the declaration it meets in
/// production; what this pair cannot prove is anything about the content of LMNT's speech.
/// </para>
/// <para>
/// The migration was blocked until the route fix landed (<c>Sdk/ADR-0048</c>): until 2026-08-15 this
/// client posted to <c>/v1/ai/speech/generate</c>, which the live API answers
/// <c>404 {"detail":"Not Found"}</c>, so a fixture pinning the client's request and a working
/// response at once was a contradiction — which strict matching surfaces instead of hiding.
/// </para>
/// </remarks>
public class LmntSpeechSynthesizerHttpTests
{
    private const string ApiKey = "test-lmnt-key";

    /// <summary>The only route the live API serves — see <c>LmntSpeechSynthesizer.HttpRoute</c>.</summary>
    private const string SynthesisPath = "/v1/ai/speech/bytes";

    /// <summary>The route this client posted to until 2026-08-15, which the vendor answers 404.</summary>
    private const string RetiredPath = "/v1/ai/speech/generate";

    /// <summary>The recorded response envelope — see its provenance sidecar.</summary>
    private const string RecordedEnvelope = "lmnt-http/synthesize-short-en-us.json";

    /// <summary>The locally built body the envelope describes the shape of — its sidecar too.</summary>
    private const string SyntheticBody = "lmnt-http/body-pcm-s16le-16khz.raw";

    /// <summary><c>LmntSpeechSynthesizer.HttpChunkSize</c> — the client's own read buffer.</summary>
    private const int HttpChunkSize = 8192;

    // Generator parameters for SyntheticBody, mirrored in its provenance sidecar. The regeneration
    // fence test re-renders the file from exactly these three numbers.
    private const int BodySampleCount = 10001;
    private const int BodyPeriodSamples = 64;
    private const short BodyAmplitude = 11000;
    private const int BodyLength = BodySampleCount * 2;

    private static readonly ProviderRecordings Recordings = ProviderRecordings.Locate();
    private static readonly RecordedResponseEnvelope Recorded = ReadEnvelope();

    /// <summary>The status, media type, length and read boundaries observed on the live response.</summary>
    private sealed record RecordedResponseEnvelope(
        HttpStatusCode Status,
        string MediaType,
        int ContentLength,
        IReadOnlyList<int> ChunkSizes);

    private static RecordedResponseEnvelope ReadEnvelope()
    {
        using var document = JsonDocument.Parse(Recordings.ReadText(RecordedEnvelope));
        var root = document.RootElement;
        return new RecordedResponseEnvelope(
            (HttpStatusCode)root.GetProperty("status").GetInt32(),
            root.GetProperty("media_type").GetString()!,
            root.GetProperty("content_length").GetInt32(),
            [.. root.GetProperty("chunk_sizes").EnumerateArray().Select(e => e.GetInt32())]);
    }

    private static byte[] Body => Recordings.ReadBytes(SyntheticBody);

    private static HttpProviderRequest SynthesisRequest(string path = SynthesisPath) =>
        HttpProviderRequest.Post(path)
            .WithHeader("X-API-Key", ApiKey)
            .WithHeader("lmnt-version", "1.0");

    /// <summary>The stub every success-path test uses: the recorded envelope over the local body.</summary>
    private static void StubRecordedEnvelope(HttpProviderMockServer server, byte[]? body = null) =>
        server.Stub(
            SynthesisRequest(),
            HttpProviderResponse.Bytes(body ?? Body, Recorded.MediaType, Recorded.Status));

    private static LmntSpeechSynthesizer SynthesizerFor(
        HttpProviderMockServer server,
        Action<LmntTtsOptions>? configure = null,
        string? originSuffix = null)
    {
        var opts = new LmntTtsOptions
        {
            ApiKey = ApiKey,
            Voice = LmntVoices.Leah,
            Transport = LmntTransport.Http,
        };
        // Format is deliberately left at its default so the suite exercises what ships.
        configure?.Invoke(opts);
        var origin = server.BaseAddress.ToString().TrimEnd('/') + originSuffix;
        return new LmntSpeechSynthesizer(Options.Create(opts), server.CreateClient(), origin);
    }

    [Fact]
    public async Task SynthesizeAsync_Http_ShouldSendApiKeyHeader()
    {
        await using var server = HttpProviderMockServer.Start();
        StubRecordedEnvelope(server);
        var synth = SynthesizerFor(server);

        await synth.SynthesizeAsync("hello http", AudioFormat.Slin16Mono16kHz).ToListAsync();

        server.ReceivedRequests.Should().ContainSingle().Subject
            .Header("X-API-Key").Should().Be(ApiKey);
    }

    [Fact]
    public async Task SynthesizeAsync_Http_ShouldThrowOperationCanceled_WhenCancelledMidStream()
    {
        // The existing `SynthesizeAsync_ShouldAbort_WhenCancelled` runs on
        // `LmntTransport.WebSocket`, so it never enters `SynthesizeHttpAsync` — the HTTP read loop
        // was the one carrying `ADR-0050 E6`'s `yield break` and had no cancellation coverage at
        // all. Transport is an option, not an implementation detail: a contract declared on
        // `SynthesizeAsync` has to hold on both.
        //
        // The synthetic body is 20 002 B against an 8 192 B read buffer, so cancelling after the
        // first chunk leaves two further reads to abort (ADR-0052 F1).
        await using var server = HttpProviderMockServer.Start();
        StubRecordedEnvelope(server);
        var synth = SynthesizerFor(server);
        using var cts = new CancellationTokenSource();

        // ADR-0052 F3: the token reaches the subject only, so the throw asserted below can only
        // have come from the synthesizer.
        var act = async () =>
        {
            await foreach (var chunk in synth.SynthesizeAsync(
                "hello http", AudioFormat.Slin16Mono16kHz, cts.Token))
            {
                chunk.Length.Should().BeGreaterThan(0);
                await cts.CancelAsync();
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SynthesizeAsync_Http_ShouldPostToBytesRoute_NotGenerate()
    {
        // Regression guard for the shipped defect. Every HTTP synthesis this client ever made went
        // to /v1/ai/speech/generate, which the live API answers 404 {"detail":"Not Found"} —
        // byte-identically to a path that does not exist. The retired fake answered 200 on any path,
        // so the suite certified a route that had never once worked.
        await using var server = HttpProviderMockServer.Start();
        StubRecordedEnvelope(server);
        var synth = SynthesizerFor(server);

        await synth.SynthesizeAsync("hello http", AudioFormat.Slin16Mono16kHz).ToListAsync();

        var request = server.ReceivedRequests.Should().ContainSingle().Subject;
        request.Method.Should().Be("POST");
        request.Path.Should().Be(SynthesisPath);
        server.UnmatchedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task SynthesizeAsync_Http_ShouldNotMatch_WhenTheOnlyRouteOfferedIsTheRetiredOne()
    {
        // The inverse, so the guard above can actually fail: the substrate offers ONLY the dead route
        // and nothing else. A client that reverted to it would match and this test would go red.
        await using var server = HttpProviderMockServer.Start();
        server.Stub(
            SynthesisRequest(RetiredPath),
            HttpProviderResponse.Bytes(Body, Recorded.MediaType, Recorded.Status));
        var synth = SynthesizerFor(server);

        var act = async () => await synth
            .SynthesizeAsync("hello", AudioFormat.Slin16Mono16kHz)
            .ToListAsync();

        await act.Should().ThrowAsync<HttpRequestException>();
        server.UnmatchedRequests.Should().ContainSingle().Subject.Path.Should().Be(SynthesisPath);
    }

    [Fact]
    public async Task SynthesizeAsync_Http_ShouldThrow_WhenClientPostsToAnUnknownRoute()
    {
        // Point the client at an origin carrying a path and the substrate refuses it exactly as the
        // live API does. Without this, a substrate that silently stopped matching would make the
        // route assertions vacuous again.
        await using var server = HttpProviderMockServer.Start();
        StubRecordedEnvelope(server);
        var synth = SynthesizerFor(server, originSuffix: "/wrong-prefix");

        var act = async () => await synth
            .SynthesizeAsync("hello", AudioFormat.Slin16Mono16kHz)
            .ToListAsync();

        await act.Should().ThrowAsync<HttpRequestException>();
        server.UnmatchedRequests.Should().ContainSingle().Subject
            .Path.Should().Be("/wrong-prefix" + SynthesisPath);
    }

    [Fact]
    public async Task SynthesizeAsync_Http_ShouldSendLmntVersionHeader()
    {
        await using var server = HttpProviderMockServer.Start();
        StubRecordedEnvelope(server);
        var synth = SynthesizerFor(server);

        await synth.SynthesizeAsync("hello", AudioFormat.Slin16Mono16kHz).ToListAsync();

        server.ReceivedRequests.Should().ContainSingle().Subject
            .Header("lmnt-version").Should().Be("1.0");
    }

    [Fact]
    public async Task SynthesizeAsync_Http_ShouldIncludeVoiceAndTextInBody()
    {
        await using var server = HttpProviderMockServer.Start();
        StubRecordedEnvelope(server);
        var synth = SynthesizerFor(server);

        await synth.SynthesizeAsync("form body test", AudioFormat.Slin16Mono16kHz).ToListAsync();

        var request = server.ReceivedRequests.Should().ContainSingle().Subject;
        request.Header("Content-Type").Should().StartWith("application/x-www-form-urlencoded");
        request.BodyAsString.Should().NotBeNullOrEmpty();
        request.BodyAsString!.Should().Contain("voice=");
        request.BodyAsString.Should().Contain("text=");
    }

    [Fact]
    public async Task SynthesizeAsync_Http_ShouldYieldTheBodyIntact_WhenChunked()
    {
        // The point of the fixture pair: a body whose length is NOT a multiple of the client's own
        // 8 KiB read buffer traverses the read loop, so a partial final chunk reaches the consumer.
        // The 10 000-byte counting pattern this replaced was closer to arithmetic than to audio, and
        // frame boundaries are still not asserted — the client yields what Stream.ReadAsync returned.
        await using var server = HttpProviderMockServer.Start();
        StubRecordedEnvelope(server);
        var expected = Body;
        var synth = SynthesizerFor(server);

        var chunks = await synth.SynthesizeAsync("large", AudioFormat.Slin16Mono16kHz).ToListAsync();

        (expected.Length % HttpChunkSize).Should().NotBe(0, "the body must not be buffer-aligned");
        chunks.Should().HaveCountGreaterThan(1, "the response must actually be chunked");
        chunks.Should().OnlyContain(c => c.Length > 0 && c.Length <= HttpChunkSize);
        chunks.Should().Contain(c => c.Length != HttpChunkSize, "a partial chunk must reach the consumer");
        chunks.SelectMany(c => c.ToArray()).Should().Equal(expected,
            "streaming must not alter a single byte of the body");
    }

    [Fact]
    public async Task SynthesizeAsync_Http_ShouldNotMatch_WhenApiKeyHeaderIsWrong()
    {
        // Strict matching (ADR-0041 D1) turns a silent pass into a failure: the retired fake read the
        // X-API-Key header but answered 200 whatever it said.
        await using var server = HttpProviderMockServer.Start();
        StubRecordedEnvelope(server);
        var synth = SynthesizerFor(server, o => o.ApiKey = "wrong-key");

        var act = async () => await synth
            .SynthesizeAsync("hello", AudioFormat.Slin16Mono16kHz)
            .ToListAsync();

        await act.Should().ThrowAsync<HttpRequestException>();
        server.UnmatchedRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task SynthesizeAsync_Http_ShouldThrow_WhenResponseIsErrorStatus()
    {
        await using var server = HttpProviderMockServer.Start();
        server.Stub(SynthesisRequest(), HttpProviderResponse.Status(HttpStatusCode.Unauthorized));
        var synth = SynthesizerFor(server);

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
        await using var server = HttpProviderMockServer.Start();
        StubRecordedEnvelope(server, body: []);
        var synth = SynthesizerFor(server);

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
    /// failure. Asserted through the substrate seeing no request at all — matched or otherwise.
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_Http_ShouldYieldNothingWithoutRequesting_WhenTextIsWhitespace()
    {
        await using var server = HttpProviderMockServer.Start();
        StubRecordedEnvelope(server);
        var synth = SynthesizerFor(server);

        var chunks = await synth.SynthesizeAsync("   ", AudioFormat.Slin16Mono16kHz).ToListAsync();

        chunks.Should().BeEmpty();
        server.ReceivedRequests.Should().BeEmpty("no request should have been issued at all");
    }

    /// <summary>
    /// The precedence rule on the other transport. The guard sits ahead of the transport split, so
    /// this path owes the same promise as the WebSocket one and is asserted separately rather than
    /// inferred from it — a shared guard is a reason to expect the same answer, not evidence of it.
    /// </summary>
    /// <remarks>
    /// The stub is left armed: if the observation were placed below the request, the stub would
    /// match and return audio, so the test would fail on the request assertion rather than pass on
    /// an unmatched request. The token goes to the subject, <see cref="CancellationToken.None"/> to
    /// the enumerator (ADR-0052 F3).
    /// </remarks>
    [Fact]
    public async Task SynthesizeAsync_Http_ShouldThrowOperationCanceled_WhenTextIsWhitespaceAndTokenAlreadyCancelled()
    {
        await using var server = HttpProviderMockServer.Start();
        StubRecordedEnvelope(server);
        var synth = SynthesizerFor(server);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await synth
            .SynthesizeAsync("   ", AudioFormat.Slin16Mono16kHz, cts.Token)
            .ToListAsync(CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        server.ReceivedRequests.Should().BeEmpty("the cancellation is observed before any request is issued");
    }

    [Fact]
    public void RecordedEnvelope_ShouldDescribeTheResponseTheStubServes_WhenReadFromRecordingsTree()
    {
        // Fixture-integrity fence, and the reason the envelope is load-bearing rather than decorative:
        // every success-path stub above takes its status and media type from this file, so if the
        // capture were re-saved with different values the stubs would change with it. These are the
        // numbers its sidecar records.
        Recorded.Status.Should().Be(HttpStatusCode.OK);
        Recorded.MediaType.Should().Be("application/vnd.lmnt.audio-int16",
            "the vendor declares its own type for int16 PCM, not audio/raw or audio/L16");
        Recorded.ContentLength.Should().Be(Recorded.ChunkSizes.Sum(),
            "the length must be the reads that produced it, or one of the two was edited by hand");
        Recorded.ChunkSizes.Should().HaveCountGreaterThan(1, "a real audio response arrives in pieces");
        Recorded.ChunkSizes.Max().Should().Be(HttpChunkSize,
            "the boundaries are the client's own 8 KiB reads — that is what makes them comparable");
        Recorded.ChunkSizes[^1].Should().BeLessThan(HttpChunkSize,
            "the live response was not buffer-aligned either");
    }

    [Fact]
    public void SyntheticBody_ShouldMatchItsDocumentedGeneratorParameters_WhenRegeneratedLocally()
    {
        // The "commit a small generator" half of the source-audio rule (protocol guide §6): the
        // committed bytes are reproducible from three numbers in the sidecar, not magic. They stand in
        // for LMNT's audio precisely because they are not LMNT's audio — §7 rates the surface
        // not-cleared, so the body half of this pair is the repository's own.
        var body = Body;

        body.Should().HaveCount(BodyLength, "the sidecar records this exact length");
        (body.Length % HttpChunkSize).Should().NotBe(0);
        body.Should().Equal(SyntheticPcm.Triangle(BodySampleCount, BodyPeriodSamples, BodyAmplitude));
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
