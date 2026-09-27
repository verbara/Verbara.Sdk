using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// How an AMI connection ends: <see cref="AmiConnectionState.Disconnected"/> is reported only once the
/// ending that reports it has released the connection, and a <c>DisconnectAsync</c> or
/// <c>DisposeAsync</c> that arrives while another ending is still releasing waits for that release
/// instead of returning early.
/// </summary>
/// <remarks>
/// <para>
/// Each peer is an in-memory <see cref="PipedSocket"/>, a fresh one per connect. The release is held
/// open by construction: an <c>OnEvent</c> handler blocks its dispatch on a gate, and the release waits
/// for the event pump, which waits for that dispatch. Nothing else is left to chance.
/// </para>
/// <para>
/// Every wait is bounded by <see cref="Bound"/> and ends on the signal it asserts. The one exception is
/// <see cref="HeldReleaseWindow"/>, an observation window for an absence (the state never reads
/// <see cref="AmiConnectionState.Disconnected"/> while the release is held), paired with its positive
/// control: the same poll does see <see cref="AmiConnectionState.Disconnected"/> once the gate opens.
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

        var disconnect = connection.DisconnectAsync().AsTask();
        var whileHeld = await FirstDisconnectedReadAsync(connection, socket, HeldReleaseWindow);
        var stillReleasing = !disconnect.IsCompleted;

        dispatch.Open();
        var afterRelease = await FirstDisconnectedReadAsync(connection, socket, Bound);

        whileHeld.Should().BeNull(
            "the handler holds the release open, so the connection must not report Disconnected yet: " +
            "a caller who sees Disconnected must find the socket released");
        stillReleasing.Should().BeTrue("DisconnectAsync cannot finish while the handler holds the release open");
        afterRelease.Should().Be(DisconnectedRead(disposeCount: 1),
            "positive control: once the handler returns, the same poll sees Disconnected, with the socket released exactly once");
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

        var disconnect = connection.DisconnectAsync().AsTask();
        // Only places the DisposeAsync below; what this watch reads is asserted by the test above.
        await FirstDisconnectedReadAsync(connection, socket, HeldReleaseWindow);
        var disposeCountAtReturn = DisposeCountWhenDisposeAsyncReturnsAsync(connection, socket);

        dispatch.Open();

        (await ResultWithinBoundAsync(disposeCountAtReturn)).Should().Be(1,
            "a DisposeAsync issued while DisconnectAsync is still releasing returns only after that release, " +
            "so the socket has been disposed exactly once when it returns");
        connection.State.Should().Be(AmiConnectionState.Disconnected);
        (await CompletesWithinBoundAsync(disconnect)).Should().BeTrue("DisconnectAsync returns once the release has finished");
    }

    /// <summary>
    /// A pin, green before and after the single-flight ending: it holds where a lost connection's ending
    /// is forgotten. A connection lost with AutoReconnect off was released, not disposed, so its caller
    /// may connect it again; when that connect fails it leaves an open socket behind (the state reads
    /// Connecting), and a later <c>DisposeAsync</c> must release it rather than join the old, finished
    /// ending. Forgetting the old ending only once a connect succeeds would leave this socket open.
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
            "DisposeAsync releases the socket the failed connect left open, instead of joining the ending that released the first one");
        first.DisposeCount.Should().Be(1);
        connection.State.Should().Be(AmiConnectionState.Disconnected);
    }

    private static AmiConnection Create(PipedSocketFactory factory, bool autoReconnect,
        ILogger<AmiConnection>? logger = null) =>
        new(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            // Only the test ends the connection.
            EnableHeartbeat = false,
            AutoReconnect = autoReconnect,
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(200),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(200),
        }), factory, logger ?? NullLogger<AmiConnection>.Instance);

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
    /// Reads <see cref="AmiConnection.State"/> until it reads Disconnected, and describes that read with
    /// the socket's disposal count taken right after it; <see langword="null"/> when
    /// <paramref name="limit"/> passes first.
    /// </summary>
    private static async Task<string?> FirstDisconnectedReadAsync(AmiConnection connection, PipedSocket socket,
        TimeSpan limit)
    {
        using var limitCts = new CancellationTokenSource(limit);
        while (true)
        {
            if (connection.State == AmiConnectionState.Disconnected)
                return DisconnectedRead(socket.DisposeCount);

            if (limitCts.IsCancellationRequested)
                return null;

            await Task.Delay(5); // fence-allow: LOOP-DRIVER — AmiConnection exposes State but no state-change signal
        }
    }

    private static string DisconnectedRead(int disposeCount) =>
        $"Disconnected with the socket disposed {disposeCount} time(s)";

    /// <summary>Calls <c>DisposeAsync</c> now, and reads the socket's disposal count the moment it returns.</summary>
    private static async Task<int?> DisposeCountWhenDisposeAsyncReturnsAsync(AmiConnection connection, PipedSocket socket)
    {
        await connection.DisposeAsync();
        return socket.DisposeCount;
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

    private static async Task<int?> ResultWithinBoundAsync(Task<int?> task)
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
