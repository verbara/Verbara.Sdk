using System.Net.WebSockets;
using System.Reactive.Linq;
using Verbara.Sdk.VoiceAi.AudioSocket;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.FunctionCalling;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Internal;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Bridge;

/// <summary>
/// A bridge event subscriber's failure is its own: every other subscriber of <see cref="OpenAiRealtimeBridge.Events"/>
/// still receives every event, the session goes on serving the call and is not counted failed, and each failure is
/// counted once on <c>voiceai.realtime.subscriber_faults</c> and then logged once at Warning. A logger that throws on
/// that Warning changes none of it.
/// </summary>
/// <remarks>
/// The vendor's script is the same in every session: speech started, speech stopped, one transcript delta and the final
/// transcript, sent after the client's <c>session.update</c>. The observation ends when the direct subscriber has the
/// final transcript, or when the session ends first (what a subscriber's exception did up to 2.10.x). The counter is
/// read by name, so this class compiles on a tree without it; the assembly runs its tests one at a time.
/// </remarks>
public sealed class OpenAiRealtimeBridgeSubscriberIsolationTests
{
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    private const string MeterName = "Verbara.Sdk.VoiceAi.OpenAiRealtime";
    private const string SubscriberFaults = "voiceai.realtime.subscriber_faults";
    private const string Failed = "openai_realtime.sessions.failed";

    private static readonly string[] VendorScript =
    [
        """{"type":"input_audio_buffer.speech_started"}""",
        """{"type":"input_audio_buffer.speech_stopped"}""",
        """{"type":"response.output_audio_transcript.delta","delta":"the"}""",
        """{"type":"response.output_audio_transcript.done","transcript":"the answer survives"}""",
    ];

    private static readonly Type[] ScriptEvents =
    [
        typeof(RealtimeSpeechStartedEvent), typeof(RealtimeSpeechStoppedEvent),
        typeof(RealtimeTranscriptEvent), typeof(RealtimeTranscriptEvent),
    ];

    [Fact]
    public async Task HandleSessionAsync_ShouldDeliverEveryEventToTheOtherSubscriberAndCountAndLogEach_WhenTheFirstThrowsOnEveryEvent()
    {
        var log = new SubscriberLogger();
        var thrower = new ThrowingObserver();

        var seen = await ServeAsync(log, events => events.Subscribe(thrower));

        var warnings = log.SubscriberWarnings;
        using var scope = new AssertionScope();
        seen.FaultText.Should().Be("none",
            $"a bridge event subscriber's failure is its own: the session does not fail (fault: {seen.FaultText})");
        seen.EndedWhileServed.Should().BeFalse("the session goes on serving the call");
        seen.Events.Select(e => e.GetType()).Should().Equal(ScriptEvents,
            "the second subscriber receives every event, in order");
        thrower.Calls.Should().Be(ScriptEvents.Length, "the first subscriber stays subscribed and is called on every event");
        seen.SubscriberFaults.Should().Be(ScriptEvents.Length, "one count per failure");
        seen.Failed.Should().Be(0, "the session is not counted failed");
        warnings.Should().HaveCount(ScriptEvents.Length, "one Warning per failure");
        warnings.Should().OnlyContain(w => w.Exception is InvalidOperationException && w.Exception.Message == "event-handler",
            "each Warning carries the subscriber's exception");
        warnings.Select(w => w.Message).Should().Contain(
            m => m.Contains(nameof(RealtimeSpeechStartedEvent), StringComparison.Ordinal),
            "the Warning names the event the subscriber threw on");
        seen.ChannelId.Should().NotBe(Guid.Empty);
        warnings.Should().OnlyContain(w => w.Message.StartsWith($"[{seen.ChannelId}] ", StringComparison.Ordinal),
            "each Warning opens with the channel id of the call whose event the subscriber threw on");
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldDeliverEveryEventAndCount_WhenTheLoggerThrowsOnTheReportToo()
    {
        var log = new SubscriberLogger { ThrowOnSubscriberReport = true };

        var seen = await ServeAsync(log, events => events.Subscribe(new ThrowingObserver()));

        using var scope = new AssertionScope();
        seen.FaultText.Should().Be("none", $"a failing report changes nothing (fault: {seen.FaultText})");
        seen.EndedWhileServed.Should().BeFalse();
        seen.Events.Select(e => e.GetType()).Should().Equal(ScriptEvents,
            "the second subscriber receives every event whatever the logger throws");
        seen.SubscriberFaults.Should().Be(ScriptEvents.Length, "the fault is counted before the log is attempted");
        seen.Failed.Should().Be(0);
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldEndOnlyTheComposedSubscriptionAndCountItOnce_WhenAHandlerBehindWhereThrows()
    {
        var log = new SubscriberLogger();
        var composedCalls = 0;

        var seen = await ServeAsync(log, events => events.Where(_ => true).Subscribe(_ =>
        {
            Interlocked.Increment(ref composedCalls);
            throw new InvalidOperationException("composed-handler");
        }));

        using var scope = new AssertionScope();
        seen.FaultText.Should().Be("none", $"the publisher throws nothing (fault: {seen.FaultText})");
        Volatile.Read(ref composedCalls).Should().Be(1, "the operator ends its subscription on the handler's first exception");
        seen.SubscriberFaults.Should().Be(1, "that one failure is counted once");
        log.SubscriberWarnings.Should().ContainSingle("and logged once");
        seen.Events.Select(e => e.GetType()).Should().Equal(ScriptEvents, "the direct subscriber receives every event");
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldCountAndLogNothing_WhenNoSubscriberThrows()
    {
        var log = new SubscriberLogger();

        var seen = await ServeAsync(log, extra: null);

        using var scope = new AssertionScope();
        seen.FaultText.Should().Be("none");
        seen.Events.Select(e => e.GetType()).Should().Equal(ScriptEvents);
        seen.SubscriberFaults.Should().Be(0, "no subscriber failed");
        log.SubscriberWarnings.Should().BeEmpty();
    }

    [Fact]
    public async Task DisposeAsync_ShouldCompleteTheOtherSubscriberAndCountAndLogOnce_WhenASubscriberThrowsOnTheCompletion()
    {
        using var metrics = new MeterCapture(MeterName);
        var log = new SubscriberLogger();
        var bridge = new OpenAiRealtimeBridge(
            Options.Create(new OpenAiRealtimeOptions { ApiKey = "test-key" }), new RealtimeFunctionRegistry([]), log);
        using var first = bridge.Events.Subscribe(new ThrowingObserver());
        var completions = 0;
        using var second = bridge.Events.Subscribe(_ => { }, () => Interlocked.Increment(ref completions));

        var disposing = await Record.ExceptionAsync(() => bridge.DisposeAsync().AsTask());

        using var scope = new AssertionScope();
        disposing.Should().BeNull("a bridge event subscriber's failure is its own: DisposeAsync returns normally");
        Volatile.Read(ref completions).Should().Be(1, "the second subscriber receives the completion");
        metrics.Get(SubscriberFaults).Should().Be(1);
        log.SubscriberWarnings.Should().ContainSingle()
            .Which.Message.Should().Contain("completion", "the Warning names the completion as what the subscriber threw on");
    }

    [Fact]
    public async Task DisposeAsync_ShouldLetOutOfMemoryThrough_WhenASubscriberThrowsItOnTheCompletion()
    {
        using var metrics = new MeterCapture(MeterName);
        var bridge = new OpenAiRealtimeBridge(
            Options.Create(new OpenAiRealtimeOptions { ApiKey = "test-key" }), new RealtimeFunctionRegistry([]),
            NullLogger<OpenAiRealtimeBridge>.Instance);
        using var first = bridge.Events.Subscribe(_ => { }, () => throw OutOfMemory());

        var disposing = () => bridge.DisposeAsync().AsTask();

        await disposing.Should().ThrowAsync<OutOfMemoryException>("the process cannot go on, so no guard hides it");
        metrics.Get(SubscriberFaults).Should().Be(0);
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldLetOutOfMemoryThrough_WhenASubscriberThrowsItOnAnEvent()
    {
        var seen = await ServeAsync(new SubscriberLogger(), events => events.Subscribe(_ => throw OutOfMemory(), () => { }));

        using var scope = new AssertionScope();
        seen.Fault.Should().BeOfType<OutOfMemoryException>("the process cannot go on, so no guard hides it");
        seen.SubscriberFaults.Should().Be(0, "an out-of-memory failure is not a subscriber fault");
    }

    [Fact]
    public async Task DisposeAsync_ShouldCountTheFaultBeforeTheWarning_WhenTheLoggerThrowsOutOfMemoryOnIt()
    {
        using var metrics = new MeterCapture(MeterName);
        var bridge = new OpenAiRealtimeBridge(
            Options.Create(new OpenAiRealtimeOptions { ApiKey = "test-key" }), new RealtimeFunctionRegistry([]),
            new SubscriberLogger { OutOfMemoryOnSubscriberReport = true });
        using var first = bridge.Events.Subscribe(new ThrowingObserver());

        var disposing = () => bridge.DisposeAsync().AsTask();

        await disposing.Should().ThrowAsync<OutOfMemoryException>("the report's guard does not hide an out-of-memory failure");
        metrics.Get(SubscriberFaults).Should().Be(1, "the fault is counted before the Warning is attempted");
    }

    [Fact]
    public async Task SubscriberGuard_ShouldCountAndLogOnceAndThrowNothing_WhenTheSubscriberThrowsOnAnError()
    {
        using var metrics = new MeterCapture(MeterName);
        var log = new SubscriberLogger();
        await using var bridge = new OpenAiRealtimeBridge(
            Options.Create(new OpenAiRealtimeOptions { ApiKey = "test-key" }), new RealtimeFunctionRegistry([]), log);
        var guard = new OpenAiRealtimeBridge.SubscriberGuard(bridge, new ErrorThrowingObserver());

        var raising = Record.Exception(() => guard.OnError(new InvalidOperationException("the stream failed")));

        using var scope = new AssertionScope();
        raising.Should().BeNull("the arm no publish reaches still keeps a subscriber's failure its own");
        metrics.Get(SubscriberFaults).Should().Be(1);
        log.SubscriberWarnings.Should().ContainSingle().Which.Message.Should().Contain("error");
    }

    [Fact]
    public async Task Events_ShouldThrowArgumentNullAndCountNothing_WhenTheObserverIsNull()
    {
        using var metrics = new MeterCapture(MeterName);
        await using var bridge = new OpenAiRealtimeBridge(
            Options.Create(new OpenAiRealtimeOptions { ApiKey = "test-key" }), new RealtimeFunctionRegistry([]),
            NullLogger<OpenAiRealtimeBridge>.Instance);

        var subscribing = () => bridge.Events.Subscribe(null!);

        subscribing.Should().Throw<ArgumentNullException>("a null observer is the caller's error, as on the subject itself");
        metrics.Get(SubscriberFaults).Should().Be(0, "no subscriber exists, so none failed");
    }

    // The only exception the guard lets through is one only the runtime may throw; a test has to raise it itself.
#pragma warning disable CA1859, CA2201, MA0012
    private static Exception OutOfMemory() => new OutOfMemoryException();
#pragma warning restore CA1859, CA2201, MA0012

    private sealed record Observation(
        Exception? Fault, bool EndedWhileServed, IReadOnlyList<RealtimeEvent> Events, long SubscriberFaults, long Failed,
        Guid ChannelId)
    {
        public string FaultText => Fault is null ? "none" : $"{Fault.GetType().FullName}: {Fault.Message}";
    }

    /// <summary>
    /// One session against the fake vendor. <paramref name="extra"/> subscribes first, then the direct collector; the
    /// vendor speaks <see cref="VendorScript"/>, and the observation is taken once the collector has the final
    /// transcript or the session ended first. The session is then cancelled and the caller's side released.
    /// </summary>
    private static async Task<Observation> ServeAsync(
        ILogger<OpenAiRealtimeBridge> log, Func<IObservable<RealtimeEvent>, IDisposable>? extra)
    {
        await using var fakeOpenAi = new RealtimeFakeServer { HoldOpenUntilDisposed = true };
        fakeOpenAi.Start();

        await using var audioServer = new AudioSocketServer(
            new AudioSocketOptions { Port = 0 }, NullLogger<AudioSocketServer>.Instance);
        var started = new TaskCompletionSource<AudioSocketSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        audioServer.OnSessionStarted += session =>
        {
            started.TrySetResult(session);
            return ValueTask.CompletedTask;
        };
        await audioServer.StartAsync(CancellationToken.None);
        await using var client = new AudioSocketClient("127.0.0.1", audioServer.BoundPort, Guid.NewGuid());
        await client.ConnectAsync(CancellationToken.None);
        var session = await started.Task.WaitAsync(SignalTimeout);

        await using var bridge = new OpenAiRealtimeBridge(
            Options.Create(new OpenAiRealtimeOptions
            {
                ApiKey = "test-key",
                Model = "gpt-realtime",
                InputFormat = Audio.AudioFormat.Slin16Mono8kHz,
            }),
            new RealtimeFunctionRegistry([]),
            log)
        {
            BaseUri = new Uri($"ws://127.0.0.1:{fakeOpenAi.Port}/"),
        };
        using var metrics = new MeterCapture(MeterName);
        using var first = extra?.Invoke(bridge.Events);
        using var collector = new RealtimeEventCollector(
            bridge.Events, e => e.OfType<RealtimeTranscriptEvent>().Any(t => t.IsFinal));
        using var cts = new CancellationTokenSource();

        var sessionTask = bridge.HandleSessionAsync(session, cts.Token).AsTask();
        await fakeOpenAi.SessionUpdateReceived.WaitAsync(SignalTimeout);
        try
        {
            foreach (var frame in VendorScript)
                await fakeOpenAi.SendEventAsync(frame);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or InvalidOperationException or ObjectDisposedException)
        {
            // The session ended under the vendor's sends; the assertions report how.
        }

        await Record.ExceptionAsync(() => Task.WhenAny(collector.Satisfied, sessionTask).WaitAsync(SignalTimeout));

        var observation = new Observation(
            Fault: sessionTask.IsFaulted ? sessionTask.Exception!.InnerException : null,
            EndedWhileServed: sessionTask.IsCompleted,
            Events: collector.Events,
            SubscriberFaults: metrics.Get(SubscriberFaults),
            Failed: metrics.Get(Failed),
            ChannelId: session.ChannelId);

        await cts.CancelAsync();
        await Record.ExceptionAsync(() => sessionTask.WaitAsync(SignalTimeout));
        await audioServer.StopAsync(CancellationToken.None);
        return observation;
    }

    private sealed class ThrowingObserver : IObserver<RealtimeEvent>
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void OnNext(RealtimeEvent value)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("event-handler");
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted() => throw new InvalidOperationException("completion-handler");
    }

    private sealed class ErrorThrowingObserver : IObserver<RealtimeEvent>
    {
        public void OnNext(RealtimeEvent value)
        {
        }

        public void OnError(Exception error) => throw new InvalidOperationException("error-handler");

        public void OnCompleted()
        {
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    /// <summary>Keeps every line; optionally throws on the subscriber-failure report, as a failing provider would.</summary>
    private sealed class SubscriberLogger : ILogger<OpenAiRealtimeBridge>
    {
        private readonly Lock _gate = new();
        private readonly List<LogEntry> _entries = [];

        public bool ThrowOnSubscriberReport { get; init; }

        public bool OutOfMemoryOnSubscriberReport { get; init; }

        public IReadOnlyList<LogEntry> SubscriberWarnings
        {
            get
            {
                lock (_gate)
                {
                    return [.. _entries.Where(static e =>
                        e.Level == LogLevel.Warning && e.Message.Contains("subscriber", StringComparison.Ordinal))];
                }
            }
        }

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
            lock (_gate) _entries.Add(new LogEntry(logLevel, message, exception));
        }
    }
}
