using Verbara.Sdk.Ami.Connection;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// <see cref="AmiConnection.Reconnected"/> reaches every handler, one after the other: a handler that throws is logged,
/// and the handlers subscribed after it still hear the reconnect. A consumer that subscribed before the live server, and
/// whose handler throws, must not keep the server's reload from running.
/// </summary>
/// <remarks>
/// A real <see cref="AmiConnection"/> over the in-memory <see cref="PipedSocket"/> harness; every socket's peer logs in
/// and answers Pings. Every wait is bounded by <see cref="Bound"/> and ends on the signal it asserts.
/// </remarks>
public sealed class AmiConnectionReconnectedDeliveryTests
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Reconnected_ShouldReachLaterHandlers_WhenAnEarlierHandlerThrows()
    {
        using var peerCts = new CancellationTokenSource(Bound * 3);
        var sockets = new PipedSocketFactory();
        var serving = ServeEverySocketAsync(sockets, peerCts.Token);
        var logger = new SignalingLogger<AmiConnection>();
        await using var connection = new AmiConnection(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = true,
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(50),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(50),
        }), sockets, logger);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        var later = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Reconnected += () => throw new InvalidOperationException("a consumer's handler that throws");
        connection.Reconnected += () => later.TrySetResult();
        var errorLogged = logger.Logged("[AMI] Reconnect handler error");

        sockets.Created[0].CloseFromPeer();
        var reached = await CompletesWithinBoundAsync(later.Task);
        var logged = await CompletesWithinBoundAsync(errorLogged);

        using (new AssertionScope())
        {
            reached.Should().BeTrue("a handler that throws does not keep the handlers after it from hearing the reconnect");
            logged.Should().BeTrue("the handler's exception is logged");
            logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Line.Contains("[AMI] Reconnect handler error"),
                "the throw is logged as an error");
        }

        await peerCts.CancelAsync();
        await serving;
    }

    /// <summary>
    /// A reconnect completes while a <see cref="AmiConnection.Lost"/> handler of the same outage still holds the
    /// notification queue, so its <see cref="AmiConnection.Reconnected"/> waits in the queue; the caller then ends the
    /// connection. <c>DisconnectAsync</c> and <c>DisposeAsync</c> raise no <see cref="AmiConnection.Reconnected"/>
    /// (<see cref="IAmiConnection.DisconnectAsync"/>), so the queued one is not delivered once the queue reaches it.
    /// </summary>
    /// <remarks>
    /// The test waits for the queue to grow past the held <c>Lost</c>, which is the reconnect loop queueing
    /// <c>Reconnected</c>. It never waits on <see cref="AmiConnectionState.Connected"/>: the connect writes it before the
    /// loop checks for an ending and queues the notification, so a dispose there would make the loop skip the queueing,
    /// and the test would pass for the wrong reason.
    /// </remarks>
    [Fact]
    public async Task DisposeAsync_ShouldDeliverNoQueuedReconnected_WhenTheCallerEndsTheConnectionFirst()
    {
        var run = await HoldLostUntilReconnectedIsQueuedAsync();

        await run.Connection.DisposeAsync().AsTask().WaitAsync(Bound);
        run.Gate.Set();
        await run.Connection.PendingNotifications.WaitAsync(Bound);
        await run.StopPeersAsync();

        Volatile.Read(ref run.ReconnectedCalls[0]).Should().Be(0,
            "the caller's ending was recorded before the queue reached the Reconnected, and an ending raises no Reconnected");
    }

    /// <summary>The positive control of the test above: the same queue, without the caller's ending, delivers it once.</summary>
    [Fact]
    public async Task Reconnected_ShouldBeDeliveredOnce_WhenTheLostHandlerReleasesTheQueueAndNobodyEndsTheConnection()
    {
        var run = await HoldLostUntilReconnectedIsQueuedAsync();

        run.Gate.Set();
        await run.Connection.PendingNotifications.WaitAsync(Bound);
        var calls = Volatile.Read(ref run.ReconnectedCalls[0]);
        await run.Connection.DisposeAsync().AsTask().WaitAsync(Bound);
        await run.StopPeersAsync();

        calls.Should().Be(1, "the queued Reconnected reaches its handler once the Lost handler before it returns");
    }

    private sealed record HeldQueueRun(
        AmiConnection Connection, ManualResetEventSlim Gate, int[] ReconnectedCalls, CancellationTokenSource PeerCts, Task Serving)
    {
        public async Task StopPeersAsync()
        {
            await PeerCts.CancelAsync();
            await Serving;
            PeerCts.Dispose();
            Gate.Dispose();
        }
    }

    /// <summary>
    /// Connects, holds the <see cref="AmiConnection.Lost"/> handler of a loss on a gate, and returns once the reconnect
    /// that follows has queued its <see cref="AmiConnection.Reconnected"/> behind it.
    /// </summary>
    private static async Task<HeldQueueRun> HoldLostUntilReconnectedIsQueuedAsync()
    {
        var peerCts = new CancellationTokenSource(Bound * 3);
        var sockets = new PipedSocketFactory();
        var serving = ServeEverySocketAsync(sockets, peerCts.Token);
        var connection = new AmiConnection(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = true,
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(20),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(20),
        }), sockets, new SignalingLogger<AmiConnection>());
        var gate = new ManualResetEventSlim(false);
        var lostEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnectedCalls = new int[1];
        connection.Lost += _ =>
        {
            lostEntered.TrySetResult();
            gate.Wait();
        };
        connection.Reconnected += () => Interlocked.Increment(ref reconnectedCalls[0]);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);

        sockets.Created[0].CloseFromPeer();
        await lostEntered.Task.WaitAsync(Bound);
        var tailHoldingLost = connection.PendingNotifications;
        await TailMovedPastAsync(connection, tailHoldingLost).WaitAsync(Bound);

        return new HeldQueueRun(connection, gate, reconnectedCalls, peerCts, serving);
    }

    /// <summary>Completes once the notification queue has a tail other than <paramref name="tail"/>.</summary>
    private static async Task TailMovedPastAsync(AmiConnection connection, Task tail)
    {
        while (ReferenceEquals(connection.PendingNotifications, tail))
            await Task.Delay(5); // fence-allow: LOOP-DRIVER — PendingNotifications exposes the queue's tail but no signal when it moves
    }

    /// <summary>Logs in every socket the connection creates and answers its Pings, until <paramref name="ct"/> is cancelled.</summary>
    private static async Task ServeEverySocketAsync(PipedSocketFactory sockets, CancellationToken ct)
    {
        var peers = new List<Task>();
        try
        {
            while (true)
            {
                var peer = await sockets.NextAsync(ct);
                peers.Add(Task.Run(() => ServeAsync(peer, ct), CancellationToken.None));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The test is over.
        }

        await Task.WhenAll(peers);
    }

    private static async Task ServeAsync(PipedSocket peer, CancellationToken ct)
    {
        try
        {
            await peer.CompleteLoginAsync(ct);
            await peer.AnswerPingsAsync(ct);
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
