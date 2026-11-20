using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Live.Agents;

namespace Verbara.Sdk.Live.Tests.Agents;

/// <summary>
/// An agent subscriber's failure is its own: every later subscriber of the same event still hears it, the events that
/// follow in the same report are still raised, the failure is logged once at Warning naming the event and counted once
/// on <c>live.agents.subscriber_faults</c>, and nothing leaves the call that made the change. The counter is read by
/// name, so this class compiles on a tree without it.
/// </summary>
public sealed class AgentSubscriberIsolationTests
{
    private const string SubscriberFaults = "live.agents.subscriber_faults";

    public enum AgentEvent
    {
        AgentLoggedIn,
        AgentLoggedOff,
        AgentStateChanged,
        AgentConnected,
        QueueCallerConnected,
        QueueCallerCompleted,
    }

    public static TheoryData<AgentEvent> AllEvents => new(Enum.GetValues<AgentEvent>());

    [Theory]
    [MemberData(nameof(AllEvents))]
    public void Raise_ShouldReachTheSecondHandlerLogAndCountOnce_WhenTheFirstHandlerThrows(AgentEvent agentEvent)
    {
        var logger = new CapturingLogger();
        var manager = new AgentManager(logger);
        Prepare(manager);
        var firstHeard = 0;
        var secondHeard = 0;
        var failure = new InvalidOperationException("first-handler");
        Subscribe(manager, agentEvent, () =>
        {
            firstHeard++;
            throw failure;
        });
        Subscribe(manager, agentEvent, () => secondHeard++);
        using var faults = new FaultCounter();

        var firstRaise = Record.Exception(() => Raise(manager, agentEvent, "a"));
        var secondHeardAfterFirstRaise = secondHeard;
        var faultsAfterFirstRaise = faults.Count;
        var warningsAfterFirstRaise = logger.Warnings;
        var secondRaise = Record.Exception(() => Raise(manager, agentEvent, "b"));

        using var scope = new AssertionScope();
        firstRaise.Should().BeNull($"a {agentEvent} handler's failure must not leave the raising call");
        secondHeardAfterFirstRaise.Should().Be(1, $"the handler after a throwing one must still hear {agentEvent}, once");
        faultsAfterFirstRaise.Should().Be(1, $"{SubscriberFaults} rises by one per failure");
        warningsAfterFirstRaise.Should().ContainSingle("one Warning per failure")
            .Which.Should().Match<LogEntry>(w => w.Message.Contains("event=" + agentEvent, StringComparison.Ordinal)
                && w.Message.Contains("subject=" + ExpectedSubject(agentEvent, "a") + ";", StringComparison.Ordinal)
                && ReferenceEquals(w.Exception, failure));
        secondRaise.Should().BeNull();
        firstHeard.Should().Be(2, "the throwing handler stays subscribed and hears the next event");
        secondHeard.Should().Be(2);
        faults.Count.Should().Be(2);
    }

    [Theory]
    [MemberData(nameof(AllEvents))]
    public void Raise_ShouldReachEveryHandlerAndCountNothing_WhenNoHandlerThrows(AgentEvent agentEvent)
    {
        var manager = new AgentManager(NullLogger.Instance);
        Prepare(manager);
        var heard = new List<string>();
        Subscribe(manager, agentEvent, () => heard.Add("first"));
        Subscribe(manager, agentEvent, () => heard.Add("second"));
        using var faults = new FaultCounter();

        Raise(manager, agentEvent, "a");

        using var scope = new AssertionScope();
        heard.Should().Equal(["first", "second"], "the negative control: two handlers that do not throw both hear the event in order");
        faults.Count.Should().Be(0);
    }

    [Fact]
    public void OnAgentConnect_ShouldRaiseAgentConnected_WhenAnAgentStateChangedHandlerThrows()
    {
        var logger = new CapturingLogger();
        var manager = new AgentManager(logger);
        manager.OnAgentLogin("1001");
        var stateHeard = 0;
        var connected = new List<(string AgentId, string? LinkedId, string? Interface)>();
        manager.AgentStateChanged += _ => throw new InvalidOperationException("state-handler");
        manager.AgentStateChanged += _ => stateHeard++;
        manager.AgentConnected += (agentId, linkedId, iface) => connected.Add((agentId, linkedId, iface));
        using var faults = new FaultCounter();

        var thrown = Record.Exception(() => manager.OnAgentConnect("1001", "PJSIP/caller-1", "linked-1", "Agent/1001"));

        using var scope = new AssertionScope();
        thrown.Should().BeNull("an AgentStateChanged handler's failure must not leave OnAgentConnect");
        stateHeard.Should().Be(1, "the later AgentStateChanged handler hears the change");
        connected.Should().Equal([("1001", "linked-1", "Agent/1001")], "the AgentConnected that follows is still raised");
        manager.GetById("1001")!.State.Should().Be(AgentState.OnCall);
        faults.Count.Should().Be(1);
        logger.Warnings.Should().ContainSingle().Which.Message.Should().Contain("event=" + nameof(AgentManager.AgentStateChanged));
    }

    [Fact]
    public void Raise_ShouldCountTheFaultAndReachTheLaterHandlers_WhenTheLoggerThrowsToo()
    {
        var logger = new ReportThrowingLogger<AgentManager>("Subscriber failed");
        var manager = new AgentManager(logger);
        manager.OnAgentLogin("1001");
        var secondHeard = 0;
        manager.AgentStateChanged += _ => throw new InvalidOperationException("first-handler");
        manager.AgentStateChanged += _ => secondHeard++;
        using var faults = new FaultCounter();

        var thrown = Record.Exception(() => manager.OnAgentPaused("1001", true));

        using var scope = new AssertionScope();
        thrown.Should().BeNull("a failing report changes nothing: the raising call returns normally");
        secondHeard.Should().Be(1, "the later handler receives the event");
        faults.Count.Should().Be(1, "the fault is counted before it is reported");
        logger.Thrown.Should().Be(1, "premise: the logger threw on the report");
    }

    // One subscriber takes the raise's single-target path, two its invocation-list walk: each shape is guarded on both.
    public static TheoryData<AgentEvent, int> AllEventsByHandlerCount
    {
        get
        {
            var data = new TheoryData<AgentEvent, int>();
            foreach (var agentEvent in Enum.GetValues<AgentEvent>())
            {
                data.Add(agentEvent, 1);
                data.Add(agentEvent, 2);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AllEvents))]
    public void Raise_ShouldLogCountAndReturn_WhenTheOnlyHandlerThrows(AgentEvent agentEvent)
    {
        var logger = new CapturingLogger();
        var manager = new AgentManager(logger);
        Prepare(manager);
        var heard = 0;
        var failure = new InvalidOperationException("only-handler");
        Subscribe(manager, agentEvent, () =>
        {
            heard++;
            throw failure;
        });
        using var faults = new FaultCounter();

        var firstRaise = Record.Exception(() => Raise(manager, agentEvent, "a"));
        var warningsAfterFirstRaise = logger.Warnings;
        var secondRaise = Record.Exception(() => Raise(manager, agentEvent, "b"));

        using var scope = new AssertionScope();
        firstRaise.Should().BeNull($"the only {agentEvent} handler's failure must not leave the raising call");
        warningsAfterFirstRaise.Should().ContainSingle("one Warning per failure")
            .Which.Should().Match<LogEntry>(w => w.Message.Contains("event=" + agentEvent, StringComparison.Ordinal)
                && w.Message.Contains("subject=" + ExpectedSubject(agentEvent, "a") + ";", StringComparison.Ordinal)
                && ReferenceEquals(w.Exception, failure));
        secondRaise.Should().BeNull();
        heard.Should().Be(2, "the throwing handler stays subscribed and hears the next event");
        faults.Count.Should().Be(2);
    }

    [Theory]
    [MemberData(nameof(AllEventsByHandlerCount))]
    public void Raise_ShouldNotCatchOutOfMemory_WhenAHandlerThrowsIt(AgentEvent agentEvent, int handlers)
    {
        var logger = new CapturingLogger();
        var manager = new AgentManager(logger);
        Prepare(manager);
        var laterHeard = 0;
        // The one exception type the isolation lets through; only the runtime may throw it, so the test raises it itself.
#pragma warning disable CA2201, MA0012
        Subscribe(manager, agentEvent, () => throw new OutOfMemoryException("handler-oom"));
#pragma warning restore CA2201, MA0012
        if (handlers == 2)
            Subscribe(manager, agentEvent, () => laterHeard++);
        using var faults = new FaultCounter();

        var thrown = Record.Exception(() => Raise(manager, agentEvent, "a"));

        using var scope = new AssertionScope();
        thrown.Should().BeOfType<OutOfMemoryException>($"out of memory is not a subscriber's failure ({agentEvent}, {handlers} handler(s))");
        laterHeard.Should().Be(0);
        logger.Warnings.Should().BeEmpty();
        faults.Count.Should().Be(0);
    }

    [Fact]
    public void Raise_ShouldCountTheFaultBeforeTheWarning_WhenTheLoggerThrowsOutOfMemoryOnIt()
    {
        var manager = new AgentManager(new OutOfMemoryReportLogger());
        manager.OnAgentLogin("1001");
        manager.AgentStateChanged += _ => throw new InvalidOperationException("handler");
        using var faults = new FaultCounter();

        var thrown = Record.Exception(() => manager.OnAgentPaused("1001", true));

        using var scope = new AssertionScope();
        thrown.Should().BeOfType<OutOfMemoryException>("the report's own guard lets out of memory through, as the raise's does");
        faults.Count.Should().Be(1, "the fault is counted before the Warning is written, so it stays counted whatever the logger does");
    }

    [Fact]
    public void OnAgentComplete_ShouldReachTheLaterHandler_WhenAnAgentStateChangedHandlerThrows()
    {
        var manager = new AgentManager(NullLogger.Instance);
        manager.OnAgentLogin("1001");
        manager.OnAgentConnect("1001", "PJSIP/caller-1", "linked-1", "Agent/1001");
        var heard = 0;
        manager.AgentStateChanged += _ => throw new InvalidOperationException("state-handler");
        manager.AgentStateChanged += _ => heard++;
        using var faults = new FaultCounter();

        var thrown = Record.Exception(() => manager.OnAgentComplete("1001", talkTimeSecs: 30));

        using var scope = new AssertionScope();
        thrown.Should().BeNull("an AgentStateChanged handler's failure must not leave OnAgentComplete");
        heard.Should().Be(1);
        manager.GetById("1001")!.CallsTaken.Should().Be(1, "the completion is applied before the event is raised");
        faults.Count.Should().Be(1);
    }

    [Fact]
    public void AnnounceLogin_ShouldAnnounceEveryReplacedAgent_WhenAnAgentLoggedInHandlerThrows()
    {
        var manager = new AgentManager(NullLogger.Instance);
        var announced = new List<string>();
        manager.AgentLoggedIn += _ => throw new InvalidOperationException("login-handler");
        manager.AgentLoggedIn += a => announced.Add(a.AgentId);
        using var faults = new FaultCounter();
        var replaced = manager.ReplaceWithSnapshot(
            [new AgentSnapshotRow("1001", null, "PJSIP/1001", true), new AgentSnapshotRow("2001", null, "PJSIP/2001", true)],
            manager.LiveEventMark());

        var thrown = Record.Exception(() =>
        {
            foreach (var agent in replaced)
                manager.AnnounceLogin(agent);
        });

        using var scope = new AssertionScope();
        thrown.Should().BeNull("a reload's announcement returns normally past a throwing subscriber");
        announced.Should().Equal(["1001", "2001"], "both agents the list reports logged in are announced");
        faults.Count.Should().Be(2);
    }

    // The Warning's subject: the agent's id, or the queue caller's Uniqueid for the two internal queue-caller reports.
    private static string ExpectedSubject(AgentEvent agentEvent, string id) =>
        agentEvent is AgentEvent.QueueCallerConnected or AgentEvent.QueueCallerCompleted ? "caller-" + id : id;

    private static void Prepare(AgentManager manager)
    {
        manager.OnAgentLogin("a");
        manager.OnAgentLogin("b");
    }

    private static void Subscribe(AgentManager manager, AgentEvent agentEvent, Action handler)
    {
        switch (agentEvent)
        {
            case AgentEvent.AgentLoggedIn: manager.AgentLoggedIn += _ => handler(); break;
            case AgentEvent.AgentLoggedOff: manager.AgentLoggedOff += _ => handler(); break;
            case AgentEvent.AgentStateChanged: manager.AgentStateChanged += _ => handler(); break;
            case AgentEvent.AgentConnected: manager.AgentConnected += (_, _, _) => handler(); break;
            case AgentEvent.QueueCallerConnected: manager.QueueCallerConnected += (_, _, _) => handler(); break;
            case AgentEvent.QueueCallerCompleted: manager.QueueCallerCompleted += _ => handler(); break;
            default: throw new ArgumentOutOfRangeException(nameof(agentEvent), agentEvent, null);
        }
    }

    private static void Raise(AgentManager manager, AgentEvent agentEvent, string id)
    {
        switch (agentEvent)
        {
            case AgentEvent.AgentLoggedIn: manager.OnAgentLogin(id, "PJSIP/" + id); break;
            case AgentEvent.AgentLoggedOff: manager.OnAgentLogoff(id); break;
            case AgentEvent.AgentStateChanged: manager.OnAgentPaused(id, true); break;
            case AgentEvent.AgentConnected:
                manager.OnAgentConnect(id, "PJSIP/caller-" + id, "linked-" + id, "Agent/" + id);
                break;
            case AgentEvent.QueueCallerConnected:
                manager.OnQueueCallerConnected("caller-" + id, id, "Agent/" + id);
                break;
            case AgentEvent.QueueCallerCompleted:
                manager.OnQueueCallerCompleted(new QueueCallerCompletion("caller-" + id, "linked-" + id, "Agent/" + id, 5, "sales"));
                break;
            default: throw new ArgumentOutOfRangeException(nameof(agentEvent), agentEvent, null);
        }
    }

    /// <summary>Counts <c>live.agents.subscriber_faults</c> measurements made on the constructing thread.</summary>
    private sealed class FaultCounter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private long _count;

        public FaultCounter()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Verbara.Sdk.Live" && instrument.Name == SubscriberFaults)
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
            {
                if (Environment.CurrentManagedThreadId == _thread)
                    Interlocked.Add(ref _count, value);
            });
            _listener.Start();
        }

        public long Count => Interlocked.Read(ref _count);

        public void Dispose() => _listener.Dispose();
    }

    public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    /// <summary>A logger that throws <see cref="OutOfMemoryException"/> on the subscriber-failure report.</summary>
    private sealed class OutOfMemoryReportLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
#pragma warning disable CA2201, MA0012
            if (formatter(state, exception).Contains("Subscriber failed", StringComparison.Ordinal))
                throw new OutOfMemoryException("logger-oom");
#pragma warning restore CA2201, MA0012
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Warnings => [.. _entries.Where(static e => e.Level == LogLevel.Warning)];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));
    }
}
