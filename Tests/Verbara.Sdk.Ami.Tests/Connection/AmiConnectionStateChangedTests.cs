using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Transport;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// <see cref="AmiConnection.StateChanged"/> announces every change of <see cref="AmiConnection.State"/> once, in the order
/// the state was written, so each change's previous state is the state the change before it announced: across the
/// caller's connect, a loss, the reconnect loop's attempts and the caller's ending. It shares one ordered queue with the
/// loss announcement and <see cref="AmiConnection.Reconnected"/>; its handlers run one at a time on the thread pool, outside
/// any event dispatch; a handler that throws is logged and the ones after it still run; a handler may end the connection
/// and wait; nothing is queued without a handler; and once the caller's ending is recorded no connect announces a state
/// after it. An <see cref="IAmiConnection"/> written before the event still compiles and raises nothing.
/// </summary>
/// <remarks>
/// <para>
/// A real <see cref="AmiConnection"/> over the in-memory <see cref="PipedSocket"/> harness, with a 50 ms reconnect backoff.
/// Nothing waits on the wall clock: every wait is bounded by <see cref="Bound"/> and ends on the signal it asserts. A
/// sequence is read only once it is complete: the test disposes the connection, which joins any ending in flight, then
/// awaits <c>PendingNotifications</c>, the tail of the queue the changes are delivered on.
/// </para>
/// <para>
/// The dispatch mark (<c>_inDispatch</c>) is a private field, read by reflection (a test project is not AOT-published, and
/// an <c>extern</c> accessor reads as unmanaged code to the code scan). A renamed field fails the test with
/// <see cref="MissingFieldException"/>.
/// </para>
/// </remarks>
public sealed class AmiConnectionStateChangedTests : IAsyncLifetime, IDisposable
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
    public async Task StateChanged_ShouldAnnounceAChain_WhenAnOutageRecoversAndTheCallerDisposes()
    {
        var sockets = new PipedSocketFactory();
        // The second socket is the first reconnect attempt: its login is rejected. The third logs in.
        Serve(sockets, rejectLogin: socket => socket == 2);
        var connection = Create(sockets);
        var log = new ConcurrentQueue<object>();
        connection.StateChanged += log.Enqueue;
        var reconnected = NewSignal();
        connection.Reconnected += () =>
        {
            log.Enqueue(nameof(AmiConnection.Reconnected));
            reconnected.TrySetResult();
        };
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);

        sockets.Created[0].CloseFromPeer();
        var back = await CompletesWithinBoundAsync(reconnected.Task);
        var drained = await EndAndDrainAsync(connection);

        var changes = log.OfType<AmiConnectionStateChange>().ToList();
        using (new AssertionScope())
        {
            back.Should().BeTrue("the second reconnect attempt logs in");
            drained.Should().BeTrue("the dispose returns and every queued change is delivered");
            changes.Select(c => (c.Previous, c.Current, c.ByCaller)).Should().Equal(
                [
                    (AmiConnectionState.Initial, AmiConnectionState.Connecting, true),
                    (AmiConnectionState.Connecting, AmiConnectionState.Connected, true),
                    (AmiConnectionState.Connected, AmiConnectionState.Reconnecting, false),
                    (AmiConnectionState.Reconnecting, AmiConnectionState.Connecting, false),
                    (AmiConnectionState.Connecting, AmiConnectionState.Reconnecting, false),
                    (AmiConnectionState.Reconnecting, AmiConnectionState.Connecting, false),
                    (AmiConnectionState.Connecting, AmiConnectionState.Connected, false),
                    (AmiConnectionState.Connected, AmiConnectionState.Disconnecting, true),
                    (AmiConnectionState.Disconnecting, AmiConnectionState.Disconnected, true),
                ],
                "every state the connection took is announced once, in the order it was written");
            changes.Zip(changes.Skip(1)).Should().OnlyContain(pair => pair.First.Current == pair.Second.Previous,
                "each change's previous state is the state the change before it announced");
            changes.Where(c => c.IsLoss).Should().ContainSingle("one outage is one loss")
                .Which.Cause.Should().BeNull("the peer ended the stream");
            if (changes.Count == 9)
            {
                changes[4].Cause.Should().BeOfType<AmiAuthenticationException>("the failed attempt carries its own error");
                changes.Where((_, i) => i != 4).Should().OnlyContain(c => c.Cause == null,
                    "nothing failed in any other change");
            }

            changes.Should().NotContain(c => c.IsFinal, "the caller's own ending is never final");
            IndexOf(log, AmiConnectionState.Connecting, AmiConnectionState.Connected, byCaller: false)
                .Should().BeLessThan(log.ToList().IndexOf(nameof(AmiConnection.Reconnected)),
                    "the reconnect's change to Connected is delivered before its Reconnected");
        }
    }

    [Fact]
    public async Task StateChanged_ShouldBeDeliveredBeforeLost_WhenThePeerClosesTheStream()
    {
        var sockets = new PipedSocketFactory();
        Serve(sockets);
        var connection = Create(sockets);
        var log = new ConcurrentQueue<object>();
        connection.StateChanged += log.Enqueue;
        var lost = NewSignal();
        connection.Lost += _ =>
        {
            log.Enqueue(nameof(AmiConnection.Lost));
            lost.TrySetResult();
        };
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);

        sockets.Created[0].CloseFromPeer();
        var raised = await CompletesWithinBoundAsync(lost.Task);
        var drained = await EndAndDrainAsync(connection);

        using (new AssertionScope())
        {
            raised.Should().BeTrue("the peer closed an established connection nobody asked to end");
            drained.Should().BeTrue("the dispose returns and every queued change is delivered");
            var lossAt = IndexOf(log, AmiConnectionState.Connected, AmiConnectionState.Reconnecting, byCaller: false);
            lossAt.Should().BeGreaterThanOrEqualTo(0, "the loss is announced as a change");
            lossAt.Should().BeLessThan(log.ToList().IndexOf(nameof(AmiConnection.Lost)),
                "the loss's change is delivered before the loss is announced");
        }
    }

    [Fact]
    public async Task StateChanged_ShouldStillReachLaterHandlers_WhenAHandlerThrows()
    {
        var sockets = new PipedSocketFactory();
        Serve(sockets);
        var logger = new SignalingLogger<AmiConnection>();
        var connection = Create(sockets, logger);
        var seen = new ConcurrentQueue<AmiConnectionStateChange>();
        var thrown = 0;
        connection.StateChanged += _ =>
        {
            Interlocked.Increment(ref thrown);
            throw new InvalidOperationException("a subscriber's own failure");
        };
        connection.StateChanged += seen.Enqueue;
        var reconnected = NewSignal();
        connection.Reconnected += () => reconnected.TrySetResult();
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);

        sockets.Created[0].CloseFromPeer();
        var back = await CompletesWithinBoundAsync(reconnected.Task);
        var drained = await EndAndDrainAsync(connection);

        using (new AssertionScope())
        {
            back.Should().BeTrue("a handler that throws does not keep the connection from reconnecting");
            drained.Should().BeTrue("the dispose returns and every queued change is delivered");
            seen.Select(c => c.Current).Should().Equal(
                [
                    AmiConnectionState.Connecting, AmiConnectionState.Connected, AmiConnectionState.Reconnecting,
                    AmiConnectionState.Connecting, AmiConnectionState.Connected, AmiConnectionState.Disconnecting,
                    AmiConnectionState.Disconnected,
                ],
                "the handler after the one that throws receives every change");
            Volatile.Read(ref thrown).Should().Be(seen.Count, "the throwing handler is called for every change too");
            logger.Entries.Count(e => e.Line.Contains("[AMI] State-change handler error", StringComparison.Ordinal))
                .Should().Be(Volatile.Read(ref thrown), "each failure is logged");
        }
    }

    [Fact]
    public async Task StateChanged_ShouldNotDeadlock_WhenAHandlerDisposesTheConnectionAndWaits()
    {
        var sockets = new PipedSocketFactory();
        Serve(sockets);
        var connection = Create(sockets);
        var handled = NewSignal<AmiConnectionState>();
        var after = new ConcurrentQueue<AmiConnectionStateChange>();
        connection.StateChanged += change =>
        {
            if (!change.IsLoss)
            {
                after.Enqueue(change);
                return;
            }

            // A consumer that drops the PBX on its first loss and blocks until the connection is gone.
#pragma warning disable VSTHRD002 // the synchronous wait is the case under test
            connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
            handled.TrySetResult(connection.State);
        };
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);

        sockets.Created[0].CloseFromPeer();
        var returned = await CompletesWithinBoundAsync(handled.Task);
        var drained = await EndAndDrainAsync(connection);

        using (new AssertionScope())
        {
            returned.Should().BeTrue("the handler's DisposeAsync returns: no ending waits for the queue that runs the handler");
            drained.Should().BeTrue("a later dispose returns and every queued change is delivered");
            if (returned)
                (await handled.Task).Should().Be(AmiConnectionState.Disconnected, "the dispose has ended the connection when it returns");

            after.Select(c => (c.Previous, c.Current)).Should().EndWith(
                [
                    (AmiConnectionState.Reconnecting, AmiConnectionState.Disconnecting),
                    (AmiConnectionState.Disconnecting, AmiConnectionState.Disconnected),
                ],
                "the ending the handler ran is announced behind it, on the same queue");
        }
    }

    [Fact]
    public async Task StateChanged_ShouldQueueNothing_WhenItHasNoHandler()
    {
        var sockets = new PipedSocketFactory();
        Serve(sockets);
        var connection = Create(sockets);
        var before = connection.PendingNotifications;

        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        await connection.DisposeAsync().AsTask().WaitAsync(Bound);

        connection.PendingNotifications.Should().BeSameAs(before,
            "four state changes with no handler, and no Lost or Reconnected handler, queue nothing");
    }

    [Fact]
    public void StateChanged_ShouldRaiseNothing_WhenTheImplementationPredatesTheEvent()
    {
        IAmiConnection legacy = new LegacyAmiConnection();
        var raised = 0;
        void Handler(AmiConnectionStateChange _) => Interlocked.Increment(ref raised);

        var subscribe = Record.Exception(() => legacy.StateChanged += Handler);
        var unsubscribe = Record.Exception(() => legacy.StateChanged -= Handler);

        using (new AssertionScope())
        {
            subscribe.Should().BeNull("the interface's default accessors accept a subscription");
            unsubscribe.Should().BeNull("and its removal");
            raised.Should().Be(0, "an implementation that does not provide the event raises nothing");
        }
    }

    [Theory]
    [InlineData(AmiConnectionConnectRacingEndingTests.ChallengeAnswered)]
    [InlineData(AmiConnectionConnectRacingEndingTests.VersionProbePending)]
    public async Task StateChanged_ShouldNotAnnounceConnectedAfterDisconnecting_WhenTheCallersConnectRacesItsDispose(string point)
    {
        var sockets = new AmiConnectionConnectRacingEndingTests.HoldingSocketFactory(new PipedSocketFactory());
        var connection = Create(sockets);
        var changes = new ConcurrentQueue<AmiConnectionStateChange>();
        connection.StateChanged += changes.Enqueue;
        var peer = Task.Run(() => AmiConnectionConnectRacingEndingTests.PlayUntilAsync(sockets, point, _peerCts.Token),
            _peerCts.Token);
        _peers.Add(peer);

        var connecting = connection.ConnectAsync().AsTask();
        var input = await peer.WaitAsync(Bound);
        // The caller's ending is recorded and runs to completion while the attempt is held, then the attempt resumes.
        await connection.DisposeAsync().AsTask().WaitAsync(Bound);
        input.Release();
        var thrown = await Record.ExceptionAsync(() => connecting.WaitAsync(Bound));
        var drained = await CompletesWithinBoundAsync(connection.PendingNotifications);

        var announced = changes.ToList();
        using (new AssertionScope())
        {
            thrown.Should().NotBeNull("the attempt the ending overtook does not connect");
            thrown.Should().NotBeOfType<TimeoutException>("the attempt ends once released");
            drained.Should().BeTrue("every queued change is delivered");
            announced.Select(c => (c.Previous, c.Current, c.ByCaller)).Should().Equal(
                [
                    (AmiConnectionState.Initial, AmiConnectionState.Connecting, true),
                    (AmiConnectionState.Connecting, AmiConnectionState.Disconnecting, true),
                    (AmiConnectionState.Disconnecting, AmiConnectionState.Disconnected, true),
                ],
                "once the caller's ending is recorded, the attempt it overtook writes and announces no state");
            announced.SkipWhile(c => c.Current != AmiConnectionState.Disconnecting)
                .Should().NotContain(c => c.Current == AmiConnectionState.Connected,
                    "no Connected is announced after the ending's Disconnecting");
            connection.State.Should().Be(AmiConnectionState.Disconnected, "a disposed connection never reads Connected");
        }
    }

    [Fact]
    public async Task StateChanged_ShouldRunOutsideTheDispatch_WhenAnEventHandlerDisposesTheConnection()
    {
        var sockets = new PipedSocketFactory();
        Serve(sockets);
        var connection = Create(sockets);
        var markInHandler = NewSignal<bool>();
        var markInStateChange = NewSignal<bool>();
        connection.StateChanged += change =>
        {
            if (change.Current == AmiConnectionState.Disconnecting)
                markInStateChange.TrySetResult(InDispatch(connection));
        };
        connection.OnEvent += async _ =>
        {
            markInHandler.TrySetResult(InDispatch(connection));
            // The caller's ending, from inside the connection's own event dispatch: its state changes are queued there.
            await connection.DisposeAsync();
        };
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);

        var written = await sockets.Created[0].WriteEventAsync("FullyBooted");
        var inHandler = await CompletesWithinBoundAsync(markInHandler.Task);
        var inStateChange = await CompletesWithinBoundAsync(markInStateChange.Task);

        using (new AssertionScope())
        {
            written.Should().BeTrue("the peer sends one event");
            inHandler.Should().BeTrue("the event reaches the handler");
            inStateChange.Should().BeTrue("the handler's dispose is announced");
            if (inHandler)
                (await markInHandler.Task).Should().BeTrue("the control: the event handler runs inside the dispatch, and the mark reads so");
            if (inStateChange)
                (await markInStateChange.Task).Should().BeFalse(
                    "a StateChanged handler runs on the notification queue, outside the dispatch that queued the change");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private AmiConnection Create(ISocketConnectionFactory sockets, SignalingLogger<AmiConnection>? logger = null)
    {
        var connection = new AmiConnection(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = true,
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(50),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(50),
            // Limits, never waits: nothing here runs until them.
            ConnectionTimeout = TimeSpan.FromMinutes(1),
            DefaultResponseTimeout = TimeSpan.FromMinutes(1),
        }), sockets, logger ?? new SignalingLogger<AmiConnection>());
        _connections.Add(connection);
        return connection;
    }

    /// <summary>
    /// Plays the Asterisk peer of every socket the connection creates: the n-th (from 1) rejects the login when
    /// <paramref name="rejectLogin"/> says so, else logs in, answers Pings and reads everything else unanswered.
    /// </summary>
    private void Serve(PipedSocketFactory sockets, Func<int, bool>? rejectLogin = null)
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
                    served.Add(Task.Run(() => ServeAsync(peer, reject, ct), CancellationToken.None));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The test is over.
            }

            await Task.WhenAll(served);
        }, CancellationToken.None));
    }

    private static async Task ServeAsync(PipedSocket peer, bool rejectLogin, CancellationToken ct)
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
                if (PipedSocket.IsPing(action))
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

    /// <summary>
    /// Disposes the connection, which joins any ending in flight, then awaits every notification queued up to then.
    /// False when either does not complete within <see cref="Bound"/>.
    /// </summary>
    private static async Task<bool> EndAndDrainAsync(AmiConnection connection) =>
        await CompletesWithinBoundAsync(connection.DisposeAsync().AsTask())
        && await CompletesWithinBoundAsync(connection.PendingNotifications);

    private static int IndexOf(IEnumerable<object> log, AmiConnectionState previous, AmiConnectionState current, bool byCaller) =>
        log.ToList().FindIndex(entry => entry is AmiConnectionStateChange c
            && c.Previous == previous && c.Current == current && c.ByCaller == byCaller);

    /// <summary>The connection's dispatch mark, as the current execution context reads it.</summary>
    private static bool InDispatch(AmiConnection connection) =>
        ((AsyncLocal<bool>)PrivateField(typeof(AmiConnection), "_inDispatch").GetValue(connection)!).Value;

    private static FieldInfo PrivateField(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicFields)] Type owner, string name) =>
        owner.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(owner.FullName, name);

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

    /// <summary>
    /// An <see cref="IAmiConnection"/> written against 2.6.1, before <see cref="IAmiConnection.StateChanged"/> existed: it
    /// compiles against this version with warnings as errors, and its subscribers are never called.
    /// </summary>
    private sealed class LegacyAmiConnection : IAmiConnection
    {
        public AmiConnectionState State => AmiConnectionState.Initial;

        public string? AsteriskVersion => null;

        public event Func<ManagerEvent, ValueTask>? OnEvent
        {
            add { }
            remove { }
        }

        public event Action? Reconnected
        {
            add { }
            remove { }
        }

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<ManagerResponse> SendActionAsync(ManagerAction action, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<TResponse> SendActionAsync<TResponse>(ManagerAction action, CancellationToken cancellationToken = default)
            where TResponse : ManagerResponse =>
            throw new NotSupportedException();

        public IAsyncEnumerable<ManagerEvent> SendEventGeneratingActionAsync(
            ManagerAction action, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IDisposable Subscribe(IObserver<ManagerEvent> observer) => throw new NotSupportedException();

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
