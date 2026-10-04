using Verbara.Sdk.Ami.Connection;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using static Verbara.Sdk.Ami.Tests.Connection.AmiDispatchTestKit;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// An event observer whose <c>OnNext</c> throws is logged once at Warning and counted once on
/// <c>ami.events.observer_faults</c>, and delivery goes on — to that observer too. Before, the dispatch caught and
/// dropped the exception: no line, no count.
/// </summary>
/// <remarks>
/// Every cell carries a non-throwing <c>OnEvent</c> handler as its fence: handlers run after every observer's
/// <c>OnNext</c> and its guard, so once that handler has seen the sentinel written last, every observer failure of
/// every earlier event has been logged and counted. The AMI instruments are process-wide, so the class runs in
/// <see cref="AmiEventsDroppedMetricGroup"/>, apart from every other class that makes a handler fail.
/// </remarks>
[Collection(AmiEventsDroppedMetricGroup.Name)]
public sealed class AmiConnectionObserverFaultTests
{
    private const string ObserverFaults = "ami.events.observer_faults";
    private const string HandlerFaults = "ami.events.handler_faults";
    private const string ObserverFaultFragment = "Observer threw on";

    [Fact]
    public async Task Observer_ShouldBeLoggedAndCountedOnce_WhenItThrowsOnOneEvent()
    {
        using var peer = new CancellationTokenSource(Bound * 2);
        using var counters = new AmiCounterTotals(ObserverFaults, HandlerFaults);
        var factory = new PipedSocketFactory();
        var logger = new DispatchLogger();
        await using var connection = Create(factory, logger);
        var throwing = new CountingObserver(throwOn: n => n == 1);
        var second = new CountingObserver(throwOn: _ => false);
        var fence = new CountingObserver(throwOn: _ => false);
        using var s1 = connection.Subscribe(throwing);
        using var s2 = connection.Subscribe(second);
        connection.OnEvent += fence.HandleAsync;
        var socket = await ConnectAsync(connection, factory, peer);

        await WritePeersAsync(socket, 0, 5);
        await WriteSentinelAsync(socket);
        var reached = await CompletesWithinBoundAsync(fence.SentinelReached);

        var faults = logger.Containing(ObserverFaultFragment);
        using (new AssertionScope())
        {
            reached.Should().BeTrue("the handler receives every event up to the sentinel written last");
            faults.Should().ContainSingle("the one observer failure is logged once");
            faults.Should().OnlyContain(e => e.Level == LogLevel.Warning);
            faults.Select(e => e.State.GetValueOrDefault("EventType")).Should().Equal(["PeerStatus"],
                "the line names the type of the event the observer threw on");
            faults.Should().OnlyContain(e => e.Exception is InvalidOperationException, "the observer's exception is logged with it");
            counters.Total(ObserverFaults).Should().Be(1, "the one observer failure is counted once on ami.events.observer_faults");
            counters.Total(HandlerFaults).Should().Be(0, "an observer failure is not a handler failure");
            throwing.Count.Should().Be(6, "the observer that threw stays subscribed and receives all six events");
            second.Count.Should().Be(6, "the second observer receives all six events");
            fence.Count.Should().Be(6, "the handler receives all six events, the one the observer threw on included");
        }
    }

    [Fact]
    public async Task Observer_ShouldBeLoggedAndCountedEveryTime_WhenItThrowsOnEveryEvent()
    {
        using var peer = new CancellationTokenSource(Bound * 2);
        using var counters = new AmiCounterTotals(ObserverFaults, HandlerFaults);
        var factory = new PipedSocketFactory();
        var logger = new DispatchLogger();
        await using var connection = Create(factory, logger);
        var throwing = new CountingObserver(throwOn: _ => true);
        var fence = new CountingObserver(throwOn: _ => false);
        using var subscription = connection.Subscribe(throwing);
        connection.OnEvent += fence.HandleAsync;
        var socket = await ConnectAsync(connection, factory, peer);

        await WritePeersAsync(socket, 0, 4);
        await WriteSentinelAsync(socket);
        var reached = await CompletesWithinBoundAsync(fence.SentinelReached);

        var faults = logger.Containing(ObserverFaultFragment);
        using (new AssertionScope())
        {
            reached.Should().BeTrue("the handler receives every event up to the sentinel written last");
            faults.Should().HaveCount(5, "each of the five failures is logged");
            faults.Should().OnlyContain(e => e.Level == LogLevel.Warning);
            counters.Total(ObserverFaults).Should().Be(5, "each of the five failures is counted");
            throwing.Count.Should().Be(5, "the observer receives all five events");
        }
    }

    /// <summary>
    /// The control: the listener and the logger are live — a handler failure is seen on both — and a handler failure is
    /// not an observer failure. Green before the fix.
    /// </summary>
    [Fact]
    public async Task HandlerFailure_ShouldNotBeCountedAsAnObserverFailure_WhenAHandlerThrowsOnce()
    {
        using var peer = new CancellationTokenSource(Bound * 2);
        using var counters = new AmiCounterTotals(ObserverFaults, HandlerFaults);
        var factory = new PipedSocketFactory();
        var logger = new DispatchLogger();
        await using var connection = Create(factory, logger);
        var throwingHandler = new CountingObserver(throwOn: n => n == 1);
        var observer = new CountingObserver(throwOn: _ => false);
        using var subscription = connection.Subscribe(observer);
        connection.OnEvent += throwingHandler.HandleAsync;
        var socket = await ConnectAsync(connection, factory, peer);

        await WritePeersAsync(socket, 0, 5);
        await WriteSentinelAsync(socket);
        var reached = await CompletesWithinBoundAsync(throwingHandler.SentinelReached);

        using (new AssertionScope())
        {
            reached.Should().BeTrue("the handler receives every event up to the sentinel written last");
            logger.Containing("OnEvent handler threw on").Should().ContainSingle()
                .Which.Level.Should().Be(LogLevel.Warning);
            counters.Total(HandlerFaults).Should().Be(1, "the handler's one failure is counted on ami.events.handler_faults");
            counters.Total(ObserverFaults).Should().Be(0, "a handler failure is not an observer failure");
            logger.Containing(ObserverFaultFragment).Should().BeEmpty("no observer threw");
            observer.Count.Should().Be(6, "the observer receives all six events");
            throwingHandler.Count.Should().Be(6, "the handler that threw goes on receiving events");
        }
    }

    /// <summary>
    /// An observer and an <c>OnEvent</c> handler at once: counts every event it receives, records it before it throws
    /// when <c>throwOn</c> says so (by delivery number, from 1), and completes <see cref="SentinelReached"/> on the
    /// sentinel.
    /// </summary>
    private sealed class CountingObserver(Func<int, bool> throwOn) : IObserver<ManagerEvent>
    {
        private readonly TaskCompletionSource _sentinel = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public Task SentinelReached => _sentinel.Task;

        public ValueTask HandleAsync(ManagerEvent evt)
        {
            OnNext(evt);
            return ValueTask.CompletedTask;
        }

        public void OnNext(ManagerEvent value)
        {
            var n = Interlocked.Increment(ref _count);
            if (IsSentinel(value))
                _sentinel.TrySetResult();
            if (throwOn(n))
                throw new InvalidOperationException("The subscriber fails on this event.");
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}
