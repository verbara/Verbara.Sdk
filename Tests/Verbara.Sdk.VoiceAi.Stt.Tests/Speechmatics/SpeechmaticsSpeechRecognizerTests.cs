using System.Text.Json;
using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.Stt.Speechmatics;
using Verbara.Sdk.VoiceAi.Stt.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.Speechmatics;

/// <summary>
/// Transport: WebSocket. Deliberately NOT migrated to the WireMock substrate — WireMock.NET matches
/// HTTP/1.1 requests and cannot hold the duplex session these tests drive (ADR-0041 D2), so
/// <c>SpeechmaticsFakeServer</c> on <c>WebSocketTestServer</c> stays. Fidelity here comes from the
/// frames in <c>Recordings/speechmatics-stt/</c> (D4), not from a different server. Speechmatics STT
/// is <c>permitted</c> for capturing Output (<c>docs/guides/provider-recording-protocol.md</c> §7 —
/// ToS §10.3 assigns the customer all IP in Transcripts), so — unlike Deepgram and AssemblyAI — its
/// terms are not what stands between this suite and a real capture, and as of 2026-08-16 neither is a
/// missing credential: a working one exists and has opened a live session against this surface, which
/// streamed no audio and so elicited no transcript frame. What is missing is a capture run, which
/// makes a real capture a demonstrably reachable upgrade path. Meanwhile the frames take
/// §7's documentation-derived route, <c>class: "synthetic"</c> with a <c>source_schema</c> block.
/// That closes the field-set half of the D4 gap and not the drift half. The Speechmatics <em>TTS</em>
/// suite is a separate, HTTP-transport surface and does migrate (§4.5).
/// </summary>
public class SpeechmaticsSpeechRecognizerTests : IAsyncDisposable
{
    private readonly SpeechmaticsFakeServer _server;

    public SpeechmaticsSpeechRecognizerTests()
    {
        _server = new SpeechmaticsFakeServer();
        _server.Start();
    }

    /// <summary>
    /// Build a recognizer pointed at this test's fake server through <c>BaseUri</c> — the same
    /// option an operator sets to pick a region — rather than through a test-only constructor.
    /// </summary>
    /// <remarks>
    /// There used to be an <c>internal</c> constructor taking a fake port, and
    /// <c>SpeechmaticsSpeechRecognizer.BuildUri</c> branched on it. The consequence was that the
    /// production URI expression was executed by no test at all: every assertion about the session
    /// URL — including "the credential is not in it" — was made against a line that only ever ran
    /// under test. Configuring <c>BaseUri</c> costs nothing (its own validation admits <c>ws://</c>)
    /// and makes these tests exercise the expression that ships.
    /// </remarks>
    private SpeechmaticsSpeechRecognizer BuildRecognizer(Action<SpeechmaticsOptions>? configure = null)
    {
        var opts = new SpeechmaticsOptions
        {
            ApiKey = "test-key",
            BaseUri = $"ws://127.0.0.1:{_server.Port}/v2",
        };
        configure?.Invoke(opts);
        return new SpeechmaticsSpeechRecognizer(Options.Create(opts));
    }

    /// <summary>
    /// The transcript the SDK is expected to build out of a recorded frame: every result's first
    /// alternative, joined with a single space, which is exactly what
    /// <c>SpeechmaticsSpeechRecognizer</c> does. Derived from the recording rather than hard-coded,
    /// so the two independent readers must agree on the frame's bytes.
    /// </summary>
    private static string RecordedJoinedTranscript(string frame)
    {
        using var document = JsonDocument.Parse(SpeechmaticsFakeServer.ReadFrame(frame));
        var parts = document.RootElement.GetProperty("results").EnumerateArray()
            .Select(result => result.GetProperty("alternatives")[0].GetProperty("content").GetString()!);
        return string.Join(' ', parts);
    }

    /// <summary>The already-assembled segment text the vendor publishes at <c>metadata.transcript</c>.</summary>
    private static string RecordedVendorTranscript(string frame)
    {
        using var document = JsonDocument.Parse(SpeechmaticsFakeServer.ReadFrame(frame));
        return document.RootElement.GetProperty("metadata").GetProperty("transcript").GetString()!;
    }

    [Fact]
    public async Task StreamAsync_ShouldYieldRecordedTranscripts_WhenReplayingDocumentedFrames()
    {
        // The default seed is the two recorded frames verbatim: partial then final.
        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        var partial = RecordedJoinedTranscript(SpeechmaticsFakeServer.PartialTranscriptFrame);
        var final = RecordedJoinedTranscript(SpeechmaticsFakeServer.FinalTranscriptFrame);
        partial.Should().NotBeNullOrWhiteSpace("a frame that transcribes to nothing asserts nothing");
        final.Should().NotBeNullOrWhiteSpace("a frame that transcribes to nothing asserts nothing");

        results.Should().HaveCount(2);
        results[0].IsFinal.Should().BeFalse();
        results[0].Transcript.Should().Be(partial);
        results[1].IsFinal.Should().BeTrue();
        results[1].Transcript.Should().Be(final);
    }

    [Fact]
    public async Task StreamAsync_ShouldTolerateUnmodelledSiblingFields_WhenFrameCarriesFullDocumentedFieldSet()
    {
        // The point of the recording. SpeechmaticsTranscriptMessage models three values — message,
        // and each result's first alternative's content and confidence — out of everything
        // Speechmatics documents. The first block fences the fixture: reduce it back to the old
        // message + results[].alternatives[] shape and this fails loudly instead of silently taking
        // the assertion below with it.
        using (var document = JsonDocument.Parse(SpeechmaticsFakeServer.ReadFrame(SpeechmaticsFakeServer.FinalTranscriptFrame)))
        {
            var root = document.RootElement;
            foreach (var unmodelled in new[] { "format", "metadata", "forced" })
            {
                root.TryGetProperty(unmodelled, out _)
                    .Should().BeTrue("the recorded frame must carry '{0}', which the SDK does not model", unmodelled);
            }

            var metadata = root.GetProperty("metadata");
            foreach (var metadataField in new[] { "start_time", "end_time", "transcript" })
            {
                metadata.TryGetProperty(metadataField, out _)
                    .Should().BeTrue("metadata must carry '{0}' — the SDK reads none of it", metadataField);
            }

            var results = root.GetProperty("results");
            results.GetArrayLength().Should().BeGreaterThan(1, "a single-token stream cannot expose the join behaviour");
            foreach (var resultField in new[] { "type", "start_time", "end_time", "is_eos" })
            {
                results[0].TryGetProperty(resultField, out _)
                    .Should().BeTrue("a recorded result must carry '{0}'", resultField);
            }

            var alternative = results[0].GetProperty("alternatives")[0];
            foreach (var alternativeField in new[] { "language", "display", "speaker", "tags" })
            {
                alternative.TryGetProperty(alternativeField, out _)
                    .Should().BeTrue("a recorded alternative must carry '{0}'", alternativeField);
            }

            // The punctuation token is load-bearing for the divergence test below.
            var last = results[results.GetArrayLength() - 1];
            last.GetProperty("type").GetString().Should().Be("punctuation");
            last.TryGetProperty("attaches_to", out _).Should().BeTrue();
        }

        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(SpeechmaticsFakeServer.ReadFrame(SpeechmaticsFakeServer.FinalTranscriptFrame));

        var recognizer = BuildRecognizer();
        var yielded = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        yielded.Should().ContainSingle()
            .Which.Transcript.Should().Be(RecordedJoinedTranscript(SpeechmaticsFakeServer.FinalTranscriptFrame));
    }

    [Fact]
    public async Task StreamAsync_ShouldSpaceJoinTokens_WhenFrameCarriesPunctuationAttachedToPrevious()
    {
        // The finding this migration surfaced, asserted as the behaviour it is rather than fixed
        // here — no src/** file is in this change's scope. Speechmatics publishes the assembled
        // segment at metadata.transcript, and publishes a word_delimiter on RecognitionStarted;
        // SpeechmaticsSpeechRecognizer reads neither and space-joins the token stream, so a
        // punctuation result with attaches_to "previous" gains a space the vendor's own text does
        // not have. A single-token fake could never show this.
        var vendorText = RecordedVendorTranscript(SpeechmaticsFakeServer.FinalTranscriptFrame);
        var sdkText = RecordedJoinedTranscript(SpeechmaticsFakeServer.FinalTranscriptFrame);
        sdkText.Should().NotBe(vendorText, "the recording must actually exercise the divergence");
        sdkText.Replace(" ", string.Empty).Should().Be(vendorText.Replace(" ", string.Empty),
            "the two differ only in whitespace — the tokens themselves agree");

        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(SpeechmaticsFakeServer.ReadFrame(SpeechmaticsFakeServer.FinalTranscriptFrame));

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().ContainSingle().Which.Transcript.Should().Be(sdkText);
    }

    [Fact]
    public async Task StreamAsync_ShouldSendStartRecognition_WithAudioAndTranscriptionConfig()
    {
        _server.ResultMessages.Clear();
        var recognizer = BuildRecognizer(o =>
        {
            o.Language = "es";
            o.OperatingPoint = "enhanced";
            o.EnablePartials = true;
            o.MaxDelaySeconds = 2;
            o.SampleRate = 16000;
        });

        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.ReceivedStartRecognitionJson.Should().NotBeNullOrEmpty();
        var json = _server.ReceivedStartRecognitionJson!;
        json.Should().Contain("\"message\":\"StartRecognition\"");
        json.Should().Contain("\"encoding\":\"pcm_s16le\"");
        json.Should().Contain("\"language\":\"es\"");
        json.Should().Contain("\"operating_point\":\"enhanced\"");
        json.Should().Contain("\"enable_partials\":true");
        // The request-target is the language pack and nothing else — the credential is a header now.
        _server.ReceivedRequestUri.Should().NotBeNullOrEmpty();
        _server.ReceivedRequestUri!.Should().EndWith("/v2/es");
    }

    [Fact]
    public async Task StreamAsync_ShouldAuthenticateWithABearerHeader_WhenOpeningTheSession()
    {
        // The defect this replaces: the key travelled as ?jwt=, which the service answers by
        // upgrading (101) and then closing 4001 not_authorised — so the whole provider was unusable
        // and this suite was green anyway, because the fake never looked at the credential.
        // Measured 2026-08-15: the same key in this header reached RecognitionStarted.
        _server.ResultMessages.Clear();
        var recognizer = BuildRecognizer();

        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.ReceivedAuthorizationHeader.Should().Be("Bearer test-key");
    }

    [Fact]
    public async Task StreamAsync_ShouldKeepTheCredentialOutOfTheUrl_WhenOpeningTheSession()
    {
        // Separate from the header assertion on purpose: sending the header while still sending
        // ?jwt= would satisfy that one and leave the long-lived key in a request-target that lands
        // in every proxy and access log along the way.
        _server.ResultMessages.Clear();
        var recognizer = BuildRecognizer(o => o.ApiKey = "sk-not-a-real-key");

        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.ReceivedRequestUri.Should().NotBeNullOrEmpty();
        _server.ReceivedRequestUri!.Should().NotContain("jwt");
        _server.ReceivedRequestUri!.Should().NotContain("sk-not-a-real-key");
        _server.ReceivedAuthorizationHeader.Should().Be("Bearer sk-not-a-real-key");
    }

    [Fact]
    public async Task StreamAsync_ShouldYieldPartialTranscript_WhenAddPartialTranscript()
    {
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(SpeechmaticsFakeServer.BuildPartialTranscriptJson("hola", 0.80f));

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().ContainSingle();
        results[0].Transcript.Should().Be("hola");
        results[0].IsFinal.Should().BeFalse();
        results[0].Confidence.Should().BeApproximately(0.80f, 0.01f);
    }

    [Fact]
    public async Task StreamAsync_ShouldYieldFinalTranscript_WhenAddTranscript()
    {
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(SpeechmaticsFakeServer.BuildFinalTranscriptJson("hola mundo", 0.99f));

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().ContainSingle();
        results[0].Transcript.Should().Be("hola mundo");
        results[0].IsFinal.Should().BeTrue();
        results[0].Confidence.Should().BeApproximately(0.99f, 0.01f);
    }

    [Fact]
    public async Task StreamAsync_ShouldIgnoreLifecycleMessages_WhenEndOfTranscript()
    {
        // Server sends RecognitionStarted automatically. Add only EndOfTranscript — no
        // AddPartialTranscript / AddTranscript — so we expect zero yielded results. Both lifecycle
        // frames are now the recorded ones, so the message filter is exercised against the shapes
        // Speechmatics documents — including RecognitionStarted's nested language_pack_info —
        // rather than against two-field placeholders.
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(SpeechmaticsFakeServer.BuildEndOfTranscriptJson());

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().BeEmpty();
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
    /// <c>last_seq_no</c> is compared against the count the fake kept independently, not against a
    /// literal: the client and the server have to agree on how many audio chunks crossed the
    /// socket, and a hard-coded 3 would still pass if both drifted together.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldSendEndOfStreamNumberedWithTheAudioChunkCount_WhenInputEnds()
    {
        var recognizer = BuildRecognizer();
        await recognizer.StreamAsync(ThreeFrames(), AudioFormat.Slin16Mono8kHz).ToListAsync();
        await SessionEndedAsync();

        using var terminator = JsonDocument.Parse(_server.ReceivedEndOfStreamJson!);
        terminator.RootElement.GetProperty("message").GetString().Should().Be("EndOfStream");
        terminator.RootElement.GetProperty("last_seq_no").GetInt32()
            .Should().Be(_server.ReceivedFrameCount).And.Be(3);
    }

    /// <summary>
    /// The load-bearing half of the pair. §3.6d streamed one utterance of ten spoken digits into
    /// this surface three ways: half-close alone returned 0/10 digits — twenty
    /// <c>AddPartialTranscript</c> messages, not one <c>AddTranscript</c>, and no
    /// <c>EndOfTranscript</c> — the terminator alone returned 10/10, and sending both returned 0/10
    /// again. So the half-close is not merely redundant here, it destroys the result even when the
    /// terminator precedes it. This test fails the moment a client sends it again.
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

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _server.DisposeAsync();
    }
}
