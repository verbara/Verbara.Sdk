using System.Net.Sockets;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// A caller's <see cref="AmiConnection.ConnectAsync"/> that ends without a connection leaves the connection
/// <see cref="AmiConnectionState.Disconnected"/>, with the socket that attempt created released, and rethrows what ended
/// it unchanged: a refused dial, a banner that is not Asterisk's, a rejected login, or the caller's own cancellation.
/// <see cref="AmiConnectionState.Connecting"/> means a connect attempt is running, and after a failed one none is.
/// </summary>
/// <remarks>
/// <para>
/// The state is read inside the caller's <c>catch</c>: whatever the failed attempt writes, it has written before the
/// exception reaches the caller. A later <see cref="AmiConnection.ConnectAsync"/> on the same connection still proceeds,
/// and the reconnect loop's own failed attempt still returns the connection to
/// <see cref="AmiConnectionState.Reconnecting"/>; both are pinned here because the fix must not move them.
/// </para>
/// <para>
/// Each peer is an in-memory <see cref="PipedSocket"/>. Every wait is bounded by <see cref="Bound"/> and ends on the
/// signal it waits for; no time is a synchronisation.
/// </para>
/// </remarks>
public sealed class AmiConnectionFailedConnectTests
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ConnectAsync_ShouldLeaveDisconnected_WhenTheLoginIsRejected()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var sockets = new PipedSocketFactory();
        await using var connection = Create(sockets);
        var peer = PlayAsync(sockets, RejectLoginAsync, peerCts.Token);

        var (thrown, stateInCatch) = await ConnectAndCatchAsync(connection, CancellationToken.None);
        await peer.WaitAsync(Bound);

        using (new AssertionScope())
        {
            thrown.Should().BeOfType<AmiAuthenticationException>("the peer's rejection reaches the caller unchanged")
                .Which.Message.Should().Be("AMI login failed: Authentication failed");
            stateInCatch.Should().Be(AmiConnectionState.Disconnected,
                "no connect attempt runs once the caller's attempt has failed, so the state is not Connecting");
            sockets.Created.Should().HaveCount(1, "the attempt dialled one socket");
            sockets.Created[0].DisposeCount.Should().Be(1, "the attempt releases the socket it created before it rethrows");
        }
    }

    [Fact]
    public async Task ConnectAsync_ShouldLeaveDisconnected_WhenTheDialIsRefused()
    {
        var sockets = new PipedSocketFactory { ConnectsAccepted = 0 };
        await using var connection = Create(sockets);

        var (thrown, stateInCatch) = await ConnectAndCatchAsync(connection, CancellationToken.None);

        using (new AssertionScope())
        {
            thrown.Should().BeOfType<SocketException>("the refused dial reaches the caller unchanged")
                .Which.SocketErrorCode.Should().Be(SocketError.ConnectionRefused);
            stateInCatch.Should().Be(AmiConnectionState.Disconnected,
                "no connect attempt runs once the caller's attempt has failed, so the state is not Connecting");
            sockets.Created.Should().HaveCount(1, "the attempt dialled one socket");
            sockets.Created[0].DisposeCount.Should().Be(1, "the attempt releases the socket it created before it rethrows");
        }
    }

    [Fact]
    public async Task ConnectAsync_ShouldLeaveDisconnected_WhenTheBannerIsNotAsterisks()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var sockets = new PipedSocketFactory();
        await using var connection = Create(sockets);
        // The peer's first message is an AMI response, not the protocol identifier a banner is.
        var peer = PlayAsync(sockets, static (socket, _) => socket.RespondAsync("Error", "1"), peerCts.Token);

        var (thrown, stateInCatch) = await ConnectAndCatchAsync(connection, CancellationToken.None);
        await peer.WaitAsync(Bound);

        using (new AssertionScope())
        {
            thrown.Should().BeOfType<AmiProtocolException>("a banner that is not Asterisk's reaches the caller as it does today");
            stateInCatch.Should().Be(AmiConnectionState.Disconnected,
                "no connect attempt runs once the caller's attempt has failed, so the state is not Connecting");
            sockets.Created.Should().HaveCount(1, "the attempt dialled one socket");
            sockets.Created[0].DisposeCount.Should().Be(1, "the attempt releases the socket it created before it rethrows");
        }
    }

    [Fact]
    public async Task ConnectAsync_ShouldLeaveDisconnected_WhenTheCallerCancelsTheAttempt()
    {
        var sockets = new PipedSocketFactory();
        await using var connection = Create(sockets);
        using var callerCts = new CancellationTokenSource();

        // The peer accepts the dial and never sends a banner, so the attempt waits for it until the caller cancels.
        var connect = ConnectAndCatchAsync(connection, callerCts.Token);
        await sockets.NextAsync(CancellationToken.None).AsTask().WaitAsync(Bound);
        await callerCts.CancelAsync();
        var (thrown, stateInCatch) = await connect.WaitAsync(Bound);

        using (new AssertionScope())
        {
            thrown.Should().BeAssignableTo<OperationCanceledException>("the caller's own cancellation reaches the caller");
            stateInCatch.Should().Be(AmiConnectionState.Disconnected,
                "no connect attempt runs once the caller has withdrawn it, so the state is not Connecting");
            sockets.Created.Should().HaveCount(1, "the attempt dialled one socket");
            sockets.Created[0].DisposeCount.Should().Be(1, "the attempt releases the socket it created before it rethrows");
        }
    }

    [Fact]
    public async Task ConnectAsync_ShouldAnnounceTheFailureAsTheCallers_WhenTheLoginIsRejected()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var sockets = new PipedSocketFactory();
        await using var connection = Create(sockets);
        var changes = new List<AmiConnectionStateChange>();
        connection.StateChanged += changes.Add;
        var peer = PlayAsync(sockets, RejectLoginAsync, peerCts.Token);

        var (thrown, _) = await ConnectAndCatchAsync(connection, CancellationToken.None);
        await peer.WaitAsync(Bound);
        await connection.PendingNotifications.WaitAsync(Bound);

        using (new AssertionScope())
        {
            changes.Select(c => (c.Previous, c.Current)).Should().Equal(
                [(AmiConnectionState.Initial, AmiConnectionState.Connecting), (AmiConnectionState.Connecting, AmiConnectionState.Disconnected)],
                "the failed attempt announces one change out of Connecting, straight to Disconnected");
            changes.Should().OnlyContain(c => c.ByCaller, "both changes are the caller's connect");
            changes[^1].Cause.Should().BeSameAs(thrown, "the change carries what ended the attempt");
            changes[^1].IsFinal.Should().BeFalse("the caller's failed connect is not the connection giving up");
        }
    }

    [Fact]
    public async Task ConnectAsync_ShouldAnnounceNoCause_WhenTheCallerCancelsTheAttempt()
    {
        var sockets = new PipedSocketFactory();
        await using var connection = Create(sockets);
        var changes = new List<AmiConnectionStateChange>();
        connection.StateChanged += changes.Add;
        using var callerCts = new CancellationTokenSource();

        var connect = ConnectAndCatchAsync(connection, callerCts.Token);
        await sockets.NextAsync(CancellationToken.None).AsTask().WaitAsync(Bound);
        await callerCts.CancelAsync();
        await connect.WaitAsync(Bound);
        await connection.PendingNotifications.WaitAsync(Bound);

        using (new AssertionScope())
        {
            changes.Should().HaveCount(2, "the attempt announced Connecting, then its end");
            changes[^1].Current.Should().Be(AmiConnectionState.Disconnected);
            changes[^1].ByCaller.Should().BeTrue("the caller withdrew its own attempt");
            changes[^1].Cause.Should().BeNull("a withdrawal by the caller's own token is no failure to report");
        }
    }

    [Fact]
    public async Task ConnectAsync_ShouldConnect_WhenCalledAgainAfterAFailedAttempt()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var sockets = new PipedSocketFactory();
        await using var connection = Create(sockets);
        var rejected = PlayAsync(sockets, RejectLoginAsync, peerCts.Token);
        var (firstThrown, _) = await ConnectAndCatchAsync(connection, CancellationToken.None);
        await rejected.WaitAsync(Bound);

        var accepted = PlayAsync(sockets, static (socket, ct) => socket.CompleteLoginAsync(ct), peerCts.Token);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        await accepted.WaitAsync(Bound);

        using (new AssertionScope())
        {
            firstThrown.Should().BeOfType<AmiAuthenticationException>("the first attempt's login was rejected");
            connection.State.Should().Be(AmiConnectionState.Connected, "a failed connect does not end the connection for good");
            sockets.Created.Should().HaveCount(2, "the second connect dialled a socket of its own");
        }
    }

    [Fact]
    public async Task ReconnectLoop_ShouldReturnToReconnecting_WhenAnAttemptFails()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        // The first socket connects; every later one refuses its dial, as a peer that is down does.
        var sockets = new PipedSocketFactory { ConnectsAccepted = 1 };
        var logger = new SignalingLogger<AmiConnection>();
        await using var connection = new AmiConnection(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = true,
            // The first attempt waits 20 ms; the second one waits out a minute, which no wait here reaches.
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(20),
            ReconnectMultiplier = 3000,
            ReconnectMaxDelay = TimeSpan.FromMinutes(1),
        }), sockets, logger);
        var served = PlayAsync(sockets, static (socket, ct) => socket.CompleteLoginAsync(ct), peerCts.Token);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        await served.WaitAsync(Bound);
        var attemptFailed = logger.Logged("[AMI] Reconnect attempt failed");

        sockets.Created[0].CloseFromPeer();
        await attemptFailed.WaitAsync(Bound);

        using (new AssertionScope())
        {
            connection.State.Should().Be(AmiConnectionState.Reconnecting,
                "the loop's failed attempt returns the connection to Reconnecting, and the loop waits for its next attempt");
            sockets.Created.Should().HaveCount(2, "the loop made one attempt, and it was refused");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static AmiConnection Create(PipedSocketFactory sockets) =>
        new(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = true,
            // A limit, never a wait: no attempt here runs until it.
            ConnectionTimeout = TimeSpan.FromMinutes(1),
        }), sockets, NullLogger<AmiConnection>.Instance);

    /// <summary>The caller's connect, and the state it reads inside its own <c>catch</c>.</summary>
    private static async Task<(Exception? Thrown, AmiConnectionState StateInCatch)> ConnectAndCatchAsync(
        AmiConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await connection.ConnectAsync(cancellationToken).AsTask().WaitAsync(Bound, CancellationToken.None);
            return (null, connection.State);
        }
        catch (Exception ex) when (ex is not TimeoutException)
        {
            return (ex, connection.State);
        }
    }

    /// <summary>Plays the peer of the next socket the connection creates.</summary>
    private static Task PlayAsync(PipedSocketFactory sockets, Func<PipedSocket, CancellationToken, Task> play, CancellationToken ct) =>
        Task.Run(async () =>
        {
            var socket = await sockets.NextAsync(ct);
            await play(socket, ct);
        }, ct);

    private static async Task RejectLoginAsync(PipedSocket peer, CancellationToken ct)
    {
        await peer.WriteAsync("Asterisk Call Manager/6.0.0\r\n");
        var challenge = await peer.ReadActionAsync(ct) ?? throw new InvalidOperationException("No challenge.");
        await peer.RespondAsync("Success", PipedSocket.ActionIdOf(challenge), [new("Challenge", "abc123")]);
        var login = await peer.ReadActionAsync(ct) ?? throw new InvalidOperationException("No login.");
        await peer.RespondAsync("Error", PipedSocket.ActionIdOf(login), [new("Message", "Authentication failed")]);
    }
}
