using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Live.MeetMe;

namespace Verbara.Sdk.Live.Tests.MeetMe;

/// <summary>
/// A conference subscriber's failure is its own: every later subscriber of the same event still hears it, the table
/// finishes its work (a reload's reconciliation included), the failure is logged once at Warning naming the event and
/// counted once on <c>live.meetme.subscriber_faults</c>, and nothing leaves the call that made the change. The counter
/// is read by name, so this class compiles on a tree without it.
/// </summary>
public sealed class ConferenceSubscriberIsolationTests
{
    private const string SubscriberFaults = "live.meetme.subscriber_faults";
    private const string Room = "100";

    public enum ConferenceEvent
    {
        UserJoined,
        UserLeft,
        UserStateChanged,
    }

    public static TheoryData<ConferenceEvent> AllEvents => new(Enum.GetValues<ConferenceEvent>());

    [Theory]
    [MemberData(nameof(AllEvents))]
    public void Raise_ShouldReachTheSecondHandlerLogAndCountOnce_WhenTheFirstHandlerThrows(ConferenceEvent conferenceEvent)
    {
        var logger = new CapturingLogger();
        var manager = new MeetMeManager(logger);
        Prepare(manager, conferenceEvent);
        var firstHeard = 0;
        var secondHeard = 0;
        var failure = new InvalidOperationException("first-handler");
        Subscribe(manager, conferenceEvent, () =>
        {
            firstHeard++;
            throw failure;
        });
        Subscribe(manager, conferenceEvent, () => secondHeard++);
        using var faults = new FaultCounter();

        var firstRaise = Record.Exception(() => Raise(manager, conferenceEvent, "a"));
        var secondHeardAfterFirstRaise = secondHeard;
        var faultsAfterFirstRaise = faults.Count;
        var warningsAfterFirstRaise = logger.Warnings;
        var secondRaise = Record.Exception(() => Raise(manager, conferenceEvent, "b"));

        using var scope = new AssertionScope();
        firstRaise.Should().BeNull($"a {conferenceEvent} handler's failure must not leave the raising call");
        secondHeardAfterFirstRaise.Should().Be(1, $"the handler after a throwing one must still hear {conferenceEvent}, once");
        faultsAfterFirstRaise.Should().Be(1, $"{SubscriberFaults} rises by one per failure");
        warningsAfterFirstRaise.Should().ContainSingle("one Warning per failure")
            .Which.Should().Match<LogEntry>(w => w.Message.Contains(conferenceEvent.ToString(), StringComparison.Ordinal)
                && w.Message.Contains(Channel("a"), StringComparison.Ordinal)
                && ReferenceEquals(w.Exception, failure));
        secondRaise.Should().BeNull();
        firstHeard.Should().Be(2, "the throwing handler stays subscribed and hears the next event");
        secondHeard.Should().Be(2);
        faults.Count.Should().Be(2);
    }

    [Theory]
    [MemberData(nameof(AllEvents))]
    public void Raise_ShouldReachEveryHandlerAndCountNothing_WhenNoHandlerThrows(ConferenceEvent conferenceEvent)
    {
        var manager = new MeetMeManager(NullLogger.Instance);
        Prepare(manager, conferenceEvent);
        var heard = new List<string>();
        Subscribe(manager, conferenceEvent, () => heard.Add("first"));
        Subscribe(manager, conferenceEvent, () => heard.Add("second"));
        using var faults = new FaultCounter();

        Raise(manager, conferenceEvent, "a");

        using var scope = new AssertionScope();
        heard.Should().Equal(["first", "second"], "the negative control: two handlers that do not throw both hear the event in order");
        faults.Count.Should().Be(0);
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldRemoveEveryOmittedParticipantAndReturnNormally_WhenAUserLeftHandlerThrows()
    {
        var manager = new MeetMeManager(NullLogger.Instance);
        manager.OnUserJoined(Room, 1, Channel("a"), "uid-a");
        manager.OnUserJoined(Room, 2, Channel("b"), "uid-b");
        manager.OnUserJoined(Room, 3, Channel("c"), "uid-c");
        var left = 0;
        manager.UserLeft += (_, _) =>
        {
            left++;
            throw new InvalidOperationException("left-handler");
        };
        using var faults = new FaultCounter();

        Exception? thrown;
        using (var window = manager.OpenSnapshotWindow())
        {
            thrown = Record.Exception(() => manager.ReconcileWithSnapshot(window, [Room],
                new Dictionary<string, IReadOnlyList<ConferenceSnapshotParticipant>>
                {
                    [Room] = [new ConferenceSnapshotParticipant(Channel("a"), "uid-a", false, false)],
                },
                removeOmitted: true));
        }

        using var scope = new AssertionScope();
        thrown.Should().BeNull("the reconciliation returns normally past a throwing handler");
        left.Should().Be(2, "the two participants the listing omits both leave");
        manager.GetRoom(Room)!.Users.Keys.Should().BeEquivalentTo([Channel("a")]);
        faults.Count.Should().Be(2);
    }

    [Fact]
    public void Raise_ShouldCountTheFaultAndReachTheLaterHandlers_WhenTheLoggerThrowsToo()
    {
        var logger = new ReportThrowingLogger<MeetMeManager>("Subscriber failed");
        var manager = new MeetMeManager(logger);
        var secondHeard = 0;
        manager.UserJoined += (_, _) => throw new InvalidOperationException("first-handler");
        manager.UserJoined += (_, _) => secondHeard++;
        using var faults = new FaultCounter();

        var thrown = Record.Exception(() => manager.OnUserJoined(Room, 1, Channel("a")));

        using var scope = new AssertionScope();
        thrown.Should().BeNull("a failing report changes nothing: the raising call returns normally");
        secondHeard.Should().Be(1, "the later handler receives the event");
        faults.Count.Should().Be(1, "the fault is counted before it is reported");
        logger.Thrown.Should().Be(1, "premise: the logger threw on the report");
    }

    [Fact]
    public void Raise_ShouldNotCatchOutOfMemory_WhenAHandlerThrowsIt()
    {
        var logger = new CapturingLogger();
        var manager = new MeetMeManager(logger);
#pragma warning disable CA2201, MA0012
        manager.UserJoined += (_, _) => throw new OutOfMemoryException("handler-oom");
#pragma warning restore CA2201, MA0012
        using var faults = new FaultCounter();

        var thrown = Record.Exception(() => manager.OnUserJoined(Room, 1, Channel("a")));

        using var scope = new AssertionScope();
        thrown.Should().BeOfType<OutOfMemoryException>("out of memory is not a subscriber's failure");
        logger.Warnings.Should().BeEmpty();
        faults.Count.Should().Be(0);
    }

    private static string Channel(string id) => "PJSIP/conf-" + id;

    private static void Prepare(MeetMeManager manager, ConferenceEvent conferenceEvent)
    {
        if (conferenceEvent is ConferenceEvent.UserLeft or ConferenceEvent.UserStateChanged)
        {
            manager.OnUserJoined(Room, 1, Channel("a"));
            manager.OnUserJoined(Room, 2, Channel("b"));
        }
    }

    private static void Subscribe(MeetMeManager manager, ConferenceEvent conferenceEvent, Action handler)
    {
        switch (conferenceEvent)
        {
            case ConferenceEvent.UserJoined: manager.UserJoined += (_, _) => handler(); break;
            case ConferenceEvent.UserLeft: manager.UserLeft += (_, _) => handler(); break;
            case ConferenceEvent.UserStateChanged: manager.UserStateChanged += (_, _) => handler(); break;
            default: throw new ArgumentOutOfRangeException(nameof(conferenceEvent), conferenceEvent, null);
        }
    }

    private static void Raise(MeetMeManager manager, ConferenceEvent conferenceEvent, string id)
    {
        switch (conferenceEvent)
        {
            case ConferenceEvent.UserJoined: manager.OnUserJoined(Room, 9, Channel(id)); break;
            case ConferenceEvent.UserLeft: manager.OnUserLeft(Room, Channel(id)); break;
            case ConferenceEvent.UserStateChanged: manager.OnUserMuted(Room, Channel(id), true); break;
            default: throw new ArgumentOutOfRangeException(nameof(conferenceEvent), conferenceEvent, null);
        }
    }

    /// <summary>Counts <c>live.meetme.subscriber_faults</c> measurements made on the constructing thread.</summary>
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
