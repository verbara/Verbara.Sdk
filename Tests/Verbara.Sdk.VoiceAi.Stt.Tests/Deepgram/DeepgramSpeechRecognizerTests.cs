using System.Text.Json;
using Verbara.Sdk.Audio;
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
                .ToListAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        _server.ReceivedFrameCount.Should().Be(0);
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
