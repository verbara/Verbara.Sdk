using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Reactive.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.Diagnostics;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.Tests.Manager;

/// <summary>
/// What a subscriber of the manager's notifications that throws may and may not change: the other subscribers
/// still receive every notification, the subscriber itself stays subscribed when it subscribed directly, and the
/// manager's own work after a raise — its save, its channel index, its disposal — and the live state's later
/// handlers run as they do with no throwing subscriber.
/// </summary>
/// <remarks>
/// <para>Every call is driven synchronously on the test's thread through one server's channel, queue and agent
/// tables, the paths Asterisk's events take. A drive step that lets an exception out is recorded rather than
/// rethrown, so a test reports what the exception cost instead of stopping at it.</para>
/// <para>The store saves synchronously, so a save the manager starts has completed when the step returns.</para>
/// </remarks>
[Collection(SessionSubscriberFaultsGroup.Name)]
public sealed class CallSessionManagerSubscriberIsolationTests
{
    private const string ServerId = "srv-1";

    /// <summary>The manager's two residency gauges, in ordinal order.</summary>
    private static readonly string[] ResidencyGauges = ["sessions.active", "sessions.retained"];

    [Fact]
    public async Task Events_ShouldDeliverEveryNotificationToALaterSubscriber_WhenAnEarlierSubscriberThrowsOnEveryNotification()
    {
        await using var rig = new Rig();
        var first = new Thrower(_ => true);
        using var firstSubscription = rig.Manager.Events.Subscribe(first);
        var second = new Recorder();
        using var secondSubscription = rig.Manager.Events.Subscribe(second);

        rig.NewCall("c-1", "L-1");
        rig.HangUp("c-1");

        new { SecondReceived = second.Kinds(), FirstCalled = first.Calls }.Should().BeEquivalentTo(
            new { SecondReceived = new[] { nameof(CallStartedEvent), nameof(CallEndedEvent) }, FirstCalled = 2 },
            "the first subscriber's failure is its own: the second still receives the call's start and its "
            + $"ending, and the first was called for both. Measured: {rig.Describe()}");
    }

    [Fact]
    public async Task Events_ShouldKeepCallingADirectSubscriber_WhenItThrowsOnEveryNotification()
    {
        await using var rig = new Rig();
        var thrower = new Thrower(_ => true);
        using var subscription = rig.Manager.Events.Subscribe(thrower);

        rig.NewCall("c-1", "L-1");
        rig.NewCall("c-2", "L-2");
        rig.NewCall("c-3", "L-3");

        thrower.Calls.Should().Be(3,
            "a subscriber subscribed directly that throws stays subscribed and is called once per call-started "
            + $"notification. Measured: {rig.Describe()}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueueSessionTracker_ShouldCountTheAnswer_WhenAHostSubscriberTakenBeforeItThrows(bool hostSubscriberThrows)
    {
        await using var rig = new Rig();
        using var host = hostSubscriberThrows ? rig.Manager.Events.Subscribe(new Thrower(_ => true)) : null;
        using var tracker = new QueueSessionTracker(rig.Manager, Options.Create(new SessionOptions()));

        rig.NewCall("c-1", "L-1");
        rig.Drive("CallerJoined", () => rig.Server.Queues.OnCallerJoined("sales", "PJSIP/trunk-c-1", "5551234", 1));
        rig.Drive("AgentLogin", () => rig.Server.Agents.OnAgentLogin("agent-1", "PJSIP/agent-1"));
        rig.Drive("AgentConnect", () => rig.Server.Agents.OnAgentConnect(
            "agent-1", talkingTo: "PJSIP/trunk-c-1", linkedId: "L-1", memberInterface: "PJSIP/agent-1"));

        var queue = tracker.GetByQueueName("sales");
        new { Offered = queue?.CallsOffered, Answered = queue?.CallsAnswered }.Should().BeEquivalentTo(
            new { Offered = 1, Answered = 1 },
            "the queue tracker subscribes after the host's subscriber, and still counts the queued call offered "
            + $"and answered, as it does with no throwing subscriber. Measured: {rig.Describe()}");
    }

    [Fact]
    public async Task Events_ShouldDeliverToTheOtherSubscribersAndEvaluateTheFilterOnce_WhenAFiltersPredicateThrows()
    {
        await using var rig = new Rig();
        var evaluations = 0;
        using var filtered = rig.Manager.Events
            .Where(_ =>
            {
                evaluations++;
                throw new InvalidOperationException("A filter's predicate failed.");
            })
            .Subscribe(_ => { });
        var second = new Recorder();
        using var secondSubscription = rig.Manager.Events.Subscribe(second);

        rig.NewCall("c-1", "L-1");
        rig.NewCall("c-2", "L-2");

        new { SecondReceived = second.Kinds(), Evaluations = evaluations }.Should().BeEquivalentTo(
            new { SecondReceived = new[] { nameof(CallStartedEvent), nameof(CallStartedEvent) }, Evaluations = 1 },
            "the filter's first failure ends its own subscription and nothing else: the second subscriber receives "
            + $"both call-started notifications. Measured: {rig.Describe()}");
    }

    [Fact]
    public async Task Events_ShouldDeliverToADirectSubscriberAndCallTheHandlerOnce_WhenAHandlerBehindOfTypeThrows()
    {
        await using var rig = new Rig();
        var handlerCalls = 0;
        using var typed = rig.Manager.Events
            .OfType<CallStartedEvent>()
            .Subscribe(_ =>
            {
                handlerCalls++;
                throw new InvalidOperationException("A handler behind OfType failed.");
            });
        var second = new Recorder();
        using var secondSubscription = rig.Manager.Events.Subscribe(second);

        rig.NewCall("c-1", "L-1");
        rig.NewCall("c-2", "L-2");

        new { HandlerCalls = handlerCalls, SecondReceived = second.Kinds() }.Should().BeEquivalentTo(
            new { HandlerCalls = 1, SecondReceived = new[] { nameof(CallStartedEvent), nameof(CallStartedEvent) } },
            "the composition library ends the typed subscription on its handler's first failure, and the direct "
            + $"subscriber still receives both call-started notifications. Measured: {rig.Describe()}");
    }

    [Fact]
    public async Task DisposeAsync_ShouldCompleteEverySubscriberAndWithdrawTheResidencyGauges_WhenASubscriberThrowsOnCompletion()
    {
        var rig = new Rig();
        var withdrawn = new ConcurrentBag<string>();
        using var gauges = new MeterListener();
        gauges.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, rig.Manager.ResidencyMeter)
                && instrument.Name is "sessions.active" or "sessions.retained")
                listener.EnableMeasurementEvents(instrument);
        };
        gauges.MeasurementsCompleted = (instrument, _) => withdrawn.Add(instrument.Name);
        gauges.Start();
        using var first = rig.Manager.Events.Subscribe(new Thrower(_ => false, throwsOnCompleted: true));
        var second = new Recorder();
        using var secondSubscription = rig.Manager.Events.Subscribe(second);

        Exception? escaped = null;
        try
        {
            await rig.Manager.DisposeAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            escaped = ex;
        }

        // Read before the clean-up below, so what is measured is what the disposal itself withdrew. A disposal cut
        // short leaves the gauges published; the clean-up withdraws them so no listener of a later test reads them.
        var withdrawnByTheDisposal = withdrawn.Order(StringComparer.Ordinal).ToArray();
        rig.Manager.ResidencyMeter.Dispose();
        await rig.Server.DisposeAsync();

        new
        {
            Escaped = escaped?.Message,
            SecondCompleted = second.Completed,
            Withdrawn = withdrawnByTheDisposal,
        }.Should().BeEquivalentTo(
            new
            {
                Escaped = (string?)null,
                SecondCompleted = true,
                Withdrawn = ResidencyGauges,
            },
            "the first subscriber's failure on completion neither escapes the disposal nor keeps the second "
            + "subscriber from completing, and the disposal still withdraws the residency gauges");
    }

    [Fact]
    public async Task OnHangup_ShouldSaveTheEndingAndReleaseTheChannel_WhenASubscriberThrowsOnCallEnded()
    {
        await using var rig = new Rig();
        using var thrower = rig.Manager.Events.Subscribe(new Thrower(e => e is CallEndedEvent));

        rig.NewCall("c-1", "L-1");
        var call = rig.Manager.GetByLinkedId("L-1")
            ?? throw new InvalidOperationException("premise: the new channel opened a call");
        rig.HangUp("c-1");

        new
        {
            Escaped = rig.Escaped.ToArray(),
            call.State,
            LastSaved = rig.Store.LastSavedState(call.SessionId),
            ChannelLookup = rig.Manager.GetByChannelId("c-1")?.SessionId,
        }.Should().BeEquivalentTo(
            new
            {
                Escaped = Array.Empty<string>(),
                State = CallSessionState.Failed,
                LastSaved = (CallSessionState?)CallSessionState.Failed,
                ChannelLookup = (string?)null,
            },
            "the hangup is handled without an exception reaching the live state, the store's last save of the call "
            + $"carries its ended state, and the channel's id no longer finds it. Measured: {rig.Describe()}");
    }

    [Fact]
    public async Task OnNewChannel_ShouldSaveTheCall_WhenASubscriberThrowsOnCallStarted()
    {
        await using var rig = new Rig();
        using var thrower = rig.Manager.Events.Subscribe(new Thrower(e => e is CallStartedEvent));

        rig.NewCall("c-1", "L-1");
        var call = rig.Manager.GetByLinkedId("L-1")
            ?? throw new InvalidOperationException("premise: the new channel opened a call");

        rig.Store.Saves(call.SessionId).Should().Be(1,
            $"the manager saves the call it opened whatever a subscriber of its start throws. Measured: {rig.Describe()}");
    }

    [Fact]
    public async Task ChannelManager_ShouldRunTheHandlersRegisteredAfterTheEngine_WhenASubscriberThrowsOnEveryNotification()
    {
        await using var rig = new Rig();
        var added = 0;
        var removed = 0;
        rig.Server.Channels.ChannelAdded += _ => added++;
        rig.Server.Channels.ChannelRemoved += _ => removed++;
        using var thrower = rig.Manager.Events.Subscribe(new Thrower(_ => true));

        rig.NewCall("c-1", "L-1");
        rig.HangUp("c-1");

        new { Added = added, Removed = removed }.Should().BeEquivalentTo(
            new { Added = 1, Removed = 1 },
            "the live state's handlers registered after the session engine run for the channel's addition and its "
            + $"removal, whatever a subscriber of the engine throws. Measured: {rig.Describe()}");
    }

    [Fact]
    public async Task Events_ShouldLogOneWarningAndCountOneFault_WhenASubscriberThrowsOnceOnCallStarted()
    {
        var logger = new RecordingLogger();
        using var faults = new FaultCounter();
        await using var rig = new Rig(logger);
        using var thrower = rig.Manager.Events.Subscribe(new Thrower(e => e is CallStartedEvent));

        rig.NewCall("c-1", "L-1");
        var call = rig.Manager.GetByLinkedId("L-1")
            ?? throw new InvalidOperationException("premise: the new channel opened a call");
        rig.HangUp("c-1");

        new { Warnings = logger.Warnings(), Counted = faults.Total }.Should().BeEquivalentTo(
            new
            {
                Warnings = new[]
                {
                    new Report(nameof(CallStartedEvent), call.SessionId, nameof(InvalidOperationException)),
                },
                Counted = 1L,
            },
            "one failure is reported once: one Warning naming the call-started notification and its session, with "
            + $"the exception, and one fault counted. Measured: {rig.Describe()}");
    }

    [Fact]
    public async Task Events_ShouldLogAndCountEveryFailure_WhenASubscriberThrowsOnEveryNotification()
    {
        var logger = new RecordingLogger();
        using var faults = new FaultCounter();
        await using var rig = new Rig(logger);
        using var thrower = rig.Manager.Events.Subscribe(new Thrower(_ => true));

        rig.NewCall("c-1", "L-1");
        var call = rig.Manager.GetByLinkedId("L-1")
            ?? throw new InvalidOperationException("premise: the new channel opened a call");
        rig.HangUp("c-1");

        new { Warnings = logger.Warnings(), Counted = faults.Total }.Should().BeEquivalentTo(
            new
            {
                Warnings = new[]
                {
                    new Report(nameof(CallStartedEvent), call.SessionId, nameof(InvalidOperationException)),
                    new Report(nameof(CallEndedEvent), call.SessionId, nameof(InvalidOperationException)),
                },
                Counted = 2L,
            },
            "a subscriber that throws on the call's start and on its ending is reported for each failure: two "
            + $"Warnings and two faults counted. Measured: {rig.Describe()}");
    }

    [Fact]
    public async Task DisposeAsync_ShouldLogOneWarningNamingTheCompletion_WhenASubscriberThrowsOnCompletion()
    {
        var logger = new RecordingLogger();
        using var faults = new FaultCounter();
        var rig = new Rig(logger);
        using var first = rig.Manager.Events.Subscribe(new Thrower(_ => false, throwsOnCompleted: true));

        await rig.Manager.DisposeAsync();
        await rig.Server.DisposeAsync();

        new { Warnings = logger.Warnings(), Counted = faults.Total }.Should().BeEquivalentTo(
            new
            {
                Warnings = new[] { new Report("completion", null, nameof(InvalidOperationException)) },
                Counted = 1L,
            },
            "a failure on the completion is reported once, naming the completion and no session, and counted once");
    }

    /// <summary>
    /// A manager attached to a server over a substituted connection, saving to <see cref="CountingStore"/>.
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly IAmiConnection _connection = Substitute.For<IAmiConnection>();

        public Rig(ILogger<CallSessionManager>? logger = null)
        {
            _connection.AsteriskVersion.Returns("22.9.0");
            Server = new VerbaraServer(_connection, NullLogger<VerbaraServer>.Instance);
            Manager = new CallSessionManager(
                Options.Create(new SessionOptions()), logger ?? NullLogger<CallSessionManager>.Instance, Store);
            Manager.AttachToServer(Server, ServerId);
        }

        public VerbaraServer Server { get; }

        public CallSessionManager Manager { get; }

        public CountingStore Store { get; } = new();

        /// <summary>Each drive step that let an exception out, as <c>step:ExceptionType</c>.</summary>
        public List<string> Escaped { get; } = [];

        /// <summary>One inbound leg, answered, carrying <paramref name="linkedId"/>.</summary>
        public void NewCall(string uniqueId, string linkedId) =>
            Drive($"NewChannel {uniqueId}", () => Server.Channels.OnNewChannel(
                uniqueId, $"PJSIP/trunk-{uniqueId}", ChannelState.Up, context: "from-trunk", linkedId: linkedId));

        public void HangUp(string uniqueId) =>
            Drive($"Hangup {uniqueId}", () => Server.Channels.OnHangup(uniqueId, HangupCause.NormalClearing));

        /// <summary>Runs one drive step; an exception it lets out is recorded in <see cref="Escaped"/>.</summary>
        public void Drive(string step, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Escaped.Add($"{step}:{ex.GetType().Name}");
            }
        }

        public string Describe() => $"escaped to the raiser: [{string.Join(", ", Escaped)}]";

        public async ValueTask DisposeAsync()
        {
            await Manager.DisposeAsync();
            await Server.DisposeAsync();
        }
    }

    /// <summary>Counts each call's saves and keeps the state each was last saved in.</summary>
    private sealed class CountingStore : SessionStoreBase
    {
        private readonly ConcurrentDictionary<string, int> _saves = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, CallSessionState> _lastSaved = new(StringComparer.Ordinal);

        public int Saves(string sessionId) => _saves.GetValueOrDefault(sessionId);

        public CallSessionState? LastSavedState(string sessionId) =>
            _lastSaved.TryGetValue(sessionId, out var state) ? state : null;

        public override ValueTask SaveAsync(CallSession session, CancellationToken ct)
        {
            _saves.AddOrUpdate(session.SessionId, 1, static (_, n) => n + 1);
            _lastSaved[session.SessionId] = session.State;
            return ValueTask.CompletedTask;
        }

        public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) =>
            ValueTask.FromResult<CallSession?>(null);
    }

    /// <summary>A subscriber that throws on the notifications <paramref name="throwsOn"/> selects.</summary>
    private sealed class Thrower(Func<SessionDomainEvent, bool> throwsOn, bool throwsOnCompleted = false)
        : IObserver<SessionDomainEvent>
    {
        public int Calls { get; private set; }

        public void OnNext(SessionDomainEvent value)
        {
            Calls++;
            if (throwsOn(value))
                throw new InvalidOperationException($"A subscriber failed on {value.GetType().Name}.");
        }

        public void OnError(Exception error)
        {
            // The manager's stream never faults; nothing to do.
        }

        public void OnCompleted()
        {
            if (throwsOnCompleted)
                throw new InvalidOperationException("A subscriber failed on completion.");
        }
    }

    /// <summary>One Warning as the report reads it: the notification's type, its session id, the exception's type.</summary>
    private sealed record Report(string? EventType, string? SessionId, string? Exception);

    /// <summary>Keeps every Warning written to it, read as a <see cref="Report"/>.</summary>
    private sealed class RecordingLogger : ILogger<CallSessionManager>
    {
        private readonly ConcurrentQueue<Report> _warnings = new();

        public Report[] Warnings() => [.. _warnings];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning)
                return;

            var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                    properties[pair.Key] = pair.Value;
            }

            _warnings.Enqueue(new Report(
                properties.GetValueOrDefault("EventType") as string,
                properties.GetValueOrDefault("SessionId") as string,
                exception?.GetType().Name));
        }
    }

    /// <summary>Sums what is added to <c>sessions.events.subscriber_faults</c> while it listens.</summary>
    private sealed class FaultCounter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _total;

        public FaultCounter()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument, SessionMetrics.SubscriberFaults))
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref _total, value));
            _listener.Start();
        }

        public long Total => Interlocked.Read(ref _total);

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>A subscriber that records every notification it receives and its completion.</summary>
    private sealed class Recorder : IObserver<SessionDomainEvent>
    {
        private readonly List<SessionDomainEvent> _received = [];

        public bool Completed { get; private set; }

        public string[] Kinds() => [.. _received.Select(e => e.GetType().Name)];

        public void OnNext(SessionDomainEvent value) => _received.Add(value);

        public void OnError(Exception error)
        {
            // The manager's stream never faults; nothing to record.
        }

        public void OnCompleted() => Completed = true;
    }
}
