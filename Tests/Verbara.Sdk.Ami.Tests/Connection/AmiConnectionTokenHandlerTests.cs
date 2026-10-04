using System.Collections.Concurrent;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using static Verbara.Sdk.Ami.Tests.Connection.AmiDispatchTestKit;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// An event handler subscribed through <see cref="AmiConnection.Subscribe(Func{ManagerEvent, CancellationToken, ValueTask})"/>
/// receives a token the caller's ending cancels, and nothing else does; it shares one list, in subscription order, with
/// the <see cref="AmiConnection.OnEvent"/> handlers; a failure is reported as an <c>OnEvent</c> handler's is, except an
/// <see cref="OperationCanceledException"/> after the caller's ending, which is the handler doing what it was asked.
/// </summary>
/// <remarks>
/// Times are hang bounds only: a handler signals that it has started and the test waits on that signal. The
/// <c>ami.events.handler_faults</c> counter is process-wide, so the class runs in <see cref="AmiEventsDroppedMetricGroup"/>.
/// </remarks>
[Collection(AmiEventsDroppedMetricGroup.Name)]
public sealed partial class AmiConnectionTokenHandlerTests
{
    private const string HandlerFaults = "ami.events.handler_faults";
    private const string HandlerFaultFragment = "OnEvent handler threw on";
    private const string StoppedFragment = "Event handler stopped on the caller's ending";

    /// <summary>How long a handler waits on its token before giving up by itself: far beyond the 1 s the ending is allowed.</summary>
    private static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(6);

    [Fact]
    public async Task Subscribe_ShouldStopTheHandlerAndEndWithinOneSecond_WhenTheCallerDisposesAndTheHandlerHonoursItsToken()
    {
        using var peer = new CancellationTokenSource(Bound * 2);
        using var counters = new AmiCounterTotals(HandlerFaults);
        var factory = new PipedSocketFactory();
        var logger = new DispatchLogger();
        var connection = Create(factory, logger);
        var entered = NewSignal();
        var stoppedByToken = 0;
        CancellationToken seen = default;
        using var subscription = connection.Subscribe(async (_, ct) =>
        {
            seen = ct;
            entered.TrySetResult();
            try
            {
                await Task.Delay(HandlerWait, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Interlocked.Exchange(ref stoppedByToken, 1);
                throw;
            }
        });
        var socket = await ConnectAsync(connection, factory, peer);

        await WritePeersAsync(socket, 0, 1);
        (await CompletesWithinBoundAsync(entered.Task)).Should().BeTrue("the handler starts on the event");
        var started = TimeProvider.System.GetTimestamp();
        var ended = await CompletesWithinBoundAsync(connection.DisposeAsync().AsTask());
        var elapsed = TimeProvider.System.GetElapsedTime(started);

        using (new AssertionScope())
        {
            ended.Should().BeTrue();
            seen.IsCancellationRequested.Should().BeTrue("the caller's ending cancels the handler's token");
            Volatile.Read(ref stoppedByToken).Should().Be(1, "the handler returned because its token was cancelled");
            elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "the handler stopped when asked, so the ending did not wait out its 6 s");
            logger.Entries.Where(e => e.Level >= LogLevel.Warning).Should().BeEmpty("honouring the token is not a fault");
            counters.Total(HandlerFaults).Should().Be(0, "honouring the token is not counted as a fault");
            logger.Containing(StoppedFragment).Should().ContainSingle("one Debug line records the stop");
            logger.Containing(StoppedFragment).Should().OnlyContain(e => e.Level == LogLevel.Debug);
        }
    }

    [Fact]
    public async Task Subscribe_ShouldStillHoldTheEnding_WhenTheHandlerIgnoresItsToken()
    {
        using var peer = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var connection = Create(factory, new DispatchLogger());
        var entered = NewSignal();
        var release = NewSignal();
        var tokenCancelled = NewSignal();
        var returned = 0;
        var cancelledWhenReturned = false;
        using var subscription = connection.Subscribe(async (_, ct) =>
        {
            ct.Register(() => tokenCancelled.TrySetResult());
            entered.TrySetResult();
            await release.Task;
            cancelledWhenReturned = ct.IsCancellationRequested;
            Volatile.Write(ref returned, 1);
        });
        var socket = await ConnectAsync(connection, factory, peer);

        await WritePeersAsync(socket, 0, 1);
        (await CompletesWithinBoundAsync(entered.Task)).Should().BeTrue("the handler starts on the event");
        var dispose = connection.DisposeAsync().AsTask();
        (await CompletesWithinBoundAsync(tokenCancelled.Task)).Should().BeTrue("the ending cancels the token first");
        var completedWhileHeld = dispose.IsCompleted;
        release.TrySetResult();
        var ended = await CompletesWithinBoundAsync(dispose);

        using (new AssertionScope())
        {
            ended.Should().BeTrue();
            completedWhileHeld.Should().BeFalse("the ending waits for the dispatch in progress");
            Volatile.Read(ref returned).Should().Be(1, "DisposeAsync completed only after the handler returned");
            cancelledWhenReturned.Should().BeTrue("the token reads cancelled when the handler returns");
        }
    }

    [Fact]
    public async Task Subscribe_ShouldNotCancelTheToken_WhenTheConnectionIsLostAndReconnects()
    {
        const int buffered = 20;
        using var peer = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var logger = new DispatchLogger();
        await using var connection = Create(factory, logger, o =>
        {
            o.AutoReconnect = true;
            o.ReconnectInitialDelay = TimeSpan.FromMilliseconds(5);
            o.ReconnectMaxDelay = TimeSpan.FromMilliseconds(5);
        });
        var held = NewSignal();
        var release = NewSignal();
        var afterReconnect = NewSignal();
        var received = new ConcurrentQueue<(string? Peer, bool Cancelled)>();
        var count = 0;
        using var subscription = connection.Subscribe(async (evt, ct) =>
        {
            var peerName = (evt as PeerStatusEvent)?.Peer;
            received.Enqueue((peerName, ct.IsCancellationRequested));
            if (Interlocked.Increment(ref count) == 1)
            {
                held.TrySetResult();
                await release.Task;
            }

            if (peerName == "SIP/after")
                afterReconnect.TrySetResult();
        });
        var first = await ConnectAsync(connection, factory, peer);
        await WritePeersAsync(first, 0, 1);
        (await CompletesWithinBoundAsync(held.Task)).Should().BeTrue("the handler holds the first event");
        await WritePeersAsync(first, 1, buffered);
        await PingAsync(connection, first, peer);
        var reconnected = NewSignal();
        connection.Reconnected += () => reconnected.TrySetResult();
        var next = Task.Run(async () =>
        {
            var socket = await factory.NextAsync(peer.Token);
            await socket.CompleteLoginAsync(peer.Token);
            return socket;
        }, peer.Token);

        first.CloseFromPeer();
        release.TrySetResult();
        var back = await CompletesWithinBoundAsync(reconnected.Task);
        var second = await next.WaitAsync(Bound);
        (await second.WriteEventAsync("PeerStatus", [new("Peer", "SIP/after")])).Should().BeTrue();
        var deliveredAfter = await CompletesWithinBoundAsync(afterReconnect.Task);

        var all = received.ToList();
        using (new AssertionScope())
        {
            back.Should().BeTrue("the connection reconnects after the loss");
            deliveredAfter.Should().BeTrue("the handler keeps receiving events after the reconnect");
            all.Take(1 + buffered).Select(r => r.Peer).Should().Equal(Enumerable.Range(0, 1 + buffered).Select(PeerName),
                "the loss delivers every buffered event, in order");
            all.Should().OnlyContain(r => !r.Cancelled, "a loss nobody asked for does not cancel the token, nor does the reconnect");
            connection.State.Should().Be(AmiConnectionState.Connected);
        }
    }

    [Fact]
    public async Task Handlers_ShouldBeCalledInSubscriptionOrder_WhenSubscribedThroughOnEventAndTheOverload()
    {
        using var peer = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory, new DispatchLogger());
        var order = new ConcurrentQueue<string>();
        var release = NewSignal();
        var allEntered = NewSignal();
        var entered = 0;
        var returned = 0;
        var returnedWhenNextArrived = -1;
        var next = NewSignal();

        async ValueTask Hold(string name, ManagerEvent evt)
        {
            if (IsSentinel(evt))
            {
                if (name == "A")
                {
                    Volatile.Write(ref returnedWhenNextArrived, Volatile.Read(ref returned));
                    next.TrySetResult();
                }

                return;
            }

            order.Enqueue(name);
            if (Interlocked.Increment(ref entered) == 3)
                allEntered.TrySetResult();
            await release.Task;
            Interlocked.Increment(ref returned);
        }

        connection.OnEvent += evt => Hold("A", evt);
        using var b = connection.Subscribe((evt, _) => Hold("B", evt));
        connection.OnEvent += evt => Hold("C", evt);
        var socket = await ConnectAsync(connection, factory, peer);

        await WritePeersAsync(socket, 0, 1);
        var all = await CompletesWithinBoundAsync(allEntered.Task);
        var enteredBeforeRelease = order.ToList();
        await WriteSentinelAsync(socket);
        release.TrySetResult();
        var nextDelivered = await CompletesWithinBoundAsync(next.Task);

        using (new AssertionScope())
        {
            all.Should().BeTrue("all three handlers are started before any of them is released");
            enteredBeforeRelease.Should().Equal(["A", "B", "C"], "handlers are called in the order they subscribed, whichever way");
            nextDelivered.Should().BeTrue();
            Volatile.Read(ref returnedWhenNextArrived).Should().Be(3, "the next event is delivered only after all three have returned");
        }
    }

    [Fact]
    public async Task Subscribe_ShouldRemoveTheHandler_WhenTheSubscriptionIsDisposedTwice()
    {
        using var peer = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory, new DispatchLogger());
        var tokenHandlerCalls = 0;
        var subscription = connection.Subscribe((_, _) =>
        {
            Interlocked.Increment(ref tokenHandlerCalls);
            return ValueTask.CompletedTask;
        });
        var fence = new Fence();
        connection.OnEvent += fence.HandleAsync;
        var socket = await ConnectAsync(connection, factory, peer);

        subscription.Dispose();
        var second = () => subscription.Dispose();
        second.Should().NotThrow("a second disposal does nothing");
        await WritePeersAsync(socket, 0, 5);
        await WriteSentinelAsync(socket);
        var reached = await CompletesWithinBoundAsync(fence.SentinelReached);

        using (new AssertionScope())
        {
            reached.Should().BeTrue();
            Volatile.Read(ref tokenHandlerCalls).Should().Be(0, "the disposed subscription's handler receives none of the events");
            fence.Count.Should().Be(6, "the OnEvent handler alongside receives all of them");
        }
    }

    /// <summary>
    /// The same delegate subscribed twice through the overload is two entries: disposing the first removes that entry,
    /// not the last equal one, so the second keeps its place behind the <c>OnEvent</c> handler subscribed between them.
    /// </summary>
    [Fact]
    public async Task Subscribe_ShouldRemoveThatSubscriptionNotTheLastEqualHandler_WhenTheSameHandlerIsSubscribedTwice()
    {
        using var peer = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory, new DispatchLogger());
        var order = new ConcurrentQueue<string>();
        Func<ManagerEvent, CancellationToken, ValueTask> h = (evt, _) =>
        {
            if (!IsSentinel(evt))
                order.Enqueue("H");
            return ValueTask.CompletedTask;
        };
        var fence = new Fence();
        var s1 = connection.Subscribe(h);
        connection.OnEvent += evt =>
        {
            if (!IsSentinel(evt))
                order.Enqueue("G");
            return ValueTask.CompletedTask;
        };
        var s2 = connection.Subscribe(h);
        connection.OnEvent += fence.HandleAsync;
        var socket = await ConnectAsync(connection, factory, peer);

        s1.Dispose();
        await WritePeersAsync(socket, 0, 1);
        await WriteSentinelAsync(socket);
        var first = await CompletesWithinBoundAsync(fence.SentinelReached);
        var afterFirstDispose = order.ToList();
        order.Clear();
        fence.Reset();
        s2.Dispose();
        await WritePeersAsync(socket, 1, 1);
        await WriteSentinelAsync(socket);
        var second = await CompletesWithinBoundAsync(fence.SentinelReached);
        var afterSecondDispose = order.ToList();

        using (new AssertionScope())
        {
            first.Should().BeTrue();
            second.Should().BeTrue();
            afterFirstDispose.Should().Equal(["G", "H"], "the first subscription's entry is gone and the second keeps its place after G");
            afterSecondDispose.Should().Equal(["G"], "with both subscriptions disposed H is not entered while G is");
        }
    }

    [Fact]
    public async Task Subscribe_ShouldReportTheFailureAsAnOnEventHandlerIs_WhenTheHandlerThrows()
    {
        const int later = 10;
        using var peer = new CancellationTokenSource(Bound * 2);
        using var counters = new AmiCounterTotals(HandlerFaults);
        var factory = new PipedSocketFactory();
        var logger = new DispatchLogger();
        await using var connection = Create(factory, logger);
        var calls = 0;
        var sentinel = NewSignal();
        using var subscription = connection.Subscribe((evt, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new InvalidOperationException("The handler fails on this event.");
            if (IsSentinel(evt))
                sentinel.TrySetResult();
            return ValueTask.CompletedTask;
        });
        var observer = new Fence();
        using var observed = connection.Subscribe(observer);
        var socket = await ConnectAsync(connection, factory, peer);

        await WritePeersAsync(socket, 0, 1 + later);
        await WriteSentinelAsync(socket);
        var reached = await CompletesWithinBoundAsync(sentinel.Task);

        var faults = logger.Containing(HandlerFaultFragment);
        using (new AssertionScope())
        {
            reached.Should().BeTrue("delivery goes on after the handler threw");
            faults.Should().ContainSingle();
            faults.Should().OnlyContain(e => e.Level == LogLevel.Warning);
            faults.Select(e => e.State.GetValueOrDefault("EventType")).Should().Equal(["PeerStatus"]);
            counters.Total(HandlerFaults).Should().Be(1);
            Volatile.Read(ref calls).Should().Be(1 + later + 1, $"the handler receives all {later} later events and the sentinel");
            observer.Count.Should().Be(1 + later + 1, "the observer receives every event");
        }
    }

    [Theory]
    [InlineData("thrown")]
    [InlineData("task")]
    public async Task Subscribe_ShouldReportACancellationAsAFault_WhenTheCallerHasNotEndedTheConnection(string how)
    {
        using var peer = new CancellationTokenSource(Bound * 2);
        using var counters = new AmiCounterTotals(HandlerFaults);
        var factory = new PipedSocketFactory();
        var logger = new DispatchLogger();
        await using var connection = Create(factory, logger);
        var calls = 0;
        var sentinel = NewSignal();
        using var own = new CancellationTokenSource();
        await own.CancelAsync();
        using var subscription = connection.Subscribe((evt, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                return how == "thrown"
                    ? throw new OperationCanceledException(own.Token)
                    : ValueTask.FromCanceled(own.Token);
            }

            if (IsSentinel(evt))
                sentinel.TrySetResult();
            return ValueTask.CompletedTask;
        });
        var socket = await ConnectAsync(connection, factory, peer);

        await WritePeersAsync(socket, 0, 1);
        await WriteSentinelAsync(socket);
        var reached = await CompletesWithinBoundAsync(sentinel.Task);

        using (new AssertionScope())
        {
            reached.Should().BeTrue();
            logger.Containing(HandlerFaultFragment).Should().ContainSingle("a cancellation before the caller's ending is a fault");
            logger.Containing(HandlerFaultFragment).Should().OnlyContain(e => e.Level == LogLevel.Warning);
            counters.Total(HandlerFaults).Should().Be(1);
            logger.Containing(StoppedFragment).Should().BeEmpty();
        }
    }

    [Fact]
    public async Task OnEvent_ShouldStillHoldTheEnding_WhenItsHandlerWaits()
    {
        using var peer = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var connection = Create(factory, new DispatchLogger());
        var entered = NewSignal();
        var release = NewSignal();
        var ending = NewSignal();
        var returned = 0;
        connection.OnEvent += async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            Volatile.Write(ref returned, 1);
        };
        // A probe on the same event that learns, through its token, when the caller's ending has begun.
        using var probe = connection.Subscribe((_, ct) =>
        {
            ct.Register(() => ending.TrySetResult());
            return ValueTask.CompletedTask;
        });
        var socket = await ConnectAsync(connection, factory, peer);

        await WritePeersAsync(socket, 0, 1);
        (await CompletesWithinBoundAsync(entered.Task)).Should().BeTrue();
        var dispose = connection.DisposeAsync().AsTask();
        (await CompletesWithinBoundAsync(ending.Task)).Should().BeTrue("the caller's ending has begun");
        var completedWhileHeld = dispose.IsCompleted;
        release.TrySetResult();
        var ended = await CompletesWithinBoundAsync(dispose);

        using (new AssertionScope())
        {
            ended.Should().BeTrue();
            completedWhileHeld.Should().BeFalse("an OnEvent handler holds the ending, as before");
            Volatile.Read(ref returned).Should().Be(1, "DisposeAsync completed only after the handler returned");
        }
    }

    /// <summary>
    /// The valve's own pin: an <c>OnEvent</c> handler whose task ends cancelled during the caller's ending is still a
    /// fault, as it was before the overload existed. Only a token handler is excused.
    /// </summary>
    [Fact]
    public async Task OnEvent_ShouldStillReportACancellationAsAFault_WhenItEndsDuringTheCallersEnding()
    {
        using var peer = new CancellationTokenSource(Bound * 2);
        using var counters = new AmiCounterTotals(HandlerFaults);
        using var own = new CancellationTokenSource();
        var factory = new PipedSocketFactory();
        var logger = new DispatchLogger();
        var connection = Create(factory, logger);
        var entered = NewSignal();
        var overloadSawEnding = NewSignal();
        connection.OnEvent += async _ =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, own.Token);
        };
        using var alongside = connection.Subscribe(async (_, ct) =>
        {
            var cancelled = NewSignal();
            using var registration = ct.Register(() => cancelled.TrySetResult());
            await cancelled.Task;
            overloadSawEnding.TrySetResult();
        });
        var socket = await ConnectAsync(connection, factory, peer);

        await WritePeersAsync(socket, 0, 1);
        (await CompletesWithinBoundAsync(entered.Task)).Should().BeTrue();
        var dispose = connection.DisposeAsync().AsTask();
        (await CompletesWithinBoundAsync(overloadSawEnding.Task)).Should().BeTrue("the overload handler's token reads cancelled");
        await own.CancelAsync();
        var ended = await CompletesWithinBoundAsync(dispose);

        using (new AssertionScope())
        {
            ended.Should().BeTrue();
            logger.Containing(HandlerFaultFragment).Should().ContainSingle("an OnEvent handler's cancellation is a fault, as before");
            logger.Containing(HandlerFaultFragment).Should().OnlyContain(e => e.Level == LogLevel.Warning);
            counters.Total(HandlerFaults).Should().Be(1);
            logger.Containing(StoppedFragment).Should().BeEmpty("no handler stopped on its token");
        }
    }

    [Fact]
    public async Task Subscribe_ShouldThrowArgumentNullAndSubscribeNothing_WhenTheHandlerIsNull()
    {
        using var peer = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory, new DispatchLogger());

        var act = () => connection.Subscribe((Func<ManagerEvent, CancellationToken, ValueTask>)null!);

        act.Should().Throw<ArgumentNullException>();
        var fence = new Fence();
        connection.OnEvent += fence.HandleAsync;
        var socket = await ConnectAsync(connection, factory, peer);
        await WritePeersAsync(socket, 0, 2);
        await WriteSentinelAsync(socket);
        (await CompletesWithinBoundAsync(fence.SentinelReached)).Should().BeTrue(
            "the dispatch is unharmed: nothing null was subscribed");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A Ping the peer answers: once it returns, every event written before the answer has been read.</summary>
    private static async Task PingAsync(AmiConnection connection, PipedSocket socket, CancellationTokenSource peer)
    {
        var ping = connection.SendActionAsync(new PingAction()).AsTask();
        var action = await socket.ReadActionAsync(peer.Token).WaitAsync(Bound);
        PipedSocket.IsPing(action).Should().BeTrue("the connection sends the Ping");
        (await socket.RespondAsync("Success", PipedSocket.ActionIdOf(action!))).Should().BeTrue();
        (await CompletesWithinBoundAsync(ping)).Should().BeTrue("the Ping is answered");
    }

    /// <summary>
    /// An observer and an <c>OnEvent</c> handler that never fails: counts every event and completes
    /// <see cref="SentinelReached"/> on the sentinel.
    /// </summary>
    private sealed class Fence : IObserver<ManagerEvent>
    {
        private TaskCompletionSource _sentinel = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public Task SentinelReached => Volatile.Read(ref _sentinel).Task;

        public void Reset() =>
            Volatile.Write(ref _sentinel, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public ValueTask HandleAsync(ManagerEvent evt)
        {
            OnNext(evt);
            return ValueTask.CompletedTask;
        }

        public void OnNext(ManagerEvent value)
        {
            Interlocked.Increment(ref _count);
            if (IsSentinel(value))
                Volatile.Read(ref _sentinel).TrySetResult();
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}
