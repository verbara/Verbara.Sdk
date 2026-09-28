using Verbara.Sdk;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Tests.Connection;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// The pool's removal of a server is a caller's ending of the AMI connection behind it, so it completes
/// even when it is awaited from inside that connection's own event dispatch: the removal returns with the
/// connection released, instead of waiting on the dispatch that is waiting on it.
/// </summary>
/// <remarks>
/// <para>
/// The connection is a real <see cref="AmiConnection"/> over the in-memory <see cref="PipedSocket"/> harness
/// the AMI tests use, linked into this project. The server is handed in with
/// <see cref="VerbaraServerPool.AddExistingServer"/> and never started, so no state load has to be answered;
/// the connection's heartbeat is off, so only the test's event reaches the dispatch.
/// </para>
/// <para>
/// Every wait is bounded by <see cref="Bound"/> and ends on the signal it asserts. Reaching the bound is a
/// failure, never an observation.
/// </para>
/// </remarks>
public sealed class VerbaraServerPoolEndingTests
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RemoveServerAsync_ShouldComplete_WhenCalledFromInsideTheServersOwnEventDispatch()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var sockets = new PipedSocketFactory();
        var connection = new AmiConnectionFactory(sockets, NullLoggerFactory.Instance).Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            // Only the test's event reaches the dispatch, and only the removal ends the connection.
            EnableHeartbeat = false,
            AutoReconnect = true,
        });
        var socket = await ConnectAsync(connection, sockets, peerCts);
        await using var pool = new VerbaraServerPool(Substitute.For<IAmiConnectionFactory>(), NullLoggerFactory.Instance);
        pool.AddExistingServer("handed-in", new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance));

        var removed = new TaskCompletionSource<RemovalSeen>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OnEvent += async _ =>
        {
            await pool.RemoveServerAsync("handed-in");
            removed.TrySetResult(new RemovalSeen(connection.State, socket.DisposeCount));
        };
        (await socket.WriteEventAsync("FullyBooted")).Should().BeTrue("the peer sends one event");

        var seen = await ResultWithinBoundAsync(removed.Task);
        var laterDispose = await CompletesWithinBoundAsync(connection.DisposeAsync().AsTask());

        using (new AssertionScope())
        {
            seen.Should().Be(new RemovalSeen(AmiConnectionState.Disconnected, DisposeCount: 1),
                "the removal awaited inside the server's own dispatch returns, having disposed the connection behind " +
                "the server without waiting for that dispatch: the socket released exactly once and the connection Disconnected");
            laterDispose.Should().BeTrue("a DisposeAsync after the removal completes");
            socket.DisposeCount.Should().Be(1, "the socket is released exactly once, and a later DisposeAsync does not release it again");
            pool.ServerCount.Should().Be(0, "the server has left the pool");
        }
    }

    /// <summary>
    /// Connects, with the next socket's peer completing the login; returns that socket. The peer's reads
    /// end with <paramref name="peerCts"/>.
    /// </summary>
    private static async Task<PipedSocket> ConnectAsync(IAmiConnection connection, PipedSocketFactory sockets,
        CancellationTokenSource peerCts)
    {
        var loggedIn = Task.Run(async () =>
        {
            var peer = await sockets.NextAsync(peerCts.Token);
            await peer.CompleteLoginAsync(peerCts.Token);
            return peer;
        }, peerCts.Token);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        return await loggedIn.WaitAsync(Bound);
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

    /// <summary>The task's result if it completes within <see cref="Bound"/>, a hang bound; else <see langword="null"/>.</summary>
    private static async Task<T?> ResultWithinBoundAsync<T>(Task<T> task) where T : class
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

    /// <summary>What the handler saw the moment its removal returned: the connection's state and the socket's disposal count.</summary>
    private sealed record RemovalSeen(AmiConnectionState State, int DisposeCount);
}
