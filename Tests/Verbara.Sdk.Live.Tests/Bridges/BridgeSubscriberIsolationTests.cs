using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Live.Bridges;

namespace Verbara.Sdk.Live.Tests.Bridges;

/// <summary>
/// A bridge subscriber's failure is its own: every later subscriber of the same event still hears it, the table finishes
/// its work (a reload's reconciliation included), the failure is logged once at Warning naming the event and counted once
/// on <c>live.bridges.subscriber_faults</c>, and nothing leaves the call that made the change. The counter is read by
/// name, so this class compiles on a tree without it.
/// </summary>
public sealed class BridgeSubscriberIsolationTests
{
    private const string SubscriberFaults = "live.bridges.subscriber_faults";

    public enum BridgeEvent
    {
        BridgeCreated,
        BridgeDestroyed,
        ChannelEntered,
        ChannelLeft,
        TransferOccurred,
        // TransferOccurred again, raised by an attended transfer: it names no channel.
        AttendedTransferOccurred,
    }

    public static TheoryData<BridgeEvent> AllEvents => new(Enum.GetValues<BridgeEvent>());

    [Theory]
    [MemberData(nameof(AllEvents))]
    public void Raise_ShouldReachTheSecondHandlerLogAndCountOnce_WhenTheFirstHandlerThrows(BridgeEvent bridgeEvent)
    {
        var logger = new CapturingLogger();
        var manager = new BridgeManager(logger);
        Prepare(manager, bridgeEvent);
        var firstHeard = 0;
        var secondHeard = 0;
        var failure = new InvalidOperationException("first-handler");
        Subscribe(manager, bridgeEvent, () =>
        {
            firstHeard++;
            throw failure;
        });
        Subscribe(manager, bridgeEvent, () => secondHeard++);
        using var faults = new FaultCounter();

        var firstRaise = Record.Exception(() => Raise(manager, bridgeEvent, "a"));
        var secondHeardAfterFirstRaise = secondHeard;
        var faultsAfterFirstRaise = faults.Count;
        var warningsAfterFirstRaise = logger.Warnings;
        var secondRaise = Record.Exception(() => Raise(manager, bridgeEvent, "b"));

        using var scope = new AssertionScope();
        firstRaise.Should().BeNull($"a {bridgeEvent} handler's failure must not leave the raising call");
        secondHeardAfterFirstRaise.Should().Be(1, $"the handler after a throwing one must still hear {bridgeEvent}, once");
        faultsAfterFirstRaise.Should().Be(1, $"{SubscriberFaults} rises by one per failure");
        warningsAfterFirstRaise.Should().ContainSingle("one Warning per failure")
            .Which.Should().Match<LogEntry>(w => w.Message.Contains("event=" + EventName(bridgeEvent), StringComparison.Ordinal)
                && w.Message.Contains("bridge_id=bridge-a ", StringComparison.Ordinal)
                && w.Message.Contains("channel=" + ExpectedChannel(bridgeEvent, "a") + ";", StringComparison.Ordinal)
                && ReferenceEquals(w.Exception, failure));
        secondRaise.Should().BeNull();
        firstHeard.Should().Be(2, "the throwing handler stays subscribed and hears the next event");
        secondHeard.Should().Be(2);
        faults.Count.Should().Be(2);
    }

    [Theory]
    [MemberData(nameof(AllEvents))]
    public void Raise_ShouldReachEveryHandlerAndCountNothing_WhenNoHandlerThrows(BridgeEvent bridgeEvent)
    {
        var manager = new BridgeManager(NullLogger.Instance);
        Prepare(manager, bridgeEvent);
        var heard = new List<string>();
        Subscribe(manager, bridgeEvent, () => heard.Add("first"));
        Subscribe(manager, bridgeEvent, () => heard.Add("second"));
        using var faults = new FaultCounter();

        Raise(manager, bridgeEvent, "a");

        using var scope = new AssertionScope();
        heard.Should().Equal(["first", "second"], "the negative control: two handlers that do not throw both hear the event in order");
        faults.Count.Should().Be(0);
    }

    [Fact]
    public void Raise_ShouldCountTheFaultAndReachTheLaterHandlers_WhenTheLoggerThrowsToo()
    {
        var logger = new ReportThrowingLogger<BridgeManager>("Subscriber failed");
        var manager = new BridgeManager(logger);
        manager.OnBridgeCreated("bridge-a", "basic", "simple_bridge", null, null);
        var secondHeard = 0;
        manager.ChannelEntered += (_, _) => throw new InvalidOperationException("first-handler");
        manager.ChannelEntered += (_, _) => secondHeard++;
        using var faults = new FaultCounter();

        var thrown = Record.Exception(() => manager.OnChannelEntered("bridge-a", "ch.a"));

        using var scope = new AssertionScope();
        thrown.Should().BeNull("a failing report changes nothing: the raising call returns normally");
        secondHeard.Should().Be(1, "the later handler receives the event");
        faults.Count.Should().Be(1, "the fault is counted before it is reported");
        logger.Thrown.Should().Be(1, "premise: the logger threw on the report");
    }

    // One subscriber takes the raise's single-target path, two its invocation-list walk: each shape is guarded on both.
    public static TheoryData<BridgeEvent, int> AllEventsByHandlerCount
    {
        get
        {
            var data = new TheoryData<BridgeEvent, int>();
            foreach (var bridgeEvent in Enum.GetValues<BridgeEvent>())
            {
                data.Add(bridgeEvent, 1);
                data.Add(bridgeEvent, 2);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AllEvents))]
    public void Raise_ShouldLogCountAndReturn_WhenTheOnlyHandlerThrows(BridgeEvent bridgeEvent)
    {
        var logger = new CapturingLogger();
        var manager = new BridgeManager(logger);
        Prepare(manager, bridgeEvent);
        var heard = 0;
        var failure = new InvalidOperationException("only-handler");
        Subscribe(manager, bridgeEvent, () =>
        {
            heard++;
            throw failure;
        });
        using var faults = new FaultCounter();

        var firstRaise = Record.Exception(() => Raise(manager, bridgeEvent, "a"));
        var warningsAfterFirstRaise = logger.Warnings;
        var secondRaise = Record.Exception(() => Raise(manager, bridgeEvent, "b"));

        using var scope = new AssertionScope();
        firstRaise.Should().BeNull($"the only {bridgeEvent} handler's failure must not leave the raising call");
        warningsAfterFirstRaise.Should().ContainSingle("one Warning per failure")
            .Which.Should().Match<LogEntry>(w => w.Message.Contains("event=" + EventName(bridgeEvent), StringComparison.Ordinal)
                && w.Message.Contains("bridge_id=bridge-a ", StringComparison.Ordinal)
                && w.Message.Contains("channel=" + ExpectedChannel(bridgeEvent, "a") + ";", StringComparison.Ordinal)
                && ReferenceEquals(w.Exception, failure));
        secondRaise.Should().BeNull();
        heard.Should().Be(2, "the throwing handler stays subscribed and hears the next event");
        faults.Count.Should().Be(2);
    }

    [Theory]
    [MemberData(nameof(AllEventsByHandlerCount))]
    public void Raise_ShouldNotCatchOutOfMemory_WhenAHandlerThrowsIt(BridgeEvent bridgeEvent, int handlers)
    {
        var logger = new CapturingLogger();
        var manager = new BridgeManager(logger);
        Prepare(manager, bridgeEvent);
        var laterHeard = 0;
        // The one exception type the isolation lets through; only the runtime may throw it, so the test raises it itself.
#pragma warning disable CA2201, MA0012
        Subscribe(manager, bridgeEvent, () => throw new OutOfMemoryException("handler-oom"));
#pragma warning restore CA2201, MA0012
        if (handlers == 2)
            Subscribe(manager, bridgeEvent, () => laterHeard++);
        using var faults = new FaultCounter();

        var thrown = Record.Exception(() => Raise(manager, bridgeEvent, "a"));

        using var scope = new AssertionScope();
        thrown.Should().BeOfType<OutOfMemoryException>($"out of memory is not a subscriber's failure ({bridgeEvent}, {handlers} handler(s))");
        laterHeard.Should().Be(0);
        logger.Warnings.Should().BeEmpty();
        faults.Count.Should().Be(0);
    }

    [Fact]
    public void Raise_ShouldCountTheFaultBeforeTheWarning_WhenTheLoggerThrowsOutOfMemoryOnIt()
    {
        var manager = new BridgeManager(new OutOfMemoryReportLogger());
        manager.OnBridgeCreated("bridge-a", "basic", "simple_bridge", null, null);
        manager.ChannelEntered += (_, _) => throw new InvalidOperationException("handler");
        using var faults = new FaultCounter();

        var thrown = Record.Exception(() => manager.OnChannelEntered("bridge-a", "ch.a"));

        using var scope = new AssertionScope();
        thrown.Should().BeOfType<OutOfMemoryException>("the report's own guard lets out of memory through, as the raise's does");
        faults.Count.Should().Be(1, "the fault is counted before the Warning is written, so it stays counted whatever the logger does");
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldRemoveEveryOmittedBridgeAndAdmitTheListedOne_WhenABridgeDestroyedHandlerThrows()
    {
        var manager = new BridgeManager(NullLogger.Instance);
        manager.OnBridgeCreated("gone-1", "basic", "simple_bridge", null, null);
        manager.OnBridgeCreated("gone-2", "basic", "simple_bridge", null, null);
        var destroyed = new List<string>();
        manager.BridgeDestroyed += _ => throw new InvalidOperationException("destroyed-handler");
        manager.BridgeDestroyed += b => destroyed.Add(b.BridgeUniqueid);
        using var faults = new FaultCounter();

        Exception? thrown;
        using (var window = manager.OpenSnapshotWindow())
        {
            thrown = Record.Exception(() => manager.ReconcileWithSnapshot(window,
                [new BridgeSnapshotEntry("listed", "basic", "simple_bridge", null, null)],
                new Dictionary<string, IReadOnlyCollection<string>> { ["listed"] = ["ch.1"] },
                removeOmitted: true));
        }

        using var scope = new AssertionScope();
        thrown.Should().BeNull("a reload's reconciliation completes past a throwing subscriber");
        destroyed.Should().BeEquivalentTo(["gone-1", "gone-2"], "both bridges the listing omits are removed and announced");
        manager.ActiveBridges.Select(b => b.BridgeUniqueid).Should().Equal(["listed"], "the listed bridge is admitted");
        manager.GetBridgeForChannel("ch.1")!.BridgeUniqueid.Should().Be("listed");
        faults.Count.Should().Be(2);
    }

    // The Warning's channel: the channel's unique id for a channel event, the transfer's target for a blind transfer, and
    // none for a bridge's own creation or destruction or an attended transfer.
    private static string ExpectedChannel(BridgeEvent bridgeEvent, string id) => bridgeEvent switch
    {
        BridgeEvent.ChannelEntered or BridgeEvent.ChannelLeft => "ch." + id,
        BridgeEvent.TransferOccurred => "PJSIP/target-" + id,
        _ => "(null)",
    };

    private static string EventName(BridgeEvent bridgeEvent) =>
        bridgeEvent == BridgeEvent.AttendedTransferOccurred ? nameof(BridgeEvent.TransferOccurred) : bridgeEvent.ToString();

    private static void Prepare(BridgeManager manager, BridgeEvent bridgeEvent)
    {
        if (bridgeEvent is BridgeEvent.BridgeCreated or BridgeEvent.TransferOccurred or BridgeEvent.AttendedTransferOccurred)
            return;

        manager.OnBridgeCreated("bridge-a", "basic", "simple_bridge", null, null);
        manager.OnBridgeCreated("bridge-b", "basic", "simple_bridge", null, null);
        if (bridgeEvent == BridgeEvent.ChannelLeft)
        {
            manager.OnChannelEntered("bridge-a", "ch.a");
            manager.OnChannelEntered("bridge-b", "ch.b");
        }
    }

    private static void Subscribe(BridgeManager manager, BridgeEvent bridgeEvent, Action handler)
    {
        switch (bridgeEvent)
        {
            case BridgeEvent.BridgeCreated: manager.BridgeCreated += _ => handler(); break;
            case BridgeEvent.BridgeDestroyed: manager.BridgeDestroyed += _ => handler(); break;
            case BridgeEvent.ChannelEntered: manager.ChannelEntered += (_, _) => handler(); break;
            case BridgeEvent.ChannelLeft: manager.ChannelLeft += (_, _) => handler(); break;
            case BridgeEvent.TransferOccurred or BridgeEvent.AttendedTransferOccurred:
                manager.TransferOccurred += _ => handler();
                break;
            default: throw new ArgumentOutOfRangeException(nameof(bridgeEvent), bridgeEvent, null);
        }
    }

    private static void Raise(BridgeManager manager, BridgeEvent bridgeEvent, string id)
    {
        var bridgeId = "bridge-" + id;
        switch (bridgeEvent)
        {
            case BridgeEvent.BridgeCreated: manager.OnBridgeCreated(bridgeId, "basic", "simple_bridge", null, null); break;
            case BridgeEvent.BridgeDestroyed: manager.OnBridgeDestroyed(bridgeId); break;
            case BridgeEvent.ChannelEntered: manager.OnChannelEntered(bridgeId, "ch." + id); break;
            case BridgeEvent.ChannelLeft: manager.OnChannelLeft(bridgeId, "ch." + id); break;
            case BridgeEvent.TransferOccurred: manager.OnBlindTransfer(bridgeId, "PJSIP/target-" + id, "100", "default"); break;
            case BridgeEvent.AttendedTransferOccurred: manager.OnAttendedTransfer(bridgeId, "second-" + id, "Bridge", "Success"); break;
            default: throw new ArgumentOutOfRangeException(nameof(bridgeEvent), bridgeEvent, null);
        }
    }

    /// <summary>Counts <c>live.bridges.subscriber_faults</c> measurements made on the constructing thread.</summary>
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
