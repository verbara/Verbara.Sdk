using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Verbara.Sdk.Audio;
using Verbara.Sdk.TestInfrastructure.WebSocket;
using Verbara.Sdk.VoiceAi.Stt.Deepgram;
using Verbara.Sdk.VoiceAi.Stt.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.Deepgram;

/// <summary>
/// Transport: WebSocket. Deliberately NOT migrated to the WireMock substrate — WireMock.NET matches
/// HTTP/1.1 requests and cannot hold the duplex session these tests drive (ADR-0041 D2), so
/// <c>DeepgramFakeServer</c> stays. Fidelity here comes from the frames in
/// <c>Recordings/deepgram-stt/</c> (D4), not from a different server. Deepgram is <c>not-cleared</c>
/// for capturing Output (<c>docs/guides/provider-recording-protocol.md</c> §7), so those frames take
/// that section's documentation-derived route — authored to Deepgram's published streaming schema
/// with fictional values, <c>class: "synthetic"</c>, <c>terms.verdict: "not-applicable"</c> — rather
/// than being captured. That closes the field-set half of the D4 gap and not the drift half.
/// </summary>
public class DeepgramSpeechRecognizerTests : IAsyncDisposable
{
    private readonly DeepgramFakeServer _server;

    public DeepgramSpeechRecognizerTests()
    {
        _server = new DeepgramFakeServer();
        _server.Start();
    }

    /// <summary>The credential every test in this class sends, asserted on by the fake.</summary>
    private const string TestApiKey = "test-key";

    /// <summary>
    /// Reaches the fake through <see cref="DeepgramOptions.BaseUri"/>, so route, query and credential
    /// all come from shipped code — including the <c>model</c> and <c>language</c> parameters the
    /// test-only branch used to omit.
    /// </summary>
    private DeepgramSpeechRecognizer BuildRecognizer(Action<DeepgramOptions>? configure = null)
    {
        var opts = new DeepgramOptions
        {
            ApiKey = TestApiKey,
            BaseUri = $"ws://127.0.0.1:{_server.Port}/v1/listen"
        };
        configure?.Invoke(opts);
        return new DeepgramSpeechRecognizer(Options.Create(opts));
    }

    /// <summary>
    /// The transcript a recorded frame actually carries, read with <see cref="JsonDocument"/> rather
    /// than hard-coded. Two independent readers — this one and the SDK's source-generated parser —
    /// must agree on the frame's bytes; hard-coding the sentence would instead assert what the frame
    /// was expected to say, and would have to be edited whenever it is re-authored.
    /// </summary>
    private static string RecordedTranscript(string frame)
    {
        using var document = JsonDocument.Parse(DeepgramFakeServer.ReadFrame(frame));
        return document.RootElement
            .GetProperty("channel").GetProperty("alternatives")[0]
            .GetProperty("transcript").GetString()!;
    }

    [Fact]
    public async Task StreamAsync_ShouldYieldRecordedTranscripts_WhenReplayingDocumentedFrames()
    {
        // The default seed is the two recorded frames verbatim: interim then final.
        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        var interim = RecordedTranscript(DeepgramFakeServer.InterimResultsFrame);
        var final = RecordedTranscript(DeepgramFakeServer.FinalResultsFrame);
        interim.Should().NotBeNullOrWhiteSpace("a frame that transcribes to nothing asserts nothing");
        final.Should().NotBeNullOrWhiteSpace("a frame that transcribes to nothing asserts nothing");

        results.Should().HaveCount(2);
        results[0].IsFinal.Should().BeFalse();
        results[0].Transcript.Should().Be(interim);
        results[1].IsFinal.Should().BeTrue();
        results[1].Transcript.Should().Be(final);
    }

    [Fact]
    public async Task StreamAsync_ShouldTolerateUnmodelledSiblingFields_WhenFrameCarriesFullDocumentedFieldSet()
    {
        // The point of the recording. DeepgramResultMessage models four values — type, is_final,
        // and each alternative's transcript and confidence — out of everything Deepgram documents.
        // The first block fences the fixture: reduce it back to a five-field object and this fails
        // loudly instead of silently taking the assertion below with it.
        using (var document = JsonDocument.Parse(DeepgramFakeServer.ReadFrame(DeepgramFakeServer.FinalResultsFrame)))
        {
            var root = document.RootElement;
            foreach (var unmodelled in new[]
                     { "channel_index", "duration", "start", "speech_final", "metadata", "from_finalize", "entities" })
            {
                root.TryGetProperty(unmodelled, out _)
                    .Should().BeTrue("the recorded frame must carry '{0}', which the SDK does not model", unmodelled);
            }

            var alternative = root.GetProperty("channel").GetProperty("alternatives")[0];
            alternative.TryGetProperty("words", out var words).Should().BeTrue();
            words.GetArrayLength().Should().BeGreaterThan(0, "word-level detail is unmodelled sibling data too");
            alternative.TryGetProperty("languages", out _).Should().BeTrue();
        }

        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(DeepgramFakeServer.ReadFrame(DeepgramFakeServer.FinalResultsFrame));

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().ContainSingle()
            .Which.Transcript.Should().Be(RecordedTranscript(DeepgramFakeServer.FinalResultsFrame));
    }

    [Fact]
    public async Task StreamAsync_ShouldIgnoreFrame_WhenTypeIsNotResults()
    {
        // Deepgram interleaves control frames with transcripts; the parser filters on
        // type != "Results". Nothing asserted that until a recorded Metadata frame existed to send.
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(DeepgramFakeServer.ReadFrame(DeepgramFakeServer.MetadataFrame));
        _server.ResultMessages.Add(DeepgramFakeServer.ReadFrame(DeepgramFakeServer.FinalResultsFrame));

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().ContainSingle()
            .Which.Transcript.Should().Be(RecordedTranscript(DeepgramFakeServer.FinalResultsFrame));
    }

    [Fact]
    public async Task StreamAsync_ShouldYieldInterimResult()
    {
        // BuildResultJson patches these three values into the recorded frame of the matching
        // finality, so the suite still drives them while the rest of the schema survives.
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(DeepgramFakeServer.BuildResultJson("hola", 0.8f, isFinal: false));
        _server.ResultMessages.Add(DeepgramFakeServer.BuildResultJson("hola mundo", 0.99f, isFinal: true));

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().HaveCount(2);
        results[0].IsFinal.Should().BeFalse();
        results[0].Transcript.Should().Be("hola");
        results[1].IsFinal.Should().BeTrue();
        results[1].Transcript.Should().Be("hola mundo");
    }

    [Fact]
    public async Task StreamAsync_ShouldSendAudioFrames()
    {
        var recognizer = BuildRecognizer();
        await recognizer.StreamAsync(ThreeFrames(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.ReceivedFrameCount.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task StreamAsync_ShouldYieldFinalResult_WithCorrectConfidence()
    {
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(DeepgramFakeServer.BuildResultJson("prueba", 0.95f, isFinal: true));

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().ContainSingle(r => r.IsFinal && r.Confidence == 0.95f);
    }

    [Fact]
    public async Task StreamAsync_ShouldComplete_WhenServerClosesConnection()
    {
        var recognizer = BuildRecognizer();
        var act = async () =>
            await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();
        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Door 3 (<c>ADR-0050</c> E2c). This surface had <b>no</b> abort test at all — not one that asserted
    /// the wrong thing, one that did not exist, because the fake could not abort without hanging until it
    /// was ported off <c>HttpListener</c> above. A socket killed mid-session was therefore the least
    /// examined of the three doors here.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldThrowTransportFailure_WhenServerAbortsMidSession()
    {
        _server.AbortAfterSend = true;
        var recognizer = BuildRecognizer();

        var act = async () =>
            await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.Transport);
        failure.InnerException.Should().BeOfType<WebSocketException>();
    }

    /// <summary>
    /// The fourth door, and on this surface the one that matters most: §1.3a measured this vendor
    /// rejecting a bad credential with <c>HTTP 401</c> at the upgrade, on both its surfaces, which is
    /// exactly the failure <c>ADR-0050</c> E7 wraps. This test drives the no-HTTP-answer half — a
    /// refused connection, hence no code — because no fake in this suite can answer an upgrade with a
    /// status yet; the <c>401</c> mapping itself is asserted on the factory
    /// (<c>SpeechProviderFailureExceptionTests</c>).
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldThrowHandshakeFailure_WhenNothingAcceptsTheUpgrade()
    {
        var recognizer = BuildRecognizer(
            o => o.BaseUri = $"ws://127.0.0.1:{ClosedPort.Reserve()}/v1/listen");

        var act = async () =>
            await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.Handshake);
        failure.Code.Should().BeNull("a refused connection produced no HTTP answer to report");
        failure.InnerException.Should().BeAssignableTo<WebSocketException>();
    }

    /// <summary>
    /// Door 1 (<c>ADR-0050</c> E2a), driving the one branch of this change that rests on documentation
    /// rather than measurement — see <c>DeepgramFakeServer.ErrorFrameJson</c> for why: §1.3a measured this
    /// vendor rejecting a bad credential with <c>HTTP 401</c> at the upgrade on both its surfaces, so no
    /// live session has ever produced an in-band failure frame to copy. The frame below conforms to the
    /// published streaming schema. <see cref="SpeechProviderFailureException.Code"/> is asserted null on
    /// purpose: the client reads only the description here, because a <c>code</c> member is exactly the
    /// kind of detail no run has confirmed.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldThrowErrorFrameFailure_WhenTheServerSendsAnErrorFrame()
    {
        _server.ErrorFrameJson =
            """{"type":"Error","description":"DATA-0000: deepgram did not receive audio data","message":"NET-0001"}""";
        var recognizer = BuildRecognizer();

        var act = async () =>
            await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.ErrorFrame);
        failure.Code.Should().BeNull("this surface's frame shape is documented, not measured");
        failure.Message.Should().Contain("did not receive audio data");
    }

    /// <summary>
    /// Door 2 (<c>ADR-0050</c> E2b): the close code was discarded here as it was at all eight clients, so
    /// a session the vendor ended abnormally looked exactly like one that finished transcribing.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldThrowCloseCodeFailure_WhenServerClosesAbnormally()
    {
        _server.EndSessionSilently = true;
        _server.CloseStatus = WebSocketCloseStatus.PolicyViolation;   // 1008
        _server.CloseStatusDescription = "DATA-0000";
        var recognizer = BuildRecognizer();

        var act = async () =>
            await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.CloseCode);
        failure.Code.Should().Be("1008");
        failure.Message.Should().Contain("DATA-0000");
    }

    /// <summary>
    /// D2 (<c>ADR-0050</c> E5) on the recognition side: the vendor accepted the upgrade, sent no message of
    /// any kind — not even its <c>Metadata</c> summary — and closed normally.
    /// </summary>
    /// <remarks>
    /// The asserted value is <c>"Deepgram"</c>, and it is worth pinning rather than skipping because this
    /// vendor's two surfaces label themselves differently: the synthesizer reports <c>"DeepgramTts"</c>,
    /// this recognizer the bare vendor name. Whoever reads a `"Deepgram"` failure in a log is reading the
    /// STT half. That asymmetry is this vendor's alone — Speechmatics and Cartesia also ship both surfaces
    /// and use one label for each pair, so the surface is <em>not</em> recoverable from the provider name
    /// in general. Recorded here as measured behaviour; renaming any of the six is observable to callers
    /// and to metric tags, so it is not done under this change.
    /// </remarks>
    [Fact]
    public async Task StreamAsync_ShouldThrowEmptyResult_WhenTheVendorSendsNoMessageAtAll()
    {
        _server.EndSessionSilently = true;
        var recognizer = BuildRecognizer();

        var act = async () =>
            await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        var empty = (await act.Should().ThrowAsync<SpeechProviderEmptyResultException>()).Which;
        empty.Should().NotBeOfType<SpeechProviderFailureException>(
            "the session was clean — it was simply silent");
        empty.Provider.Should().Be("Deepgram");
    }

    /// <summary>
    /// The recognition half of E5, and the asymmetry against synthesis the rule turns on: a session
    /// carrying only the vendor's <c>Metadata</c> summary produced no transcript and is nonetheless
    /// healthy — noise with no speech is a session that correctly yielded nothing. Only zero
    /// <em>messages</em> is a failure, which is what the test above drives.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldYieldNothingWithoutFailing_WhenTheSessionCarriesOnlyControlFrames()
    {
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(DeepgramFakeServer.ReadFrame(DeepgramFakeServer.MetadataFrame));

        var recognizer = BuildRecognizer();

        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task StreamAsync_ShouldAbort_WhenCancelled()
    {
        // Deterministic contract (test-determinism fence): a pre-cancelled token throws
        // OperationCanceledException at iterator entry, before any provider request is
        // issued — independent of scheduling/mock latency. No wall-clock race against the
        // fake server (see openspec/changes/stt-cancellation-test-fence).
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var recognizer = BuildRecognizer();

        var act = async () =>
            await recognizer.StreamAsync(
                    SttFrameGenerators.EndlessFrames(), AudioFormat.Slin16Mono8kHz, cts.Token)
                // ADR-0052 F3: the consumer holds no token. Passing the cancelled one to ToListAsync
                // makes the enumerator throw on our behalf, and the assertion then cannot tell a
                // propagated throw from a silent `yield break` in the subject.
                .ToListAsync(CancellationToken.None);
        await act.Should().ThrowAsync<OperationCanceledException>();

        _server.ReceivedFrameCount.Should().Be(0);
    }

    /// <summary>
    /// The case seven of the eight WebSocket surfaces never had: cancellation observed while the
    /// server still has frames to give.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sibling above cancels before enumeration starts, so it exercises the entry guard and no
    /// socket is ever opened. This one cancels from inside the caller's own <c>await foreach</c>,
    /// one transcript in, with the fake parked mid-delivery — the shape §3.1 of
    /// <c>voiceai-midstream-cancellation-coverage</c> prescribes, and the one the HTTP-transport
    /// Lmnt test already uses.
    /// </para>
    /// <para>
    /// Two conditions are asserted rather than assumed, because "it threw" alone would also pass if
    /// the session had ended on the server's own close and the test were crediting cancellation with
    /// someone else's work: the socket was live at the moment the token fired, and the gate was
    /// holding an undelivered frame. This fake sends no greeting, so the interim result is its first outbound message and a hold at 1 leaves the final result unsent.
    /// </para>
    /// <para>
    /// Negative-tested by neutralising the hold (§3.6): with delivery ungated the caller observes
    /// <b>two</b> transcripts before the cancel takes effect, so <c>observed</c> is what detects a
    /// cancel that is not actually mid-flight — not the gate's own bookkeeping.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task StreamAsync_ShouldAbort_WhenCancelledMidDelivery()
    {
        var gate = new OutboundFrameGate(1);
        _server.OutboundGate = gate;

        using var cts = new CancellationTokenSource();
        var recognizer = BuildRecognizer();

        WebSocketState? stateAtCancel = null;
        var observed = 0;

        var act = async () =>
        {
            // ADR-0052 F3: the token goes to StreamAsync and nowhere else. The consumer holds none,
            // so a throw here is the recognizer's own.
            await foreach (var result in recognizer.StreamAsync(
                               SttFrameGenerators.EndlessFrames(), AudioFormat.Slin16Mono8kHz, cts.Token))
            {
                observed++;
                stateAtCancel = _server.SocketState;
                await cts.CancelAsync();
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();

        observed.Should().Be(1, "the cancel must land after a transcript reached the caller, not before");
        stateAtCancel.Should().Be(
            WebSocketState.Open,
            "cancellation has to be observed on a live socket, or the stream ended on the server's "
            + "close instead and the test would be crediting cancellation with someone else's work");
        gate.Held.IsCompleted.Should().BeTrue("the fake must still have had a frame to send");
        gate.Delivered.Should().Be(1, "exactly the interim result was delivered");
    }

    [Fact]
    public async Task StreamAsync_ShouldSendTheCloseStreamTerminator_WhenInputEnds()
    {
        var recognizer = BuildRecognizer();
        await recognizer.StreamAsync(ThreeFrames(), AudioFormat.Slin16Mono8kHz).ToListAsync();
        await SessionEndedAsync();

        _server.ReceivedTerminatorText.Should().Be("""{"type":"CloseStream"}""");
    }

    /// <summary>
    /// Deepgram is the one surface where §3.6d measured the half-close and the terminator as
    /// equivalent — 10/10 digits either way — so this test guards a decision rather than a defect:
    /// all four clients end input the same way, and a reader who restores the half-close here on
    /// the grounds that it used to work would have to re-run the measurement to find out that it
    /// is the two other surfaces, not this one, that would break.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldNotHalfCloseTheOutputSide_WhenInputEnds()
    {
        var recognizer = BuildRecognizer();
        await recognizer.StreamAsync(ThreeFrames(), AudioFormat.Slin16Mono8kHz).ToListAsync();
        await SessionEndedAsync();

        _server.ReceivedClientCloseFrame.Should().BeFalse();
    }

    /// <summary>
    /// Witnesses the <c>CloseSent</c> disjunct in this fake's receive loop — the one fence in the
    /// sweep's inventory that no test in this suite could reach.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fence is <c>while (ws.State is WebSocketState.Open or WebSocketState.CloseSent)</c>. The
    /// <c>CloseSent</c> disjunct is what keeps the loop alive across the fake's <em>own</em> close:
    /// the terminator branch answers, calls <c>CloseOutputAsync</c> — which moves the server socket
    /// to <c>CloseSent</c> — and then falls back to the top of the loop. With only <c>Open</c> there,
    /// the loop would exit on that very evaluation and the client's close frame would never be read,
    /// so <c>ReceivedClientCloseFrame</c> would read <see langword="false"/> for every client alike.
    /// </para>
    /// <para>
    /// No shipped recognizer can produce that condition: none of the four sends a close frame at all
    /// (§4.1 — <c>grep 'CloseAsync\|CloseOutputAsync' src/Verbara.Sdk.VoiceAi.Stt/</c> returns one
    /// hit and it is a comment). So the witness has to be a client that does, and this test is one:
    /// a raw <see cref="ClientWebSocket"/> driven by hand. <c>src/</c> is deliberately not touched —
    /// the sweep proved this fence live by temporarily reinstating the removed half-close, which is
    /// a measurement technique, not a change (§4.4).
    /// </para>
    /// <para>
    /// The pair of assertions is the test. The terminator having been recorded proves the fake
    /// reached the branch that closes its own output, so the close frame that follows is read from
    /// <c>CloseSent</c> and not from <c>Open</c> — without it, a client that closed without a
    /// terminator would satisfy the second assertion while never exercising the fence at all. Frame
    /// ordering is TCP's, not a race: the terminator is written first, so it is read first.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Session_ShouldKeepReadingPastItsOwnClose_WhenTheClientHalfCloses()
    {
        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/v1/listen"), CancellationToken.None);

        // A frame of audio, then the terminator: the fake answers it and half-closes its own output,
        // which is the state the fence is about.
        await client.SendAsync(new byte[320].AsMemory(), WebSocketMessageType.Binary, true, CancellationToken.None);
        await client.SendAsync(
            Encoding.UTF8.GetBytes("""{"type":"CloseStream"}""").AsMemory(),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);

        // The half-close this suite's own recognizers never perform.
        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);

        await SessionEndedAsync();

        _server.ReceivedTerminatorText.Should().Be(
            """{"type":"CloseStream"}""",
            "the fake must have reached the branch that closes its own output before the client's "
            + "close arrives, or the frame below was read from Open and the fence was never used");

        _server.ReceivedClientCloseFrame.Should().BeTrue(
            "the loop must keep reading while the server socket is CloseSent, or the client's close "
            + "frame is never seen and every client reads as one that did not half-close");
    }

    /// <summary>
    /// Wait for the fake's session handler to return before asserting on what the client sent last.
    /// <c>StreamAsync</c> returns as soon as the server closes, which can be before the server has
    /// read the frames the client sent just before that — so without this join point a half-close
    /// assertion is a race the defect wins. The bound is a liveness guard, not a synchronisation
    /// delay: it is never reached on a passing run.
    /// </summary>
    private Task SessionEndedAsync() => _server.SessionCompleted.WaitAsync(TimeSpan.FromSeconds(10));

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> SingleFrame()
    {
        yield return new byte[320];
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> ThreeFrames()
    {
        for (int i = 0; i < 3; i++) yield return new byte[320];
        await Task.CompletedTask;
    }

    [Fact]
    public async Task StreamAsync_ShouldAuthenticateWithTheTokenScheme_WhenOpeningASession()
    {
        var recognizer = BuildRecognizer();
        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.ReceivedAuthorization.Should().Be($"Token {TestApiKey}");
    }

    [Fact]
    public async Task StreamAsync_ShouldSendModelAndLanguage_WhenOpeningASession()
    {
        // These two parameters were absent from the client's test-only URI branch, so the fake
        // received a request production never sends and no test could see the difference. They are
        // set to non-default values here for the same reason the voice id is in the ElevenLabs
        // suite: a default would also match a client that ignored the option.
        var recognizer = BuildRecognizer(o =>
        {
            o.Model = "nova-3";
            o.Language = "pt";
        });
        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        var query = new Uri($"ws://localhost{_server.ReceivedRequestUri}").Query;
        query.Should().Contain("model=nova-3").And.Contain("language=pt");
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _server.DisposeAsync();
    }
}
