using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// The token a caller hands <see cref="AmiConnection.ConnectAsync"/> cancels that connect — the dial, the banner, the
/// login and the version read — and has no authority over the session once the connect has returned. Cancelling it
/// afterwards neither ends the session nor announces it lost, with or without <c>AutoReconnect</c>; it still withdraws a
/// connect in progress; and cancelling one action's token never touches the session at all.
/// </summary>
/// <remarks>
/// No test sleeps. After a post-connect cancel, a Ping round trip is the synchronisation point: its response can only
/// be read by a reader loop still running after the cancel. Then the notification queue is drained and the assertions
/// read what was announced after the cancel. <see cref="Bound"/> is a hang bound, never the oracle.
/// </remarks>
public sealed class AmiConnectionConnectTokenTests
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConnectAsync_ShouldKeepTheSession_WhenTheConnectTokenIsCancelledAfterTheConnect(bool autoReconnect)
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var sockets = new PipedSocketFactory();
        await using var connection = Create(sockets, autoReconnect);
        var changes = new List<AmiConnectionStateChange>();
        var losses = new List<Exception?>();
        connection.StateChanged += changes.Add;
        connection.Lost += losses.Add;
        using var connectCts = new CancellationTokenSource();
        var peer = PlayLoginThenPingsAsync(sockets, peerCts.Token);

        await connection.ConnectAsync(connectCts.Token).AsTask().WaitAsync(Bound);
        await connection.PendingNotifications.WaitAsync(Bound);
        var changesBeforeCancel = changes.Count;

        await connectCts.CancelAsync();

        var ping = await PingAsync(connection);
        await connection.PendingNotifications.WaitAsync(Bound);

        using (new AssertionScope())
        {
            changes.Skip(changesBeforeCancel).Select(c => $"{c.Previous}>{c.Current}").Should().BeEmpty(
                "cancelling the connect token after the connect has returned announces no state change");
            losses.Should().BeEmpty("the session the connect established was not lost");
            connection.State.Should().Be(AmiConnectionState.Connected);
            ping.Should().Be("Success", "the session the connect established still answers");
            sockets.Created.Should().HaveCount(1, "the peer saw one login: nothing reconnected");
        }

        _ = peer;
    }

    [Fact]
    public async Task ConnectAsync_ShouldThrowOperationCanceledAndReleaseTheSocket_WhenTheTokenIsCancelledWhileTheLoginResponseIsAwaited()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var sockets = new PipedSocketFactory();
        await using var connection = Create(sockets, autoReconnect: true);
        using var connectCts = new CancellationTokenSource();
        var loginRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The peer answers the challenge, reads the login and never answers it.
        var peer = Task.Run(async () =>
        {
            var socket = await sockets.NextAsync(peerCts.Token);
            await socket.WriteAsync("Asterisk Call Manager/6.0.0\r\n");
            var challenge = await socket.ReadActionAsync(peerCts.Token);
            await socket.RespondAsync("Success", PipedSocket.ActionIdOf(challenge ?? ""), [new("Challenge", "abc123")]);
            await socket.ReadActionAsync(peerCts.Token);
            loginRead.TrySetResult();
        });

        var connect = ConnectAndCatchAsync(connection, connectCts.Token);
        await loginRead.Task.WaitAsync(Bound);
        await connectCts.CancelAsync();
        var (thrown, stateInCatch) = await connect.WaitAsync(Bound);

        using (new AssertionScope())
        {
            thrown.Should().BeAssignableTo<OperationCanceledException>("the token still cancels a connect in progress");
            stateInCatch.Should().Be(AmiConnectionState.Disconnected);
            sockets.Created.Should().HaveCount(1, "the attempt dialled one socket");
            sockets.Created[0].DisposeCount.Should().Be(1, "the attempt releases the socket it dialled");
        }

        _ = peer;
    }

    [Fact]
    public async Task SendEventGeneratingActionAsync_ShouldLeaveTheSessionConnected_WhenTheActionTokenIsCancelled()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var sockets = new PipedSocketFactory();
        await using var connection = Create(sockets, autoReconnect: true);
        var changes = new List<AmiConnectionStateChange>();
        var losses = new List<Exception?>();
        connection.StateChanged += changes.Add;
        connection.Lost += losses.Add;
        var actionRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The peer starts the action's event list and never ends it; it answers every Ping.
        var peer = Task.Run(async () =>
        {
            var socket = await sockets.NextAsync(peerCts.Token);
            await socket.CompleteLoginAsync(peerCts.Token);
            while (await socket.ReadActionAsync(peerCts.Token) is { } action)
            {
                if (PipedSocket.IsPing(action))
                {
                    await socket.RespondAsync("Success", PipedSocket.ActionIdOf(action));
                    continue;
                }

                await socket.RespondAsync("Success", PipedSocket.ActionIdOf(action), [new("EventList", "start")]);
                actionRead.TrySetResult();
            }
        });

        await connection.ConnectAsync(CancellationToken.None).AsTask().WaitAsync(Bound);
        await connection.PendingNotifications.WaitAsync(Bound);
        var changesBeforeCancel = changes.Count;

        using var actionCts = new CancellationTokenSource();
        var action = ReadToEndAsync(connection.SendEventGeneratingActionAsync(new StatusAction(), actionCts.Token));
        await actionRead.Task.WaitAsync(Bound);
        await actionCts.CancelAsync();
        var thrown = await action.WaitAsync(Bound);

        var ping = await PingAsync(connection);
        await connection.PendingNotifications.WaitAsync(Bound);

        using (new AssertionScope())
        {
            thrown.Should().BeAssignableTo<OperationCanceledException>("the action's own token ends the action");
            changes.Skip(changesBeforeCancel).Select(c => $"{c.Previous}>{c.Current}").Should().BeEmpty(
                "an action's token has no authority over the session");
            losses.Should().BeEmpty();
            connection.State.Should().Be(AmiConnectionState.Connected);
            ping.Should().Be("Success");
            sockets.Created.Should().HaveCount(1);
        }

        _ = peer;
    }

    private static AmiConnection Create(PipedSocketFactory sockets, bool autoReconnect) =>
        new(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = autoReconnect,
            ConnectionTimeout = TimeSpan.FromMinutes(1),
            DefaultEventTimeout = TimeSpan.FromMinutes(1),
        }), sockets, NullLogger<AmiConnection>.Instance);

    private static Task PlayLoginThenPingsAsync(PipedSocketFactory sockets, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            var socket = await sockets.NextAsync(cancellationToken);
            await socket.CompleteLoginAsync(cancellationToken);
            await socket.AnswerPingsAsync(cancellationToken);
        }, CancellationToken.None);

    /// <summary>The Ping's response, or the type of what the connection threw instead.</summary>
    private static async Task<string> PingAsync(AmiConnection connection)
    {
        try
        {
            var response = await connection.SendActionAsync(new PingAction()).AsTask().WaitAsync(Bound);
            return response.Response ?? "null";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ex.GetType().Name;
        }
    }

    private static async Task<(Exception? Thrown, AmiConnectionState StateInCatch)> ConnectAndCatchAsync(
        AmiConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await connection.ConnectAsync(cancellationToken);
            return (null, connection.State);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (ex, connection.State);
        }
    }

    /// <summary>What ended the enumeration, or <see langword="null"/> when it ended without throwing.</summary>
    private static async Task<Exception?> ReadToEndAsync(IAsyncEnumerable<ManagerEvent> events)
    {
        try
        {
            await foreach (var _ in events)
            {
            }

            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ex;
        }
    }
}
