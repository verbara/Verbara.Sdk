using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Live.Queues;
using Verbara.Sdk.Live.Server;

namespace Verbara.Sdk.Live.Tests.Queues;

/// <summary>
/// A queue subscriber's failure is its own: every later subscriber of the same event still hears it, the table finishes
/// its work, the failure is logged once at Warning naming the event and counted once on
/// <c>live.queues.subscriber_faults</c>, and nothing leaves the call that made the change. The counter is read by name,
/// so this class compiles on a tree without it.
/// </summary>
public sealed class QueueSubscriberIsolationTests
{
    private const string SubscriberFaults = "live.queues.subscriber_faults";
    private const string Queue = "sales";
    private static readonly EventLossEpoch Epoch = new(0, 1, 0);

    public enum QueueEvent
    {
        QueueUpdated,
        MemberAdded,
        MemberRemoved,
        MemberStatusChanged,
        MemberPaused,
        CallerJoined,
        CallerLeft,
    }

    public static TheoryData<QueueEvent> AllEvents => new(Enum.GetValues<QueueEvent>());

    [Theory]
    [MemberData(nameof(AllEvents))]
    public void Raise_ShouldReachTheSecondHandlerLogAndCountOnce_WhenTheFirstHandlerThrows(QueueEvent queueEvent)
    {
        var logger = new CapturingLogger();
        var manager = new QueueManager(logger);
        Prepare(manager, queueEvent);
        var firstHeard = 0;
        var secondHeard = 0;
        var failure = new InvalidOperationException("first-handler");
        Subscribe(manager, queueEvent, () =>
        {
            firstHeard++;
            throw failure;
        });
        Subscribe(manager, queueEvent, () => secondHeard++);
        using var faults = new FaultCounter();

        var firstRaise = Record.Exception(() => Raise(manager, queueEvent, "a"));
        var secondHeardAfterFirstRaise = secondHeard;
        var faultsAfterFirstRaise = faults.Count;
        var warningsAfterFirstRaise = logger.Warnings;
        var secondRaise = Record.Exception(() => Raise(manager, queueEvent, "b"));

        using var scope = new AssertionScope();
        firstRaise.Should().BeNull($"a {queueEvent} handler's failure must not leave the raising call");
        secondHeardAfterFirstRaise.Should().Be(1, $"the handler after a throwing one must still hear {queueEvent}, once");
        faultsAfterFirstRaise.Should().Be(1, $"{SubscriberFaults} rises by one per failure");
        warningsAfterFirstRaise.Should().ContainSingle("one Warning per failure")
            .Which.Should().Match<LogEntry>(w => w.Message.Contains("event=" + queueEvent, StringComparison.Ordinal)
                && w.Message.Contains("subject=" + ExpectedSubject(queueEvent, "a") + ";", StringComparison.Ordinal)
                && ReferenceEquals(w.Exception, failure));
        secondRaise.Should().BeNull();
        firstHeard.Should().Be(2, "the throwing handler stays subscribed and hears the next event");
        secondHeard.Should().Be(2);
        faults.Count.Should().Be(2);
    }

    [Theory]
    [MemberData(nameof(AllEvents))]
    public void Raise_ShouldReachEveryHandlerAndCountNothing_WhenNoHandlerThrows(QueueEvent queueEvent)
    {
        var manager = new QueueManager(NullLogger.Instance);
        Prepare(manager, queueEvent);
        var heard = new List<string>();
        Subscribe(manager, queueEvent, () => heard.Add("first"));
        Subscribe(manager, queueEvent, () => heard.Add("second"));
        using var faults = new FaultCounter();

        Raise(manager, queueEvent, "a");

        using var scope = new AssertionScope();
        heard.Should().Equal(["first", "second"], "the negative control: two handlers that do not throw both hear the event in order");
        faults.Count.Should().Be(0);
    }

    [Fact]
    public void Raise_ShouldCountTheFaultAndReachTheLaterHandlers_WhenTheLoggerThrowsToo()
    {
        var logger = new ReportThrowingLogger<QueueManager>("Subscriber failed");
        var manager = new QueueManager(logger);
        manager.OnMemberAdded(Queue, "PJSIP/a", "a", 0, false, 1);
        var secondHeard = 0;
        manager.MemberStatusChanged += (_, _) => throw new InvalidOperationException("first-handler");
        manager.MemberStatusChanged += (_, _) => secondHeard++;
        using var faults = new FaultCounter();

        var thrown = Record.Exception(() => manager.OnMemberStatusChanged(Queue, "PJSIP/a", 2));

        using var scope = new AssertionScope();
        thrown.Should().BeNull("a failing report changes nothing: the raising call returns normally");
        secondHeard.Should().Be(1, "the later handler receives the event");
        faults.Count.Should().Be(1, "the fault is counted before it is reported");
        logger.Thrown.Should().Be(1, "premise: the logger threw on the report");
    }

    public enum OutOfMemoryShape
    {
        OneValue,
        TwoValues,
        CallerQueueStatus,
    }

    public static TheoryData<OutOfMemoryShape> AllOutOfMemoryShapes => new(Enum.GetValues<OutOfMemoryShape>());

    [Theory]
    [MemberData(nameof(AllOutOfMemoryShapes))]
    public void Raise_ShouldNotCatchOutOfMemory_WhenAHandlerThrowsIt(OutOfMemoryShape shape)
    {
        var logger = new CapturingLogger();
        var manager = new QueueManager(logger);
        Action raise;
        // The one exception type the isolation lets through; only the runtime may throw it, so the test raises it itself.
#pragma warning disable CA2201, MA0012
        switch (shape)
        {
            case OutOfMemoryShape.OneValue:
                manager.QueueUpdated += _ => throw new OutOfMemoryException("handler-oom");
                raise = () => manager.OnQueueParams(Queue, 0, "ringall", 0, 0, 0, 0, 0);
                break;
            case OutOfMemoryShape.TwoValues:
                manager.OnMemberAdded(Queue, "PJSIP/a", "a", 0, false, 1);
                manager.MemberStatusChanged += (_, _) => throw new OutOfMemoryException("handler-oom");
                raise = () => manager.OnMemberStatusChanged(Queue, "PJSIP/a", 2);
                break;
            case OutOfMemoryShape.CallerQueueStatus:
                manager.CallerQueueStatus += (_, _, _) => throw new OutOfMemoryException("handler-oom");
                raise = () => manager.OnCallerQueueStatus("u1", "PJSIP/c-1", "TIMEOUT");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }
#pragma warning restore CA2201, MA0012
        using var faults = new FaultCounter();

        var thrown = Record.Exception(raise);

        using var scope = new AssertionScope();
        thrown.Should().BeOfType<OutOfMemoryException>($"out of memory is not a subscriber's failure ({shape})");
        logger.Warnings.Should().BeEmpty();
        faults.Count.Should().Be(0);
    }

    public enum InternalQueueEvent
    {
        CallerQueueStatus,
        CallerAbandonReported,
        QueueSnapshotCompleted,
    }

    public static TheoryData<InternalQueueEvent> AllInternalEvents => new(Enum.GetValues<InternalQueueEvent>());

    [Theory]
    [MemberData(nameof(AllInternalEvents))]
    public void RaiseInternal_ShouldReachTheSecondHandlerLogAndCountOnce_WhenTheFirstHandlerThrows(
        InternalQueueEvent internalEvent)
    {
        var logger = new CapturingLogger();
        var manager = new QueueManager(logger);
        var secondHeard = 0;
        var failure = new InvalidOperationException("internal-handler");
        Action raise;
        switch (internalEvent)
        {
            case InternalQueueEvent.CallerQueueStatus:
                manager.CallerQueueStatus += (_, _, _) => throw failure;
                manager.CallerQueueStatus += (_, _, _) => secondHeard++;
                raise = () => manager.OnCallerQueueStatus("u1", "PJSIP/c-1", "TIMEOUT");
                break;
            case InternalQueueEvent.CallerAbandonReported:
                manager.CallerAbandonReported += (_, _) => throw failure;
                manager.CallerAbandonReported += (_, _) => secondHeard++;
                raise = () => manager.OnCallerAbandonReported("u1", Queue);
                break;
            case InternalQueueEvent.QueueSnapshotCompleted:
                manager.QueueSnapshotCompleted += _ => throw failure;
                manager.QueueSnapshotCompleted += _ => secondHeard++;
                raise = () =>
                {
                    using var window = manager.OpenSnapshotWindow();
                    manager.CompleteSnapshot(window);
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(internalEvent), internalEvent, null);
        }
        using var faults = new FaultCounter();

        var thrown = Record.Exception(raise);

        using var scope = new AssertionScope();
        thrown.Should().BeNull($"the SDK's own {internalEvent} subscribers are isolated the same way");
        secondHeard.Should().Be(1, $"the handler after a throwing one must still hear {internalEvent}, once");
        faults.Count.Should().Be(1, $"{SubscriberFaults} rises by one per failure");
        logger.Warnings.Should().ContainSingle("one Warning per failure")
            .Which.Should().Match<LogEntry>(w => w.Message.Contains("event=" + internalEvent, StringComparison.Ordinal)
                && ReferenceEquals(w.Exception, failure));
    }

    [Fact]
    public void OnCallerLeaveReported_ShouldReachTheSessionLayersLaterHandler_WhenAnEarlierInternalHandlerThrows()
    {
        var logger = new CapturingLogger();
        var manager = new QueueManager(logger);
        var reached = new List<string>();
        manager.CallerLeaveReported += _ => throw new InvalidOperationException("internal-handler");
        manager.CallerLeaveReported += report => reached.Add(report.UniqueId);
        using var faults = new FaultCounter();

        var thrown = Record.Exception(() => manager.OnCallerLeaveReported("u1", Queue, "PJSIP/c-1", Epoch));

        using var scope = new AssertionScope();
        thrown.Should().BeNull("the SDK's own subscribers are isolated the same way");
        reached.Should().Equal(["u1"]);
        faults.Count.Should().Be(1);
        logger.Warnings.Should().ContainSingle().Which.Message.Should().Contain(nameof(QueueManager.CallerLeaveReported));
    }

    [Fact]
    public void OnSnapshotMember_ShouldAdmitEveryRowAndReturnNormally_WhenAMemberAddedHandlerThrows()
    {
        var manager = new QueueManager(NullLogger.Instance);
        var added = 0;
        manager.MemberAdded += (_, _) =>
        {
            added++;
            throw new InvalidOperationException("snapshot-member-handler");
        };
        using var faults = new FaultCounter();

        Exception? thrown;
        using (var window = manager.OpenSnapshotWindow())
        {
            thrown = Record.Exception(() =>
            {
                manager.OnSnapshotMember(window, Queue, "PJSIP/a", "a", 0, false, 1);
                manager.OnSnapshotMember(window, Queue, "PJSIP/b", "b", 0, false, 1);
            });
        }

        using var scope = new AssertionScope();
        thrown.Should().BeNull("a reload's row whose handler throws returns normally");
        added.Should().Be(2, "both rows are announced");
        manager.GetByName(Queue)!.Members.Keys.Should().BeEquivalentTo(["PJSIP/a", "PJSIP/b"]);
        faults.Count.Should().Be(2);
    }

    // The Warning's subject: the member's interface or the caller's channel, or the queue itself for QueueUpdated.
    private static string ExpectedSubject(QueueEvent queueEvent, string id) =>
        queueEvent == QueueEvent.QueueUpdated ? "queue-" + id : "PJSIP/" + id;

    private static void Prepare(QueueManager manager, QueueEvent queueEvent)
    {
        switch (queueEvent)
        {
            case QueueEvent.MemberRemoved or QueueEvent.MemberStatusChanged or QueueEvent.MemberPaused:
                manager.OnMemberAdded(Queue, "PJSIP/a", "a", 0, false, 1);
                manager.OnMemberAdded(Queue, "PJSIP/b", "b", 0, false, 1);
                break;
            case QueueEvent.CallerLeft:
                manager.OnCallerJoined(Queue, "PJSIP/a", "100", 1);
                manager.OnCallerJoined(Queue, "PJSIP/b", "101", 2);
                break;
        }
    }

    private static void Subscribe(QueueManager manager, QueueEvent queueEvent, Action handler)
    {
        switch (queueEvent)
        {
            case QueueEvent.QueueUpdated: manager.QueueUpdated += _ => handler(); break;
            case QueueEvent.MemberAdded: manager.MemberAdded += (_, _) => handler(); break;
            case QueueEvent.MemberRemoved: manager.MemberRemoved += (_, _) => handler(); break;
            case QueueEvent.MemberStatusChanged: manager.MemberStatusChanged += (_, _) => handler(); break;
            case QueueEvent.MemberPaused: manager.MemberPaused += (_, _) => handler(); break;
            case QueueEvent.CallerJoined: manager.CallerJoined += (_, _) => handler(); break;
            case QueueEvent.CallerLeft: manager.CallerLeft += (_, _) => handler(); break;
            default: throw new ArgumentOutOfRangeException(nameof(queueEvent), queueEvent, null);
        }
    }

    private static void Raise(QueueManager manager, QueueEvent queueEvent, string id)
    {
        var subject = "PJSIP/" + id;
        switch (queueEvent)
        {
            case QueueEvent.QueueUpdated: manager.OnQueueParams("queue-" + id, 0, "ringall", 0, 0, 0, 0, 0); break;
            case QueueEvent.MemberAdded: manager.OnMemberAdded(Queue, subject, id, 0, false, 1); break;
            case QueueEvent.MemberRemoved: manager.OnMemberRemoved(Queue, subject); break;
            case QueueEvent.MemberStatusChanged: manager.OnMemberStatusChanged(Queue, subject, 2); break;
            case QueueEvent.MemberPaused: manager.OnMemberPaused(Queue, subject, true); break;
            case QueueEvent.CallerJoined: manager.OnCallerJoined(Queue, subject, "100", 1); break;
            case QueueEvent.CallerLeft: manager.OnCallerLeft(Queue, subject); break;
            default: throw new ArgumentOutOfRangeException(nameof(queueEvent), queueEvent, null);
        }
    }

    /// <summary>Counts <c>live.queues.subscriber_faults</c> measurements made on the constructing thread.</summary>
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
