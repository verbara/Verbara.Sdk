using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Channels;
using Verbara.Sdk.Live.Server;

namespace Verbara.Sdk.Live.Tests.Channels;

/// <summary>
/// A channel subscriber's failure is its own: the later subscribers still hear the event, the table finishes its work,
/// the failure is logged once at Warning and counted once on <c>live.channels.subscriber_faults</c>, and nothing
/// leaves the call that made the change. The counter is read by name, so this class compiles on a tree without it.
/// </summary>
public sealed class ChannelSubscriberIsolationTests
{
    private const string SubscriberFaults = "live.channels.subscriber_faults";
    private const string ObserverFaults = "ami.events.observer_faults";

    public enum ChannelEvent
    {
        ChannelAdded,
        ChannelRemoved,
        ChannelStateChanged,
        ChannelDialBegin,
        ChannelDialEnd,
        ChannelHeld,
        ChannelUnheld,
    }

    public static TheoryData<ChannelEvent> AllEvents => new(Enum.GetValues<ChannelEvent>());

    [Theory]
    [MemberData(nameof(AllEvents))]
    public void Raise_ShouldReachTheSecondHandlerAndReturnNormally_WhenTheFirstHandlerThrows(ChannelEvent channelEvent)
    {
        var manager = new ChannelManager(NullLogger.Instance);
        manager.OnNewChannel("a.1", "PJSIP/a", ChannelState.Ring);
        manager.OnNewChannel("b.1", "PJSIP/b", ChannelState.Ring);
        var firstHeard = 0;
        var secondHeard = 0;
        Subscribe(manager, channelEvent, _ =>
        {
            firstHeard++;
            throw new InvalidOperationException("first-handler");
        });
        Subscribe(manager, channelEvent, _ => secondHeard++);

        var firstRaise = Record.Exception(() => Raise(manager, channelEvent, "a.1", "new-a.1"));
        var secondHeardAfterFirstRaise = secondHeard;
        var secondRaise = Record.Exception(() => Raise(manager, channelEvent, "b.1", "new-b.1"));

        using var scope = new AssertionScope();
        firstRaise.Should().BeNull($"a {channelEvent} handler's failure must not leave the raising call");
        secondHeardAfterFirstRaise.Should().Be(1, $"the handler after a throwing one must still hear {channelEvent}, once");
        secondRaise.Should().BeNull();
        firstHeard.Should().Be(2, "the throwing handler stays subscribed and hears the next event");
        secondHeard.Should().Be(2);
    }

    [Theory]
    [MemberData(nameof(AllEvents))]
    public void Raise_ShouldReachEveryHandler_WhenNoHandlerThrows(ChannelEvent channelEvent)
    {
        var manager = new ChannelManager(NullLogger.Instance);
        manager.OnNewChannel("a.1", "PJSIP/a", ChannelState.Ring);
        var heard = new List<string>();
        Subscribe(manager, channelEvent, _ => heard.Add("first"));
        Subscribe(manager, channelEvent, _ => heard.Add("second"));

        Raise(manager, channelEvent, "a.1", "new-a.1");

        heard.Should().Equal(["first", "second"], "the negative control: two handlers that do not throw both hear the event in order");
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldRemoveAndAdmitEveryChannel_WhenTheRemovedAndAddedHandlersThrow()
    {
        var manager = new ChannelManager(NullLogger.Instance);
        manager.OnNewChannel("held-1", "PJSIP/held-1", ChannelState.Up);
        manager.OnNewChannel("held-2", "PJSIP/held-2", ChannelState.Up);
        manager.OnNewChannel("held-3", "PJSIP/held-3", ChannelState.Up);
        var removed = 0;
        var added = 0;
        manager.ChannelRemoved += _ =>
        {
            removed++;
            throw new InvalidOperationException("removed-handler");
        };
        manager.ChannelAdded += _ =>
        {
            added++;
            throw new InvalidOperationException("added-handler");
        };
        ChannelSnapshotEntry[] snapshot =
        [
            new("new-1", "PJSIP/new-1", ChannelState.Up),
            new("new-2", "PJSIP/new-2", ChannelState.Up),
        ];

        Exception? thrown;
        using (var window = manager.OpenReadWindow())
            thrown = Record.Exception(() => manager.ReconcileWithSnapshot(snapshot, window));

        using var scope = new AssertionScope();
        thrown.Should().BeNull("the reconciliation returns normally past a throwing handler");
        removed.Should().Be(3, "the three held channels the snapshot omits are removed");
        added.Should().Be(2, "the two channels the snapshot lists are admitted");
        manager.ActiveChannels.Select(static c => c.UniqueId).Should().BeEquivalentTo(["new-1", "new-2"]);
    }

    [Fact]
    public async Task ChannelRemoved_ShouldBeReportedAsAChannelSubscriberFailure_WhenItsHandlerThrowsOnTheServersEventPath()
    {
        var logger = new CapturingLogger<VerbaraServer>();
        var connection = Substitute.For<IAmiConnection>();
        IObserver<ManagerEvent>? observer = null;
        connection.Subscribe(Arg.Do<IObserver<ManagerEvent>>(obs => observer = obs)).Returns(Substitute.For<IDisposable>());
        connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(Empty());
        await using var server = new VerbaraServer(connection, logger);
        await server.StartAsync();
        observer.Should().NotBeNull();
        observer!.OnNext(new NewChannelEvent { UniqueId = "gone.1", Channel = "PJSIP/gone", ChannelState = "Up" });
        var failure = new InvalidOperationException("removed-handler");
        server.Channels.ChannelRemoved += _ => throw failure;
        using var faults = new FaultCounter();
        var logged = logger.Entries.Count;

        var thrown = Record.Exception(() => observer.OnNext(new HangupEvent { UniqueId = "gone.1", Channel = "PJSIP/gone", Cause = 16 }));

        using var scope = new AssertionScope();
        thrown.Should().BeNull("the AMI observer receives no subscriber failure, so nothing is logged as an AMI observer failure");
        var warnings = logger.Entries.Skip(logged).Where(static e => e.Level == LogLevel.Warning).ToList();
        warnings.Should().ContainSingle("one Warning per failure");
        warnings.Should().AllSatisfy(w =>
        {
            w.Message.Should().Contain(nameof(ChannelManager.ChannelRemoved)).And.Contain("gone.1");
            w.Exception.Should().BeSameAs(failure);
        });
        faults.Count(SubscriberFaults).Should().Be(1, $"{SubscriberFaults} rises by one");
        faults.Count(ObserverFaults).Should().Be(0, $"{ObserverFaults} does not rise");
    }

    [Fact]
    public void Raise_ShouldCountTheFaultAndReachTheLaterHandlers_WhenTheLoggerThrowsToo()
    {
        var manager = new ChannelManager(new ThrowingLogger());
        manager.OnNewChannel("a.1", "PJSIP/a", ChannelState.Ring);
        var secondHeard = 0;
        manager.ChannelStateChanged += _ => throw new InvalidOperationException("first-handler");
        manager.ChannelStateChanged += _ => secondHeard++;
        using var faults = new FaultCounter();

        var thrown = Record.Exception(() => manager.OnNewState("a.1", ChannelState.Up));

        using var scope = new AssertionScope();
        thrown.Should().BeNull("a failing report changes nothing: the raising call returns normally");
        secondHeard.Should().Be(1, "the later handler receives the event");
        faults.Count(SubscriberFaults).Should().Be(1, "the fault is counted before it is reported");
    }

    [Fact]
    public void Raise_ShouldNotCatchOutOfMemory_WhenAHandlerThrowsIt()
    {
        var logger = new CapturingLogger<ChannelManager>();
        var manager = new ChannelManager(logger);
        manager.OnNewChannel("a.1", "PJSIP/a", ChannelState.Ring);
        // The one exception type the isolation lets through; only the runtime may throw it, so the test raises it itself.
#pragma warning disable CA2201, MA0012
        manager.ChannelStateChanged += _ => throw new OutOfMemoryException("handler-oom");
#pragma warning restore CA2201, MA0012
        using var faults = new FaultCounter();

        var thrown = Record.Exception(() => manager.OnNewState("a.1", ChannelState.Up));

        using var scope = new AssertionScope();
        thrown.Should().BeOfType<OutOfMemoryException>("out of memory is not a subscriber's failure");
        logger.Entries.Where(static e => e.Level == LogLevel.Warning).Should().BeEmpty();
        faults.Count(SubscriberFaults).Should().Be(0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void OnNewState_ShouldAllocateAtMostOneEntryAndOneFrame_WhateverTheNumberOfHandlers(int handlers)
    {
        const int channels = 1_000;
        const int observedEventBound = 200; // the living one-entry bound of ChannelManagerAllocationTests
        var ids = Enumerable.Range(0, channels)
            .Select(i => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{i}.1")).ToArray();
        var manager = new ChannelManager(NullLogger.Instance);
        foreach (var id in ids)
            manager.OnNewChannel(id, "PJSIP/" + id, ChannelState.Ring);
        var heard = 0;
        for (var h = 0; h < handlers; h++)
            manager.ChannelStateChanged += _ => heard++;

        for (var round = 0; round < 3; round++)
            foreach (var id in ids)
                manager.OnNewState(id, ChannelState.Up);
        heard = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var id in ids)
            manager.OnNewState(id, ChannelState.Up);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        heard.Should().Be(channels * handlers);
        allocated.Should().BeLessThanOrEqualTo((long)observedEventBound * channels,
            $"{handlers} handler(s) cost the one-entry bound and nothing per handler");
    }

    private static void Subscribe(ChannelManager manager, ChannelEvent channelEvent, Action<AsteriskChannel> handler)
    {
        switch (channelEvent)
        {
            case ChannelEvent.ChannelAdded: manager.ChannelAdded += handler; break;
            case ChannelEvent.ChannelRemoved: manager.ChannelRemoved += handler; break;
            case ChannelEvent.ChannelStateChanged: manager.ChannelStateChanged += handler; break;
            case ChannelEvent.ChannelDialBegin: manager.ChannelDialBegin += handler; break;
            case ChannelEvent.ChannelDialEnd: manager.ChannelDialEnd += handler; break;
            case ChannelEvent.ChannelHeld: manager.ChannelHeld += handler; break;
            case ChannelEvent.ChannelUnheld: manager.ChannelUnheld += handler; break;
            default: throw new ArgumentOutOfRangeException(nameof(channelEvent), channelEvent, null);
        }
    }

    private static void Raise(ChannelManager manager, ChannelEvent channelEvent, string heldId, string newId)
    {
        switch (channelEvent)
        {
            case ChannelEvent.ChannelAdded: manager.OnNewChannel(newId, "PJSIP/" + newId, ChannelState.Ring); break;
            case ChannelEvent.ChannelRemoved: manager.OnHangup(heldId); break;
            case ChannelEvent.ChannelStateChanged: manager.OnNewState(heldId, ChannelState.Up); break;
            case ChannelEvent.ChannelDialBegin: manager.OnDialBegin(heldId, "dest", "PJSIP/dest", dialString: null); break;
            case ChannelEvent.ChannelDialEnd: manager.OnDialEnd(heldId, "ANSWER"); break;
            case ChannelEvent.ChannelHeld: manager.OnHold(heldId, "default"); break;
            case ChannelEvent.ChannelUnheld: manager.OnUnhold(heldId); break;
            default: throw new ArgumentOutOfRangeException(nameof(channelEvent), channelEvent, null);
        }
    }

    private static async IAsyncEnumerable<ManagerEvent> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }

    /// <summary>Counts the named counters' measurements made on the constructing thread, so parallel tests add nothing.</summary>
    private sealed class FaultCounter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);
        private readonly int _thread = Environment.CurrentManagedThreadId;

        public FaultCounter()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Name is SubscriberFaults or ObserverFaults)
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
            {
                if (Environment.CurrentManagedThreadId == _thread)
                    _counts.AddOrUpdate(instrument.Name, value, (_, total) => total + value);
            });
            _listener.Start();
        }

        public long Count(string name) => _counts.GetValueOrDefault(name);

        public void Dispose() => _listener.Dispose();
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries => [.. _entries];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));
    }

    /// <summary>A logger whose provider throws on every Warning or above it is asked to write.</summary>
    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("logger-provider");
    }
}
