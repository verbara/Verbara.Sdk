using System.Collections.Concurrent;
using System.Threading.Channels;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Ami.Connection;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// <see cref="AmiConnection.ReconnectSettled"/>: the internal notice, read by the live server, that a session's pending
/// reconnect has ended. It follows the last handler of that session's <see cref="AmiConnection.Reconnected"/> or
/// <see cref="AmiConnection.CallerReconnected"/>, finds <see cref="AmiConnection.ReconnectPending"/> false, names the
/// session, and is raised only by the delivery that cleared that session's pending reconnect.
/// </summary>
public sealed partial class AmiConnectionReconnectedDeliveryTests
{
    [Fact]
    public async Task ReconnectSettled_ShouldFollowTheLastReconnectedHandlerOnceAndNameTheSession_WhenTheReconnectLoopBringsItBack()
    {
        using var peerCts = new CancellationTokenSource(Bound * 3);
        var sockets = new PipedSocketFactory();
        var serving = ServeEverySocketAsync(sockets, peerCts.Token);
        await using var connection = new AmiConnection(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = true,
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(20),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(20),
        }), sockets, new SignalingLogger<AmiConnection>());
        var delivered = new ConcurrentQueue<string>();
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Reconnected += () => delivered.Enqueue($"first pending={connection.ReconnectPending}");
        connection.Reconnected += () => delivered.Enqueue($"second pending={connection.ReconnectPending}");
        connection.ReconnectSettled += session =>
        {
            delivered.Enqueue($"settled {session} pending={connection.ReconnectPending}");
            settled.TrySetResult();
        };
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);

        sockets.Created[0].CloseFromPeer();
        var raised = await CompletesWithinBoundAsync(settled.Task);
        await connection.PendingNotifications.WaitAsync(Bound);
        var sequence = string.Join(", ", delivered);
        await peerCts.CancelAsync();
        await serving;

        using (new AssertionScope())
        {
            raised.Should().BeTrue("the second session's delivery ended its pending reconnect");
            sequence.Should().Be("first pending=True, second pending=True, settled 2 pending=False",
                "the reconnect stays pending until the last handler returned, and its end is announced once, after it");
        }
    }

    [Fact]
    public async Task ReconnectSettled_ShouldFollowTheCallerReconnectedHandlers_WhenTheCallerBringsTheSessionBack()
    {
        await using var rig = new LostSessionRig();
        var delivered = new ConcurrentQueue<string>();
        rig.Connection.CallerReconnected += session => delivered.Enqueue($"session {session}");
        rig.Connection.ReconnectSettled += session =>
            delivered.Enqueue($"settled {session} pending={rig.Connection.ReconnectPending}");

        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Connection.Lost += _ => lost.TrySetResult();

        await rig.ConnectFirstAsync();
        await rig.LoseTheFirstSessionAsync();
        await lost.Task.WaitAsync(Bound);
        await rig.Connection.ConnectAsync().AsTask().WaitAsync(Bound);
        await rig.Connection.PendingNotifications.WaitAsync(Bound);

        string.Join(", ", delivered).Should().Be("session 2, settled 2 pending=False",
            "the caller's connect owes its session's handlers too, and their end is announced once, after them");
    }

    /// <summary>
    /// The second session's <c>Reconnected</c> handler holds the queue while that session is lost and the third logs in.
    /// The held delivery ends nothing — the pending reconnect is the third session's — so only the third session's
    /// delivery announces an end.
    /// </summary>
    [Fact]
    public async Task ReconnectSettled_ShouldBeRaisedOnlyForTheSurvivingSession_WhenTheHandlerOfALostSessionReturnsAfterTheNextLogin()
    {
        using var peerCts = new CancellationTokenSource(Bound * 3);
        var sockets = new PipedSocketFactory();
        var logins = Channel.CreateUnbounded<PipedSocket>();
        var serving = ServeAndSignalEveryLoginAsync(sockets, logins.Writer, peerCts.Token);
        var thirdConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connected = 0;
        var logger = new TailReadingLogger(line =>
        {
            if (line.StartsWith("[AMI] Connected", StringComparison.Ordinal) && Interlocked.Increment(ref connected) == 3)
                thirdConnected.TrySetResult();
        });
        var connection = new AmiConnection(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = true,
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(20),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(20),
        }), sockets, logger);
        using var gate = new ManualResetEventSlim(false);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = new ConcurrentQueue<long>();
        var thirdSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Reconnected += () =>
        {
            if (connection.EventLossMark.Session != 2)
                return;
            secondEntered.TrySetResult();
            gate.Wait(Bound * 3);
        };
        connection.ReconnectSettled += session =>
        {
            settled.Enqueue(session);
            if (session == 3)
                thirdSettled.TrySetResult();
        };

        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(Bound);
            await logins.Reader.ReadAsync(peerCts.Token).AsTask().WaitAsync(Bound);

            sockets.Created[0].CloseFromPeer();
            await logins.Reader.ReadAsync(peerCts.Token).AsTask().WaitAsync(Bound);
            await secondEntered.Task.WaitAsync(Bound);

            sockets.Created[1].CloseFromPeer();
            await logins.Reader.ReadAsync(peerCts.Token).AsTask().WaitAsync(Bound);
            await thirdConnected.Task.WaitAsync(Bound);

            gate.Set();
            var raised = await CompletesWithinBoundAsync(thirdSettled.Task);
            await connection.PendingNotifications.WaitAsync(Bound);

            using (new AssertionScope())
            {
                raised.Should().BeTrue("the third session's delivery ended its pending reconnect");
                settled.Should().Equal([3L],
                    "the second session's delivery, ending after the third login, cleared nothing and announces nothing");
            }
        }
        finally
        {
            gate.Set();
            await connection.DisposeAsync().AsTask().WaitAsync(Bound);
            await peerCts.CancelAsync();
            await serving;
        }
    }

    [Fact]
    public async Task ReconnectSettled_ShouldNotBeRaised_WhenTheCallerEndsTheConnectionBeforeTheQueuedReconnectedIsDelivered()
    {
        var run = await HoldLostUntilReconnectedIsQueuedAsync();
        var settled = new ConcurrentQueue<long>();
        run.Connection.ReconnectSettled += settled.Enqueue;

        await run.Connection.DisposeAsync().AsTask().WaitAsync(Bound);
        run.Gate.Set();
        await run.Connection.PendingNotifications.WaitAsync(Bound);
        await run.StopPeersAsync();

        settled.Should().BeEmpty("an ending recorded while the Reconnected waited delivers nothing, so it ends nothing");
    }

    [Fact]
    public async Task ReconnectSettled_ShouldLogASubscribersThrowAndLeaveNothingPending_WhenTheSubscriberThrows()
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
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(20),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(20),
        }), sockets, logger);
        connection.ReconnectSettled += _ => throw new InvalidOperationException("a subscriber that throws");
        var errorLogged = logger.Logged("[AMI] Reconnect handler error");
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);

        sockets.Created[0].CloseFromPeer();
        var logged = await CompletesWithinBoundAsync(errorLogged);
        await connection.PendingNotifications.WaitAsync(Bound);
        var pending = connection.ReconnectPending;
        await peerCts.CancelAsync();
        await serving;

        using (new AssertionScope())
        {
            logged.Should().BeTrue("the subscriber's throw is logged as a handler's is");
            logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Line.Contains("[AMI] Reconnect handler error"));
            pending.Should().BeFalse("the pending reconnect was cleared before its end was announced");
        }
    }
}
