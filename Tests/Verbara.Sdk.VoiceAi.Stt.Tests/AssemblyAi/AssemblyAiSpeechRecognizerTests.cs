using System.Net.WebSockets;
using System.Text.Json;
using Verbara.Sdk.Audio;
using Verbara.Sdk.TestInfrastructure.WebSocket;
using Verbara.Sdk.VoiceAi.Stt.AssemblyAi;
using Verbara.Sdk.VoiceAi.Stt.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.AssemblyAi;

/// <summary>
/// Transport: WebSocket. Deliberately NOT migrated to the WireMock substrate — WireMock.NET matches
/// HTTP/1.1 requests and cannot hold the duplex session these tests drive (ADR-0041 D2), so
/// <c>AssemblyAiFakeServer</c> on <c>WebSocketTestServer</c> stays. Fidelity here comes from the
/// frames in <c>Recordings/assemblyai-stt/</c> (D4), not from a different server. AssemblyAI is
/// <c>not-cleared</c> for capturing Output (<c>docs/guides/provider-recording-protocol.md</c> §7) —
/// and for this vendor that is a terms blocker, not a missing credential, because the express
/// customer-ownership-of-Outputs clause was deleted in a later revision. Those frames therefore take
/// §7's documentation-derived route — authored to AssemblyAI's published Universal Streaming schema
/// with fictional values, <c>class: "synthetic"</c>, <c>terms.verdict: "not-applicable"</c> — rather
/// than being captured. That closes the field-set half of the D4 gap and not the drift half.
/// </summary>
public class AssemblyAiSpeechRecognizerTests : IAsyncDisposable
{
    /// <summary>
    /// The vendor's stated floor and ceiling in bytes of the audio this suite streams, derived through
    /// the same conversion the client uses rather than written out — the one test that cares pins them
    /// to 800 and 16000 so the derivation itself is anchored to real units.
    /// </summary>
    private static readonly int FloorBytes =
        AudioFormat.Slin16Mono8kHz.BytesPerFrame(TimeSpan.FromMilliseconds(50));

    private static readonly int CeilingBytes =
        AudioFormat.Slin16Mono8kHz.BytesPerFrame(TimeSpan.FromSeconds(1));

    private readonly AssemblyAiFakeServer _server;

    public AssemblyAiSpeechRecognizerTests()
    {
        _server = new AssemblyAiFakeServer();
        _server.Start();
    }

    /// <summary>The credential every test in this class sends, asserted on by the fake.</summary>
    private const string TestApiKey = "test-key";

    /// <summary>
    /// Reaches the fake through <see cref="AssemblyAiOptions.BaseUri"/>, so the route, the query and
    /// the raw-key <c>Authorization</c> header all come from shipped code.
    /// </summary>
    private AssemblyAiSpeechRecognizer BuildRecognizer(Action<AssemblyAiOptions>? configure = null)
    {
        var opts = new AssemblyAiOptions
        {
            ApiKey = TestApiKey,
            BaseUri = $"ws://127.0.0.1:{_server.Port}/v3/ws"
        };
        configure?.Invoke(opts);
        return new AssemblyAiSpeechRecognizer(Options.Create(opts));
    }

    /// <summary>
    /// The transcript a recorded frame actually carries, read with <see cref="JsonDocument"/> rather
    /// than hard-coded. Two independent readers — this one and the SDK's source-generated parser —
    /// must agree on the frame's bytes; hard-coding the sentence would instead assert what the frame
    /// was expected to say, and would have to be edited whenever it is re-authored.
    /// </summary>
    private static string RecordedTranscript(string frame)
    {
        using var document = JsonDocument.Parse(AssemblyAiFakeServer.ReadFrame(frame));
        return document.RootElement.GetProperty("transcript").GetString()!;
    }

    [Fact]
    public async Task StreamAsync_ShouldYieldRecordedTurns_WhenReplayingDocumentedFrames()
    {
        // The default seed is the two recorded frames verbatim: interim then final.
        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        var interim = RecordedTranscript(AssemblyAiFakeServer.InterimTurnFrame);
        var final = RecordedTranscript(AssemblyAiFakeServer.FinalTurnFrame);
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
        // The point of the recording. AssemblyAiTurnMessage models four values — type, transcript,
        // end_of_turn and turn_is_formatted — out of everything AssemblyAI documents on a Turn, and
        // the two lifecycle frames are modelled not at all. The first block fences the fixtures:
        // shrink any of them back towards the four-field object this migration retires and this
        // fails loudly instead of silently taking the assertion below with it.
        using (var turn = JsonDocument.Parse(AssemblyAiFakeServer.ReadFrame(AssemblyAiFakeServer.FinalTurnFrame)))
        {
            var root = turn.RootElement;
            foreach (var unmodelled in new[]
                     {
                         "turn_order", "end_of_turn_confidence", "language_code",
                         "language_confidence", "speaker_label",
                     })
            {
                root.TryGetProperty(unmodelled, out _)
                    .Should().BeTrue("the recorded Turn frame must carry '{0}', which the SDK does not model", unmodelled);
            }

            root.TryGetProperty("words", out var words).Should().BeTrue();
            words.GetArrayLength().Should().BeGreaterThan(0, "word-level detail is unmodelled sibling data too");
            foreach (var wordField in new[] { "text", "start", "end", "confidence", "word_is_final", "speaker" })
            {
                words[0].TryGetProperty(wordField, out _)
                    .Should().BeTrue("a recorded word must carry '{0}'", wordField);
            }
        }

        using (var begin = JsonDocument.Parse(AssemblyAiFakeServer.ReadFrame(AssemblyAiFakeServer.BeginFrame)))
        {
            begin.RootElement.TryGetProperty("id", out _).Should().BeTrue();
            begin.RootElement.TryGetProperty("expires_at", out _).Should().BeTrue();
            begin.RootElement.TryGetProperty("configuration", out var configuration).Should().BeTrue();
            configuration.TryGetProperty("api_version", out _)
                .Should().BeTrue("the Begin frame's nested configuration object is unmodelled sibling data");
        }

        using (var termination = JsonDocument.Parse(AssemblyAiFakeServer.ReadFrame(AssemblyAiFakeServer.TerminationFrame)))
        {
            termination.RootElement.TryGetProperty("audio_duration_seconds", out _).Should().BeTrue();
            termination.RootElement.TryGetProperty("session_duration_seconds", out _).Should().BeTrue();
        }

        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(AssemblyAiFakeServer.ReadFrame(AssemblyAiFakeServer.FinalTurnFrame));

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().ContainSingle()
            .Which.Transcript.Should().Be(RecordedTranscript(AssemblyAiFakeServer.FinalTurnFrame));
    }

    [Fact]
    public async Task StreamAsync_ShouldSurfaceZeroConfidence_WhenTurnCarriesOnlyEndOfTurnConfidence()
    {
        // A finding the recording makes visible. The SDK hard-codes 0f "because AssemblyAI v3 Turn
        // messages do not include a per-turn confidence scalar" — but the documented schema carries
        // two: end_of_turn_confidence on the turn and confidence on every word. Neither is a
        // transcript-accuracy score, so surfacing 0f is defensible; what was not previously true is
        // that a reader could see the trade-off from the suite.
        using (var document = JsonDocument.Parse(AssemblyAiFakeServer.ReadFrame(AssemblyAiFakeServer.FinalTurnFrame)))
        {
            document.RootElement.GetProperty("end_of_turn_confidence").GetDouble()
                .Should().BeGreaterThan(0, "the recorded frame must actually carry a confidence to ignore");
        }

        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(AssemblyAiFakeServer.ReadFrame(AssemblyAiFakeServer.FinalTurnFrame));

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().ContainSingle().Which.Confidence.Should().Be(0f);
    }

    /// <summary>
    /// The sample-rate expectation here used to read <c>sample_rate=16000</c> while the audio being
    /// streamed was <see cref="AudioFormat.Slin16Mono8kHz"/> — the assertion encoded the defect. The
    /// option is set to 16000 deliberately, so that what this now proves is the caller's format
    /// overriding it rather than the two happening to agree.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldConnect_WithCorrectQueryString_WhenStarted()
    {
        _server.ResultMessages.Clear();
        var recognizer = BuildRecognizer(o =>
        {
            o.SampleRate = 16000;
            o.FormatTurns = 1;
            o.EndOfTurnConfidenceThreshold = 800;
        });

        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.ReceivedRequestUri.Should().NotBeNullOrEmpty();
        _server.ReceivedRequestUri!.Should().Contain(
            "sample_rate=8000",
            "the service derives each audio message's duration from the declared rate, so it has to be the rate of the audio actually sent");
        _server.ReceivedRequestUri.Should().NotContain("sample_rate=16000");
        _server.ReceivedRequestUri.Should().Contain("format_turns=1");
        _server.ReceivedRequestUri.Should().Contain("end_of_turn_confidence_threshold=800");
    }

    /// <summary>
    /// The other half of the rate contract, and the only path on which the option is still read: a
    /// format that states no rate cannot override anything. It is also the sharpest available test of
    /// the coupling that matters — the floor is computed from whatever rate ends up declared, so
    /// falling back to a 16 kHz option moves the floor to 1600 bytes for the very same audio. A client
    /// that declared one rate and sized by another is what the vendor answers
    /// <c>3007 Input Duration Violation: 25.0 ms</c> (measured 2026-08-17), and that divergence cannot
    /// be expressed here.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldDeclareTheOptionSampleRate_WhenTheCallerFormatCarriesNone()
    {
        _server.ResultMessages.Clear();
        var recognizer = BuildRecognizer(o => o.SampleRate = 16000);

        await recognizer.StreamAsync(
            Frames(count: 1, bytesEach: 320),
            new AudioFormat(0, 1, 16, AudioEncoding.LinearPcm)).ToListAsync();
        await SessionEndedAsync();

        var fallbackFloor = AudioFormat.Slin16Mono16kHz.BytesPerFrame(TimeSpan.FromMilliseconds(50));
        fallbackFloor.Should().Be(2 * FloorBytes, "the same 50 ms costs twice the bytes at twice the rate");

        _server.ReceivedRequestUri!.Should().Contain("sample_rate=16000");
        _server.AudioMessageByteCounts.Should().Equal([fallbackFloor]);
    }

    /// <summary>
    /// The degenerate branch, stated rather than left implicit. A format with no sample width cannot be
    /// converted into a duration at all, so there is no threshold to coalesce against and frames pass
    /// through one per message — the shape this fix replaces, kept deliberately over throwing, since
    /// nothing in this repo produces such a format and inventing a sample width would be guessing at
    /// the caller's wire format.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldPassFramesThrough_WhenTheFormatCarriesNoSampleWidth()
    {
        _server.ResultMessages.Clear();
        var recognizer = BuildRecognizer();

        await recognizer.StreamAsync(
            Frames(count: 2, bytesEach: 320),
            new AudioFormat(8000, 1, 0, AudioEncoding.LinearPcm)).ToListAsync();
        await SessionEndedAsync();

        _server.AudioMessageByteCounts.Should().Equal([320, 320]);
    }

    /// <summary>
    /// The closing test for §3.18, and the assertion is on what left the client. AssemblyAI answers any
    /// audio message shorter than 50 ms with
    /// <c>3007 Input Duration Violation … Expected between 50 and 1000 ms</c> and ends the session;
    /// this client sent one message per caller frame, and an Asterisk AudioSocket source yields 20 ms
    /// frames, so every such session died — invisibly, since the receive loop drops the error frame
    /// (§4.15). A fake that merely counted frames could not fail that client, which is how it shipped.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldCoalesceAboveTheVendorFloor_WhenCallerYields20MsFrames()
    {
        // Anchor the derived thresholds to the vendor's stated numbers in real units once, so the rest
        // of the suite can compute them without hard-coding byte counts that would drift.
        FloorBytes.Should().Be(800, "50 ms of 8 kHz 16-bit mono PCM");
        CeilingBytes.Should().Be(16000, "1000 ms of the same");

        _server.ResultMessages.Clear();
        var recognizer = BuildRecognizer();

        // Eight 20 ms frames: 160 ms of audio that the shipped client turned into eight rejections.
        await recognizer.StreamAsync(
            Frames(count: 8, bytesEach: 320), AudioFormat.Slin16Mono8kHz).ToListAsync();
        await SessionEndedAsync();

        var messages = _server.AudioMessageByteCounts;
        messages.Should().NotBeEmpty();
        messages.Should().OnlyContain(bytes => bytes >= FloorBytes, "the vendor rejects anything shorter");
        messages.Should().OnlyContain(bytes => bytes <= CeilingBytes, "and anything longer");
        messages.Count.Should().BeLessThan(8, "coalescing is the point; one message per frame is the defect");
        messages.Sum().Should().BeGreaterThanOrEqualTo(8 * 320, "no caller audio may be dropped on the way out");
        messages.Sum().Should().BeLessThan(
            8 * 320 + FloorBytes,
            "only the trailing remainder may be padded, and only up to the floor — a client that padded every message would satisfy the bound above");
    }

    /// <summary>
    /// The single-frame case is not an edge here — it is the exact input the rest of this suite has
    /// always used, and the shape the shipped client turned into one 20 ms message. The remainder is
    /// padded with silence to the floor rather than sent short: sending it as-is was measured working
    /// three runs of three on 2026-08-17, and rejected anyway because three consecutive sub-floor
    /// messages drew <c>3007</c> with zero finals — a thin, unstated tolerance that costs the whole
    /// transcript when it breaks.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldPadTheTrailingRemainderToTheFloor_WhenInputEndsBelowIt()
    {
        _server.ResultMessages.Clear();
        var recognizer = BuildRecognizer();

        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();
        await SessionEndedAsync();

        _server.AudioMessageByteCounts.Should().Equal([FloorBytes]);
    }

    /// <summary>
    /// The window is two-sided and both ends were measured: a single 2000 ms message draws the same
    /// <c>3007</c> as a 20 ms one. This is reachable from one caller frame rather than from
    /// accumulation — the AudioSocket codec's 3-byte length field admits payloads far larger than
    /// anything this repo emits today — so the split is not defensive padding.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldSplitAtTheVendorCeiling_WhenOneCallerFrameExceedsIt()
    {
        _server.ResultMessages.Clear();
        var recognizer = BuildRecognizer();

        // One frame carrying 2000 ms of audio — twice the ceiling, and rejected whole.
        await recognizer.StreamAsync(
            Frames(count: 1, bytesEach: 2 * CeilingBytes), AudioFormat.Slin16Mono8kHz).ToListAsync();
        await SessionEndedAsync();

        var messages = _server.AudioMessageByteCounts;
        messages.Should().OnlyContain(bytes => bytes <= CeilingBytes && bytes >= FloorBytes);
        messages.Sum().Should().Be(2 * CeilingBytes, "splitting must not lose or invent audio");
    }

    [Fact]
    public async Task StreamAsync_ShouldYieldFinalTurn_WhenEndOfTurnTrue()
    {
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(AssemblyAiFakeServer.BuildTurnJson("hola mundo", endOfTurn: true));

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().ContainSingle();
        results[0].Transcript.Should().Be("hola mundo");
        results[0].IsFinal.Should().BeTrue();
    }

    [Fact]
    public async Task StreamAsync_ShouldYieldInterimTurn_WhenEndOfTurnFalse()
    {
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(AssemblyAiFakeServer.BuildTurnJson("hola", endOfTurn: false));

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().ContainSingle();
        results[0].Transcript.Should().Be("hola");
        results[0].IsFinal.Should().BeFalse();
    }

    [Fact]
    public async Task StreamAsync_ShouldIgnoreBeginAndTermination_NotYieldResult()
    {
        // Server automatically sends Begin on connect. Add only a Termination after — no Turn.
        // Both are now the recorded control frames, so the parser's type filter is exercised
        // against the shapes AssemblyAI documents rather than against two-field placeholders.
        //
        // This is also the guard on the recognition half of ADR-0050 E5: a lifecycle-only session
        // produced no transcript and is nonetheless healthy (turn detection flushes on any trigger, so
        // noise with no speech correctly yields nothing). Zero results must stay an empty list here —
        // what E5 reports on this surface is a session with no vendor message at all, which the test
        // further down drives.
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(AssemblyAiFakeServer.BuildTerminationJson());

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().BeEmpty();
    }

    /// <summary>
    /// Door 3 (<c>ADR-0050</c> E2c), and the inverse of what this test used to assert. Under
    /// <c>NotThrowAsync</c> a socket killed mid-session ended the transcript stream exactly as the
    /// vendor's own <c>Termination</c> does.
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
        failure.Code.Should().BeNull("a dead socket carries no vendor code");
        failure.InnerException.Should().BeOfType<WebSocketException>();
    }

    /// <summary>
    /// The fourth door, and the one no receive-loop test can reach: a session that never opens.
    /// <c>ADR-0050</c> E7 wraps the bare <see cref="WebSocketException"/> so the caller reads one type
    /// whether this vendor validates at the upgrade or in band. A refused connection carries no HTTP
    /// answer, hence no code; the answered-with-a-status branch is asserted on the factory itself
    /// (<c>SpeechProviderFailureExceptionTests</c>) because no fake in this suite can reject an
    /// upgrade yet.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldThrowHandshakeFailure_WhenNothingAcceptsTheUpgrade()
    {
        var recognizer = BuildRecognizer(o => o.BaseUri = $"ws://127.0.0.1:{ClosedPort.Reserve()}/v3/ws");

        var act = async () =>
            await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.Handshake);
        failure.Code.Should().BeNull("a refused connection produced no HTTP answer to report");
        failure.InnerException.Should().BeAssignableTo<WebSocketException>();
    }

    /// <summary>
    /// Door 1 (<c>ADR-0050</c> E2a) with the failure this surface was measured producing: a message
    /// shorter than the vendor's 50 ms floor is answered <c>3007</c> in band, and the session ends. The
    /// client now coalesces to that window, so this frame is no longer reachable from a caller's frame
    /// size — but the door it escaped through is what this test holds shut, and until §3.18 it was the
    /// literal production failure (every session from a 20 ms AudioSocket source died here, silently).
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldThrowErrorFrameFailure_WhenTheServerRejectsAMessageInBand()
    {
        _server.ErrorFrameJson =
            """{"type":"Error","error_code":3007,"error":"Input Duration Violation: 20.0 ms. Expected between 50 and 1000 ms"}""";
        var recognizer = BuildRecognizer();

        var act = async () =>
            await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.ErrorFrame);
        failure.Code.Should().Be("3007");
        failure.Message.Should().Contain("Expected between 50 and 1000 ms");
    }

    /// <summary>
    /// Door 2 (<c>ADR-0050</c> E2b): this vendor was measured putting the same <c>3007</c> in the close
    /// code as in the frame, so this test drives the code alone — no frame at all — and the client must
    /// still report it.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldThrowCloseCodeFailure_WhenServerClosesAbnormally()
    {
        _server.EndSessionSilently = true;
        _server.CloseStatus = (WebSocketCloseStatus)3007;
        _server.CloseStatusDescription = "Input Duration Violation";
        var recognizer = BuildRecognizer();

        var act = async () =>
            await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.CloseCode);
        failure.Code.Should().Be("3007");
    }

    /// <summary>
    /// D2 (<c>ADR-0050</c> E5) on the recognition side: the vendor accepted the upgrade, sent no message
    /// of any kind — not even the <c>Begin</c> greeting — and closed normally. Nothing failed on the
    /// wire, so this is not a <see cref="SpeechProviderFailureException"/>; it is also not the healthy
    /// zero-transcript session above, and the two must not report the same way.
    /// </summary>
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
        empty.Provider.Should().Be("AssemblyAI");
    }

    [Fact]
    public async Task StreamAsync_ShouldAbort_WhenCancelled()
    {
        // Deterministic contract (test-determinism fence): a pre-cancelled token throws
        // OperationCanceledException at iterator entry, before any provider request is
        // issued — independent of scheduling/mock latency. No wall-clock race against the
        // fake server (see openspec/changes/archive/2026-07-05-stt-cancellation-test-fence).
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

    [Fact]
    public async Task StreamAsync_ShouldSendTheTerminateTerminator_WhenInputEnds()
    {
        var recognizer = BuildRecognizer();
        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();
        await SessionEndedAsync();

        _server.ReceivedTerminatorText.Should().Be("""{"type":"Terminate"}""");
    }

    /// <summary>
    /// The load-bearing half of the pair. §3.6d streamed one utterance of ten spoken digits into
    /// this surface three ways: half-close alone returned 0/10 digits and not one end-of-turn
    /// message, the terminator alone returned 10/10, and sending both returned 0/10 — so the
    /// half-close is not merely redundant here, it destroys the result even when the terminator
    /// precedes it. This test fails the moment a client sends it again.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldNotHalfCloseTheOutputSide_WhenInputEnds()
    {
        var recognizer = BuildRecognizer();
        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();
        await SessionEndedAsync();

        _server.ReceivedClientCloseFrame.Should().BeFalse();
    }

    /// <summary>
    /// Wait for the fake's session handler to return before asserting on what the client sent last.
    /// <c>StreamAsync</c> returns as soon as the server closes, which can be before the server has
    /// read the frames the client sent just before that — so without this join point a half-close
    /// assertion is a race the defect wins. The bound is a liveness guard, not a synchronisation
    /// delay: it is never reached on a passing run.
    /// </summary>
    private Task SessionEndedAsync() => _server.SessionCompleted.WaitAsync(TimeSpan.FromSeconds(10));

    /// <summary>
    /// One 20 ms frame at 8 kHz — what an Asterisk AudioSocket source yields, and what the shipped
    /// client turned into one 20 ms message the vendor rejects.
    /// </summary>
    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> SingleFrame()
    {
        yield return new byte[320];
        await Task.CompletedTask;
    }

    /// <summary>Silence, in frames of a size the test chooses. Only the byte counts are asserted on.</summary>
    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Frames(int count, int bytesEach)
    {
        for (var i = 0; i < count; i++)
            yield return new byte[bytesEach];

        await Task.CompletedTask;
    }

    [Fact]
    public async Task StreamAsync_ShouldAuthenticateWithTheRawKey_WhenOpeningASession()
    {
        var recognizer = BuildRecognizer();
        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        // Exactly the key: AssemblyAI takes no scheme prefix, so this also fails a client that
        // helpfully adds one.
        _server.ReceivedAuthorization.Should().Be(TestApiKey);
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _server.DisposeAsync();
    }
}
