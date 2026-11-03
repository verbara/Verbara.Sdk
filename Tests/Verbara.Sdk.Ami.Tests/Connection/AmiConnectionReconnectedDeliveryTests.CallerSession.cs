using System.Collections.Concurrent;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// <see cref="AmiConnection.CallerReconnected"/>: the internal notice of a session the caller's own <c>ConnectAsync</c>
/// brought back after an earlier session ended. It follows the loss on the notification queue and names the session, the
/// first session and the reconnect loop's raise none, and the public <see cref="AmiConnection.Reconnected"/> stays the
/// loop's alone.
/// </summary>
public sealed partial class AmiConnectionReconnectedDeliveryTests
{
    [Fact]
    public async Task CallerReconnected_ShouldNameTheNewSessionBehindItsLossAndRaiseNoReconnected_WhenTheCallerReconnects()
    {
        await using var rig = new LostSessionRig();
        using var gate = new ManualResetEventSlim(false);
        var lostHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new ConcurrentQueue<string>();
        rig.Connection.Lost += _ =>
        {
            lostHeld.TrySetResult();
            gate.Wait(Bound);
            delivered.Enqueue("lost");
        };
        rig.Connection.Reconnected += () => delivered.Enqueue("reconnected");
        rig.Connection.CallerReconnected += session => delivered.Enqueue($"session {session}");

        await rig.ConnectFirstAsync();
        await rig.LoseTheFirstSessionAsync();
        await lostHeld.Task.WaitAsync(Bound);
        await rig.Connection.ConnectAsync().AsTask().WaitAsync(Bound);
        var pendingBehindTheLoss = rig.Connection.ReconnectPending;
        var deliveredBehindTheLoss = string.Join(", ", delivered);
        gate.Set();
        await rig.Connection.PendingNotifications.WaitAsync(Bound);

        new
        {
            PendingBehindTheLoss = pendingBehindTheLoss,
            DeliveredBehindTheLoss = deliveredBehindTheLoss,
            Delivered = string.Join(", ", delivered),
            PendingAfter = rig.Connection.ReconnectPending,
        }.Should().BeEquivalentTo(
            new
            {
                PendingBehindTheLoss = true,
                DeliveredBehindTheLoss = string.Empty,
                Delivered = "lost, session 2",
                PendingAfter = false,
            },
            "the session is owed its handlers from its login until they have returned, behind the loss before it, and "
            + "the public Reconnected stays the reconnect loop's");
    }

    [Fact]
    public async Task CallerReconnected_ShouldNotBeRaised_ForTheFirstSession()
    {
        await using var rig = new LostSessionRig();
        var delivered = new ConcurrentQueue<long>();
        rig.Connection.CallerReconnected += delivered.Enqueue;

        await rig.ConnectFirstAsync();
        var pending = rig.Connection.ReconnectPending;
        await rig.Connection.PendingNotifications.WaitAsync(Bound);

        new { Delivered = delivered.ToArray(), Pending = pending }.Should().BeEquivalentTo(
            new { Delivered = Array.Empty<long>(), Pending = false },
            "no session came before the first, so nothing was lost and nothing comes back");
    }

    /// <summary>
    /// The notice is decided at the login, so a live server that subscribes after the connect does not hear it and its
    /// start loads that session once. The loss held on the queue keeps a notice queued from being delivered before the
    /// late handler is in place.
    /// </summary>
    [Fact]
    public async Task CallerReconnected_ShouldQueueNothingAndLeaveNothingPending_WhenNothingListensAtTheLogin()
    {
        await using var rig = new LostSessionRig();
        using var gate = new ManualResetEventSlim(false);
        var lostHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Connection.Lost += _ =>
        {
            lostHeld.TrySetResult();
            gate.Wait(Bound);
        };

        await rig.ConnectFirstAsync();
        await rig.LoseTheFirstSessionAsync();
        await lostHeld.Task.WaitAsync(Bound);
        await rig.Connection.ConnectAsync().AsTask().WaitAsync(Bound);
        var pending = rig.Connection.ReconnectPending;
        var delivered = new ConcurrentQueue<long>();
        rig.Connection.CallerReconnected += delivered.Enqueue;
        gate.Set();
        await rig.Connection.PendingNotifications.WaitAsync(Bound);

        new { Pending = pending, Delivered = delivered.ToArray() }.Should().BeEquivalentTo(
            new { Pending = false, Delivered = Array.Empty<long>() },
            "with no handler at the login no notice is owed, and a handler that subscribes later is not told of that session");
    }

    [Fact]
    public async Task CallerReconnected_ShouldNotBeRaised_WhenTheReconnectLoopBringsTheSessionBack()
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
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new ConcurrentQueue<long>();
        connection.Reconnected += () => reconnected.TrySetResult();
        connection.CallerReconnected += delivered.Enqueue;

        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        sockets.Created[0].CloseFromPeer();
        var heard = await CompletesWithinBoundAsync(reconnected.Task);
        await connection.PendingNotifications.WaitAsync(Bound);
        await connection.DisposeAsync().AsTask().WaitAsync(Bound);
        await peerCts.CancelAsync();
        await serving;

        new { Heard = heard, Delivered = delivered.ToArray() }.Should().BeEquivalentTo(
            new { Heard = true, Delivered = Array.Empty<long>() },
            "a session the loop brought back is announced once, on Reconnected");
    }

    [Fact]
    public async Task CallerReconnected_ShouldNameOnlyTheSessionThatLoggedIn_WhenAConnectFailsBeforeIt()
    {
        await using var rig = new LostSessionRig();
        var delivered = new ConcurrentQueue<long>();
        rig.Connection.CallerReconnected += delivered.Enqueue;

        await rig.ConnectFirstAsync();
        await rig.LoseTheFirstSessionAsync();
        await rig.LossDisconnected.WaitAsync(Bound);
        rig.Sockets.RefuseConnects = true;
        var refused = await LostSessionRig.OutcomeAsync(rig.Connection.ConnectAsync().AsTask());
        await rig.Connection.PendingNotifications.WaitAsync(Bound);
        var afterTheFailure = delivered.ToArray();
        rig.Sockets.RefuseConnects = false;
        await rig.Connection.ConnectAsync().AsTask().WaitAsync(Bound);
        await rig.Connection.PendingNotifications.WaitAsync(Bound);

        using (new AssertionScope())
        {
            refused.Should().NotBeNull("the socket refused the connect");
            afterTheFailure.Should().BeEmpty("a connect that failed started no session");
            delivered.Should().Equal([2L], "the session that logged in is the second, and a failed attempt is not one");
        }
    }

    [Fact]
    public async Task CallerReconnected_ShouldNotBeDelivered_WhenItsSessionIsNoLongerCurrent()
    {
        await using var rig = new LostSessionRig();
        using var gate = new ManualResetEventSlim(false);
        var lostHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new ConcurrentQueue<long>();
        var lostCalls = 0;
        rig.Connection.Lost += _ =>
        {
            if (Interlocked.Increment(ref lostCalls) != 1)
                return;

            lostHeld.TrySetResult();
            gate.Wait(Bound);
        };
        rig.Connection.CallerReconnected += delivered.Enqueue;

        await rig.ConnectFirstAsync();
        await rig.LoseTheFirstSessionAsync();
        await lostHeld.Task.WaitAsync(Bound);
        await rig.Connection.ConnectAsync().AsTask().WaitAsync(Bound);
        rig.Sockets.Created[1].CloseFromPeer();
        // Read directly: the held queue delivers no StateChanged.
        SpinWait.SpinUntil(() => rig.Connection.State != AmiConnectionState.Connected, Bound) // fence-allow: GUARD-TIMEOUT — failure bound on an observed state, never the winning arm of a green run
            .Should().BeTrue("the peer closed the second session");
        await rig.Connection.ConnectAsync().AsTask().WaitAsync(Bound);
        gate.Set();
        await rig.Connection.PendingNotifications.WaitAsync(Bound);

        delivered.Should().Equal([3L], "the session lost before its notice was delivered has no reload owed to it");
    }

    [Fact]
    public async Task CallerReconnected_ShouldNotBeDelivered_WhenTheCallerEndsTheConnectionFirst()
    {
        await using var rig = new LostSessionRig();
        using var gate = new ManualResetEventSlim(false);
        var lostHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new ConcurrentQueue<long>();
        rig.Connection.Lost += _ =>
        {
            lostHeld.TrySetResult();
            gate.Wait(Bound);
        };
        rig.Connection.CallerReconnected += delivered.Enqueue;

        await rig.ConnectFirstAsync();
        await rig.LoseTheFirstSessionAsync();
        await lostHeld.Task.WaitAsync(Bound);
        await rig.Connection.ConnectAsync().AsTask().WaitAsync(Bound);
        await rig.Connection.DisposeAsync().AsTask().WaitAsync(Bound);
        gate.Set();
        await rig.Connection.PendingNotifications.WaitAsync(Bound);

        delivered.Should().BeEmpty("an ending recorded while the notice waited ends the connection for good");
    }

    [Fact]
    public async Task CallerReconnected_ShouldReachLaterHandlers_WhenAnEarlierHandlerThrows()
    {
        await using var rig = new LostSessionRig();
        var delivered = new ConcurrentQueue<long>();
        rig.Connection.CallerReconnected += _ => throw new InvalidOperationException("a handler that throws");
        rig.Connection.CallerReconnected += delivered.Enqueue;

        await rig.ConnectFirstAsync();
        await rig.LoseTheFirstSessionAsync();
        await rig.LossDisconnected.WaitAsync(Bound);
        await rig.Connection.ConnectAsync().AsTask().WaitAsync(Bound);
        await rig.Connection.PendingNotifications.WaitAsync(Bound);

        using (new AssertionScope())
        {
            delivered.Should().Equal([2L], "a handler that throws does not keep the handlers after it from hearing");
            rig.Log.Entries.Should().Contain(e => e.Line.Contains("[AMI] Reconnect handler error"), "the throw is logged");
        }
    }

    /// <summary>
    /// The preceding session's fence: nothing of the new session exists until the lost session's buffered events have been
    /// delivered, so the notice cannot overtake them.
    /// </summary>
    [Fact]
    public async Task CallerReconnected_ShouldFollowTheLostSessionsDelivery_WhenAnEventOfItIsStillBeingDelivered()
    {
        await using var rig = new LostSessionRig();
        var delivered = new ConcurrentQueue<long>();
        rig.Connection.CallerReconnected += delivered.Enqueue;
        rig.HoldTheFirstEvent();

        await rig.ConnectFirstAsync();
        await rig.LoseTheFirstSessionAsync();
        await rig.LossDisconnecting.WaitAsync(Bound);
        var connect = rig.Connection.ConnectAsync().AsTask();
        var parked = await rig.ParkedAsync(connect);
        await rig.Connection.PendingNotifications.WaitAsync(Bound);
        var heldDelivery = new { Delivered = delivered.ToArray(), Sockets = rig.Sockets.Created.Count };
        rig.OpenTheGate();
        await connect.WaitAsync(Bound);
        await rig.Connection.PendingNotifications.WaitAsync(Bound);

        using (new AssertionScope())
        {
            parked.Should().BeTrue("the connect waits for the lost session's release");
            heldDelivery.Should().BeEquivalentTo(
                new { Delivered = Array.Empty<long>(), Sockets = 1 },
                "the new session does not exist while the lost session's event is in delivery");
            delivered.Should().Equal([2L], "and the notice follows once that delivery is over");
        }
    }
}
