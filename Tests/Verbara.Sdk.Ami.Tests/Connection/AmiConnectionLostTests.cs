using System.Collections.Concurrent;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// <c>AmiConnection.Lost</c> announces, once, each ending of an established connection that its caller did not ask
/// for, with what ended it: <see langword="null"/> when the stream ended, a <see cref="TimeoutException"/> when the
/// heartbeat's Ping went unanswered, and the reader's own exception when the stream could not be read. It is raised
/// after <see cref="AmiConnection.State"/> has left <see cref="AmiConnectionState.Connected"/>, before the
/// <see cref="AmiConnection.Reconnected"/> of the same outage, whether <c>AutoReconnect</c> is on or off; never for the
/// caller's own <c>DisconnectAsync</c> or <c>DisposeAsync</c>, and not a second time when the reconnect loop gives up.
/// Its handlers run one at a time on the thread pool, read when the announcement is delivered: a handler that throws is
/// logged and the ones after it still run, a slow one holds neither the reconnect nor anything but the notifications
/// queued behind it, and one that disposes the connection and waits does not deadlock.
/// </summary>
/// <remarks>
/// <para>
/// A real <see cref="AmiConnection"/> over the in-memory <see cref="PipedSocket"/> harness, with a 50 ms reconnect
/// backoff and, where it is on, a 50 ms heartbeat whose Ping waits 250 ms. Every socket's peer logs in and answers Pings
/// unless the test says otherwise.
/// </para>
/// <para>
/// Nothing here waits on the wall clock: every wait is bounded by <see cref="Bound"/> and ends on the signal it asserts.
/// An absence is proved on a drained queue: the test ends the connection with <c>DisposeAsync</c>, which joins any
/// ending in flight, the give-up's included, then awaits <c>PendingNotifications</c>, the tail of the queue the
/// announcements are delivered on, and only then counts. The signals used besides the handlers' own are the
/// connection's log lines: the n-th <c>[AMI] Reconnecting</c> is written after the n-th loss was queued, and the n-th
/// <c>[AMI] Connected</c> once the n-th session is up.
/// </para>
/// </remarks>
public sealed class AmiConnectionLostTests
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Lost_ShouldBeRaisedOnceWithNoCause_WhenThePeerClosesTheStream()
    {
        using var peerCts = new CancellationTokenSource(Bound * 3);
        var sockets = new PipedSocketFactory();
        var lost = NewSignal<Exception?>();
        var causes = new ConcurrentQueue<Exception?>();
        var statesSeen = new ConcurrentQueue<AmiConnectionState>();
        // The reconnect's peer logs in only once the handler has read the state, so the reading cannot see the next
        // session's Connected however late the handler is delivered.
        var serving = ServeEverySocketAsync(sockets, peerCts.Token,
            beforeLogin: socket => socket == 2 ? lost.Task : Task.CompletedTask);
        var connection = Create(sockets);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        var reconnected = NewSignal();
        connection.Lost += cause =>
        {
            statesSeen.Enqueue(connection.State);
            causes.Enqueue(cause);
            lost.TrySetResult(cause);
        };
        connection.Reconnected += () => reconnected.TrySetResult();

        sockets.Created[0].CloseFromPeer();
        var raised = await CompletesWithinBoundAsync(lost.Task);
        var back = raised && await CompletesWithinBoundAsync(reconnected.Task);
        var drained = await EndAndDrainAsync(connection);

        using (new AssertionScope())
        {
            raised.Should().BeTrue("the peer closed an established connection nobody asked to end");
            back.Should().BeTrue("the connection reconnects to the peer that is still up");
            drained.Should().BeTrue("the dispose returns and every queued notification is delivered");
            causes.Should().Equal([null], "the loss is announced once, and an ended stream carries no exception");
            statesSeen.Should().ContainSingle().Which.Should().NotBe(AmiConnectionState.Connected,
                "the loss is announced after State has left Connected");
        }

        await peerCts.CancelAsync();
        await serving;
    }

    [Fact]
    public async Task Lost_ShouldCarryATimeoutException_WhenTheHeartbeatGoesUnanswered()
    {
        using var peerCts = new CancellationTokenSource(Bound * 3);
        var sockets = new PipedSocketFactory();
        // The peer logs in and never answers a Ping: a PBX that is silent, not gone.
        var serving = ServeEverySocketAsync(sockets, peerCts.Token, answerPings: false);
        var connection = Create(sockets, heartbeat: true);
        var lost = NewSignal<Exception?>();
        connection.Lost += cause => lost.TrySetResult(cause);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);

        var raised = await CompletesWithinBoundAsync(lost.Task);
        var drained = await EndAndDrainAsync(connection);

        using (new AssertionScope())
        {
            raised.Should().BeTrue("an unanswered heartbeat ends an established connection nobody asked to end");
            (raised ? await lost.Task : null).Should().BeOfType<TimeoutException>(
                "the loss is announced as a heartbeat timeout, not as a stream the peer closed");
            drained.Should().BeTrue("the dispose returns and every queued notification is delivered");
        }

        await peerCts.CancelAsync();
        await serving;
    }

    /// <summary>
    /// The transport's exception reaches the announcement as the very instance the stream failed with: the AMI reader
    /// does not wrap it.
    /// </summary>
    [Fact]
    public async Task Lost_ShouldCarryTheReadersException_WhenTheStreamCannotBeRead()
    {
        using var peerCts = new CancellationTokenSource(Bound * 3);
        var sockets = new PipedSocketFactory();
        var serving = ServeEverySocketAsync(sockets, peerCts.Token);
        var connection = Create(sockets);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        var lost = NewSignal<Exception?>();
        var raisedCount = 0;
        connection.Lost += cause =>
        {
            Interlocked.Increment(ref raisedCount);
            lost.TrySetResult(cause);
        };
        var injected = new IOException("the transport broke mid-read");

        sockets.Created[0].FaultFromPeer(injected);
        var raised = await CompletesWithinBoundAsync(lost.Task);
        var drained = await EndAndDrainAsync(connection);

        using (new AssertionScope())
        {
            raised.Should().BeTrue("a stream that cannot be read ends an established connection nobody asked to end");
            (raised ? await lost.Task : null).Should().BeSameAs(injected,
                "the loss carries the exception the reader failed with");
            drained.Should().BeTrue("the dispose returns and every queued notification is delivered");
            Volatile.Read(ref raisedCount).Should().Be(1, "one loss is announced once");
        }

        await peerCts.CancelAsync();
        await serving;
    }

    [Theory]
    [InlineData(nameof(AmiConnection.DisconnectAsync))]
    [InlineData(nameof(AmiConnection.DisposeAsync))]
    public async Task Lost_ShouldNotBeRaised_WhenTheCallerEndsTheConnection(string ending)
    {
        using var peerCts = new CancellationTokenSource(Bound * 3);
        var sockets = new PipedSocketFactory();
        var serving = ServeEverySocketAsync(sockets, peerCts.Token);
        var connection = Create(sockets);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        var raised = 0;
        // Subscribed while the caller ends the connection, as a live server is when a host stops the connection first.
        connection.Lost += _ => Interlocked.Increment(ref raised);

        var ended = await CompletesWithinBoundAsync(EndAsync(connection, ending));
        var drained = await EndAndDrainAsync(connection);

        using (new AssertionScope())
        {
            ended.Should().BeTrue($"{ending} returns");
            drained.Should().BeTrue("a later dispose returns and every queued notification is delivered");
            connection.State.Should().Be(AmiConnectionState.Disconnected);
            Volatile.Read(ref raised).Should().Be(0, $"the caller's {ending} is an ending it asked for, not a lost connection");
        }

        await peerCts.CancelAsync();
        await serving;
    }

    [Theory]
    [InlineData(nameof(AmiConnection.DisconnectAsync))]
    [InlineData(nameof(AmiConnection.DisposeAsync))]
    public async Task Lost_ShouldNotBeRaised_WhenAnEventHandlerEndsTheConnection(string ending)
    {
        using var peerCts = new CancellationTokenSource(Bound * 3);
        var sockets = new PipedSocketFactory();
        var serving = ServeEverySocketAsync(sockets, peerCts.Token);
        var connection = Create(sockets);
        var raised = 0;
        connection.Lost += _ => Interlocked.Increment(ref raised);
        var handlerEnded = NewSignal();
        connection.OnEvent += async _ =>
        {
            // The caller's ending, from inside the connection's own event dispatch.
            await EndAsync(connection, ending);
            handlerEnded.TrySetResult();
        };
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);

        var written = await sockets.Created[0].WriteEventAsync("FullyBooted");
        var ended = await CompletesWithinBoundAsync(handlerEnded.Task);
        var drained = await EndAndDrainAsync(connection);

        using (new AssertionScope())
        {
            written.Should().BeTrue("the peer sends one event");
            ended.Should().BeTrue($"the handler's {ending} returns");
            drained.Should().BeTrue("a later dispose returns and every queued notification is delivered");
            connection.State.Should().Be(AmiConnectionState.Disconnected);
            Volatile.Read(ref raised).Should().Be(0,
                $"a {ending} called from the connection's own event dispatch is the caller's ending, not a lost connection");
        }

        await peerCts.CancelAsync();
        await serving;
    }

    [Fact]
    public async Task Lost_ShouldBeRaisedOnlyForTheLoss_WhenTheReconnectLoopGivesUp()
    {
        using var peerCts = new CancellationTokenSource(Bound * 3);
        // Only the first socket accepts its connect: every reconnect is refused, as by an Asterisk that stays down.
        var sockets = new PipedSocketFactory { ConnectsAccepted = 1 };
        var serving = ServeEverySocketAsync(sockets, peerCts.Token);
        var logger = new SignalingLogger<AmiConnection>();
        var connection = Create(sockets, logger: logger, maxReconnectAttempts: 3);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        var causes = new ConcurrentQueue<Exception?>();
        connection.Lost += causes.Enqueue;
        // Nothing else ends this connection, so the line is the loop giving up, written once it has released.
        var gaveUp = logger.Logged("[AMI] Disconnected");

        sockets.Created[0].CloseFromPeer();
        var ended = await CompletesWithinBoundAsync(gaveUp);
        var stateAtGiveUp = connection.State;
        var drained = await EndAndDrainAsync(connection);

        using (new AssertionScope())
        {
            ended.Should().BeTrue("the loop gives up after its attempts");
            stateAtGiveUp.Should().Be(AmiConnectionState.Disconnected, "the give-up reads Disconnected");
            drained.Should().BeTrue("a later dispose returns and every queued notification is delivered");
            causes.Should().Equal([null],
                "the outage is one loss, announced when it happened; giving up, and each refused connect, is no second loss");
        }

        await peerCts.CancelAsync();
        await serving;
    }

    [Fact]
    public async Task Lost_ShouldBeRaisedOnce_WhenAutoReconnectIsOff()
    {
        using var peerCts = new CancellationTokenSource(Bound * 3);
        var sockets = new PipedSocketFactory();
        var serving = ServeEverySocketAsync(sockets, peerCts.Token);
        var logger = new SignalingLogger<AmiConnection>();
        var connection = Create(sockets, logger: logger, autoReconnect: false);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        var causes = new ConcurrentQueue<Exception?>();
        connection.Lost += causes.Enqueue;
        var endedForGood = logger.Logged("[AMI] Disconnected");

        sockets.Created[0].CloseFromPeer();
        var ended = await CompletesWithinBoundAsync(endedForGood);
        var stateAtEnd = connection.State;
        var drained = await EndAndDrainAsync(connection);

        using (new AssertionScope())
        {
            ended.Should().BeTrue("without AutoReconnect the lost connection is ended");
            stateAtEnd.Should().Be(AmiConnectionState.Disconnected);
            drained.Should().BeTrue("a later dispose returns and every queued notification is delivered");
            causes.Should().Equal([null], "a connection lost for good without AutoReconnect is announced once, with no exception");
        }

        await peerCts.CancelAsync();
        await serving;
    }

    [Fact]
    public async Task Lost_ShouldPrecedeReconnected_WhenTheConnectionIsLostTwice()
    {
        using var peerCts = new CancellationTokenSource(Bound * 3);
        var sockets = new PipedSocketFactory();
        var serving = ServeEverySocketAsync(sockets, peerCts.Token);
        var connection = Create(sockets);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        var trail = new ConcurrentQueue<string>();
        var reconnected = new[] { NewSignal(), NewSignal() };
        var reconnects = 0;
        connection.Lost += _ => trail.Enqueue("lost");
        connection.Reconnected += () =>
        {
            trail.Enqueue("reconnected");
            var n = Interlocked.Increment(ref reconnects);
            if (n <= reconnected.Length)
                reconnected[n - 1].TrySetResult();
        };

        sockets.Created[0].CloseFromPeer();
        var firstBack = await CompletesWithinBoundAsync(reconnected[0].Task);
        var secondBack = false;
        if (firstBack)
        {
            sockets.Created[1].CloseFromPeer();
            secondBack = await CompletesWithinBoundAsync(reconnected[1].Task);
        }

        var drained = await EndAndDrainAsync(connection);

        using (new AssertionScope())
        {
            firstBack.Should().BeTrue("the connection reconnects after the first outage");
            secondBack.Should().BeTrue("the connection reconnects after the second outage");
            drained.Should().BeTrue("the dispose returns and every queued notification is delivered");
            trail.Should().Equal(["lost", "reconnected", "lost", "reconnected"],
                "each outage is announced once, and before the Reconnected of the same outage");
        }

        await peerCts.CancelAsync();
        await serving;
    }

    [Fact]
    public async Task Lost_ShouldReachEveryHandler_WhenOneThrows()
    {
        using var peerCts = new CancellationTokenSource(Bound * 3);
        var sockets = new PipedSocketFactory();
        var serving = ServeEverySocketAsync(sockets, peerCts.Token);
        var logger = new SignalingLogger<AmiConnection>();
        var connection = Create(sockets, logger: logger);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        var second = NewSignal();
        var reconnected = NewSignal();
        connection.Lost += _ => throw new InvalidOperationException("a handler that throws");
        connection.Lost += _ => second.TrySetResult();
        connection.Reconnected += () => reconnected.TrySetResult();

        sockets.Created[0].CloseFromPeer();
        var reached = await CompletesWithinBoundAsync(second.Task);
        var back = await CompletesWithinBoundAsync(reconnected.Task);
        var drained = await EndAndDrainAsync(connection);

        using (new AssertionScope())
        {
            reached.Should().BeTrue("a handler that throws does not keep the handlers after it from being told");
            back.Should().BeTrue("a handler that throws does not stop the reconnect");
            drained.Should().BeTrue("the dispose returns and every queued notification is delivered");
            // A peer's close logs no reader error, and no Reconnected handler throws here: the error is the handler's.
            logger.Entries.Should().Contain(e => e.Level == LogLevel.Error, "the handler's exception is logged as an error");
        }

        await peerCts.CancelAsync();
        await serving;
    }

    [Fact]
    public async Task Lost_ShouldNotHoldTheReconnect_WhenAHandlerIsSlow()
    {
        using var peerCts = new CancellationTokenSource(Bound * 3);
        var sockets = new PipedSocketFactory();
        var serving = ServeEverySocketAsync(sockets, peerCts.Token);
        var logger = new SignalingLogger<AmiConnection>();
        var connection = Create(sockets, logger: logger);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        var trail = new ConcurrentQueue<string>();
        var entered = NewSignal();
        var release = NewSignal();
        var reconnected = NewSignal();
        connection.Lost += _ =>
        {
            entered.TrySetResult();
            // Holds the delivery until the test releases it; the bound only keeps a failed run from blocking a
            // thread-pool thread for good.
#pragma warning disable VSTHRD002 // a handler that blocks is the case under test
            release.Task.Wait(Bound * 2);
#pragma warning restore VSTHRD002
            trail.Enqueue("lost-returned");
        };
        connection.Reconnected += () =>
        {
            trail.Enqueue("reconnected");
            reconnected.TrySetResult();
        };

        sockets.Created[0].CloseFromPeer();
        var heldEntered = await CompletesWithinBoundAsync(entered.Task);
        var loggedIn = heldEntered && await CompletesWithinBoundAsync(logger.Logged("[AMI] Connected", times: 2));
        var stateWhileHeld = connection.State;
        var reconnectedWhileHeld = reconnected.Task.IsCompleted;
        release.TrySetResult();
        var back = await CompletesWithinBoundAsync(reconnected.Task);
        var drained = await EndAndDrainAsync(connection);

        using (new AssertionScope())
        {
            heldEntered.Should().BeTrue("the peer's close is a loss, and the slow handler is told of it");
            loggedIn.Should().BeTrue("a slow loss handler does not hold the reconnect");
            stateWhileHeld.Should().Be(AmiConnectionState.Connected, "the connection is back while the handler still runs");
            reconnectedWhileHeld.Should().BeFalse("Reconnected is delivered only once the loss handler of its outage has returned");
            back.Should().BeTrue("once released, Reconnected is delivered");
            drained.Should().BeTrue("the dispose returns and every queued notification is delivered");
            trail.Should().Equal(["lost-returned", "reconnected"]);
        }

        await peerCts.CancelAsync();
        await serving;
    }

    [Fact]
    public async Task Lost_ShouldNotDeadlock_WhenAHandlerDisposesTheConnectionAndWaits()
    {
        using var peerCts = new CancellationTokenSource(Bound * 3);
        var sockets = new PipedSocketFactory();
        var serving = ServeEverySocketAsync(sockets, peerCts.Token);
        var connection = Create(sockets);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        var handled = NewSignal<DisposeSeen>();
        connection.Lost += _ =>
        {
            // A consumer that drops the PBX on its first loss and blocks until the connection is gone.
#pragma warning disable VSTHRD002 // the synchronous wait is the case under test
            connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
            handled.TrySetResult(new DisposeSeen(connection.State, sockets.Created.Count));
        };

        sockets.Created[0].CloseFromPeer();
        var seen = await CompletesWithinBoundAsync(handled.Task) ? await handled.Task : null;
        var drained = await EndAndDrainAsync(connection);

        using (new AssertionScope())
        {
            (seen is not null).Should().BeTrue("the handler's DisposeAsync returns, instead of waiting on the delivery that runs the handler");
            drained.Should().BeTrue("a later dispose returns and every queued notification is delivered");
            connection.State.Should().Be(AmiConnectionState.Disconnected);
            if (seen is not null)
            {
                seen.State.Should().Be(AmiConnectionState.Disconnected, "the dispose has ended the connection when it returns");
                sockets.Created.Should().HaveCount(seen.Sockets,
                    "the dispose joined the reconnect loop, so a connection its handler disposed dials no more");
            }
        }

        await peerCts.CancelAsync();
        await serving;
    }

    /// <summary>
    /// The handlers are read when the announcement is delivered, not when it is queued: a handler that unsubscribed
    /// while its announcement waited behind an earlier notification is not called.
    /// </summary>
    [Fact]
    public async Task Lost_ShouldNotReachAHandler_WhenItUnsubscribesBeforeDelivery()
    {
        using var peerCts = new CancellationTokenSource(Bound * 3);
        var sockets = new PipedSocketFactory();
        var serving = ServeEverySocketAsync(sockets, peerCts.Token);
        var logger = new SignalingLogger<AmiConnection>();
        var connection = Create(sockets, logger: logger);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        var blockerEntered = NewSignal();
        var release = NewSignal();
        var secondReconnect = NewSignal();
        var reconnects = 0;
        connection.Reconnected += () =>
        {
            if (Interlocked.Increment(ref reconnects) == 1)
            {
                // The first reconnect's delivery holds the queue until the test releases it.
                blockerEntered.TrySetResult();
#pragma warning disable VSTHRD002 // a handler that blocks is the case under test
                release.Task.Wait(Bound * 2);
#pragma warning restore VSTHRD002
            }
            else
            {
                secondReconnect.TrySetResult();
            }
        };
        var heard = 0;
        var firstHeard = NewSignal();
        Action<Exception?> counting = _ =>
        {
            Interlocked.Increment(ref heard);
            firstHeard.TrySetResult();
        };
        connection.Lost += counting;

        // Outage 1: heard, then the reconnect, whose Reconnected blocks the queue.
        sockets.Created[0].CloseFromPeer();
        var firstLossHeard = await CompletesWithinBoundAsync(firstHeard.Task);
        var blocked = firstLossHeard && await CompletesWithinBoundAsync(blockerEntered.Task);
        var secondQueued = false;
        if (blocked)
        {
            // Outage 2, on the reconnect's socket: its loss is queued behind the blocked Reconnected.
            sockets.Created[1].CloseFromPeer();
            secondQueued = await CompletesWithinBoundAsync(logger.Logged("[AMI] Reconnecting", times: 2));
        }

        connection.Lost -= counting;
        release.TrySetResult();
        var back = secondQueued && await CompletesWithinBoundAsync(secondReconnect.Task);
        var drained = await EndAndDrainAsync(connection);

        using (new AssertionScope())
        {
            firstLossHeard.Should().BeTrue("the first outage is announced to the subscribed handler");
            blocked.Should().BeTrue("the first reconnect's Reconnected holds the queue");
            secondQueued.Should().BeTrue("the second outage's loss is queued, and the loop reconnects");
            back.Should().BeTrue("the second reconnect is delivered once the queue is released");
            drained.Should().BeTrue("the dispose returns and every queued notification is delivered");
            Volatile.Read(ref heard).Should().Be(1,
                "the handler unsubscribed before the second loss was delivered, so it is not called for it");
        }

        await peerCts.CancelAsync();
        await serving;
    }

    private static AmiConnection Create(PipedSocketFactory sockets, bool heartbeat = false, bool autoReconnect = true,
        int maxReconnectAttempts = 0, SignalingLogger<AmiConnection>? logger = null) =>
        new(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = heartbeat,
            HeartbeatInterval = TimeSpan.FromMilliseconds(50),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(250),
            AutoReconnect = autoReconnect,
            MaxReconnectAttempts = maxReconnectAttempts,
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(50),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(50),
        }), sockets, logger ?? new SignalingLogger<AmiConnection>());

    /// <summary>
    /// Disposes the connection, which joins any ending in flight, then awaits every notification queued up to then.
    /// False when either does not complete within <see cref="Bound"/>.
    /// </summary>
    private static async Task<bool> EndAndDrainAsync(AmiConnection connection) =>
        await CompletesWithinBoundAsync(connection.DisposeAsync().AsTask())
        && await CompletesWithinBoundAsync(connection.PendingNotifications);

    private static Task EndAsync(AmiConnection connection, string ending) =>
        ending == nameof(AmiConnection.DisconnectAsync)
            ? connection.DisconnectAsync().AsTask()
            : connection.DisposeAsync().AsTask();

    /// <summary>
    /// Plays the Asterisk peer of every socket the connection creates: the n-th (from 1) logs in once
    /// <paramref name="beforeLogin"/>'s task for n has completed, then answers Pings when
    /// <paramref name="answerPings"/> is set and reads everything else unanswered. Ends when <paramref name="ct"/> is
    /// cancelled.
    /// </summary>
    private static async Task ServeEverySocketAsync(PipedSocketFactory sockets, CancellationToken ct,
        bool answerPings = true, Func<int, Task>? beforeLogin = null)
    {
        var peers = new List<Task>();
        try
        {
            while (true)
            {
                var peer = await sockets.NextAsync(ct);
                var gate = beforeLogin?.Invoke(peers.Count + 1) ?? Task.CompletedTask;
                peers.Add(Task.Run(() => ServeAsync(peer, answerPings, gate, ct), CancellationToken.None));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The test is over.
        }

        await Task.WhenAll(peers);
    }

    private static async Task ServeAsync(PipedSocket peer, bool answerPings, Task gate, CancellationToken ct)
    {
        try
        {
            await gate.WaitAsync(ct);
            await peer.CompleteLoginAsync(ct);
            while (await peer.ReadActionAsync(ct) is { } action)
            {
                if (answerPings && PipedSocket.IsPing(action))
                    await peer.RespondAsync("Success", PipedSocket.ActionIdOf(action));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The test is over.
        }
        catch (InvalidOperationException)
        {
            // The connection closed this socket before the login finished, or refused its connect.
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> NewSignal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<bool> CompletesWithinBoundAsync(Task task)
    {
        try
        {
            await task.WaitAsync(Bound);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>What a loss handler that disposed the connection saw once its dispose returned.</summary>
    private sealed record DisposeSeen(AmiConnectionState State, int Sockets);
}
