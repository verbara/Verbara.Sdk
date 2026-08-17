using System.Net.WebSockets;
using System.Text.Json;
using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.Stt.Cartesia;
using Verbara.Sdk.VoiceAi.Stt.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.Cartesia;

/// <summary>
/// Transport: WebSocket. Deliberately NOT migrated to the WireMock substrate — WireMock.NET matches
/// HTTP/1.1 requests and cannot hold the duplex session these tests drive (ADR-0041 D2), so
/// <c>CartesiaFakeServer</c> on <c>WebSocketTestServer</c> stays. Fidelity here comes from the frames
/// in <c>Recordings/cartesia-stt/</c> (D4), not from a different server. Cartesia is
/// <c>permitted-with-conditions</c> for capturing Output
/// (<c>docs/guides/provider-recording-protocol.md</c> §7), so — unlike Deepgram and AssemblyAI — its
/// terms are not what stands between this suite and a real capture; no capture credential exists in
/// this environment, and a capture stays a known, cleared upgrade path. Meanwhile the frames take
/// §7's documentation-derived route, <c>class: "synthetic"</c> with a <c>source_schema</c> block.
/// That closes the field-set half of the D4 gap and not the drift half.
/// </summary>
public class CartesiaSpeechRecognizerTests : IAsyncDisposable
{
    private readonly CartesiaFakeServer _server;

    public CartesiaSpeechRecognizerTests()
    {
        _server = new CartesiaFakeServer();
        _server.Start();
    }

    /// <summary>The credential every test in this class sends, asserted on by the fake.</summary>
    private const string TestApiKey = "test-key";

    /// <summary>
    /// Reaches the fake through <see cref="CartesiaOptions.BaseUri"/> — the operator-facing seam —
    /// so route, query and credential all come from shipped code. The path is the one the production
    /// default carries.
    /// </summary>
    private CartesiaSpeechRecognizer BuildRecognizer(Action<CartesiaOptions>? configure = null)
    {
        var opts = new CartesiaOptions
        {
            ApiKey = TestApiKey,
            BaseUri = $"ws://127.0.0.1:{_server.Port}/stt/websocket"
        };
        configure?.Invoke(opts);
        return new CartesiaSpeechRecognizer(Options.Create(opts));
    }

    /// <summary>
    /// The text a recorded frame actually carries, read with <see cref="JsonDocument"/> rather than
    /// hard-coded. Two independent readers — this one and the SDK's source-generated parser — must
    /// agree on the frame's bytes; hard-coding the sentence would instead assert what the frame was
    /// expected to say, and would have to be edited whenever it is re-authored.
    /// </summary>
    private static string RecordedText(string frame)
    {
        using var document = JsonDocument.Parse(CartesiaFakeServer.ReadFrame(frame));
        return document.RootElement.GetProperty("text").GetString()!;
    }

    [Fact]
    public async Task StreamAsync_ShouldYieldRecordedTranscripts_WhenReplayingDocumentedFrames()
    {
        // The default seed is the two recorded frames verbatim: interim then final.
        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        var interim = RecordedText(CartesiaFakeServer.InterimTranscriptFrame);
        var final = RecordedText(CartesiaFakeServer.FinalTranscriptFrame);
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
        // The point of the recording. CartesiaSttTranscriptMessage models four values — type, text,
        // is_final and confidence — and Cartesia does not document the fourth at all. The first
        // block fences the fixture: reduce it back to the old three-field object and this fails
        // loudly instead of silently taking the assertion below with it.
        using (var document = JsonDocument.Parse(CartesiaFakeServer.ReadFrame(CartesiaFakeServer.FinalTranscriptFrame)))
        {
            var root = document.RootElement;
            foreach (var unmodelled in new[] { "request_id", "duration", "language" })
            {
                root.TryGetProperty(unmodelled, out _)
                    .Should().BeTrue("the recorded frame must carry '{0}', which the SDK does not model", unmodelled);
            }

            root.TryGetProperty("words", out var words).Should().BeTrue();
            words.GetArrayLength().Should().BeGreaterThan(0, "word-level detail is unmodelled sibling data too");
            foreach (var wordField in new[] { "word", "start", "end" })
            {
                words[0].TryGetProperty(wordField, out _)
                    .Should().BeTrue("a recorded word must carry '{0}'", wordField);
            }
        }

        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(CartesiaFakeServer.ReadFrame(CartesiaFakeServer.FinalTranscriptFrame));

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().ContainSingle()
            .Which.Transcript.Should().Be(RecordedText(CartesiaFakeServer.FinalTranscriptFrame));
    }

    [Fact]
    public async Task StreamAsync_ShouldSurfaceZeroConfidence_WhenVendorSchemaCarriesNoConfidenceField()
    {
        // The finding this migration surfaced. Cartesia documents no confidence on the transcript
        // message and none per word, yet CartesiaSttTranscriptMessage models a nullable float and
        // falls back to 0f. Nothing asserted that fallback while the fake invented a confidence on
        // every frame; a schema-faithful recording is what makes it reachable.
        using (var document = JsonDocument.Parse(CartesiaFakeServer.ReadFrame(CartesiaFakeServer.FinalTranscriptFrame)))
        {
            document.RootElement.TryGetProperty("confidence", out _)
                .Should().BeFalse("Cartesia documents no confidence field, so a faithful recording must not carry one");
        }

        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(CartesiaFakeServer.ReadFrame(CartesiaFakeServer.FinalTranscriptFrame));

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().ContainSingle().Which.Confidence.Should().Be(0f);
    }

    [Fact]
    public async Task StreamAsync_ShouldIgnoreFrame_WhenTypeIsNotTranscript()
    {
        // Cartesia interleaves control frames with transcripts, and the recognizer deserializes
        // every text frame into its transcript DTO before filtering on type. The recorded
        // flush_done carries the documented-deprecated is_final true and no text at all, which is
        // exactly the frame a broken filter would leak through as an empty final result. Nothing in
        // this suite could send it before the fixture existed.
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(CartesiaFakeServer.ReadFrame(CartesiaFakeServer.FlushDoneFrame));
        _server.ResultMessages.Add(CartesiaFakeServer.ReadFrame(CartesiaFakeServer.FinalTranscriptFrame));

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().ContainSingle()
            .Which.Transcript.Should().Be(RecordedText(CartesiaFakeServer.FinalTranscriptFrame));
    }

    /// <summary>
    /// The session parameters belong in the query string, and the assertion moved there with them.
    /// This test used to read them out of an opening JSON frame — a frame the service does not
    /// accept, answering it with <c>Expected one of: "finalize", "done", "close"</c> — while the
    /// client sent no query at all and every real session was closed <c>1008 Missing sample_rate</c>.
    /// A fake that only inspects frames cannot see a defect that lives in the request line, which is
    /// how a client that had never opened a session kept a green suite.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldSendSessionParametersInTheQuery_WhenConnectionOpens()
    {
        var recognizer = BuildRecognizer();
        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();
        await SessionEndedAsync();

        var query = new Uri($"ws://localhost{_server.RequestUri}").Query;
        query.Should().Contain("sample_rate=8000", "the vendor names this one when it is missing")
            .And.Contain("model=ink-whisper")
            .And.Contain("language=en")
            .And.Contain("encoding=pcm_s16le");
    }

    /// <summary>
    /// The other half of the same fix: the parameters moved to the query, so nothing may be sent as
    /// an opening frame any more. Asserting the query alone would pass on a client that sent both,
    /// which is the state the vendor rejects — and the terminator is deliberately excluded here,
    /// since it is the one text frame this socket does accept.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldSendNoConfigurationFrame_WhenConnectionOpens()
    {
        var recognizer = BuildRecognizer();
        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();
        await SessionEndedAsync();

        _server.ReceivedJsonMessages.Should().BeEmpty(
            "this service accepts only \"finalize\", \"done\" and \"close\" as text");
    }

    [Fact]
    public async Task StreamAsync_ShouldYieldInterimTranscript_WhenIsFinalFalse()
    {
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(CartesiaFakeServer.BuildTranscriptJson("hola", 0.7f, isFinal: false));
        _server.ResultMessages.Add(CartesiaFakeServer.BuildTranscriptJson("hola mundo", 0.95f, isFinal: true));

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().HaveCount(2);
        results[0].IsFinal.Should().BeFalse();
        results[0].Transcript.Should().Be("hola");
        results[1].IsFinal.Should().BeTrue();
        results[1].Transcript.Should().Be("hola mundo");
    }

    [Fact]
    public async Task StreamAsync_ShouldYieldFinalTranscript_WithConfidence()
    {
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(CartesiaFakeServer.BuildTranscriptJson("prueba", 0.88f, isFinal: true));

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().ContainSingle(r => r.IsFinal && Math.Abs(r.Confidence - 0.88f) < 0.001f);
    }

    /// <summary>
    /// Door 3 (<c>ADR-0050</c> E2c), and the inverse of what this test used to assert. Under
    /// <c>NotThrowAsync</c> a socket killed mid-session ended the transcript stream exactly as the
    /// vendor's own <c>done</c> does.
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
    /// Door 1 (<c>ADR-0050</c> E2a) with the frame this surface was measured producing, twelve runs out
    /// of twelve: the session opens, the vendor answers
    /// <c>{"type":"error","code":400,"message":"Missing sample_rate: …"}</c>, and the client used to
    /// deserialize it into its transcript DTO, see a type it does not want, and drop it. The fake closes
    /// normally here so the frame is the only failure signal in the session.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldThrowErrorFrameFailure_WhenTheServerRejectsTheSessionInBand()
    {
        _server.ErrorFrameJson =
            """{"type":"error","code":400,"message":"Missing sample_rate: expected an integer"}""";
        var recognizer = BuildRecognizer();

        var act = async () =>
            await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.ErrorFrame);
        failure.Code.Should().Be("400", "the vendor puts an HTTP-shaped code inside a WebSocket session");
        failure.Message.Should().Contain("Missing sample_rate");
    }

    /// <summary>
    /// Door 2 (<c>ADR-0050</c> E2b): the other half of the same measured rejection, driven alone. The
    /// vendor closed <c>1008</c> after the frame above, and either signal must be enough on its own —
    /// this client is the one whose sessions all ended this way while its suite stayed green.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldThrowCloseCodeFailure_WhenServerClosesAbnormally()
    {
        _server.EndSessionSilently = true;
        _server.CloseStatus = WebSocketCloseStatus.PolicyViolation;   // 1008, as measured
        _server.CloseStatusDescription = "Missing sample_rate";
        var recognizer = BuildRecognizer();

        var act = async () =>
            await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.CloseCode);
        failure.Code.Should().Be("1008");
        failure.Message.Should().Contain("Missing sample_rate");
    }

    /// <summary>
    /// D2 (<c>ADR-0050</c> E5) on the recognition side: the vendor accepted the upgrade, sent no message
    /// of any kind and closed normally. Nothing failed on the wire, so this is not a
    /// <see cref="SpeechProviderFailureException"/> — and it is not the healthy zero-transcript session
    /// below either.
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
        empty.Provider.Should().Be("Cartesia");
    }

    /// <summary>
    /// The recognition half of E5, and the asymmetry against synthesis that the rule turns on: a session
    /// carrying only a control frame produced no transcript and is nonetheless healthy — turn detection
    /// flushes on any trigger, so noise with no speech correctly yields nothing. Zero results must stay
    /// an empty list; only zero <em>messages</em> is a failure.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldYieldNothingWithoutFailing_WhenTheSessionCarriesOnlyControlFrames()
    {
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(CartesiaFakeServer.ReadFrame(CartesiaFakeServer.FlushDoneFrame));

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
        // fake server (see openspec/changes/archive/2026-07-05-stt-cancellation-test-fence).
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var recognizer = BuildRecognizer();

        var act = async () =>
            await recognizer.StreamAsync(
                    SttFrameGenerators.EndlessFrames(), AudioFormat.Slin16Mono8kHz, cts.Token)
                .ToListAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        _server.ReceivedFrameCount.Should().Be(0);
    }

    /// <summary>
    /// The terminator is a bare word, not JSON — the service answers JSON on this socket with
    /// <c>Expected one of: "finalize", "done", "close"</c>, which is how the accepted set was
    /// established. Asserting the exact bytes is the point: a well-meaning refactor that wrapped it
    /// in an object the way the other three clients do would be rejected by the vendor and pass
    /// against any fake that only checked "a text frame arrived".
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldSendTheDoneTerminator_WhenInputEnds()
    {
        var recognizer = BuildRecognizer();
        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();
        await SessionEndedAsync();

        _server.ReceivedTerminatorText.Should().Be("done");
    }

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
    [Fact]
    public async Task StreamAsync_ShouldAuthenticateTheUpgrade_WhenOpeningASession()
    {
        var sut = BuildRecognizer();
        await sut.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();
        await SessionEndedAsync();

        // Asking for the header by name is what makes a renamed field a failure rather than a
        // silence: this suite passed with every auth header in the layer renamed.
        _server.ReceivedApiKey.Should().Be(TestApiKey);
        _server.ReceivedApiVersion.Should().Be("2024-11-13");

        // The route now comes from BaseUri, so this is the path production asks for.
        new Uri($"ws://localhost{_server.RequestUri}").AbsolutePath.Should().Be("/stt/websocket");
    }

    private Task SessionEndedAsync() => _server.SessionCompleted.WaitAsync(TimeSpan.FromSeconds(10));

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> SingleFrame()
    {
        yield return new byte[320];
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _server.DisposeAsync();
    }
}
