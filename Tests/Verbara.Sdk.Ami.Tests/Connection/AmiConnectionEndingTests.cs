using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// How an AMI connection ends: <see cref="AmiConnectionState.Disconnected"/> is reported only once the
/// ending that reports it has released the connection, and a <c>DisconnectAsync</c> or
/// <c>DisposeAsync</c> that arrives while another ending is still releasing waits for that release
/// instead of returning early. An ending called from inside the connection's own event dispatch, from an
/// <c>OnEvent</c> handler or an observer's <c>OnNext</c>, completes without waiting for that dispatch,
/// whether the connection is connected or reconnecting, and the event pump dispatches nothing after it.
/// </summary>
/// <remarks>
/// <para>
/// Each peer is an in-memory <see cref="PipedSocket"/>, a fresh one per connect. The release is held
/// open by construction: an <c>OnEvent</c> handler blocks its dispatch on a gate, and the release waits
/// for the event pump, which waits for that dispatch. Nothing else is left to chance.
/// </para>
/// <para>
/// Every wait is bounded by <see cref="Bound"/> and ends on the signal it asserts. The exceptions are
/// observation windows for an absence, each paired with its positive control:
/// <see cref="HeldReleaseWindow"/> (the state never reads <see cref="AmiConnectionState.Disconnected"/>
/// while the release is held; the same watch does see it once the gate opens), <see cref="DialWindow"/>
/// (the reconnect loop dials no more; a loop nothing ended dials inside it) and
/// <see cref="LaterEventWindow"/> (the pump dispatches no buffered event; a handler that does not end the
/// connection receives it inside it).
/// </para>
/// <para>
/// How many connects the reconnect loop makes is not asserted (ruling C3, ADR-0008 addendum, still open).
/// The tests read the sockets the factory actually created.
/// </para>
/// </remarks>
public sealed class AmiConnectionEndingTests
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long the state is watched while the handler holds the release open. It is the observation,
    /// not a hang bound: the unfixed connection read Disconnected within milliseconds of the call.
    /// </summary>
    private static readonly TimeSpan HeldReleaseWindow = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long a test watches for the pump to dispatch an event that was buffered behind the dispatch in
    /// progress. It is the observation, not a hang bound, and
    /// <see cref="EventPump_ShouldDispatchTheBufferedEvent_WhenTheHandlerReturnsWithoutEndingTheConnection"/>
    /// is its positive control.
    /// </summary>
    private static readonly TimeSpan LaterEventWindow = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task DisconnectAsync_ShouldNotReportDisconnected_WhileItsReleaseIsStillRunning()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var connection = Create(factory, autoReconnect: true);
        var dispatch = HoldTheDispatch(connection);
        var socket = await ConnectAsync(connection, factory, peerCts);
        (await socket.WriteEventAsync("FullyBooted")).Should().BeTrue("the peer sends one event");
        (await CompletesWithinBoundAsync(dispatch.Entered)).Should().BeTrue("the handler receives the event and holds its dispatch");

        var disconnected = WatchDisconnected(connection, socket);
        var disconnect = connection.DisconnectAsync().AsTask();
        var whileHeld = await ResultWithinAsync(disconnected, HeldReleaseWindow);
        var stillReleasing = !disconnect.IsCompleted;

        dispatch.Open();
        var afterRelease = await ResultWithinAsync(disconnected, Bound);

        whileHeld.Should().BeNull(
            "the handler holds the release open, so the connection must not report Disconnected yet: " +
            "a caller who sees Disconnected must find the socket released");
        stillReleasing.Should().BeTrue("DisconnectAsync cannot finish while the handler holds the release open");
        afterRelease.Should().Be(DisconnectedRead(disposeCount: 1),
            "positive control: once the handler returns, the same watch sees Disconnected, with the socket released exactly once");
        (await CompletesWithinBoundAsync(disconnect)).Should().BeTrue("DisconnectAsync returns once the release has finished");
        (await CompletesWithinBoundAsync(connection.DisposeAsync().AsTask())).Should().BeTrue();
        socket.DisposeCount.Should().Be(1, "a DisposeAsync after the ending disposes no socket a second time");
    }

    /// <summary>
    /// The <c>DisposeAsync</c> is issued while <c>DisconnectAsync</c> is still releasing, after the same
    /// watch the test above makes: that is where the unfixed connection had already reported
    /// Disconnected and let <c>DisposeAsync</c> return at once with the socket undisposed. The assertion
    /// has no window: whenever the call lands before the gate opens, it must not return before the
    /// release.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_ShouldWaitForTheInFlightRelease_WhenADisconnectIsStillReleasing()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var connection = Create(factory, autoReconnect: true);
        var dispatch = HoldTheDispatch(connection);
        var socket = await ConnectAsync(connection, factory, peerCts);
        (await socket.WriteEventAsync("FullyBooted")).Should().BeTrue("the peer sends one event");
        (await CompletesWithinBoundAsync(dispatch.Entered)).Should().BeTrue("the handler receives the event and holds its dispatch");

        var disconnected = WatchDisconnected(connection, socket);
        var disconnect = connection.DisconnectAsync().AsTask();
        // Only places the DisposeAsync below; what this watch reads is asserted by the test above.
        await ResultWithinAsync(disconnected, HeldReleaseWindow);
        var seenAtReturn = SeenWhenDisposeAsyncReturnsAsync(connection, factory);

        dispatch.Open();

        (await ResultWithinBoundAsync(seenAtReturn)).Should().Be(new EndingSeen(AmiConnectionState.Disconnected, DisposeCount: 1),
            "a DisposeAsync issued while DisconnectAsync is still releasing returns only after that release, the event pump " +
            "included, so when it returns the socket has been disposed exactly once and the connection reads Disconnected");
        connection.State.Should().Be(AmiConnectionState.Disconnected);
        (await CompletesWithinBoundAsync(disconnect)).Should().BeTrue("DisconnectAsync returns once the release has finished");
    }

    /// <summary>
    /// A pin, green before and after the single-flight ending: it holds where a lost connection's ending
    /// is forgotten. A connection lost with AutoReconnect off was released, not disposed, so its caller
    /// may connect it again; when that connect fails it releases its own socket and the state reads
    /// Disconnected, and a later <c>DisposeAsync</c> must neither join the old, finished ending nor release
    /// that socket a second time.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_ShouldReleaseTheSocket_WhenAConnectAfterALostConnectionFailed()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var logger = new SignalingLogger<AmiConnection>();
        var connection = Create(factory, autoReconnect: false, logger);
        // Written once the lost connection's ending has released it.
        var lost = logger.Logged("[AMI] Disconnected");
        var first = await ConnectAsync(connection, factory, peerCts);

        first.CloseFromPeer();
        (await CompletesWithinBoundAsync(lost)).Should().BeTrue("the peer's close ends the connection");
        var rejecting = Task.Run(async () =>
        {
            var peer = await factory.NextAsync(peerCts.Token);
            await RejectLoginAsync(peer, peerCts.Token);
            return peer;
        }, peerCts.Token);
        var connectAgain = async () => await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        await connectAgain.Should().ThrowAsync<AmiAuthenticationException>("the peer rejects the second login");
        var second = await rejecting.WaitAsync(Bound);

        (await CompletesWithinBoundAsync(connection.DisposeAsync().AsTask())).Should().BeTrue();

        second.DisposeCount.Should().Be(1,
            "the failed connect released its socket, and DisposeAsync, which does not join the ending that released the first one, releases it no second time");
        first.DisposeCount.Should().Be(1);
        connection.State.Should().Be(AmiConnectionState.Disconnected);
    }

    // ── An ending called from inside the connection's own event dispatch ─────────────────────────────

    [Theory]
    [InlineData(nameof(AmiConnection.DisconnectAsync))]
    [InlineData(nameof(AmiConnection.DisposeAsync))]
    public async Task OnEventHandler_ShouldCompleteTheEnding_WhenItEndsTheConnection(string ending)
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var connection = Create(factory, autoReconnect: true);
        var ended = new TaskCompletionSource<EndingSeen>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OnEvent += async _ =>
        {
            await EndAsync(connection, ending);
            ended.TrySetResult(Seen(connection, factory));
        };
        var socket = await ConnectAsync(connection, factory, peerCts);

        (await socket.WriteEventAsync("FullyBooted")).Should().BeTrue("the peer sends one event");
        var seen = await ResultWithinBoundAsync(ended.Task);
        var laterDispose = await CompletesWithinBoundAsync(connection.DisposeAsync().AsTask());

        using (new AssertionScope())
        {
            seen.Should().Be(new EndingSeen(AmiConnectionState.Disconnected, DisposeCount: 1),
                $"the handler's {ending} returns without waiting for the dispatch it runs in, with the socket released " +
                "exactly once and the connection Disconnected");
            laterDispose.Should().BeTrue("a DisposeAsync after the handler's ending completes");
            socket.DisposeCount.Should().Be(1, "the socket is released exactly once, and a later DisposeAsync does not release it again");
        }
    }

    /// <summary>
    /// The observer blocks its <c>OnNext</c>, and with it the event pump, until <c>DisposeAsync</c> completes or
    /// <see cref="Bound"/> passes, and reports which. The test's own wait is twice as long, so it ends on that
    /// report.
    /// </summary>
    [Fact]
    public async Task Observer_ShouldCompleteDisposeAsync_WhenOnNextWaitsOnIt()
    {
        using var peerCts = new CancellationTokenSource(Bound * 3);
        var factory = new PipedSocketFactory();
        var connection = Create(factory, autoReconnect: true);
        var observer = new DisposeWaitingObserver(connection);
        using var subscription = connection.Subscribe(observer);
        var socket = await ConnectAsync(connection, factory, peerCts);

        (await socket.WriteEventAsync("FullyBooted")).Should().BeTrue("the peer sends one event");
        var waited = await ValueWithinAsync(observer.Waited, Bound * 2);

        using (new AssertionScope())
        {
            waited.Should().BeTrue(
                "the DisposeAsync an observer's OnNext waits on completes without waiting for that dispatch, so the wait returns true");
            socket.DisposeCount.Should().Be(1, "the socket is released exactly once");
            connection.State.Should().Be(AmiConnectionState.Disconnected);
        }
    }

    /// <summary>
    /// The handler holds its dispatch while the peer closes the socket, so the reconnect loop starts during the
    /// dispatch, and the handler ends the connection once the loop waits in one of two places, each fixed by
    /// construction (<see cref="LoopWaitingAsync"/>). With a 1 ms backoff the gate opens when the loop's own
    /// release, before its first attempt, has disposed the lost socket: that release then waits for this very
    /// dispatch. With a 1 s backoff it opens on the loop's <c>[AMI] Reconnecting</c> line, while the loop waits
    /// out its delay. Either way the loop cannot dial before the dispatch returns, and the handler ends the
    /// connection first, so the only socket is the first one.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public async Task OnEventHandler_ShouldEndTheConnection_WhenTheReconnectLoopIsRunning(int reconnectDelayMs)
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        // Only the first socket accepts its connect: every reconnect is refused, as by an Asterisk that is down.
        var factory = new PipedSocketFactory { ConnectsAccepted = 1 };
        var logger = new SignalingLogger<AmiConnection>();
        var backoff = TimeSpan.FromMilliseconds(reconnectDelayMs);
        var connection = Create(factory, autoReconnect: true, logger, backoff);
        var dispatch = new HeldDispatch();
        var ended = new TaskCompletionSource<EndingSeen>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OnEvent += async _ =>
        {
            dispatch.Enter();
            await dispatch.Gate;
            await connection.DisconnectAsync();
            ended.TrySetResult(Seen(connection, factory));
        };
        var first = await ConnectAsync(connection, factory, peerCts);
        (await first.WriteEventAsync("FullyBooted")).Should().BeTrue("the peer sends one event");
        (await CompletesWithinBoundAsync(dispatch.Entered)).Should().BeTrue("the handler receives the event and holds its dispatch");
        var loopWaiting = LoopWaitingAsync(first, logger, backoff);

        first.CloseFromPeer();
        var waiting = await CompletesWithinBoundAsync(loopWaiting);
        dispatch.Open();
        var seen = await ResultWithinBoundAsync(ended.Task);
        var dialled = await factory.NextWithinAsync(DialWindow(backoff));

        using (new AssertionScope())
        {
            waiting.Should().BeTrue(WhereTheLoopWaits(backoff));
            seen.Should().Be(new EndingSeen(AmiConnectionState.Disconnected, DisposeCount: 1),
                "the handler's DisconnectAsync returns while the reconnect loop runs, with the socket released exactly once, " +
                "without waiting for the dispatch it runs in or for the loop, whose release may be waiting for that dispatch");
            dialled.Should().BeNull(
                $"the handler's DisconnectAsync stops the reconnect loop, so it dials no more in {DialWindow(backoff).TotalMilliseconds} ms");
            factory.Created.Should().HaveCount(1,
                "the loop cannot dial before the dispatch returns, and the handler ended the connection before it returned");
            connection.State.Should().Be(AmiConnectionState.Disconnected, "nothing the loop does afterwards overrides the handler's ending");
        }
    }

    /// <summary>
    /// The positive control of <see cref="DialWindow"/> for
    /// <see cref="OnEventHandler_ShouldEndTheConnection_WhenTheReconnectLoopIsRunning"/>, green before and after
    /// the in-dispatch ending was fixed: the same setup, with a handler that returns without ending the
    /// connection, does dial inside the window. A socket absent from the window there means the loop stopped.
    /// </summary>
    /// <remarks>
    /// The gate opens on the loop's <c>[AMI] Reconnecting</c> line for both backoffs, which the connection writes
    /// before and after the fix alike. The regression test's 1 ms placement, the lost socket's disposal, comes
    /// before the loop waits for the dispatch only once the release disposes the socket ahead of the event pump.
    /// Where the loop waits when the gate opens does not change what is observed here: it cannot dial before
    /// the dispatch returns, and once it has returned the loop dials inside the window.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public async Task ReconnectLoop_ShouldDialWithinTheObservationWindow_WhenTheHeldDispatchReturnsWithoutAnEnding(int reconnectDelayMs)
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory { ConnectsAccepted = 1 };
        var logger = new SignalingLogger<AmiConnection>();
        var backoff = TimeSpan.FromMilliseconds(reconnectDelayMs);
        var connection = Create(factory, autoReconnect: true, logger, backoff);
        var dispatch = HoldTheDispatch(connection);
        var first = await ConnectAsync(connection, factory, peerCts);
        (await first.WriteEventAsync("FullyBooted")).Should().BeTrue("the peer sends one event");
        (await CompletesWithinBoundAsync(dispatch.Entered)).Should().BeTrue("the handler receives the event and holds its dispatch");
        var reconnecting = logger.Logged("[AMI] Reconnecting");

        first.CloseFromPeer();
        (await CompletesWithinBoundAsync(reconnecting)).Should().BeTrue(
            "the peer's close starts the reconnect loop while the dispatch is in progress");
        var dialled = factory.NextWithinAsync(DialWindow(backoff));
        dispatch.Open();

        (await dialled).Should().NotBeNull(
            $"with nothing ending the connection, the loop dials within {DialWindow(backoff).TotalMilliseconds} ms once the dispatch returns");
        (await CompletesWithinBoundAsync(connection.DisposeAsync().AsTask())).Should().BeTrue();
    }

    /// <summary>
    /// The caller's <c>DisconnectAsync</c> is recorded when it is called, and its release then waits for the
    /// dispatch the handler holds. The handler then awaits <c>DisposeAsync</c>, which joins that ending: the
    /// ending must stop waiting for the dispatch that is waiting on it.
    /// </summary>
    [Fact]
    public async Task OnEventHandler_ShouldCompleteItsDisposeAsync_WhenADisconnectIsAlreadyReleasing()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var connection = Create(factory, autoReconnect: true);
        var dispatch = new HeldDispatch();
        var ended = new TaskCompletionSource<EndingSeen>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OnEvent += async _ =>
        {
            dispatch.Enter();
            await dispatch.Gate;
            await connection.DisposeAsync();
            ended.TrySetResult(Seen(connection, factory));
        };
        var socket = await ConnectAsync(connection, factory, peerCts);
        (await socket.WriteEventAsync("FullyBooted")).Should().BeTrue("the peer sends one event");
        (await CompletesWithinBoundAsync(dispatch.Entered)).Should().BeTrue("the handler receives the event and holds its dispatch");

        var disconnect = connection.DisconnectAsync().AsTask();
        dispatch.Open();
        var seen = await ResultWithinBoundAsync(ended.Task);
        var disconnected = await CompletesWithinBoundAsync(disconnect);

        using (new AssertionScope())
        {
            seen.Should().Be(new EndingSeen(AmiConnectionState.Disconnected, DisposeCount: 1),
                "the handler's DisposeAsync joins the caller's ending, which stops waiting for the dispatch that waits on it, " +
                "so it returns with the socket released exactly once and the connection Disconnected");
            disconnected.Should().BeTrue("the caller's DisconnectAsync returns");
            socket.DisposeCount.Should().Be(1);
        }
    }

    /// <summary>
    /// The reconnect loop's release before its first attempt disposes the lost socket, and then waits for the
    /// dispatch the handler holds; the lost socket's disposal is the signal that it got there. The caller's
    /// <c>DisconnectAsync</c> is then recorded, and it waits for the loop. The handler then awaits
    /// <c>DisposeAsync</c>, which joins that ending: the ending must stop waiting for the loop, which is waiting
    /// for the dispatch that is waiting on the ending.
    /// </summary>
    [Fact]
    public async Task OnEventHandler_ShouldCompleteItsDisposeAsync_WhenADisconnectIsWaitingForTheReconnectLoop()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        // Only the first socket accepts its connect: every reconnect is refused, as by an Asterisk that is down.
        var factory = new PipedSocketFactory { ConnectsAccepted = 1 };
        var logger = new SignalingLogger<AmiConnection>();
        var backoff = TimeSpan.FromMilliseconds(1);
        var connection = Create(factory, autoReconnect: true, logger, backoff);
        var dispatch = new HeldDispatch();
        var ended = new TaskCompletionSource<EndingSeen>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OnEvent += async _ =>
        {
            dispatch.Enter();
            await dispatch.Gate;
            await connection.DisposeAsync();
            ended.TrySetResult(Seen(connection, factory));
        };
        var first = await ConnectAsync(connection, factory, peerCts);
        (await first.WriteEventAsync("FullyBooted")).Should().BeTrue("the peer sends one event");
        (await CompletesWithinBoundAsync(dispatch.Entered)).Should().BeTrue("the handler receives the event and holds its dispatch");
        var loopWaiting = LoopWaitingAsync(first, logger, backoff);

        first.CloseFromPeer();
        var waiting = await CompletesWithinBoundAsync(loopWaiting);
        var disconnect = connection.DisconnectAsync().AsTask();
        dispatch.Open();
        var seen = await ResultWithinBoundAsync(ended.Task);
        var disconnected = await CompletesWithinBoundAsync(disconnect);
        var dialled = await factory.NextWithinAsync(DialWindow(backoff));

        using (new AssertionScope())
        {
            waiting.Should().BeTrue(WhereTheLoopWaits(backoff));
            seen.Should().Be(new EndingSeen(AmiConnectionState.Disconnected, DisposeCount: 1),
                "the handler's DisposeAsync joins the caller's ending, which stops waiting for the loop that waits for the dispatch, " +
                "so it returns with the socket released exactly once and the connection Disconnected");
            disconnected.Should().BeTrue("the caller's DisconnectAsync returns");
            dialled.Should().BeNull(
                $"the caller's ending stops the reconnect loop, so it dials no more in {DialWindow(backoff).TotalMilliseconds} ms");
            factory.Created.Should().HaveCount(1, "the loop dials no socket once the caller's ending is recorded");
            connection.State.Should().Be(AmiConnectionState.Disconnected);
        }
    }

    /// <summary>
    /// A second event waits in the pump's buffer behind the dispatch the handler holds, by construction: the
    /// peer writes it before it answers a Ping, and the Ping's answer has arrived. The handler then ends the
    /// connection, and that second event is never dispatched.
    /// </summary>
    [Fact]
    public async Task OnEventHandler_ShouldReceiveNoLaterEvent_WhenItEndedTheConnection()
    {
        var run = await RunWithABufferedEventAsync(handlerEnds: true);

        using (new AssertionScope())
        {
            run.Seen.Should().Be(new EndingSeen(AmiConnectionState.Disconnected, DisposeCount: 1),
                "the handler's DisposeAsync returns without waiting for the dispatch it runs in");
            run.LaterEvent.Should().BeNull(
                $"the event pump dispatches no event after the one whose handler ended the connection, so the buffered one " +
                $"is not dispatched in {LaterEventWindow.TotalMilliseconds} ms");
            run.LaterDisposeCompleted.Should().BeTrue("a DisposeAsync after the handler's ending completes");
        }
    }

    /// <summary>
    /// The positive control of <see cref="LaterEventWindow"/>, green before and after the pump learned to stop:
    /// the same buffered event reaches a handler that returns without ending the connection inside the window.
    /// </summary>
    [Fact]
    public async Task EventPump_ShouldDispatchTheBufferedEvent_WhenTheHandlerReturnsWithoutEndingTheConnection()
    {
        var run = await RunWithABufferedEventAsync(handlerEnds: false);

        run.LaterEvent.Should().Be("PeerStatus",
            $"the buffered event is dispatched within {LaterEventWindow.TotalMilliseconds} ms once the dispatch in progress returns");
        run.LaterDisposeCompleted.Should().BeTrue();
    }

    private static AmiConnection Create(PipedSocketFactory factory, bool autoReconnect,
        ILogger<AmiConnection>? logger = null, TimeSpan? backoff = null) =>
        new(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            // Only the test ends the connection.
            EnableHeartbeat = false,
            AutoReconnect = autoReconnect,
            ReconnectInitialDelay = backoff ?? TimeSpan.FromMilliseconds(200),
            ReconnectMaxDelay = backoff ?? TimeSpan.FromMilliseconds(200),
        }), factory, logger ?? NullLogger<AmiConnection>.Instance);

    /// <summary>
    /// How long a test watches for a dial once the loop should have stopped: three backoff delays, and never
    /// less than a second. It is the observation, not a hang bound, and
    /// <see cref="ReconnectLoop_ShouldDialWithinTheObservationWindow_WhenTheHeldDispatchReturnsWithoutAnEnding"/>
    /// is its positive control for each backoff used here.
    /// </summary>
    private static TimeSpan DialWindow(TimeSpan backoff) =>
        TimeSpan.FromMilliseconds(Math.Max(1000, backoff.TotalMilliseconds * 3));

    /// <summary>
    /// Completes once the reconnect loop that the peer's close of <paramref name="lost"/> starts waits where the
    /// test needs it. With a backoff under a second: in its release before its first attempt, once that release
    /// has disposed the lost socket, which it does before it waits for the event pump. Otherwise: in its backoff
    /// delay, once it has written <c>[AMI] Reconnecting</c>, right before that delay.
    /// </summary>
    private static Task LoopWaitingAsync(PipedSocket lost, SignalingLogger<AmiConnection> logger, TimeSpan backoff)
    {
        if (backoff >= TimeSpan.FromSeconds(1))
            return logger.Logged("[AMI] Reconnecting");

        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lost.DuringFirstDispose = () => released.TrySetResult();
        return released.Task;
    }

    private static string WhereTheLoopWaits(TimeSpan backoff) =>
        backoff >= TimeSpan.FromSeconds(1)
            ? "the peer's close starts the reconnect loop, which waits out its backoff delay while the dispatch is in progress"
            : "the loop's release disposes the lost socket before it waits for the dispatch: the socket first, the event pump last";

    /// <summary>
    /// Connects; the handler holds the first event's dispatch while a second event is buffered behind it, then
    /// ends the connection with <c>DisposeAsync</c> or not, and returns. Reports what the handler saw when its
    /// ending returned, and the type of any later event dispatched within <see cref="LaterEventWindow"/>.
    /// </summary>
    private static async Task<BufferedEventRun> RunWithABufferedEventAsync(bool handlerEnds)
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var connection = Create(factory, autoReconnect: true);
        var dispatch = new HeldDispatch();
        var ended = new TaskCompletionSource<EndingSeen>(TaskCreationOptions.RunContinuationsAsynchronously);
        var later = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatches = 0;
        connection.OnEvent += async evt =>
        {
            if (Interlocked.Increment(ref dispatches) > 1)
            {
                later.TrySetResult(evt.EventType);
                return;
            }

            dispatch.Enter();
            await dispatch.Gate;
            if (handlerEnds)
                await connection.DisposeAsync();

            ended.TrySetResult(Seen(connection, factory));
        };
        var socket = await ConnectAsync(connection, factory, peerCts);
        (await socket.WriteEventAsync("FullyBooted")).Should().BeTrue("the peer sends the first event");
        (await CompletesWithinBoundAsync(dispatch.Entered)).Should().BeTrue("the handler receives it and holds its dispatch");

        (await socket.WriteEventAsync("PeerStatus")).Should().BeTrue("the peer sends a second event");
        var ping = connection.SendActionAsync(new PingAction()).AsTask();
        var action = await socket.ReadActionAsync(peerCts.Token).WaitAsync(Bound);
        PipedSocket.IsPing(action).Should().BeTrue("the connection sends the Ping");
        (await socket.RespondAsync("Success", PipedSocket.ActionIdOf(action!))).Should().BeTrue();
        // The reader read the second event before the Ping's answer, so it waits in the pump's buffer.
        (await CompletesWithinBoundAsync(ping)).Should().BeTrue("the Ping is answered while the handler holds the dispatch");

        dispatch.Open();
        var seen = await ResultWithinBoundAsync(ended.Task);
        var laterEvent = await ResultWithinAsync(later.Task, LaterEventWindow);
        var laterDispose = await CompletesWithinBoundAsync(connection.DisposeAsync().AsTask());
        return new BufferedEventRun(seen, laterEvent, laterDispose);
    }

    private static Task EndAsync(AmiConnection connection, string ending) => ending switch
    {
        nameof(AmiConnection.DisposeAsync) => connection.DisposeAsync().AsTask(),
        nameof(AmiConnection.DisconnectAsync) => connection.DisconnectAsync().AsTask(),
        _ => throw new ArgumentOutOfRangeException(nameof(ending), ending, "Not a caller's ending."),
    };

    /// <summary>The state, and the first socket's disposal count, read together the moment an ending returned.</summary>
    private static EndingSeen Seen(AmiConnection connection, PipedSocketFactory factory) =>
        new(connection.State, factory.Created[0].DisposeCount);

    /// <summary>
    /// Connects, with the next socket's peer completing the login; returns that socket. The peer's reads
    /// end with <paramref name="peerCts"/>.
    /// </summary>
    private static async Task<PipedSocket> ConnectAsync(AmiConnection connection, PipedSocketFactory factory,
        CancellationTokenSource peerCts)
    {
        var loggedIn = Task.Run(async () =>
        {
            var peer = await factory.NextAsync(peerCts.Token);
            await peer.CompleteLoginAsync(peerCts.Token);
            return peer;
        }, peerCts.Token);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        return await loggedIn.WaitAsync(Bound);
    }

    /// <summary>The peer answers the challenge and rejects the login.</summary>
    private static async Task RejectLoginAsync(PipedSocket peer, CancellationToken ct)
    {
        await peer.WriteAsync("Asterisk Call Manager/6.0.0\r\n");
        var challenge = await peer.ReadActionAsync(ct) ?? throw new InvalidOperationException("No challenge.");
        await peer.RespondAsync("Success", PipedSocket.ActionIdOf(challenge), [new("Challenge", "abc123")]);
        var login = await peer.ReadActionAsync(ct) ?? throw new InvalidOperationException("No login.");
        await peer.RespondAsync("Error", PipedSocket.ActionIdOf(login), [new("Message", "Authentication failed")]);
    }

    /// <summary>
    /// An <c>OnEvent</c> handler that holds the first dispatch until <see cref="HeldDispatch.Open"/>, so
    /// every release, which waits for the event pump, is held open with it.
    /// </summary>
    private static HeldDispatch HoldTheDispatch(AmiConnection connection)
    {
        var held = new HeldDispatch();
        connection.OnEvent += async _ =>
        {
            held.Enter();
            await held.Gate;
        };
        return held;
    }

    /// <summary>
    /// Completes when <see cref="AmiConnection.StateChanged"/> delivers the change to Disconnected, with that change
    /// described with the socket's disposal count taken as it is delivered. The change is announced after the state
    /// is written, so the count is never read before the state reads Disconnected.
    /// </summary>
    private static Task<string> WatchDisconnected(AmiConnection connection, PipedSocket socket)
    {
        var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.StateChanged += change =>
        {
            if (change.Current == AmiConnectionState.Disconnected)
                seen.TrySetResult(DisconnectedRead(socket.DisposeCount));
        };
        return seen.Task;
    }

    private static string DisconnectedRead(int disposeCount) =>
        $"Disconnected with the socket disposed {disposeCount} time(s)";

    /// <summary>
    /// Calls <c>DisposeAsync</c> now, and reads the state and the socket's disposal count the moment it returns.
    /// The socket is released before the event pump, so the state is what tells a return after the whole
    /// release from one while the pump is still held.
    /// </summary>
    private static async Task<EndingSeen> SeenWhenDisposeAsyncReturnsAsync(AmiConnection connection, PipedSocketFactory factory)
    {
        await connection.DisposeAsync();
        return Seen(connection, factory);
    }

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

    private static Task<T?> ResultWithinBoundAsync<T>(Task<T> task) where T : class =>
        ResultWithinAsync(task, Bound);

    /// <summary>
    /// The task's result if it completes within <paramref name="limit"/>, else <see langword="null"/>. With
    /// <see cref="Bound"/> it is a hang bound; with an observation window, the absence it observes.
    /// </summary>
    private static async Task<T?> ResultWithinAsync<T>(Task<T> task, TimeSpan limit) where T : class?
    {
        try
        {
            return await task.WaitAsync(limit);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private static async Task<T?> ValueWithinAsync<T>(Task<T> task, TimeSpan limit) where T : struct
    {
        try
        {
            return await task.WaitAsync(limit);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>What an ending's caller saw the moment it returned: the state and the first socket's disposal count.</summary>
    private sealed record EndingSeen(AmiConnectionState State, int DisposeCount);

    private sealed record BufferedEventRun(EndingSeen? Seen, string? LaterEvent, bool LaterDisposeCompleted);

    /// <summary>
    /// An observer whose <c>OnNext</c>, on the first event, blocks until the connection's <c>DisposeAsync</c>
    /// completes or <see cref="Bound"/> passes, and reports which through <see cref="Waited"/>.
    /// </summary>
    private sealed class DisposeWaitingObserver(AmiConnection connection) : IObserver<ManagerEvent>
    {
        private readonly TaskCompletionSource<bool> _waited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary><see langword="true"/> when the <c>DisposeAsync</c> completed within the bound.</summary>
        public Task<bool> Waited => _waited.Task;

        public void OnNext(ManagerEvent value)
        {
            if (_waited.Task.IsCompleted)
                return;

            _waited.TrySetResult(connection.DisposeAsync().AsTask().Wait(Bound));
        }

        public void OnError(Exception error)
        {
            // The connection never reports one; nothing to observe.
        }

        public void OnCompleted()
        {
            // The connection never reports one; nothing to observe.
        }
    }

    private sealed class HeldDispatch
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the handler has received an event and is holding its dispatch.</summary>
        public Task Entered => _entered.Task;

        public Task Gate => _gate.Task;

        public void Enter() => _entered.TrySetResult();

        public void Open() => _gate.TrySetResult();
    }
}
