using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.Events;
using Verbara.Sdk.VoiceAi.Pipeline;
using Verbara.Sdk.VoiceAi.Stt.Deepgram;
using Verbara.Sdk.VoiceAi.Stt.Tests.Helpers;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.Deepgram;

/// <summary>
/// A Deepgram message is judged whole, and one the recognizer cannot read costs that message, never
/// the turn: a message the vendor fragments or that is larger than one read is assembled before it is
/// parsed; one that fails to parse and whose envelope says <c>"type": "Error"</c> is the provider failure
/// a readable error frame raises, carrying the raw frame; anything else unreadable is counted on
/// <c>stt.frames.unparseable</c>, tagged with the provider, and skipped.
/// </summary>
/// <remarks>
/// <para>
/// The counter is read through a <see cref="MeterListener"/>, never emitted by the test. The class is in
/// <see cref="SttProcessWideCounters"/> because that counter, and the pipeline's, are
/// process-wide: no other test moves them while one of these runs.
/// </para>
/// <para>
/// Every recognizer here runs its bounds on a <see cref="FakeTimeProvider"/>, so nothing waits on the
/// wall clock; the fake closes each session, and the only timed waits are liveness bounds.
/// </para>
/// </remarks>
[Collection(SttProcessWideCounters.Name)]
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class DeepgramUnparseableMessageTests : IAsyncLifetime
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The recognizer's end-of-input silence bound.</summary>
    private static readonly TimeSpan EndOfInputBound = TimeSpan.FromSeconds(10);

    /// <summary>A recorded final <c>Results</c> frame whose <c>is_final</c> is <c>null</c>: it fails to deserialize and names no failure.</summary>
    private static readonly string NullIsFinalFrame = WithIsFinalNull(DeepgramFakeServer.BuildResultJson("lost", 0.9f, isFinal: true));

    /// <summary>An <c>Error</c> frame whose <c>description</c> is an object: it fails to deserialize, and its envelope names the failure.</summary>
    private const string UnreadableErrorFrame =
        """{"type":"Error","description":{"code":"DATA-0000","detail":"deepgram did not receive audio data"},"message":"NET-0001"}""";

    private readonly DeepgramFakeServer _server = new();
    private readonly FakeTimeProvider _clock = new();
    private readonly UnparseableFrames _unparseable = new();

    public DeepgramUnparseableMessageTests() => _server.Start();

    /// <summary>(a) A transcript sent in 16-byte fragments is one result with the whole transcript.</summary>
    [Fact]
    public async Task StreamAsync_ShouldYieldOneWholeResult_WhenTheTranscriptMessageArrivesInFragments()
    {
        // Arrange
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(DeepgramFakeServer.BuildResultJson("hola mundo desde muy lejos", 0.95f, isFinal: true));
        _server.TextFrameFragmentBytes = 16;

        // Act
        var results = await BuildRecognizer().StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        // Assert
        results.Should().ContainSingle().Which.Transcript.Should().Be("hola mundo desde muy lejos");
        _unparseable.Deepgram.Should().Be(0, "a fragmented message is one readable message, not unparseable pieces");
    }

    /// <summary>(b) One transcript message larger than the client's 64 KiB receive buffer is delivered whole.</summary>
    [Fact]
    public async Task StreamAsync_ShouldYieldTheWholeTranscript_WhenOneMessageIsLargerThanTheReceiveBuffer()
    {
        // Arrange
        var transcript = string.Join(' ', Enumerable.Repeat("palabra", 10_000));
        var message = DeepgramFakeServer.BuildResultJson(transcript, 0.95f, isFinal: true);
        System.Text.Encoding.UTF8.GetByteCount(message).Should().BeGreaterThan(64 * 1024, "the message must outgrow one read");
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(message);

        // Act
        var results = await BuildRecognizer().StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        // Assert
        results.Should().ContainSingle().Which.Transcript.Should().Be(transcript);
        _unparseable.Deepgram.Should().Be(0);
    }

    /// <summary>
    /// (c) A message that fails to deserialize and names no failure, then a good one: no fault, the good
    /// result delivered, the unreadable one counted once with the provider named.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldCountSkipAndDeliverTheNextResult_WhenAMessageFailsToDeserializeAndNamesNoFailure()
    {
        // Arrange
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(NullIsFinalFrame);
        _server.ResultMessages.Add(DeepgramFakeServer.BuildResultJson("hola mundo", 0.95f, isFinal: true));

        // Act
        var results = await BuildRecognizer().StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        // Assert
        results.Should().ContainSingle().Which.Transcript.Should().Be("hola mundo");
        _unparseable.Deepgram.Should().Be(1, "the unreadable message is counted once, tagged voiceai.provider=Deepgram");
    }

    /// <summary>
    /// (d) An <c>Error</c> frame the recognizer cannot read is the failure a readable one raises: the
    /// error-frame signal, no code (the readable branch passes none) and the raw frame in the message.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldThrowErrorFrameFailureCarryingTheRawFrame_WhenAnErrorFrameCannotBeRead()
    {
        // Arrange
        _server.ErrorFrameJson = UnreadableErrorFrame;

        // Act
        var act = async () => await BuildRecognizer().StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        // Assert
        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.ErrorFrame);
        failure.Code.Should().BeNull("the readable error branch passes no code either");
        failure.Message.Should().Contain(UnreadableErrorFrame, "the member that failed may be the one holding the message");
        _unparseable.Deepgram.Should().Be(0, "a named failure is reported, not counted");
    }

    /// <summary>(e) The unreadable <c>Error</c> frame of (d), sent in fragments: the same failure, judged on the whole message.</summary>
    [Fact]
    public async Task StreamAsync_ShouldThrowErrorFrameFailureCarryingTheRawFrame_WhenAnUnreadableErrorFrameArrivesInFragments()
    {
        // Arrange
        _server.ErrorFrameJson = UnreadableErrorFrame;
        _server.TextFrameFragmentBytes = 16;

        // Act
        var act = async () => await BuildRecognizer().StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        // Assert
        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.ErrorFrame);
        failure.Code.Should().BeNull();
        failure.Message.Should().Contain(UnreadableErrorFrame);
        _unparseable.Deepgram.Should().Be(0);
    }

    /// <summary>(f) A text message that is not JSON at all names no failure: counted, skipped, the transcript after it delivered.</summary>
    [Fact]
    public async Task StreamAsync_ShouldCountSkipAndDeliverTheNextResult_WhenATextMessageIsNotJson()
    {
        // Arrange
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add("upstream timeout, retry later");
        _server.ResultMessages.Add(DeepgramFakeServer.BuildResultJson("hola mundo", 0.95f, isFinal: true));

        // Act
        var results = await BuildRecognizer().StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        // Assert
        results.Should().ContainSingle().Which.Transcript.Should().Be("hola mundo");
        _unparseable.Deepgram.Should().Be(1);
    }

    /// <summary>
    /// (g) A session whose every message was skipped heard the vendor: it completes with no results and
    /// no exception, as a session of readable non-transcript messages does — not the empty-session failure.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldCompleteWithNoResults_WhenEveryMessageTheVendorSentWasSkipped()
    {
        // Arrange
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(NullIsFinalFrame);
        _server.ResultMessages.Add("not json");
        _server.AnswerTerminatorWithMetadata = false;

        // Act
        var act = async () => await BuildRecognizer().StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        // Assert
        var results = (await act.Should().NotThrowAsync()).Subject;
        results.Should().BeEmpty();
        _unparseable.Deepgram.Should().Be(2);
    }

    /// <summary>(i) Control: readable messages only — green on the unfixed code, and the counter does not move.</summary>
    [Fact]
    public async Task StreamAsync_ShouldYieldEveryResultAndCountNothing_WhenEveryMessageIsReadable()
    {
        // Arrange
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(DeepgramFakeServer.ReadFrame(DeepgramFakeServer.MetadataFrame));
        _server.ResultMessages.Add(DeepgramFakeServer.BuildResultJson("hola", 0.8f, isFinal: false));
        _server.ResultMessages.Add(DeepgramFakeServer.BuildResultJson("hola mundo", 0.95f, isFinal: true));

        // Act
        var results = await BuildRecognizer().StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        // Assert
        results.Select(r => r.Transcript).Should().Equal("hola", "hola mundo");
        _unparseable.Deepgram.Should().Be(0);
    }

    /// <summary>
    /// (j) The turn behind <see cref="VoiceAiPipeline"/>: an unparseable message ahead of the final
    /// transcript costs that message, not the turn — the handler answers from the transcript and no
    /// transcription is counted as failed.
    /// </summary>
    [Fact]
    public async Task HandleSessionAsync_ShouldAnswerTheTurn_WhenAnUnparseableMessagePrecedesTheTranscript()
    {
        // Arrange
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(NullIsFinalFrame);
        _server.ResultMessages.Add(DeepgramFakeServer.BuildResultJson("hola mundo", 0.95f, isFinal: true));
        using var meters = new SttPipelineMuteVendorTests.VoiceAiMeters();
        var handler = new RecordingHandler();

        var services = new ServiceCollection();
        services.AddSingleton<IConversationHandler>(handler);
        services.AddSingleton<ITurnDetector>(new SttPipelineMuteVendorTests.OneUtteranceDetector());
        await using var provider = services.BuildServiceProvider();
        await using var pipeline = new VoiceAiPipeline(
            BuildRecognizer(),
            new SttPipelineMuteVendorTests.OneChunkSynthesizer(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new VoiceAiPipelineOptions()),
            NullLogger<VoiceAiPipeline>.Instance);
        using var errors = new SttPipelineMuteVendorTests.PipelineErrors(pipeline);
        using var settled = new TurnSettled(pipeline);
        await using var call = await SttPipelineMuteVendorTests.AudioSocketCall.StartAsync();

        var sessionTask = pipeline.HandleSessionAsync(call.Session, CancellationToken.None).AsTask();
        for (var i = 0; i < 3; i++)
            await call.Caller.SendAudioAsync(new byte[320]);

        // Act — the turn settles one way or the other: answered, or lost to a recognition error.
        var outcome = await settled.Task.WaitAsync(SignalTimeout);
        await call.HangUpAsync();
        var fault = await Record.ExceptionAsync(() => sessionTask.WaitAsync(SignalTimeout));

        // Assert
        using (new AssertionScope())
        {
            outcome.Should().BeOfType<ResponseGeneratedEvent>(
                "the turn is answered from the transcript, not lost to the message before it");
            errors.Snapshot.Should().BeEmpty("no recognition error is published");
            handler.Transcripts.Should().Equal("hola mundo");
            meters.Count("stt.transcriptions.failed").Should().Be(0);
            meters.Count("stt.transcriptions.completed").Should().Be(1);
            _unparseable.Deepgram.Should().Be(1);
            fault.Should().BeNull();
        }
    }

    public static TheoryData<string> PartialMessageEndings => ["PeerCloses", "TransportAborts", "CallerCancels", "SilenceBoundExpires"];

    /// <summary>
    /// (k1)–(k4) A message whose final fragment never arrives. One whole message first, so the vendor has
    /// been heard and the session's ending is decided by the rule that already governs it; then a
    /// non-final fragment, read by the recognizer, and the socket ends before the rest: the peer closes,
    /// the transport aborts, the caller cancels, or the end-of-input bound runs out during the partial
    /// read. The partial bytes are discarded: no result from them, nothing counted, and the ending is
    /// the one the same session has with no partial message.
    /// </summary>
    /// <remarks>
    /// The fragment is let in only once the bound has armed, so its read restarts the armed bound and
    /// publishes on <see cref="FakeTimeProvider.TimersArmed"/>: that is the receipt the test waits on
    /// before it ends the socket. The audio source ends only after the whole message reached the caller,
    /// so that message's read cannot be the one that restarts the bound.
    /// <para>
    /// The ending is asserted twice: as the caller sees it, and as the receive loop ended the result
    /// channel, read through <see cref="DeepgramSpeechRecognizer.ResultReader"/>. The second is what keeps
    /// the cancel row honest: a caller's cancel decides the caller's view on its own, so a loop that
    /// failed on the partial bytes can still look cancelled from there; the channel shows whether the
    /// loop left by the cancel, as it does with no partial message, or by failing on the fragment.
    /// </para>
    /// <para>
    /// The caller holds its first result until the loop's ending has been read. While it holds, the
    /// iterator is parked at its <c>yield return</c>: no read of the result channel is pending on the
    /// caller's token and nothing unwinds to dispose the socket, so the receive loop alone decides how
    /// the channel ends. Without the hold a cancel reaches the iterator's pending read as well, and when
    /// that side unwinds first it disposes the socket under the loop's read, which then leaves as a
    /// transport failure instead of on the cancel — which of the two wins is a matter of timing, with
    /// or without a partial message.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(PartialMessageEndings))]
    public async Task StreamAsync_ShouldDiscardThePartialMessageAndEndAsWithoutIt_WhenTheSocketEndsBeforeItsFinalFragment(string ending)
    {
        // Arrange
        var whole = DeepgramFakeServer.BuildResultJson("hola", 0.8f, isFinal: false);
        var withheld = new WithheldFinalFragment(DeepgramFakeServer.BuildResultJson("hola mundo", 0.95f, isFinal: true));
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(whole);
        _server.WithholdFinalFragment = withheld;
        var wholeSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var recognizer = BuildRecognizer();
        var channel = new TaskCompletionSource<System.Threading.Channels.ChannelReader<SpeechRecognitionResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        recognizer.ResultReader = reader =>
        {
            channel.TrySetResult(reader);
            return reader;
        };
        var results = new List<SpeechRecognitionResult>();
        var callerHolds = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var stream = Task.Run(async () =>
        {
            await foreach (var result in recognizer.StreamAsync(AudioUntil(wholeSeen.Task), AudioFormat.Slin16Mono8kHz, cts.Token))
            {
                results.Add(result);
                wholeSeen.TrySetResult();
                await callerHolds.Task;            // the iterator stays parked at its yield until the loop's ending is read
            }
        });

        Exception? loopEnding;
        try
        {
            await NextArmAsync(EndOfInputBound);   // the terminator is out and the bound armed
            withheld.SendFirstFragment();
            await NextArmAsync(EndOfInputBound);   // the fragment was read: it restarted the bound

            // Act
            switch (ending)
            {
                case "PeerCloses": withheld.Close(); break;
                case "TransportAborts": withheld.Abort(); break;
                case "CallerCancels": await cts.CancelAsync(); break;
                case "SilenceBoundExpires": _clock.Advance(EndOfInputBound); break;
                default: throw new ArgumentOutOfRangeException(nameof(ending), ending, null);
            }

            var reader = await channel.Task.WaitAsync(SignalTimeout);
            loopEnding = await Record.ExceptionAsync(() => reader.Completion.WaitAsync(SignalTimeout));
        }
        finally
        {
            callerHolds.TrySetResult();
        }

        var fault = await Record.ExceptionAsync(() => stream.WaitAsync(SignalTimeout));

        // Assert
        using (new AssertionScope())
        {
            results.Select(r => r.Transcript).Should().Equal(["hola"], "the partial message yields no result");
            _unparseable.Deepgram.Should().Be(0, "a partial message is discarded, not counted");
            switch (ending)
            {
                case "PeerCloses":
                    fault.Should().BeNull("a normal close after a heard vendor completes the session");
                    loopEnding.Should().BeNull("the loop ends the channel cleanly at a normal close");
                    break;
                case "TransportAborts":
                    fault.Should().BeOfType<SpeechProviderFailureException>()
                        .Which.Signal.Should().Be(SpeechProviderFailureSignal.Transport);
                    loopEnding.Should().BeOfType<SpeechProviderFailureException>()
                        .Which.Signal.Should().Be(SpeechProviderFailureSignal.Transport);
                    break;
                case "CallerCancels":
                    fault.Should().BeAssignableTo<OperationCanceledException>("cancellation is the caller's own instruction");
                    loopEnding.Should().BeNull("the loop leaves on the caller's cancel and ends the channel cleanly");
                    break;
                default:
                    fault.Should().BeOfType<SpeechProviderFailureException>()
                        .Which.InnerException.Should().BeOfType<TimeoutException>("the bound, not the vendor, ended the wait");
                    loopEnding.Should().BeOfType<SpeechProviderFailureException>()
                        .Which.InnerException.Should().BeOfType<TimeoutException>();
                    break;
            }
        }
    }

    /// <summary>
    /// A session whose only vendor traffic is a message that never received its final fragment heard no
    /// message: the partial bytes are discarded, so the normal close that follows is the empty session,
    /// reported as it is when the vendor sent nothing at all — not a completed one.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldReportTheEmptySession_WhenTheOnlyVendorMessageNeverReceivesItsFinalFragment()
    {
        // Arrange
        var withheld = new WithheldFinalFragment(DeepgramFakeServer.BuildResultJson("hola mundo", 0.95f, isFinal: true));
        _server.ResultMessages.Clear();
        _server.WithholdFinalFragment = withheld;
        var stream = BuildRecognizer().StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync().AsTask();

        await NextArmAsync(EndOfInputBound);   // the terminator is out and the bound armed
        withheld.SendFirstFragment();
        await NextArmAsync(EndOfInputBound);   // the fragment was read: it restarted the bound

        // Act
        withheld.Close();
        var fault = await Record.ExceptionAsync(() => stream.WaitAsync(SignalTimeout));

        // Assert
        fault.Should().BeOfType<SpeechProviderEmptyResultException>(
            "a fragment whose message never completed is not a message the vendor sent");
        _unparseable.Deepgram.Should().Be(0, "a partial message is discarded, not counted");
    }

    private DeepgramSpeechRecognizer BuildRecognizer() => new(Options.Create(new DeepgramOptions
    {
        ApiKey = "test-key",
        BaseUri = $"ws://127.0.0.1:{_server.Port}/v1/listen",
    }))
    {
        TimeProvider = _clock,
    };

    /// <summary>Waits until the recognizer arms a timer of <paramref name="due"/> on the fake clock.</summary>
    private async Task NextArmAsync(TimeSpan due)
    {
        while (await _clock.TimersArmed.ReadAsync().AsTask().WaitAsync(SignalTimeout) != due)
        {
            // Another bound (the connect bound) — not the one this waits for.
        }
    }

    private static string WithIsFinalNull(string frame)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(frame)!;
        node["is_final"] = null;
        return node.ToJsonString();
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> SingleFrame()
    {
        yield return new byte[320];
        await Task.CompletedTask;
    }

    /// <summary>One frame, then the end of input once <paramref name="until"/> completes.</summary>
    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> AudioUntil(Task until)
    {
        yield return new byte[320];
        await until.WaitAsync(SignalTimeout);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _unparseable.Dispose();
        await _server.DisposeAsync();
    }

    /// <summary>
    /// <c>stt.frames.unparseable</c> on the <c>Verbara.Sdk.VoiceAi.Stt</c> meter, summed for
    /// <c>voiceai.provider=Deepgram</c>. Zero while no such instrument exists.
    /// </summary>
    private sealed class UnparseableFrames : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _deepgram;

        public UnparseableFrames()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Verbara.Sdk.VoiceAi.Stt" && instrument.Name == "stt.frames.unparseable")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "voiceai.provider" && Equals(tag.Value, "Deepgram"))
                        Interlocked.Add(ref _deepgram, measurement);
                }
            });
            _listener.Start();
        }

        public long Deepgram => Interlocked.Read(ref _deepgram);

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>Completes on the first sign the turn settled: an answer, or a pipeline error.</summary>
    private sealed class TurnSettled : IObserver<VoiceAiPipelineEvent>, IDisposable
    {
        private readonly TaskCompletionSource<VoiceAiPipelineEvent> _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly IDisposable _subscription;

        public TurnSettled(VoiceAiPipeline pipeline) => _subscription = pipeline.Events.Subscribe(this);

        public Task<VoiceAiPipelineEvent> Task => _settled.Task;

        public void OnNext(VoiceAiPipelineEvent value)
        {
            if (value is ResponseGeneratedEvent or PipelineErrorEvent)
                _settled.TrySetResult(value);
        }

        public void OnError(Exception error)
        {
            // The pipeline's subject never faults; nothing to settle.
        }

        public void OnCompleted()
        {
            // Disposal of the pipeline; the outcome, if any, is already recorded.
        }

        public void Dispose() => _subscription.Dispose();
    }

    private sealed class RecordingHandler : IConversationHandler
    {
        private readonly List<string> _transcripts = [];
        private readonly Lock _gate = new();

        public IReadOnlyList<string> Transcripts
        {
            get
            {
                lock (_gate)
                    return [.. _transcripts];
            }
        }

        public ValueTask<string> HandleAsync(string transcript, ConversationContext context, CancellationToken ct = default)
        {
            lock (_gate)
                _transcripts.Add(transcript);
            return ValueTask.FromResult("ok");
        }
    }
}
