using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// A caller's <see cref="AmiConnection.ConnectAsync"/> on a connection whose session is live — connected, or lost and
/// reconnecting — is refused with an <see cref="InvalidOperationException"/> before it acquires anything: it dials no
/// socket, and the live session, its reader loop and its reconnect loop go on as if the call had not been made.
/// </summary>
/// <remarks>
/// <para>
/// Before this, the second connect overwrote the live session's socket, token source and reader as it went: from
/// <see cref="AmiConnectionState.Connected"/> it leaked the old session and a later <see cref="AmiConnection.DisposeAsync"/>
/// hung on the old reader loop; from <see cref="AmiConnectionState.Reconnecting"/> it silently ended the reconnect loop.
/// The second dial is refused here (<see cref="PipedSocketFactory.ConnectsAccepted"/> = 1), so the unfixed code fails
/// at once instead of waiting for a banner.
/// </para>
/// <para>
/// Each test closes the first socket from the peer before it disposes the connection, and bounds that disposal: the
/// unfixed code's cleanup waits for a reader loop running on a token it no longer holds, and only the peer's close ends it.
/// </para>
/// </remarks>
public sealed class AmiConnectionLiveSessionConnectTests
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ConnectAsync_ShouldThrowInvalidOperation_WhenTheConnectionIsConnected()
    {
        using var peerCts = new CancellationTokenSource(Bound * 3);
        var sockets = new PipedSocketFactory { ConnectsAccepted = 1 };
        var connection = Create(sockets);
        try
        {
            var served = ServeFirstAsync(sockets, peerCts.Token);
            await connection.ConnectAsync().AsTask().WaitAsync(Bound);
            var first = await served.WaitAsync(Bound);

            var thrown = await Record.ExceptionAsync(() => connection.ConnectAsync().AsTask().WaitAsync(Bound));

            using (new AssertionScope())
            {
                thrown.Should().BeOfType<InvalidOperationException>(
                    "a connect on a connection whose session is live is refused, not run over that session");
                connection.State.Should().Be(AmiConnectionState.Connected, "the refused connect leaves the live session as it was");
                sockets.Created.Should().HaveCount(1, "the refused connect dials nothing");
                first.DisposeCount.Should().Be(0, "the live session's socket stays open");
            }
        }
        finally
        {
            await EndAsync(sockets, connection);
        }
    }

    [Fact]
    public async Task ConnectAsync_ShouldThrowInvalidOperation_WhenTheConnectionIsReconnecting()
    {
        using var peerCts = new CancellationTokenSource(Bound * 3);
        var sockets = new PipedSocketFactory { ConnectsAccepted = 1 };
        var connection = Create(sockets);
        try
        {
            var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.Lost += _ => lost.TrySetResult();
            var served = ServeFirstAsync(sockets, peerCts.Token);
            await connection.ConnectAsync().AsTask().WaitAsync(Bound);
            var first = await served.WaitAsync(Bound);
            // Lost is queued once the state has left Connected: from here the connection reads Reconnecting, and the
            // loop waits out its one-minute backoff.
            first.CloseFromPeer();
            await lost.Task.WaitAsync(Bound);

            var thrown = await Record.ExceptionAsync(() => connection.ConnectAsync().AsTask().WaitAsync(Bound));

            using (new AssertionScope())
            {
                thrown.Should().BeOfType<InvalidOperationException>(
                    "a connect on a connection that is reconnecting is refused; the reconnect loop owns the next attempt");
                connection.State.Should().Be(AmiConnectionState.Reconnecting,
                    "the refused connect leaves the reconnect loop waiting for its next attempt");
                sockets.Created.Should().HaveCount(1, "the refused connect dials nothing");
            }
        }
        finally
        {
            await EndAsync(sockets, connection);
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
            // Limits, never waits: no attempt and no backoff here runs until them.
            ConnectionTimeout = TimeSpan.FromMinutes(1),
            ReconnectInitialDelay = TimeSpan.FromMinutes(1),
            ReconnectMaxDelay = TimeSpan.FromMinutes(1),
        }), sockets, NullLogger<AmiConnection>.Instance);

    /// <summary>Plays the peer of the first socket through the login, and hands it back.</summary>
    private static Task<PipedSocket> ServeFirstAsync(PipedSocketFactory sockets, CancellationToken ct) =>
        Task.Run(async () =>
        {
            var socket = await sockets.NextAsync(ct);
            await socket.CompleteLoginAsync(ct);
            return socket;
        }, ct);

    /// <summary>
    /// Closes the first socket from the peer, so a reader loop the unfixed code left on a token it no longer holds ends,
    /// then disposes the connection within the bound.
    /// </summary>
    private static async Task EndAsync(PipedSocketFactory sockets, AmiConnection connection)
    {
        if (sockets.Created.Count > 0)
            sockets.Created[0].CloseFromPeer();

        await connection.DisposeAsync().AsTask().WaitAsync(Bound);
    }
}
