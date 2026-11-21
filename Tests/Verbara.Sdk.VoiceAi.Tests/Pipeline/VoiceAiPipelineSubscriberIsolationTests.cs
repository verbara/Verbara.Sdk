using System.Collections.Concurrent;
using System.Reactive.Linq;
using Verbara.Sdk.VoiceAi.AudioSocket;
using Verbara.Sdk.VoiceAi.Events;
using Verbara.Sdk.VoiceAi.Pipeline;
using Verbara.Sdk.VoiceAi.Testing;
using Verbara.Sdk.VoiceAi.Tests.Internal;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tests.Pipeline;

/// <summary>
/// A pipeline event subscriber's failure is its own: every other subscriber of <see cref="VoiceAiPipeline.Events"/>
/// still receives every event of the conversation, the session goes on and completes as it would with no failing
/// subscriber, and each failure is counted once on <c>voiceai.pipeline.subscriber_faults</c> and then logged once at
/// Warning. A logger that throws on that Warning changes none of it.
/// </summary>
/// <remarks>
/// The counter is read by name, so this class compiles on a tree without it. The class joins
/// <see cref="SessionCounterGroup"/>: the pipeline's counters are untagged process-wide statics.
/// </remarks>
[Collection(SessionCounterGroup.Name)]
public sealed class VoiceAiPipelineSubscriberIsolationTests
{
    private const string MeterName = "Verbara.Sdk.VoiceAi";
    private const string SubscriberFaults = "voiceai.pipeline.subscriber_faults";
    private const string SessionsFailed = "voiceai.sessions.failed";
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The events one utterance's whole cycle publishes, in order.</summary>
    private static readonly Type[] CycleEvents =
    [
        typeof(SpeechStartedEvent), typeof(SpeechEndedEvent), typeof(TranscriptReceivedEvent),
        typeof(ResponseGeneratedEvent), typeof(SynthesisStartedEvent), typeof(SynthesisEndedEvent),
    ];

    [Fact]
    public async Task HandleSessionAsync_ShouldDeliverEveryEventToTheOtherSubscriberAndCountAndLogEach_WhenTheFirstThrowsOnEveryEvent()
    {
        using var metrics = new MeterCapture(MeterName);
        var logger = new CapturingLogger();
        await using var pipeline = BuildPipeline(logger);
        var thrower = new ThrowingObserver();
        using var first = pipeline.Events.Subscribe(thrower);
        using var capture = new PipelineEventCapture(pipeline);

        var run = await RunSingleUtteranceAsync(pipeline, capture);

        var warnings = logger.SubscriberWarnings;
        using var scope = new AssertionScope();
        run.Fault.Should().BeNull("a pipeline event subscriber's failure is its own: the session does not fail");
        run.CycleEnded.Should().BeTrue("the second subscriber receives the end of the utterance's cycle");
        capture.Events.Select(e => e.GetType()).Should().Equal(
            CycleEvents, "the second subscriber receives every event of the cycle, in order");
        thrower.Calls.Should().Be(CycleEvents.Length, "the first subscriber stays subscribed and is called on every event");
        metrics.Get(SubscriberFaults).Should().Be(CycleEvents.Length, "one count per failure");
        warnings.Should().HaveCount(CycleEvents.Length, "one Warning per failure");
        warnings.Should().OnlyContain(w => w.Exception is InvalidOperationException && w.Exception.Message == "event-handler",
            "each Warning carries the subscriber's exception");
        warnings.Select(w => w.Message).Should().Contain(m => m.Contains(nameof(SpeechStartedEvent), StringComparison.Ordinal),
            "the Warning names the event the subscriber threw on");
        metrics.Get(SessionsFailed).Should().Be(0, "the session is not counted failed");
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldDeliverEveryEventAndCount_WhenTheLoggerThrowsOnTheReportToo()
    {
        using var metrics = new MeterCapture(MeterName);
        var logger = new CapturingLogger { ThrowOnSubscriberReport = true };
        await using var pipeline = BuildPipeline(logger);
        using var first = pipeline.Events.Subscribe(new ThrowingObserver());
        using var capture = new PipelineEventCapture(pipeline);

        var run = await RunSingleUtteranceAsync(pipeline, capture);

        using var scope = new AssertionScope();
        run.Fault.Should().BeNull("a failing report changes nothing: the session goes on");
        capture.Events.Select(e => e.GetType()).Should().Equal(CycleEvents,
            "the second subscriber receives every event whatever the logger throws");
        metrics.Get(SubscriberFaults).Should().Be(CycleEvents.Length, "the fault is counted before the log is attempted");
        metrics.Get(SessionsFailed).Should().Be(0);
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldEndOnlyTheComposedSubscriptionAndCountItOnce_WhenAHandlerBehindWhereThrows()
    {
        using var metrics = new MeterCapture(MeterName);
        var logger = new CapturingLogger();
        await using var pipeline = BuildPipeline(logger);
        var composedCalls = 0;
        using var composed = pipeline.Events.Where(_ => true).Subscribe(_ =>
        {
            Interlocked.Increment(ref composedCalls);
            throw new InvalidOperationException("composed-handler");
        });
        using var capture = new PipelineEventCapture(pipeline);

        var run = await RunSingleUtteranceAsync(pipeline, capture);

        using var scope = new AssertionScope();
        run.Fault.Should().BeNull("the publisher throws nothing");
        Volatile.Read(ref composedCalls).Should().Be(1, "the operator ends its subscription on the handler's first exception");
        metrics.Get(SubscriberFaults).Should().Be(1, "that one failure is counted once");
        logger.SubscriberWarnings.Should().ContainSingle("and logged once");
        capture.Events.Select(e => e.GetType()).Should().Equal(CycleEvents, "the direct subscriber receives every event");
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldCountAndLogNothing_WhenNoSubscriberThrows()
    {
        using var metrics = new MeterCapture(MeterName);
        var logger = new CapturingLogger();
        await using var pipeline = BuildPipeline(logger);
        using var capture = new PipelineEventCapture(pipeline);

        var run = await RunSingleUtteranceAsync(pipeline, capture);

        using var scope = new AssertionScope();
        run.Fault.Should().BeNull();
        capture.Events.Select(e => e.GetType()).Should().Equal(CycleEvents);
        metrics.Get(SubscriberFaults).Should().Be(0, "no subscriber failed");
        logger.SubscriberWarnings.Should().BeEmpty();
    }

    [Fact]
    public async Task DisposeAsync_ShouldCompleteTheOtherSubscriberAndCountAndLogOnce_WhenASubscriberThrowsOnTheCompletion()
    {
        using var metrics = new MeterCapture(MeterName);
        var logger = new CapturingLogger();
        var pipeline = BuildPipeline(logger);
        var thrower = new ThrowingObserver();
        using var first = pipeline.Events.Subscribe(thrower);
        var completions = 0;
        using var second = pipeline.Events.Subscribe(_ => { }, () => Interlocked.Increment(ref completions));

        var disposing = await Record.ExceptionAsync(() => pipeline.DisposeAsync().AsTask());

        using var scope = new AssertionScope();
        disposing.Should().BeNull("a pipeline event subscriber's failure is its own: DisposeAsync returns normally");
        Volatile.Read(ref completions).Should().Be(1, "the second subscriber receives the completion");
        metrics.Get(SubscriberFaults).Should().Be(1);
        logger.SubscriberWarnings.Should().ContainSingle()
            .Which.Message.Should().Contain("completion", "the Warning names the completion as what the subscriber threw on");
    }

    [Fact]
    public async Task DisposeAsync_ShouldLetOutOfMemoryThrough_WhenASubscriberThrowsItOnTheCompletion()
    {
        using var metrics = new MeterCapture(MeterName);
        var pipeline = BuildPipeline(new CapturingLogger());
        using var first = pipeline.Events.Subscribe(_ => { }, () => throw OutOfMemory());

        var disposing = () => pipeline.DisposeAsync().AsTask();

        await disposing.Should().ThrowAsync<OutOfMemoryException>("the process cannot go on, so no guard hides it");
        metrics.Get(SubscriberFaults).Should().Be(0);
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldLetOutOfMemoryThrough_WhenASubscriberThrowsItOnAnEvent()
    {
        using var metrics = new MeterCapture(MeterName);
        await using var pipeline = BuildPipeline(new CapturingLogger());
        using var first = pipeline.Events.Subscribe(_ => throw OutOfMemory(), () => { });
        using var capture = new PipelineEventCapture(pipeline);

        var run = await RunSingleUtteranceAsync(pipeline, capture);

        using var scope = new AssertionScope();
        run.Fault.Should().BeOfType<OutOfMemoryException>("the process cannot go on, so no guard hides it");
        metrics.Get(SubscriberFaults).Should().Be(0, "an out-of-memory failure is not a subscriber fault");
    }

    [Fact]
    public async Task DisposeAsync_ShouldCountTheFaultBeforeTheWarning_WhenTheLoggerThrowsOutOfMemoryOnIt()
    {
        using var metrics = new MeterCapture(MeterName);
        var pipeline = BuildPipeline(new CapturingLogger { OutOfMemoryOnSubscriberReport = true });
        using var first = pipeline.Events.Subscribe(new ThrowingObserver());

        var disposing = () => pipeline.DisposeAsync().AsTask();

        await disposing.Should().ThrowAsync<OutOfMemoryException>("the report's guard does not hide an out-of-memory failure");
        metrics.Get(SubscriberFaults).Should().Be(1, "the fault is counted before the Warning is attempted");
    }

    [Fact]
    public async Task SubscriberGuard_ShouldCountAndLogOnceAndThrowNothing_WhenTheSubscriberThrowsOnAnError()
    {
        using var metrics = new MeterCapture(MeterName);
        var logger = new CapturingLogger();
        await using var pipeline = BuildPipeline(logger);
        var guard = new VoiceAiPipeline.SubscriberGuard(pipeline, new ErrorThrowingObserver());

        var raising = Record.Exception(() => guard.OnError(new InvalidOperationException("the stream failed")));

        using var scope = new AssertionScope();
        raising.Should().BeNull("the arm no publish reaches still keeps a subscriber's failure its own");
        metrics.Get(SubscriberFaults).Should().Be(1);
        logger.SubscriberWarnings.Should().ContainSingle().Which.Message.Should().Contain("error");
    }

    /// <summary>
    /// The three events published inside a provider's or the handler's own error handling. Up to 2.10.x a subscriber
    /// that threw on one of them did not end the session: its exception was taken for a failure of the step around the
    /// publish, so it counted <c>stt.transcriptions.failed</c> or <c>tts.syntheses.failed</c>, raised a
    /// <see cref="PipelineErrorEvent"/> naming that step, and dropped the rest of the turn. The subscriber's failure is
    /// now its own, so the turn finishes and no step is reported failed.
    /// </summary>
    [Theory]
    [InlineData(nameof(TranscriptReceivedEvent))]
    [InlineData(nameof(ResponseGeneratedEvent))]
    [InlineData(nameof(SynthesisEndedEvent))]
    public async Task HandleSessionAsync_ShouldFinishTheTurnAndReportNoStepFailed_WhenASubscriberThrowsOnlyOnAnEventPublishedInsideAStep(
        string eventType)
    {
        using var metrics = new MeterCapture(MeterName);
        using var stt = new MeterCapture("Verbara.Sdk.VoiceAi.Stt");
        using var tts = new MeterCapture("Verbara.Sdk.VoiceAi.Tts");
        await using var pipeline = BuildPipeline(new CapturingLogger());
        using var first = pipeline.Events.Subscribe(e =>
        {
            if (e.GetType().Name == eventType)
                throw new InvalidOperationException("selective-handler");
        });
        using var capture = new PipelineEventCapture(pipeline);

        var run = await RunSingleUtteranceAsync(pipeline, capture);

        var seen = string.Join(",", capture.Events.Select(static e =>
            e is PipelineErrorEvent error ? $"PipelineError({error.Source})" : e.GetType().Name));
        var observed = $"fault={run.Fault?.GetType().Name ?? "none"} events=[{seen}] " +
            $"stt.failed={stt.Get("stt.transcriptions.failed")} tts.failed={tts.Get("tts.syntheses.failed")} " +
            $"subscriber_faults={metrics.Get(SubscriberFaults)}";
        observed.Should().Be(
            "fault=none events=[SpeechStartedEvent,SpeechEndedEvent,TranscriptReceivedEvent,ResponseGeneratedEvent," +
            "SynthesisStartedEvent,SynthesisEndedEvent] stt.failed=0 tts.failed=0 subscriber_faults=1",
            "a subscriber's failure on an event published inside a step is the subscriber's, not the step's: the turn " +
            "finishes, no recognition or synthesis is counted failed, no PipelineErrorEvent is raised, and the failure " +
            "is counted once as the subscriber's");
    }

    [Fact]
    public async Task Events_ShouldThrowArgumentNullAndCountNothing_WhenTheObserverIsNull()
    {
        using var metrics = new MeterCapture(MeterName);
        await using var pipeline = BuildPipeline(new CapturingLogger());

        var subscribing = () => pipeline.Events.Subscribe(null!);

        subscribing.Should().Throw<ArgumentNullException>("a null observer is the caller's error, as on the subject itself");
        metrics.Get(SubscriberFaults).Should().Be(0, "no subscriber exists, so none failed");
    }

    // The only exception the guard lets through is one only the runtime may throw; a test has to raise it itself.
#pragma warning disable CA1859, CA2201, MA0012
    private static Exception OutOfMemory() => new OutOfMemoryException();
#pragma warning restore CA1859, CA2201, MA0012

    private static VoiceAiPipeline BuildPipeline(ILogger<VoiceAiPipeline> logger)
    {
        var options = new VoiceAiPipelineOptions
        {
            EndOfUtteranceSilence = TimeSpan.FromMilliseconds(60),
            BargInVoiceThreshold = TimeSpan.FromMilliseconds(40),
        };
        var services = new ServiceCollection();
        services.AddSingleton<IConversationHandler>(new FakeConversationHandler().WithResponse("respuesta"));
        var provider = services.BuildServiceProvider();

        return new VoiceAiPipeline(
            new FakeSpeechRecognizer().WithTranscript("hola"),
            new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20)),
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options),
            logger);
    }

    private sealed record Run(Exception? Fault, bool CycleEnded);

    /// <summary>
    /// Runs one session with one utterance: three voice frames, then silence until the cycle ends (or the session
    /// ends first, which is what a subscriber's exception did up to 2.10.x), then a hangup.
    /// </summary>
    private static async Task<Run> RunSingleUtteranceAsync(VoiceAiPipeline pipeline, PipelineEventCapture capture)
    {
        await using var server = new AudioSocketServer(new AudioSocketOptions { Port = 0 }, NullLogger<AudioSocketServer>.Instance);
        var started = new TaskCompletionSource<AudioSocketSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.OnSessionStarted += session =>
        {
            started.TrySetResult(session);
            return ValueTask.CompletedTask;
        };
        await server.StartAsync(CancellationToken.None);

        await using var client = new AudioSocketClient("127.0.0.1", server.BoundPort, Guid.NewGuid());
        await client.ConnectAsync(CancellationToken.None);
        var session = await started.Task.WaitAsync(SignalTimeout);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pipelineTask = pipeline.HandleSessionAsync(session, cts.Token).AsTask();
        var cycle = capture.WaitForResponseCycle();

        try
        {
            for (var i = 0; i < 3; i++)
                await client.SendAudioAsync(VoiceFrame());
            for (var i = 0; i < 4; i++)
                await client.SendAudioAsync(new byte[320]);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The session ended under the sends; the assertions report how.
        }

        await Record.ExceptionAsync(() => Task.WhenAny(cycle, pipelineTask).WaitAsync(SignalTimeout));
        var cycleEnded = cycle.IsCompletedSuccessfully;

        await Record.ExceptionAsync(() => client.SendHangupAsync().AsTask());
        var fault = await Record.ExceptionAsync(() => pipelineTask.WaitAsync(SignalTimeout));
        await server.StopAsync(CancellationToken.None);
        return new Run(fault, cycleEnded);
    }

    private static ReadOnlyMemory<byte> VoiceFrame()
    {
        var buf = new byte[320];
        for (var i = 0; i < 160; i++)
        {
            const short Sample = 5000;
            buf[i * 2] = unchecked((byte)(Sample & 0xFF));
            buf[i * 2 + 1] = (byte)(Sample >> 8);
        }
        return buf;
    }

    private sealed class ThrowingObserver : IObserver<VoiceAiPipelineEvent>
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void OnNext(VoiceAiPipelineEvent value)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("event-handler");
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted() => throw new InvalidOperationException("completion-handler");
    }

    private sealed class ErrorThrowingObserver : IObserver<VoiceAiPipelineEvent>
    {
        public void OnNext(VoiceAiPipelineEvent value)
        {
        }

        public void OnError(Exception error) => throw new InvalidOperationException("error-handler");

        public void OnCompleted()
        {
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    /// <summary>Keeps every line; optionally throws on the subscriber-failure report, as a failing provider would.</summary>
    private sealed class CapturingLogger : ILogger<VoiceAiPipeline>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public bool ThrowOnSubscriberReport { get; init; }

        public bool OutOfMemoryOnSubscriberReport { get; init; }

        public IReadOnlyList<LogEntry> SubscriberWarnings =>
            [.. _entries.Where(static e => e.Level == LogLevel.Warning && e.Message.Contains("subscriber", StringComparison.Ordinal))];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (ThrowOnSubscriberReport && message.Contains("subscriber", StringComparison.Ordinal))
                throw new InvalidOperationException("the log provider failed");
            if (OutOfMemoryOnSubscriberReport && message.Contains("subscriber", StringComparison.Ordinal))
                throw OutOfMemory();
            _entries.Enqueue(new LogEntry(logLevel, message, exception));
        }
    }
}
