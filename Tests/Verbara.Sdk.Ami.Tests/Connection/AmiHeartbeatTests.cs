using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// What the heartbeat does: a Ping every interval when it is enabled, none when it is disabled, and —
/// when a Ping goes unanswered — an ending the reader loop chooses exactly as it does for a peer-side
/// close: a reconnect when <see cref="AmiConnectionOptions.AutoReconnect"/> is on (owner ruling
/// 2026-09-26, ADR-0021), <see cref="AmiConnectionState.Disconnected"/> otherwise, and in both cases a
/// socket released exactly once.
/// </summary>
/// <remarks>
/// Each peer is an in-memory <see cref="PipedSocket"/>, a fresh one per connect, so the reconnect's own
/// connect has a peer to log in to. Every wait is bounded by <see cref="Bound"/> and ends on the signal
/// it asserts: a peer reading an action, the <c>Reconnected</c> event, a socket's disposal, a log line.
/// None of them sleeps.
/// </remarks>
public sealed class AmiHeartbeatTests
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// <summary>The window in which a disabled heartbeat must send nothing: fifty of its intervals.</summary>
    private static readonly TimeSpan SilenceWindow = TimeSpan.FromMilliseconds(500);

    private static AmiConnectionOptions HeartbeatOptions(bool autoReconnect) => new()
    {
        Hostname = "localhost",
        Username = "admin",
        Password = "secret",
        EnableHeartbeat = true,
        HeartbeatInterval = TimeSpan.FromMilliseconds(50),
        HeartbeatTimeout = TimeSpan.FromMilliseconds(250),
        // Longer than the heartbeat timeout, so the heartbeat's own timeout is what ends the Ping wait.
        DefaultResponseTimeout = TimeSpan.FromSeconds(5),
        AutoReconnect = autoReconnect,
        ReconnectInitialDelay = TimeSpan.FromMilliseconds(10),
        ReconnectMaxDelay = TimeSpan.FromMilliseconds(10),
    };

    [Fact]
    public async Task Heartbeat_ShouldSendPingAction_WhenIntervalElapses()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var options = HeartbeatOptions(autoReconnect: false);
        options.HeartbeatTimeout = TimeSpan.FromSeconds(5);
        var connection = Create(factory, options);

        // The first thing the peer reads after the login is what the heartbeat sent; it answers it.
        var firstActionAfterLogin = Task.Run(async () =>
        {
            var peer = await factory.NextAsync(peerCts.Token);
            await peer.CompleteLoginAsync(peerCts.Token);
            var action = await peer.ReadActionAsync(peerCts.Token);
            if (action is not null)
                await peer.RespondAsync("Success", PipedSocket.ActionIdOf(action));
            return action;
        }, peerCts.Token);

        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        var action = await ResultWithinBoundAsync(firstActionAfterLogin);

        action.Should().NotBeNull("an enabled heartbeat sends a Ping once its interval elapses");
        PipedSocket.IsPing(action).Should().BeTrue($"the heartbeat's action is a Ping, but the peer read: {action}");
        connection.State.Should().Be(AmiConnectionState.Connected);

        (await DisposeWithinBoundAsync(connection)).Should().BeTrue();
    }

    [Fact]
    public async Task Heartbeat_ShouldNotStart_WhenDisabled()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var options = HeartbeatOptions(autoReconnect: false);
        options.EnableHeartbeat = false;
        options.HeartbeatInterval = TimeSpan.FromMilliseconds(10);
        var connection = Create(factory, options);

        var loggedIn = Task.Run(async () =>
        {
            var peer = await factory.NextAsync(peerCts.Token);
            await peer.CompleteLoginAsync(peerCts.Token);
            return peer;
        }, peerCts.Token);

        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        var peer = await loggedIn.WaitAsync(Bound);

        var sentInWindow = await peer.ReadActionWithinAsync(SilenceWindow);

        sentInWindow.Should().BeNull(
            $"a disabled heartbeat sends nothing across fifty of its {options.HeartbeatInterval.TotalMilliseconds} ms intervals");

        // Control: the same read path sees a Ping as soon as one is sent, so the silence above is the
        // heartbeat's and not a peer that could not have heard it.
        var control = connection.SendActionAsync(new PingAction()).AsTask();
        var controlAction = await peer.ReadActionAsync(peerCts.Token);
        PipedSocket.IsPing(controlAction).Should().BeTrue();
        await peer.RespondAsync("Success", PipedSocket.ActionIdOf(controlAction!));
        (await control.WaitAsync(Bound)).Response.Should().Be("Success");

        (await DisposeWithinBoundAsync(connection)).Should().BeTrue();
    }

    [Fact]
    public async Task Heartbeat_ShouldReconnect_WhenPingTimesOutAndAutoReconnectIsOn()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var connection = Create(factory, HeartbeatOptions(autoReconnect: true));
        var reconnects = 0;
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Reconnected += () =>
        {
            Interlocked.Increment(ref reconnects);
            reconnected.TrySetResult();
        };

        // One script plays both peers in order, so the second can only ever take the second socket.
        var unansweredPing = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondPeer = Task.Run(async () =>
        {
            // The first socket's peer logs in, reads the heartbeat's Ping and never answers it.
            var first = await factory.NextAsync(peerCts.Token);
            await first.CompleteLoginAsync(peerCts.Token);
            unansweredPing.TrySetResult(await first.ReadActionAsync(peerCts.Token));

            // The socket the reconnect opens: its peer logs in and answers every Ping.
            var second = await factory.NextAsync(peerCts.Token);
            await second.CompleteLoginAsync(peerCts.Token);
            await second.AnswerPingsAsync(peerCts.Token);
        }, peerCts.Token);

        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        PipedSocket.IsPing(await unansweredPing.Task.WaitAsync(Bound)).Should().BeTrue("the first peer reads the Ping it leaves unanswered");

        var didReconnect = await CompletesWithinBoundAsync(reconnected.Task);

        didReconnect.Should().BeTrue(
            "a Ping left unanswered past HeartbeatTimeout with AutoReconnect on must reconnect, as a peer-side close does");
        connection.State.Should().Be(AmiConnectionState.Connected);
        Volatile.Read(ref reconnects).Should().Be(1);
        var sockets = factory.Created;
        sockets.Should().HaveCount(2, "the reconnect opens exactly one new socket");
        sockets[0].DisposeCount.Should().Be(1, "the dead socket is released exactly once before the new one is opened");

        (await DisposeWithinBoundAsync(connection)).Should().BeTrue();
        (await CompletesWithinBoundAsync(secondPeer)).Should().BeTrue("disposing the connection closes the second socket");
        sockets[1].DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task Heartbeat_ShouldEndDisconnectedAndReleaseTheSocket_WhenPingTimesOutAndAutoReconnectIsOff()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var logger = new SignalingLogger<AmiConnection>();
        var connection = Create(factory, HeartbeatOptions(autoReconnect: false), logger);
        // Written right after the state becomes Disconnected, and only once the ending has released
        // everything, so it is the signal that the ending completed.
        var ended = logger.Logged("[AMI] Disconnected");

        var unansweredPing = RunPeerThatNeverAnswersAPing(factory, peerCts.Token);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        PipedSocket.IsPing(await unansweredPing.WaitAsync(Bound)).Should().BeTrue("the peer reads the Ping it leaves unanswered");

        var didEnd = await CompletesWithinBoundAsync(ended);

        didEnd.Should().BeTrue(
            "a Ping left unanswered past HeartbeatTimeout with AutoReconnect off must end the connection, and the ending must complete");
        connection.State.Should().Be(AmiConnectionState.Disconnected);
        var socket = factory.Created.Should().ContainSingle().Subject;
        socket.DisposeCount.Should().Be(1, "the ending releases the socket, exactly once");

        (await DisposeWithinBoundAsync(connection)).Should().BeTrue("a later DisposeAsync completes");
        socket.DisposeCount.Should().Be(1, "DisposeAsync does not dispose an already released socket again");
        var connectAfterDispose = async () => await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        await connectAfterDispose.Should().ThrowAsync<ObjectDisposedException>(
            "DisposeAsync disposes the connection even when it had already ended on its own");
    }

    /// <summary>
    /// DisposeAsync is called as soon as the heartbeat has timed out, so it meets the ending wherever it
    /// is: not begun, in flight, or finished with the state already <see cref="AmiConnectionState.Disconnected"/>.
    /// Whichever it meets, it must return, and when it has returned the socket is released exactly once.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_ShouldReleaseTheSocket_WhenAHeartbeatTimeoutEndedTheConnection()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var logger = new SignalingLogger<AmiConnection>();
        var connection = Create(factory, HeartbeatOptions(autoReconnect: false), logger);
        var timedOut = logger.Logged("[AMI] Heartbeat timed out");

        _ = RunPeerThatNeverAnswersAPing(factory, peerCts.Token);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        (await CompletesWithinBoundAsync(timedOut)).Should().BeTrue("the heartbeat times out on the unanswered Ping");

        var disposed = await DisposeWithinBoundAsync(connection);

        disposed.Should().BeTrue("DisposeAsync must return after a heartbeat timeout ended the connection");
        var socket = factory.Created.Should().ContainSingle().Subject;
        socket.DisposeCount.Should().Be(1, "once DisposeAsync has returned the socket is released, exactly once");
        connection.State.Should().Be(AmiConnectionState.Disconnected);
    }

    /// <summary>
    /// DisposeAsync arriving while the ending is disposing the socket must wait for that release instead
    /// of running a second cleanup beside it, so the socket is disposed once and DisposeAsync returns
    /// after the release. The socket itself starts the DisposeAsync from inside its first disposal, which
    /// puts the call in that window by construction rather than by timing.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_ShouldWaitForTheRelease_WhenItArrivesWhileTheEndingReleasesTheSocket()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var connection = Create(factory, HeartbeatOptions(autoReconnect: false));
        var disposeDuringRelease = new TaskCompletionSource<Task?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var peer = Task.Run(async () =>
        {
            var peerSocket = await factory.NextAsync(peerCts.Token);
            // Set before the login completes, so before any ending can dispose the socket.
            peerSocket.DuringFirstDispose = () => disposeDuringRelease.TrySetResult(connection.DisposeAsync().AsTask());
            await peerSocket.CompleteLoginAsync(peerCts.Token);
            return await peerSocket.ReadActionAsync(peerCts.Token);
        }, peerCts.Token);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        PipedSocket.IsPing(await peer.WaitAsync(Bound)).Should().BeTrue("the peer reads the Ping it leaves unanswered");

        var dispose = await ResultWithinBoundAsync(disposeDuringRelease.Task);

        dispose.Should().NotBeNull("the heartbeat timeout's ending disposes the socket");
        (await CompletesWithinBoundAsync(dispose!)).Should().BeTrue("DisposeAsync returns once the release has finished");
        var socket = factory.Created.Should().ContainSingle().Subject;
        socket.DisposeCount.Should().Be(1, "a DisposeAsync that arrives during the release must not dispose the socket a second time");
        connection.State.Should().Be(AmiConnectionState.Disconnected);
    }

    /// <summary>
    /// The ending releases the connection but does not dispose it: only DisconnectAsync and DisposeAsync
    /// do. A caller who manages reconnects itself (AutoReconnect off) could call ConnectAsync again on a
    /// connection that ended on its own before the release existed, and still can.
    /// </summary>
    [Fact]
    public async Task ConnectAsync_ShouldConnectAgain_WhenAHeartbeatTimeoutEndedTheConnection()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var logger = new SignalingLogger<AmiConnection>();
        var connection = Create(factory, HeartbeatOptions(autoReconnect: false), logger);
        var ended = logger.Logged("[AMI] Disconnected");

        _ = RunPeerThatNeverAnswersAPing(factory, peerCts.Token);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        (await CompletesWithinBoundAsync(ended)).Should().BeTrue("the heartbeat timeout ends the connection");
        // The second socket's peer answers every Ping, so its heartbeat keeps the connection up.
        var secondPeer = Task.Run(async () =>
        {
            var peer = await factory.NextAsync(peerCts.Token);
            await peer.CompleteLoginAsync(peerCts.Token);
            await peer.AnswerPingsAsync(peerCts.Token);
        }, peerCts.Token);

        var reconnect = async () => await connection.ConnectAsync().AsTask().WaitAsync(Bound);

        await reconnect.Should().NotThrowAsync("a connection that ended on its own was released, not disposed");
        connection.State.Should().Be(AmiConnectionState.Connected);
        factory.Created.Should().HaveCount(2);
        factory.Created[0].DisposeCount.Should().Be(1);

        (await DisposeWithinBoundAsync(connection)).Should().BeTrue();
        (await CompletesWithinBoundAsync(secondPeer)).Should().BeTrue();
    }

    private static AmiConnection Create(PipedSocketFactory factory, AmiConnectionOptions options,
        ILogger<AmiConnection>? logger = null) =>
        new(Options.Create(options), factory, logger ?? NullLogger<AmiConnection>.Instance);

    /// <summary>A peer that completes the login, reads the heartbeat's Ping and never answers it.</summary>
    private static Task<string?> RunPeerThatNeverAnswersAPing(PipedSocketFactory factory, CancellationToken ct) =>
        Task.Run(async () =>
        {
            var peer = await factory.NextAsync(ct);
            await peer.CompleteLoginAsync(ct);
            return await peer.ReadActionAsync(ct);
        }, ct);

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

    private static async Task<T?> ResultWithinBoundAsync<T>(Task<T?> task) where T : class
    {
        try
        {
            return await task.WaitAsync(Bound);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private static Task<bool> DisposeWithinBoundAsync(AmiConnection connection) =>
        CompletesWithinBoundAsync(connection.DisposeAsync().AsTask());
}
