using System.Collections.Concurrent;
using System.Net.Sockets;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// What each change <see cref="AmiConnection.StateChanged"/> announces carries: the heartbeat's
/// <see cref="TimeoutException"/> on the loss it caused, a failed reconnect attempt's own error, the last failed
/// attempt's error on the give-up's final <see cref="AmiConnectionState.Disconnected"/>, the loss's cause on the final
/// change of a loss without <see cref="AmiConnectionOptions.AutoReconnect"/>, and nothing — no cause, nothing final,
/// nothing a loss — on the caller's own ending.
/// </summary>
/// <remarks>
/// <para>
/// A real <see cref="AmiConnection"/> over the in-memory <see cref="PipedSocket"/> harness. Nothing waits on the wall clock:
/// every wait is bounded by <see cref="Bound"/> and ends on the signal it asserts. A sequence is read only once it is
/// complete: the connection's ending has finished and <c>PendingNotifications</c>, the tail of the queue the changes are
/// delivered on, has run.
/// </para>
/// <para>
/// The give-up tests assert what is announced, never how many connects the loop made: that count is the reconnect
/// option's contract, pinned where the option is.
/// </para>
/// </remarks>
public sealed class AmiConnectionStateChangeCauseTests : IAsyncLifetime, IDisposable
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private readonly CancellationTokenSource _peerCts = new(Bound * 3);
    private readonly ConcurrentBag<AmiConnection> _connections = [];
    private readonly ConcurrentBag<Task> _peers = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var connection in _connections)
            await connection.DisposeAsync().AsTask().WaitAsync(Bound);

        await _peerCts.CancelAsync();
        await Task.WhenAll(_peers).WaitAsync(Bound);
    }

    // Called by the runner after DisposeAsync.
    public void Dispose() => _peerCts.Dispose();

    [Fact]
    public async Task StateChanged_ShouldCarryATimeoutException_WhenTheHeartbeatGoesUnanswered()
    {
        var sockets = new PipedSocketFactory();
        // The peer logs in and never answers a Ping: a PBX that is silent, not gone.
        Serve(sockets, answerPings: false);
        var connection = Create(sockets, options => options.EnableHeartbeat = true);
        var loss = NewSignal<AmiConnectionStateChange>();
        connection.StateChanged += change =>
        {
            if (change.IsLoss)
                loss.TrySetResult(change);
        };
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);

        var announced = await CompletesWithinBoundAsync(loss.Task);

        using (new AssertionScope())
        {
            announced.Should().BeTrue("an unanswered heartbeat ends an established connection nobody asked to end");
            if (announced)
            {
                var change = await loss.Task;
                change.Previous.Should().Be(AmiConnectionState.Connected, "a loss leaves Connected");
                change.ByCaller.Should().BeFalse("nobody asked for the loss");
                change.Cause.Should().BeOfType<TimeoutException>(
                    "the change that leaves Connected carries the heartbeat's timeout, not an end of stream");
            }
        }
    }

    [Fact]
    public async Task StateChanged_ShouldCarryTheAttemptsError_WhenAReconnectAttemptFails()
    {
        // The first socket logs in; every later one refuses its connect, as a peer that is down does.
        var sockets = new PipedSocketFactory { ConnectsAccepted = 1 };
        Serve(sockets);
        var connection = Create(sockets);
        var failed = NewSignal<AmiConnectionStateChange>();
        connection.StateChanged += change =>
        {
            if (change is { Previous: AmiConnectionState.Connecting, Current: AmiConnectionState.Reconnecting })
                failed.TrySetResult(change);
        };
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);

        sockets.Created[0].CloseFromPeer();
        var announced = await CompletesWithinBoundAsync(failed.Task);

        using (new AssertionScope())
        {
            announced.Should().BeTrue("the reconnect loop's first attempt is refused");
            if (announced)
            {
                var change = await failed.Task;
                change.ByCaller.Should().BeFalse("the reconnect loop's attempt is the connection's own");
                change.Cause.Should().BeOfType<SocketException>("the failed attempt carries the error it failed with")
                    .Which.SocketErrorCode.Should().Be(SocketError.ConnectionRefused, "the peer refused the connect");
                change.IsLoss.Should().BeFalse("a failed attempt is not a loss: the connection was not connected");
                change.IsFinal.Should().BeFalse("the loop keeps trying");
            }
        }
    }

    [Fact]
    public async Task StateChanged_ShouldAnnounceOneFinalDisconnectedWithTheLastAttemptsError_WhenEveryReconnectLoginIsRejected()
    {
        var sockets = new PipedSocketFactory();
        // The first socket logs in; every reconnect attempt's login is rejected, as after a credentials change.
        Serve(sockets, rejectLogin: socket => socket > 1);
        var connection = Create(sockets, options => options.MaxReconnectAttempts = 2);
        var changes = new ConcurrentQueue<AmiConnectionStateChange>();
        var final = NewSignal();
        connection.StateChanged += change =>
        {
            changes.Enqueue(change);
            if (change.IsFinal)
                final.TrySetResult();
        };
        var lost = 0;
        connection.Lost += _ => Interlocked.Increment(ref lost);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);

        sockets.Created[0].CloseFromPeer();
        var gaveUp = await CompletesWithinBoundAsync(final.Task);
        var drained = await CompletesWithinBoundAsync(connection.PendingNotifications);

        var announced = changes.ToList();
        var lastFailure = announced.LastOrDefault(c =>
            c is { Previous: AmiConnectionState.Connecting, Current: AmiConnectionState.Reconnecting });
        using (new AssertionScope())
        {
            gaveUp.Should().BeTrue("the reconnect loop gives up once every attempt it makes is rejected");
            drained.Should().BeTrue("every queued change is delivered");
            connection.State.Should().Be(AmiConnectionState.Disconnected, "the connection gave up");
            lastFailure.Should().NotBeNull("at least one reconnect attempt failed before the loop gave up");
            lastFailure?.Cause.Should().BeOfType<AmiAuthenticationException>("each attempt's login was rejected");
            announced.Should().ContainSingle(c => c.IsFinal, "the give-up is announced as final once")
                .Which.Should().Match<AmiConnectionStateChange>(c =>
                    c.Previous == AmiConnectionState.Disconnecting && c.Current == AmiConnectionState.Disconnected
                    && !c.ByCaller && c.Cause != null && ReferenceEquals(c.Cause, lastFailure!.Cause),
                    "the final change is the connection's own Disconnecting → Disconnected, carrying the last failed attempt's exception");
            announced.Should().ContainSingle(c => c.Current == AmiConnectionState.Disconnecting)
                .Which.Should().Match<AmiConnectionStateChange>(c =>
                    c.Previous == AmiConnectionState.Reconnecting && !c.ByCaller && ReferenceEquals(c.Cause, lastFailure!.Cause),
                    "the give-up leaves Reconnecting for Disconnecting, carrying the same exception");
            announced.Where(c => c.IsLoss).Should().ContainSingle("one outage is one loss; the give-up is not a second one");
            Volatile.Read(ref lost).Should().Be(1, "the loss announcement ran once, for the loss, and not again at the give-up");
        }
    }

    [Fact]
    public async Task StateChanged_ShouldAnnounceAFinalDisconnectedWithTheLossCause_WhenAutoReconnectIsOff()
    {
        var sockets = new PipedSocketFactory();
        Serve(sockets);
        var connection = Create(sockets, options => options.AutoReconnect = false);
        var changes = new ConcurrentQueue<AmiConnectionStateChange>();
        var final = NewSignal();
        connection.StateChanged += change =>
        {
            changes.Enqueue(change);
            if (change.IsFinal)
                final.TrySetResult();
        };
        var lostCause = NewSignal<Exception?>();
        connection.Lost += cause => lostCause.TrySetResult(cause);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);

        var error = new IOException("the transport broke mid-read");
        sockets.Created[0].FaultFromPeer(error);
        var ended = await CompletesWithinBoundAsync(final.Task);
        var lostRaised = await CompletesWithinBoundAsync(lostCause.Task);
        var drained = await CompletesWithinBoundAsync(connection.PendingNotifications);

        var announced = changes.ToList();
        using (new AssertionScope())
        {
            ended.Should().BeTrue("a loss without AutoReconnect ends the connection for good");
            lostRaised.Should().BeTrue("the loss is announced");
            drained.Should().BeTrue("every queued change is delivered");
            if (lostRaised)
                (await lostCause.Task).Should().BeSameAs(error, "the control: the loss announcement carries the read's error");
            announced.Select(c => (c.Previous, c.Current, c.ByCaller)).Should().EndWith(
                [
                    (AmiConnectionState.Connected, AmiConnectionState.Disconnecting, false),
                    (AmiConnectionState.Disconnecting, AmiConnectionState.Disconnected, false),
                ],
                "the loss is the connection's own ending");
            announced.Should().ContainSingle(c => c.IsFinal, "the end of the loss is announced as final once")
                .Which.Cause.Should().BeSameAs(error, "the final change carries the loss's cause");
            announced.Should().ContainSingle(c => c.IsLoss, "one loss")
                .Which.Cause.Should().BeSameAs(error, "the loss's change carries the same cause as the loss announcement");
        }
    }

    [Fact]
    public async Task StateChanged_ShouldAnnounceNothingFinalAndNoLoss_WhenTheCallerDisposes()
    {
        var sockets = new PipedSocketFactory();
        Serve(sockets);
        var connection = Create(sockets);
        var changes = new ConcurrentQueue<AmiConnectionStateChange>();
        connection.StateChanged += changes.Enqueue;
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);

        var disposed = await CompletesWithinBoundAsync(connection.DisposeAsync().AsTask());
        var drained = await CompletesWithinBoundAsync(connection.PendingNotifications);

        var announced = changes.ToList();
        using (new AssertionScope())
        {
            disposed.Should().BeTrue("the dispose returns");
            drained.Should().BeTrue("every queued change is delivered");
            announced.Select(c => (c.Previous, c.Current)).Should().Equal(
                [
                    (AmiConnectionState.Initial, AmiConnectionState.Connecting),
                    (AmiConnectionState.Connecting, AmiConnectionState.Connected),
                    (AmiConnectionState.Connected, AmiConnectionState.Disconnecting),
                    (AmiConnectionState.Disconnecting, AmiConnectionState.Disconnected),
                ],
                "the caller's connect and the caller's ending");
            announced.Should().OnlyContain(c => c.ByCaller && c.Cause == null && !c.IsFinal && !c.IsLoss,
                "the caller's own changes carry no cause and are neither final nor a loss");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private AmiConnection Create(PipedSocketFactory sockets, Action<AmiConnectionOptions>? configure = null)
    {
        var options = new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            HeartbeatInterval = TimeSpan.FromMilliseconds(50),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(250),
            AutoReconnect = true,
            MaxReconnectAttempts = 0,
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(20),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(20),
            // Limits, never waits: nothing here runs until them.
            ConnectionTimeout = TimeSpan.FromMinutes(1),
            DefaultResponseTimeout = TimeSpan.FromMinutes(1),
        };
        configure?.Invoke(options);
        var connection = new AmiConnection(Options.Create(options), sockets, new SignalingLogger<AmiConnection>());
        _connections.Add(connection);
        return connection;
    }

    /// <summary>
    /// Plays the Asterisk peer of every socket the connection creates: the n-th (from 1) rejects the login when
    /// <paramref name="rejectLogin"/> says so, else logs in, answers Pings when <paramref name="answerPings"/> is set and
    /// reads everything else unanswered. A socket that refuses its connect is never read by the connection, and its peer
    /// ends when the test does.
    /// </summary>
    private void Serve(PipedSocketFactory sockets, Func<int, bool>? rejectLogin = null, bool answerPings = true)
    {
        var ct = _peerCts.Token;
        _peers.Add(Task.Run(async () =>
        {
            var served = new List<Task>();
            try
            {
                while (true)
                {
                    var peer = await sockets.NextAsync(ct);
                    var reject = rejectLogin?.Invoke(served.Count + 1) ?? false;
                    served.Add(Task.Run(() => ServeAsync(peer, reject, answerPings, ct), CancellationToken.None));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The test is over.
            }

            await Task.WhenAll(served);
        }, CancellationToken.None));
    }

    private static async Task ServeAsync(PipedSocket peer, bool rejectLogin, bool answerPings, CancellationToken ct)
    {
        try
        {
            if (rejectLogin)
            {
                await peer.WriteAsync("Asterisk Call Manager/6.0.0\r\n");
                var challenge = await peer.ReadActionAsync(ct) ?? throw new InvalidOperationException("closed");
                await peer.RespondAsync("Success", PipedSocket.ActionIdOf(challenge), [new("Challenge", "abc123")]);
                var login = await peer.ReadActionAsync(ct) ?? throw new InvalidOperationException("closed");
                await peer.RespondAsync("Error", PipedSocket.ActionIdOf(login), [new("Message", "Authentication failed")]);
                return;
            }

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
}
