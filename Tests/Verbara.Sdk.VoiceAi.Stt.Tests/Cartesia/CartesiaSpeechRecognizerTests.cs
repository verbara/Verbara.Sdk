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

    private CartesiaSpeechRecognizer BuildRecognizer(Action<CartesiaOptions>? configure = null)
    {
        var opts = new CartesiaOptions { ApiKey = "test-key" };
        configure?.Invoke(opts);
        return new CartesiaSpeechRecognizer(Options.Create(opts), fakeServerPort: _server.Port);
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

    [Fact]
    public async Task StreamAsync_ShouldSendStartConfig_WhenConnectionOpens()
    {
        var recognizer = BuildRecognizer();
        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.ReceivedJsonMessages.Should().NotBeEmpty();
        var init = _server.ReceivedJsonMessages[0];
        init.Should().Contain("\"type\":\"start\"");
        init.Should().Contain("\"model\":\"ink-whisper\"");
        init.Should().Contain("\"sample_rate\":8000");
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

    [Fact]
    public async Task StreamAsync_ShouldComplete_WhenServerAborts()
    {
        _server.AbortAfterSend = true;
        var recognizer = BuildRecognizer();
        var act = async () =>
            await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();
        await act.Should().NotThrowAsync();
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
