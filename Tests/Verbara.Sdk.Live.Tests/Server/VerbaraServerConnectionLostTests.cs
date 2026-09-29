using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Verbara.Sdk.Ami;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Tests.Connection;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// <see cref="VerbaraServer.ConnectionLost"/> is raised once for each loss of an established AMI connection that the
/// caller did not ask for, with what ended it: <see langword="null"/> when the peer closed the connection, a
/// <see cref="TimeoutException"/> when the heartbeat went unanswered. It is raised after
/// <see cref="IAmiConnection.State"/> has left <see cref="AmiConnectionState.Connected"/> and before the
/// <see cref="IAmiConnection.Reconnected"/> of the same outage, including for a loss that cuts the start's own load
/// short. A handler that throws neither starves the handlers after it nor stops the reconnect, and a handler that ends
/// the connection does not deadlock. <see cref="IAmiConnection.Reconnected"/> shares that ordered delivery: a
/// consumer's handler that throws does not keep the server's reload from running, and a slow loss handler delays the
/// reload without preventing it.
/// </summary>
/// <remarks>
/// <para>
/// The connection is a real <see cref="AmiConnection"/> over the in-memory <see cref="PipedSocket"/> harness the AMI
/// tests use, linked into this project, either through <see cref="Run"/> and its <see cref="BootingAsterisk"/> peers,
/// or, where a case needs the heartbeat or more than one reconnect, through <see cref="ServeEverySocketAsync"/>, which
/// plays an idle Asterisk for every socket the connection creates.
/// </para>
/// <para>
/// Nothing here waits on the wall clock. Every wait is bounded by <see cref="Bound"/>, a hang bound a healthy run never
/// reaches, and ends on the signal it asserts. The absences these tests assert are ordered, not observed: a duplicate
/// announcement of an outage would sit in the trail before that outage's <c>Reconnected</c>, and a reload that did not
/// wait for a slow handler has asked Asterisk for its channels by the time the reconnect has logged in. Absences that
/// need the connection's notification queue drained (the caller's own ending, the give-up) are asserted by the AMI
/// tests, where that queue is visible.
/// </para>
/// </remarks>
public sealed partial class VerbaraServerConnectionLostTests
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private const string StateLoaded = "[LIVE] State loaded";

    private const string ReloadStarted = "[LIVE] Reconnected: reloading state";

    [Fact]
    public async Task ConnectionLost_ShouldFireOnceWithNoException_WhenThePeerClosesTheConnection()
    {
        using var peerCts = new CancellationTokenSource(Bound * 3);
        var sockets = new PipedSocketFactory();
        var lost = new[] { NewSignal<Exception?>(), NewSignal<Exception?>() };
        var reconnected = new[] { NewSignal(), NewSignal() };
        // Each reconnect's peer logs in only once the loss handler of the outage before it has read the state, so
        // that reading cannot see the next session's Connected.
        var serving = ServeEverySocketAsync(sockets, answerPings: true, peerCts.Token,
            beforeLogin: socket => socket switch
            {
                2 => lost[0].Task,
                3 => lost[1].Task,
                _ => Task.CompletedTask,
            });
        await using var connection = Create(sockets);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        await using var server = new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance);
        await server.StartAsync().WaitAsync(Bound);
        var trail = new ConcurrentQueue<string>();
        var statesSeen = new ConcurrentQueue<AmiConnectionState>();
        var losses = 0;
        var reconnects = 0;
        server.ConnectionLost += ex =>
        {
            statesSeen.Enqueue(connection.State);
            trail.Enqueue("lost");
            var n = Interlocked.Increment(ref losses);
            if (n <= lost.Length)
                lost[n - 1].TrySetResult(ex);
        };
        connection.Reconnected += () =>
        {
            trail.Enqueue("reconnected");
            var n = Interlocked.Increment(ref reconnects);
            if (n <= reconnected.Length)
                reconnected[n - 1].TrySetResult();
        };

        sockets.Created[0].CloseFromPeer();
        var firstLost = await CompletesWithinBoundAsync(lost[0].Task);
        var firstBack = firstLost && await CompletesWithinBoundAsync(reconnected[0].Task);
        var secondLost = false;
        var secondBack = false;
        if (firstBack)
        {
            sockets.Created[1].CloseFromPeer();
            secondLost = await CompletesWithinBoundAsync(lost[1].Task);
            secondBack = secondLost && await CompletesWithinBoundAsync(reconnected[1].Task);
        }

        using (new AssertionScope())
        {
            firstLost.Should().BeTrue("the peer closed an established connection nobody asked to end");
            (firstLost ? await lost[0].Task : null).Should().BeNull("a peer's close carries no exception");
            firstBack.Should().BeTrue("the connection reconnects to the peer that is still up");
            secondLost.Should().BeTrue("the second outage is a loss of its own");
            (secondLost ? await lost[1].Task : null).Should().BeNull("a peer's close carries no exception");
            secondBack.Should().BeTrue("the connection reconnects again");
            trail.Should().Equal(
                ["lost", "reconnected", "lost", "reconnected"],
                "each outage is announced once, before the Reconnected of the same outage");
            statesSeen.Should().HaveCount(2).And.NotContain(AmiConnectionState.Connected,
                "the loss is announced after State has left Connected");
        }

        await peerCts.CancelAsync();
        await serving;
    }

    [Fact]
    public async Task ConnectionLost_ShouldCarryATimeoutException_WhenTheHeartbeatGoesUnanswered()
    {
        using var peerCts = new CancellationTokenSource(Bound * 3);
        var sockets = new PipedSocketFactory();
        // The peer answers the login and the state load, and never a Ping: a PBX that is silent, not gone.
        var serving = ServeEverySocketAsync(sockets, answerPings: false, peerCts.Token);
        await using var connection = Create(sockets, heartbeat: true);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        await using var server = new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance);
        var lost = NewSignal<Exception?>();
        // Subscribed before the start: the heartbeat may end the session while the start's load is still running.
        server.ConnectionLost += ex => lost.TrySetResult(ex);
        await server.StartAsync().WaitAsync(Bound);

        var raised = await CompletesWithinBoundAsync(lost.Task);

        using (new AssertionScope())
        {
            raised.Should().BeTrue("an unanswered heartbeat ends an established connection nobody asked to end");
            (raised ? await lost.Task : null).Should().BeOfType<TimeoutException>(
                "the consumer is told the connection was lost to a heartbeat timeout, not closed by the peer");
        }

        await peerCts.CancelAsync();
        await serving;
    }

    [Fact]
    public async Task ConnectionLost_ShouldReachEveryHandlerAndStillReconnect_WhenAHandlerThrows()
    {
        using var peerCts = new CancellationTokenSource(Bound * 3);
        var sockets = new PipedSocketFactory();
        var serving = ServeEverySocketAsync(sockets, answerPings: true, peerCts.Token);
        await using var connection = Create(sockets);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        await using var server = new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance);
        await server.StartAsync().WaitAsync(Bound);
        var second = NewSignal();
        var reconnected = NewSignal();
        server.ConnectionLost += _ => throw new InvalidOperationException("a consumer's handler that throws");
        server.ConnectionLost += _ => second.TrySetResult();
        connection.Reconnected += () => reconnected.TrySetResult();

        sockets.Created[0].CloseFromPeer();
        var secondReached = await CompletesWithinBoundAsync(second.Task);
        var back = await CompletesWithinBoundAsync(reconnected.Task);

        using (new AssertionScope())
        {
            secondReached.Should().BeTrue("a handler that throws does not keep the handlers after it from being told");
            back.Should().BeTrue("a handler that throws does not stop the connection from reconnecting");
        }

        await peerCts.CancelAsync();
        await serving;
    }

    [Fact]
    public async Task ConnectionLost_ShouldNotDeadlock_WhenAHandlerDisposesTheConnectionAndWaits()
    {
        using var peerCts = new CancellationTokenSource(Bound * 3);
        var sockets = new PipedSocketFactory();
        var serving = ServeEverySocketAsync(sockets, answerPings: true, peerCts.Token);
        var connection = Create(sockets);
        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(Bound);
            await using var server = new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance);
            await server.StartAsync().WaitAsync(Bound);
            var handled = NewSignal<DisposeSeen>();
            server.ConnectionLost += _ =>
            {
                // A consumer that drops the PBX on its first loss and blocks until the connection is gone: the
                // synchronous shape of VerbaraServerPool.RemoveServerAsync(...).GetAwaiter().GetResult().
#pragma warning disable VSTHRD002 // the synchronous wait is the case under test
                connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
                handled.TrySetResult(new DisposeSeen(connection.State, sockets.Created.Count));
            };

            sockets.Created[0].CloseFromPeer();
            var seen = await CompletesWithinBoundAsync(handled.Task) ? await handled.Task : null;

            using (new AssertionScope())
            {
                seen.Should().NotBeNull(
                    "the handler's DisposeAsync returns, instead of waiting on the delivery that runs the handler");
                if (seen is not null)
                {
                    seen.State.Should().Be(AmiConnectionState.Disconnected, "the dispose has ended the connection when it returns");
                    sockets.Created.Should().HaveCount(seen.Sockets,
                        "the dispose joined the reconnect loop, so a connection its handler disposed dials no more");
                }
            }
        }
        finally
        {
            // Idempotent: the handler's dispose, when it ran, already ended the connection.
            await connection.DisposeAsync();
            await peerCts.CancelAsync();
            await serving;
        }
    }

    [Fact]
    public async Task Reconnected_ShouldStillReloadTheLiveState_WhenAnEarlierSubscribersHandlerThrows()
    {
        using var peerCts = new CancellationTokenSource(Bound * 3);
        var sockets = new PipedSocketFactory();
        var reloaded = NewSignal();
        // The server's reload is its Status request on the second socket, the one the reconnect opened.
        var serving = ServeEverySocketAsync(sockets, answerPings: true, peerCts.Token,
            onAction: (socket, action) =>
            {
                if (socket == 2 && action == "Status")
                    reloaded.TrySetResult();
            });
        await using var connection = Create(sockets);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        // A consumer that subscribed before the live server started, for example from a hosted service registered
        // ahead of it, and whose handler throws.
        connection.Reconnected += () => throw new InvalidOperationException("a consumer's handler that throws");
        await using var server = new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance);
        await server.StartAsync().WaitAsync(Bound);

        sockets.Created[0].CloseFromPeer();

        (await CompletesWithinBoundAsync(reloaded.Task)).Should().BeTrue(
            "the server reloads its state on the new connection even though a handler subscribed before it threw");

        await peerCts.CancelAsync();
        await serving;
    }

    [Fact]
    public async Task Reload_ShouldWaitForASlowConnectionLostHandler_WhenTheConnectionReconnects()
    {
        var first = new BootingAsterisk { BootedAtLogin = true };
        var second = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(first, second, autoReconnect: true);
        var entered = NewSignal();
        var release = NewSignal();
        run.Server.ConnectionLost += _ =>
        {
            entered.TrySetResult();
            // Holds the delivery until the test releases it; the bound only keeps a failed run from blocking a
            // thread-pool thread for good.
#pragma warning disable VSTHRD002 // a handler that blocks is the case under test
            release.Task.Wait(Bound * 2);
#pragma warning restore VSTHRD002
        };

        first.CloseSession();
        var heldEntered = await CompletesWithinBoundAsync(entered.Task);
        var loggedIn = false;
        int? statusAskedWhileHeld = null;
        bool? reloadStartedWhileHeld = null;
        var reloadedAfter = false;
        if (heldEntered)
        {
            run.ReleaseSecondPeer();
            loggedIn = await CompletesWithinBoundAsync(run.ConnectionLog.Logged("[AMI] Connected", times: 2));
            statusAskedWhileHeld = second.Asked("Status");
            reloadStartedWhileHeld = run.ServerLog.Entries.Any(e => e.Line.Contains(ReloadStarted, StringComparison.Ordinal));
            release.TrySetResult();
            reloadedAfter = await CompletesWithinBoundAsync(run.ServerLog.Logged(StateLoaded, times: 2));
        }

        release.TrySetResult();
        using (new AssertionScope())
        {
            heldEntered.Should().BeTrue("the peer's close is a loss, and the slow handler is told of it");
            loggedIn.Should().BeTrue("a slow loss handler does not hold the reconnect");
            statusAskedWhileHeld.Should().Be(0, "the reload has not asked Asterisk for anything while the loss handler runs");
            reloadStartedWhileHeld.Should().Be(false, "the reload waits for the loss handler of its outage");
            reloadedAfter.Should().BeTrue("once the handler returns, the reload runs on the new session");
            second.Asked("Status").Should().BeGreaterThanOrEqualTo(1, "the reload asked the new session for its channels");
        }
    }

    [Fact]
    public async Task ConnectionLost_ShouldFireOnce_WhenTheSessionEndsDuringTheStartsLoad()
    {
        var first = new BootingAsterisk { BootedAtLogin = true, Close = PeerClose.WhenAsked, CloseWhenAsked = "QueueStatus" };
        var second = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.ConnectAsync(first, second, autoReconnect: true);
        var trail = new ConcurrentQueue<string>();
        var reconnected = NewSignal();
        run.Server.ConnectionLost += _ => trail.Enqueue("lost");
        run.Connection.Reconnected += () =>
        {
            trail.Enqueue("reconnected");
            reconnected.TrySetResult();
        };

        var outcome = await Record.ExceptionAsync(run.StartServerAsync);
        run.ReleaseSecondPeer();
        var back = await CompletesWithinBoundAsync(reconnected.Task);

        using (new AssertionScope())
        {
            outcome.Should().BeNull("the connection is reconnecting, so the start returns and leaves the load to the reload");
            back.Should().BeTrue("the connection reconnects to the second peer");
            trail.Should().Equal(["lost", "reconnected"],
                "a loss that cuts the start's load short is announced once, before the Reconnected whose reload loads the state");
        }
    }

    [Fact]
    public async Task ConnectionLost_ShouldFire_WhenTheSessionEndsDuringTheStartsLoadAndAutoReconnectIsOff()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, Close = PeerClose.WhenAsked, CloseWhenAsked = "Agents" };
        await using var run = await Run.ConnectAsync(peer, autoReconnect: false);
        var lost = NewSignal<Exception?>();
        run.Server.ConnectionLost += ex => lost.TrySetResult(ex);

        var outcome = await Record.ExceptionAsync(run.StartServerAsync);
        var raised = await CompletesWithinBoundAsync(lost.Task);

        using (new AssertionScope())
        {
            outcome.Should().BeOfType<AmiNotConnectedException>(
                "the connection will not come back, so nothing will ever reload what the load did not finish");
            raised.Should().BeTrue("a loss that cuts the start's load short is announced whether the start returns or throws");
            (raised ? await lost.Task : null).Should().BeNull("a peer's close carries no exception");
        }
    }

    private static AmiConnection Create(PipedSocketFactory sockets, bool heartbeat = false) =>
        new(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = heartbeat,
            HeartbeatInterval = TimeSpan.FromMilliseconds(50),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(250),
            AutoReconnect = true,
            MaxReconnectAttempts = 0,
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(50),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(50),
        }), sockets, NullLogger<AmiConnection>.Instance);

    /// <summary>
    /// Plays an idle Asterisk for every socket the connection creates: the login, then a Success and an empty
    /// <c>…Complete</c> list for every action, so a state load finds nothing and completes. Pings are answered only
    /// when <paramref name="answerPings"/> is set. The n-th socket (from 1) logs in once
    /// <paramref name="beforeLogin"/>'s task for n has completed. Ends when <paramref name="ct"/> is cancelled.
    /// </summary>
    private static async Task ServeEverySocketAsync(PipedSocketFactory sockets, bool answerPings, CancellationToken ct,
        Func<int, Task>? beforeLogin = null, Action<int, string>? onAction = null)
    {
        var peers = new List<Task>();
        try
        {
            while (true)
            {
                var peer = await sockets.NextAsync(ct);
                var index = peers.Count + 1;
                var gate = beforeLogin?.Invoke(index) ?? Task.CompletedTask;
                peers.Add(Task.Run(() => ServeAsync(peer, answerPings, gate, index, onAction, ct), CancellationToken.None));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The test is over.
        }

        await Task.WhenAll(peers);
    }

    private static async Task ServeAsync(PipedSocket peer, bool answerPings, Task gate, int index,
        Action<int, string>? onAction, CancellationToken ct)
    {
        try
        {
            await gate.WaitAsync(ct);
            await peer.CompleteLoginAsync(ct);
            while (await peer.ReadActionAsync(ct) is { } action)
            {
                var id = PipedSocket.ActionIdOf(action);
                if (PipedSocket.IsPing(action))
                {
                    if (answerPings)
                        await peer.RespondAsync("Success", id);
                    continue;
                }

                await peer.RespondAsync("Success", id);
                var name = ActionName().Match(action).Groups[1].Value;
                onAction?.Invoke(index, name);
                await peer.WriteEventAsync(name + "Complete", [new("ActionID", id)]);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The test is over.
        }
        catch (InvalidOperationException)
        {
            // The connection closed this socket before the login finished.
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

    [GeneratedRegex(@"^Action:\s*(\S+)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ActionName();
}
