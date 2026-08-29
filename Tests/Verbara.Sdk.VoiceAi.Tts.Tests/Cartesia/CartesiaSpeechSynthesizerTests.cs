using System.Net.WebSockets;
using System.Text.Json;
using Verbara.Sdk.Audio;
using Verbara.Sdk.TestInfrastructure.WebSocket;
using Verbara.Sdk.VoiceAi.Tts.Cartesia;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tts.Tests.Cartesia;

/// <summary>
/// Transport: WebSocket. Deliberately NOT migrated to the WireMock substrate — WireMock.NET matches
/// HTTP/1.1 requests and cannot hold the duplex session these tests drive (ADR-0041 D2), so
/// <c>CartesiaFakeServer</c> on <c>WebSocketTestServer</c> stays. Fidelity here comes from recorded
/// frames (D4), not from a different server.
/// </summary>
public class CartesiaSpeechSynthesizerTests : IAsyncDisposable
{
    private readonly CartesiaFakeServer _server;

    public CartesiaSpeechSynthesizerTests()
    {
        _server = new CartesiaFakeServer();
        _server.Start();
    }

    /// <summary>The credential every test in this class sends, asserted on by the fake.</summary>
    private const string TestApiKey = "test-key";

    /// <summary>
    /// Built through <see cref="CartesiaOptions.BaseUri"/> rather than through a test-only
    /// constructor, so the route, the query and the credential all come from shipped code. The path
    /// here is the one the production default carries — <c>/tts/websocket</c>.
    /// </summary>
    private CartesiaSpeechSynthesizer BuildSynthesizer()
        => new(Options.Create(new CartesiaOptions
        {
            ApiKey = TestApiKey,
            VoiceId = "test-voice",
            BaseUri = $"ws://127.0.0.1:{_server.Port}/tts/websocket"
        }));

    /// <summary>The audio the fake replays, read from the same tree the fake reads.</summary>
    private static byte[] RecordedAudio => CartesiaFakeServer.ReadFrameBytes(CartesiaFakeServer.AudioChunk);

    [Fact]
    public async Task SynthesizeAsync_ShouldSendRequestJson_WithModelAndVoice()
    {
        var synth = BuildSynthesizer();
        await synth.SynthesizeAsync("hola mundo", AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.ReceivedJsonMessages.Should().NotBeEmpty();
        var request = _server.ReceivedJsonMessages[0];
        request.Should().Contain("\"model_id\":\"sonic-3\"");
        request.Should().Contain("\"id\":\"test-voice\"");
        request.Should().Contain("\"transcript\":\"hola mundo\"");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldSendANonEmptyContextId_WhenTheEndpointRequiresOne()
    {
        // Not cosmetic. The shipped request omitted context_id entirely and the live endpoint
        // answered {"type":"error","status_code":400,"done":true,"error":"context_id is invalid: …"}
        // with zero audio — so this field is the difference between a synthesis and a silence.
        var synth = BuildSynthesizer();
        await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        using var request = JsonDocument.Parse(_server.ReceivedJsonMessages[0]);
        request.RootElement.TryGetProperty("context_id", out var contextId).Should().BeTrue();
        contextId.GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldSendADistinctContextId_PerRequest()
    {
        // The field exists to correlate the frames of one synthesis; a constant would defeat the
        // only thing it does, and no test on a single request could tell the two apart.
        var synth = BuildSynthesizer();
        await synth.SynthesizeAsync("uno", AudioFormat.Slin16Mono8kHz).ToListAsync();
        await synth.SynthesizeAsync("dos", AudioFormat.Slin16Mono8kHz).ToListAsync();

        var ids = _server.ReceivedJsonMessages
            .Select(m => JsonDocument.Parse(m).RootElement.GetProperty("context_id").GetString())
            .ToList();

        ids.Should().HaveCount(2);
        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldNotHalfCloseTheSocket_AfterTheRequest()
    {
        // Measured, not stylistic: the client used to CloseOutputAsync right after the request, and
        // the live endpoint read that frame as "abandon the synthesis" — 0 frames, 0 bytes, against
        // 7 chunks and 32 694 B for a control differing only in that step. The fake records the
        // close instead of reacting to it, so this asserts on what the client did.
        var synth = BuildSynthesizer();
        await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.ClientSentCloseFrame.Should().BeFalse(
            "a Close frame before the server answers costs the caller every byte of audio");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldYieldTheAudioInsideTheChunkFrames_WhenTheServerAnswersLikeTheVendor()
    {
        // The measured transport: base64 in `data` on a `chunk` text frame. A live run of the
        // corrected request received seven of these and ZERO binary bytes, so this — not the binary
        // path below — is the case that decides whether callers hear anything.
        //
        // The point of the recording: a real waveform of a length that is NOT chunk-aligned
        // traverses the frame path, so a partial final frame reaches the consumer. Two 320-byte
        // arrays of zeros — exact multiples — could never produce one.
        //
        // Note what is NOT asserted: that the final frame is exactly `length % 320`. Frame count
        // and boundaries are the transport's business, not the client's contract; what the client
        // owes is every byte, in order, with nothing invented and nothing dropped.
        var expected = RecordedAudio;
        var synth = BuildSynthesizer();

        var frames = await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        (expected.Length % CartesiaFakeServer.AudioFrameSize).Should()
            .NotBe(0, "the recording must not be chunk-aligned");
        frames.Should().HaveCountGreaterThan(1, "the recording must actually be chunked");
        frames.Should().OnlyContain(f => f.Length > 0 && f.Length <= CartesiaFakeServer.AudioFrameSize);
        frames.Should().Contain(f => f.Length != CartesiaFakeServer.AudioFrameSize,
            "a partial frame must reach the consumer");
        frames.SelectMany(f => f.ToArray()).Should().Equal(expected,
            "streaming must not alter a single byte of the recorded audio");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldNotLeakJsonBytesIntoTheAudio_WhenDecodingAChunkFrame()
    {
        // The failure this pins: a receive loop that hands the raw text frame straight through as
        // audio. Every assertion above would still pass on byte count alone if the client yielded
        // the JSON envelope, so this one checks the audio is PCM and not UTF-8 JSON.
        var synth = BuildSynthesizer();

        var frames = await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        var all = frames.SelectMany(f => f.ToArray()).ToArray();
        all.Should().NotContain((byte)'{');
        all.Should().Equal(RecordedAudio);
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldAssembleTheWholeMessage_WhenAChunkFrameArrivesFragmented()
    {
        // The vendor sizes these frames, not this client. A loop that parsed each read as a whole
        // message would hand JSON a truncated document once a frame outgrew the receive buffer —
        // length-dependent, and therefore invisible to every short fixture in this suite.
        _server.TextFrameFragmentBytes = 16;
        var synth = BuildSynthesizer();

        var frames = await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        frames.SelectMany(f => f.ToArray()).Should().Equal(RecordedAudio);
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldYieldTheAudio_WhenTheChunkFrameAlsoCarriesUnmodelledFields()
    {
        // The recorded frame, replayed verbatim: `flush_id`, `step_time` and the echoed `context_id`
        // are on it, and the client models none of them. Tolerating an unmapped sibling is the
        // contract; throwing on one would break against a vendor that only ever adds fields.
        _server.SendRecordedChunkFrame = true;
        var synth = BuildSynthesizer();

        var frames = await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        frames.SelectMany(f => f.ToArray()).Should()
            .Equal(RecordedAudio.Take(CartesiaFakeServer.AudioFrameSize),
                "the fixture carries the first 320 bytes of the sibling tone");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldYieldBinaryAudioFrames_WhenTheServerSendsThem()
    {
        // Tolerated without evidence, deliberately: the live run measured zero binary bytes, but a
        // vendor not sending a mode on one day is not evidence the mode does not exist. This test
        // exists so the branch is not dead code — it is NOT evidence Cartesia sends binary.
        _server.Transport = CartesiaAudioTransport.Binary;
        var synth = BuildSynthesizer();

        var frames = await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        frames.SelectMany(f => f.ToArray()).Should().Equal(RecordedAudio);
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldTerminate_WhenServerSendsDone()
    {
        // The fake sends the recorded audio as binary frames, then the recorded `done` frame.
        // The synthesizer must stop iterating as soon as "done" arrives — with every audio byte
        // delivered and nothing after it.
        _server.SendDoneTerminator = true;
        var synth = BuildSynthesizer();

        var frames = await synth.SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz).ToListAsync();

        frames.SelectMany(f => f.ToArray()).Should().Equal(RecordedAudio);
    }

    [Fact]
    public void RecordedFixtures_ShouldCarryDocumentedFieldsAndExactByteLength_WhenReadFromRecordingsTree()
    {
        // Fixture-integrity fence. The fake is only as good as what is on disk: trim a documented
        // field, swap the audio or re-save the JSON and the suite would keep passing while quietly
        // testing something smaller. This fails here, next to the sidecar that explains the file,
        // instead of surfacing three tests away as a puzzling byte mismatch.
        var audio = RecordedAudio;
        audio.Should().HaveCount(2008, "the sidecar records this exact length");
        (audio.Length % CartesiaFakeServer.AudioFrameSize).Should().NotBe(0);

        using var done = JsonDocument.Parse(CartesiaFakeServer.ReadFrame(CartesiaFakeServer.DoneFrame));
        var root = done.RootElement;
        root.GetProperty("type").GetString().Should().Be("done");

        // The other three documented fields. The client reads none of them, so they reach the
        // parser as unread or unmodelled siblings — which is the whole point of recording the full
        // frame instead of {"type":"done"}.
        root.GetProperty("done").GetBoolean().Should().BeTrue();
        root.GetProperty("status_code").ValueKind.Should().Be(JsonValueKind.Number);
        root.GetProperty("context_id").GetString().Should().Be("00000000-0000-0000-0000-000000000000",
            "a correlating identifier is placeholdered, never real (protocol guide §4)");

        // The chunk frame: the seven keys the live probe measured, no more and no fewer. A key set
        // is what that run established — the values on it are our own fiction, so only the names
        // are asserted here.
        using var chunk = JsonDocument.Parse(CartesiaFakeServer.ReadFrame(CartesiaFakeServer.ChunkFrame));
        chunk.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            ["context_id", "data", "done", "flush_id", "status_code", "step_time", "type"]);
        chunk.RootElement.GetProperty("type").GetString().Should().Be("chunk");
        chunk.RootElement.GetProperty("context_id").GetString().Should()
            .Be("00000000-0000-0000-0000-000000000000");

        // And its audio is the sibling tone, not an independent blob that could drift from it.
        Convert.FromBase64String(chunk.RootElement.GetProperty("data").GetString()!).Should()
            .Equal(audio.Take(CartesiaFakeServer.AudioFrameSize));
    }

    [Fact]
    public void RecordedFixtures_ShouldMatchTheirDocumentedGeneratorParameters_WhenRegeneratedLocally()
    {
        // The "commit a small generator" half of the source-audio rule (protocol guide §6): the
        // committed bytes are reproducible from three numbers in the sidecar, not magic. If this
        // fails, either the file was edited or SyntheticPcm changed — both need a sidecar update.
        var regenerated = SyntheticPcm.Triangle(
            CartesiaFakeServer.AudioSampleCount,
            CartesiaFakeServer.AudioPeriodSamples,
            CartesiaFakeServer.AudioAmplitude);

        regenerated.Should().Equal(RecordedAudio);
    }

    /// <summary>
    /// Door 3 (ADR-0050 E2c), and this test asserted the opposite until then: it required the client
    /// to <em>complete normally</em> when the socket died mid-session, which is exactly the silent
    /// truncation the ADR removes. A caller cannot tell 12 KB of a 30 KB utterance from the whole of
    /// a short one, so "the audio ended" has to mean the audio ended.
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_ShouldThrowTransportFailure_WhenServerAbortsMidSession()
    {
        _server.AbortAfterSend = true;
        var synth = BuildSynthesizer();

        var act = async () => await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz)
            .ToListAsync();

        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.Transport);
        failure.Provider.Should().Be("Cartesia");

        // No code, because nothing was said — the evidence is the inner transport exception, which is
        // never discarded.
        failure.Code.Should().BeNull();
        failure.InnerException.Should().BeOfType<WebSocketException>();
    }

    /// <summary>
    /// The fourth door, and the one no receive-loop test can reach: a session that never opens.
    /// <c>ADR-0050</c> E7 wraps the bare <see cref="WebSocketException"/> so the caller reads one type
    /// whether this vendor validates at the upgrade or in band. A refused connection carries no HTTP
    /// answer, hence no code; the answered-with-a-status branch is asserted on the factory itself
    /// (<c>SpeechProviderFailureExceptionTests</c>) because no fake in this suite can reject an upgrade
    /// yet. Built inline rather than through <c>BuildSynthesizer</c> because the point of this test is
    /// that it never reaches the fake.
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_ShouldThrowHandshakeFailure_WhenNothingAcceptsTheUpgrade()
    {
        var synth = new CartesiaSpeechSynthesizer(Options.Create(new CartesiaOptions
        {
            ApiKey = TestApiKey,
            VoiceId = "test-voice",
            BaseUri = $"ws://127.0.0.1:{ClosedPort.Reserve()}/tts/websocket"
        }));

        var act = async () => await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz)
            .ToListAsync();

        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.Handshake);
        failure.Provider.Should().Be("Cartesia");
        failure.Code.Should().BeNull("a refused connection produced no HTTP answer to report");
        failure.InnerException.Should().BeAssignableTo<WebSocketException>();
    }

    /// <summary>
    /// Door 1 (ADR-0049 D1). The frame is the vendor's own shape; the session closes
    /// <em>normally</em> after it, so the exception can only have come from the frame.
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_ShouldThrowErrorFrameFailure_WhenServerReportsAFailure()
    {
        _server.ErrorFrameJson =
            """{"type":"error","status_code":402,"error":"insufficient credits"}""";
        var synth = BuildSynthesizer();

        var act = async () => await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz)
            .ToListAsync();

        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.ErrorFrame);

        // The vendor's own code, verbatim and unparsed — what a retry policy reads (E4).
        failure.Code.Should().Be("402");
        failure.Message.Should().Contain("insufficient credits");
    }

    /// <summary>
    /// Door 2 (ADR-0050 E2b): the close code alone, with no error frame and no audio, is a failure.
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_ShouldThrowCloseCodeFailure_WhenServerClosesAbnormally()
    {
        _server.AudioFramesToSend.Clear();
        _server.SendDoneTerminator = false;
        _server.CloseStatus = WebSocketCloseStatus.PolicyViolation;   // 1008, as measured on this vendor
        _server.CloseStatusDescription = "Missing sample_rate";
        var synth = BuildSynthesizer();

        var act = async () => await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz)
            .ToListAsync();

        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.CloseCode);
        failure.Code.Should().Be("1008");
        failure.Message.Should().Contain("Missing sample_rate");
    }

    /// <summary>
    /// D2 on this surface (ADR-0050 E5): the session ended cleanly, produced no audio, and said
    /// nothing about why — the one case that is not a <see cref="SpeechProviderFailureException"/>
    /// because there is no code and no vendor message to carry.
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_ShouldThrowEmptyResult_WhenSessionEndsCleanlyWithNoAudio()
    {
        _server.AudioFramesToSend.Clear();
        var synth = BuildSynthesizer();

        var act = async () => await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz)
            .ToListAsync();

        var empty = (await act.Should().ThrowAsync<SpeechProviderEmptyResultException>()).Which;
        empty.Provider.Should().Be("Cartesia");

        // Not the failure type: a caller catching SpeechProviderFailureException must not catch this,
        // because "the vendor told us why" and "the vendor told us nothing" are different events.
        empty.Should().NotBeOfType<SpeechProviderFailureException>();
    }

    /// <summary>
    /// The other half of E5: text that carries no speech is not asked of the provider at all, so the
    /// zero audio that follows is not a failure. Asserted through the fake seeing no session rather
    /// than through the empty result, since "did not throw" alone would also pass if the client had
    /// opened a session and been lucky.
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_ShouldYieldNothingWithoutConnecting_WhenTextIsWhitespace()
    {
        var synth = BuildSynthesizer();

        var frames = await synth.SynthesizeAsync("   \t\n ", AudioFormat.Slin16Mono8kHz).ToListAsync();

        frames.Should().BeEmpty();
        _server.ReceivedApiKey.Should().BeNull("no session should have been opened at all");
        _server.ReceivedJsonMessages.Should().BeEmpty();
    }

    /// <summary>
    /// The precedence <c>streaming-session-lifecycle</c> states: a requested cancellation outranks
    /// the empty-input shortcut. This synthesizer takes the shortcut first, so a caller who has
    /// already cancelled receives an empty sequence — indistinguishable from "nothing to say", and
    /// the SDK offers no other signal that would let it tell the two apart.
    /// </summary>
    /// <remarks>
    /// The token is handed to the subject and <see cref="CancellationToken.None"/> to the
    /// enumerator, so an <see cref="OperationCanceledException"/> observed here came out of
    /// <c>SynthesizeAsync</c> rather than out of the consumer standing in for it (ADR-0052 F3).
    /// The fake is asserted silent for the same reason the whitespace test above asserts it:
    /// "it threw" alone would also pass if the client had opened a session and been lucky.
    /// </remarks>
    [Fact]
    public async Task SynthesizeAsync_ShouldThrowOperationCanceled_WhenTextIsWhitespaceAndTokenAlreadyCancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var synth = BuildSynthesizer();

        var act = async () => await synth
            .SynthesizeAsync("   \t\n ", AudioFormat.Slin16Mono8kHz, cts.Token)
            .ToListAsync(CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _server.ReceivedApiKey.Should().BeNull("the cancellation is observed before any session is opened");
        _server.ReceivedJsonMessages.Should().BeEmpty();
    }

    /// <summary>
    /// The eighth WebSocket surface's first cancellation test — this class had none, so a caller
    /// cancelling Cartesia synthesis was the one provider whose behaviour nothing here stated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The contract this asserts is the one the other seven assert: a token already cancelled when
    /// the caller starts enumerating throws <see cref="OperationCanceledException"/> and reaches no
    /// provider. What is worth writing down is that this synthesizer arrives there by a different
    /// route. The other three TTS synthesizers open with <c>ct.ThrowIfCancellationRequested()</c>
    /// (Deepgram, ElevenLabs, Lmnt); this one has no such guard, so the throw comes out of
    /// <c>ClientWebSocket.ConnectAsync</c> — measured 10/10 as <c>TaskCanceledException</c> raised
    /// from <c>CartesiaSpeechSynthesizer.SynthesizeAsync</c>'s connect line, carrying the linked
    /// <c>connectCts</c> token rather than the caller's. <c>TaskCanceledException</c> derives from
    /// <see cref="OperationCanceledException"/>, so the assertion below holds either way, and that
    /// is deliberate: it states the contract, not the mechanism, and would keep holding if an entry
    /// guard were added later.
    /// </para>
    /// <para>
    /// Asserted through the fake seeing nothing rather than through the throw alone — and through
    /// <em>both</em> of the fake's session-entry witnesses, not just the JSON. <c>ReceivedApiKey</c>
    /// is written the moment the upgrade completes, so a test that checked only
    /// <c>ReceivedJsonMessages</c> would still pass if the session had opened and merely sent no
    /// request. Six of the seven pre-existing cancellation tests assert only that weaker half.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SynthesizeAsync_ShouldAbort_WhenCancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var synth = BuildSynthesizer();

        var act = async () => await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz, cts.Token)
            // ADR-0052 F3: the consumer holds no token. Passing the cancelled one to ToListAsync
            // makes the enumerator throw on our behalf, and the assertion then cannot tell a
            // propagated throw from a silent `yield break` in the subject.
            .ToListAsync(CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();

        _server.ReceivedApiKey.Should().BeNull("no session should have been opened at all");
        _server.ReceivedJsonMessages.Should().BeEmpty();
    }

    /// <summary>
    /// The case this surface never had: cancellation observed while the server still has
    /// audio to give.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sibling above cancels before enumeration starts, so it exercises the connect path and
    /// no session is ever opened. This one cancels from inside the caller's own
    /// <c>await foreach</c>, one audio chunk in, with the fake parked mid-delivery — the shape
    /// §3.3 of <c>voiceai-midstream-cancellation-coverage</c> prescribes.
    /// </para>
    /// <para>
    /// Two conditions are asserted rather than assumed, because "it threw" alone would also pass
    /// if the session had ended on the server's own close and the test were crediting
    /// cancellation with someone else's work: the socket was live at the moment the token fired,
    /// and the gate was holding an undelivered frame. The gate counts the fake's <em>outbound</em>
    /// messages; this surface sends no greeting, so a hold at 1 means the first <c>chunk</c> frame
    /// was delivered and the second was not — the recorded audio chunks into seven of them, so
    /// there is always a remainder to hold.
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
            await foreach (var chunk in synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz, cts.Token))
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
        gate.Delivered.Should().Be(1, "exactly the first chunk frame was delivered");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldAuthenticateTheUpgrade_WhenOpeningASession()
    {
        var synth = BuildSynthesizer();
        await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        // The header name is asserted by asking for it by name: a client that sent the key under any
        // other field leaves this null, which is the defect the whole suite was blind to.
        _server.ReceivedApiKey.Should().Be(TestApiKey);
        _server.ReceivedApiVersion.Should().Be("2024-11-13");

        // And the route came from CartesiaOptions.BaseUri rather than from a branch, so this is the
        // path production asks for.
        _server.ReceivedRequestUri.Should().Be("/tts/websocket");
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _server.DisposeAsync();
    }
}
