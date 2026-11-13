using System.Collections.Concurrent;
using System.Reflection;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// <see cref="VerbaraServer.ChannelTableSettled"/>: the server's notice that the end of a pending reconnect found its
/// channel table covered. A pending reconnect is the one input of <see cref="VerbaraServer.ChannelTableCovered"/> that
/// turns true with no channel read completing after it, so a reload whose read completed while the reconnect was still
/// pending — behind a <c>Reconnected</c> handler that outlived it — is announced covered here, once; a read that completes
/// after the reconnect ended is announced by its own <see cref="VerbaraServer.ChannelSnapshotCompleted"/>, as before.
/// </summary>
/// <remarks>
/// A real <see cref="AmiConnection"/> over <see cref="Run"/>. A <c>Reconnected</c> handler subscribed after the server's
/// waits for the server's reload to return, so the reload's read completes while the reconnect is pending by
/// construction. Every wait ends on the signal it asserts; <see cref="Run.Bound"/> is reached only by a defect.
/// </remarks>
public sealed class ChannelTableSettledTests
{
    [Fact]
    public async Task ChannelTableSettled_ShouldBeRaisedOnceWithTheTableCovered_WhenAReconnectedHandlerAfterTheServersOutlivesTheReload()
    {
        var first = new BootingAsterisk { BootedAtLogin = true };
        var second = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(first, second, autoReconnect: true);
        var watched = Watch(run.Server);
        var pendingAtReload = LateHandlerWaitingFor(run.Connection, run.Server);

        first.CloseSession();
        run.ReleaseSecondPeer();
        var pending = await pendingAtReload.WaitAsync(Run.Bound);
        var raised = await CompletesWithinBoundAsync(watched.SettledOnce);
        await PendingNotificationsAsync(run.Connection);

        using (new AssertionScope())
        {
            pending.Should().BeTrue("premise: the reload returned while the late handler kept the reconnect pending");
            watched.Completions.Should().Equal([false],
                "premise: the reload's read completed while the reconnect was pending, so it found the table uncovered");
            raised.Should().BeTrue("the end of the pending reconnect found the table covered");
            watched.Settled.Should().Equal([true], "it is announced once, with the table covered");
        }
    }

    /// <summary>
    /// The same window on the caller's route: the session the caller's own connect brought back is pending until its
    /// <c>CallerReconnected</c> handlers return, and one subscribed after the server's outlives the reload.
    /// </summary>
    [Fact]
    public async Task ChannelTableSettled_ShouldBeRaisedOnceWithTheTableCovered_WhenACallerReconnectedHandlerAfterTheServersOutlivesTheReload()
    {
        var first = new BootingAsterisk { BootedAtLogin = true };
        var second = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(first, second, autoReconnect: false);
        var watched = Watch(run.Server);
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Server.ConnectionLost += _ => lost.TrySetResult();
        var pendingAtReload = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<long> late = _ =>
        {
#pragma warning disable VSTHRD002 // The test's point: a handler that blocks the notification queue until the reload returned
            var finished = run.Server.ReconnectReload.Wait(Run.Bound);
#pragma warning restore VSTHRD002
            pendingAtReload.TrySetResult(finished && run.Server.IsReconnectPending);
        };
        // Internal to the connection and not part of this suite's view of it, so it is subscribed the way Live's own
        // handler is, after the server's.
        typeof(AmiConnection).GetEvent("CallerReconnected", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetAddMethod(nonPublic: true)!.Invoke(run.Connection, [late]);

        first.CloseSession();
        await lost.Task.WaitAsync(Run.Bound);
        run.ReleaseSecondPeer();
        await run.Connection.ConnectAsync().AsTask().WaitAsync(Run.Bound);
        var pending = await pendingAtReload.Task.WaitAsync(Run.Bound);
        var raised = await CompletesWithinBoundAsync(watched.SettledOnce);
        await PendingNotificationsAsync(run.Connection);

        using (new AssertionScope())
        {
            pending.Should().BeTrue("premise: the reload returned while the late handler kept the caller's session pending");
            watched.Completions.Should().Equal([false],
                "premise: the reload's read completed while the reconnect was pending, so it found the table uncovered");
            raised.Should().BeTrue("the end of the caller's pending reconnect found the table covered");
            watched.Settled.Should().Equal([true], "it is announced once, with the table covered");
        }
    }

    [Fact]
    public async Task ChannelTableSettled_ShouldNotBeRaised_WhenTheReloadsReadCompletesAfterTheReconnectEnded()
    {
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new BootingAsterisk { BootedAtLogin = true };
        var second = new BootingAsterisk { BootedAtLogin = true, StatusAnsweredAfter = answer.Task };
        await using var run = await Run.StartAsync(first, second, autoReconnect: true);
        var watched = Watch(run.Server);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Server.ChannelSnapshotCompleted += _ => completed.TrySetResult();

        first.CloseSession();
        run.ReleaseSecondPeer();
        await second.AskedAtLeast("Status", 1).WaitAsync(Run.Bound);
        await PendingNotificationsAsync(run.Connection);
        var pendingBeforeTheAnswer = run.Server.IsReconnectPending;
        answer.SetResult();
        await completed.Task.WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            pendingBeforeTheAnswer.Should().BeFalse("premise: the reconnect ended before the reload's read completed");
            watched.Completions.Should().Equal([true], "the read that completes after the reconnect ended announces itself");
            watched.Settled.Should().BeEmpty("the end of the reconnect found the reload's read still open, the table uncovered");
        }
    }

    [Fact]
    public async Task ChannelTableSettled_ShouldBeRaisedOnceByEachServer_WhenTwoServersShareTheConnection()
    {
        var first = new BootingAsterisk { BootedAtLogin = true };
        var second = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(first, second, autoReconnect: true);
        await using var other = new VerbaraServer(run.Connection, NullLogger<VerbaraServer>.Instance) { TimeProvider = run.Clock };
        await other.StartAsync().WaitAsync(Run.Bound);
        var watchedFirst = Watch(run.Server);
        var watchedOther = Watch(other);
        var pendingAtReload = LateHandlerWaitingFor(run.Connection, run.Server, other);

        first.CloseSession();
        run.ReleaseSecondPeer();
        var pending = await pendingAtReload.WaitAsync(Run.Bound);
        var raisedFirst = await CompletesWithinBoundAsync(watchedFirst.SettledOnce);
        var raisedOther = await CompletesWithinBoundAsync(watchedOther.SettledOnce);
        await PendingNotificationsAsync(run.Connection);

        using (new AssertionScope())
        {
            pending.Should().BeTrue("premise: both reloads returned while the late handler kept the reconnect pending");
            raisedFirst.Should().BeTrue("the end of the pending reconnect found the first server's table covered");
            raisedOther.Should().BeTrue("and the second server's, each of which subscribed on its own");
            watchedFirst.Settled.Should().Equal([true], "each server announces once, for itself");
            watchedOther.Settled.Should().Equal([true], "each server announces once, for itself");
        }
    }

    [Fact]
    public async Task ChannelTableSettled_ShouldNotBeRaised_WhenTheServerIsDisposedBeforeThePendingReconnectEnds()
    {
        var first = new BootingAsterisk { BootedAtLogin = true };
        var second = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(first, second, autoReconnect: true);
        var watched = Watch(run.Server);
        using var hold = new HandlerHold();
        var reloaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Connection.Reconnected += () =>
        {
#pragma warning disable VSTHRD002 // The test's point: a handler that blocks the notification queue past the reload
            reloaded.TrySetResult(run.Server.ReconnectReload.Wait(Run.Bound));
#pragma warning restore VSTHRD002
            hold.Block();
        };

        first.CloseSession();
        run.ReleaseSecondPeer();
        var finished = await reloaded.Task.WaitAsync(Run.Bound);
        await hold.Held.WaitAsync(Run.Bound);
        await run.Server.DisposeAsync().AsTask().WaitAsync(Run.Bound);
        hold.Release();
        await PendingNotificationsAsync(run.Connection);

        using (new AssertionScope())
        {
            finished.Should().BeTrue("premise: the reload returned before the server was disposed");
            watched.Settled.Should().BeEmpty("a disposed server announces nothing");
        }
    }

    /// <summary>
    /// Two channel reads apply at once: B, whose barrier ran out, writes the verified flag down and is held before it
    /// leaves; A, whose wait was met, writes it up and leaves while B is still counted, so A's own completion is not
    /// raised. B, leaving last, finds the table covered and announces it.
    /// </summary>
    [Fact]
    public async Task ChannelTableSettled_ShouldBeRaisedOnceWithTheTableCovered_WhenADeferredApplyLeavesLastBehindAFullOne()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        var watched = Watch(run.Server);
        using var gate = new DispatchGate();
        using var subscription = run.Connection.Subscribe(gate);
        using var bWritten = new ManualResetEventSlim();
        using var bGo = new ManualResetEventSlim();
        run.Server.ChannelApplicationWritten = full =>
        {
            if (full || bWritten.IsSet) return;
            bWritten.Set();
            bGo.Wait(Run.Bound * 3);
        };

        var b = await StartDeferredAsync(run, peer, gate, bWritten);
        gate.Release();
        await run.Server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound);
        var completeWhileBCounted = run.Server.ChannelTableComplete;
        var settledWhileBCounted = watched.Settled.Count;
        bGo.Set();
        await b.WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            completeWhileBCounted.Should().BeFalse("premise: B is still counted when A leaves");
            settledWhileBCounted.Should().Be(0, "premise: nothing announced the table while B was counted");
            watched.Completions.Should().BeEmpty("A left while B was counted, and B did not apply in full");
            watched.Settled.Should().Equal([true], "B, leaving last, announces the covered table once");
        }
    }

    /// <summary>
    /// The same two reads with the exits the other way round: B leaves while A is still applying, and A, leaving last,
    /// raises its own completion. B announces nothing.
    /// </summary>
    [Fact]
    public async Task ChannelTableSettled_ShouldNotBeRaised_WhenTheDeferredApplyLeavesBeforeTheFullOne()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusChannels = [] };
        await using var run = await Run.StartAsync(peer);
        var watched = Watch(run.Server);
        using var gate = new DispatchGate();
        using var subscription = run.Connection.Subscribe(gate);
        using var bWritten = new ManualResetEventSlim();
        using var bGo = new ManualResetEventSlim();
        using var aApplying = new ManualResetEventSlim();
        using var aGo = new ManualResetEventSlim();
        run.Server.ChannelApplicationWritten = full =>
        {
            if (full || bWritten.IsSet) return;
            bWritten.Set();
            bGo.Wait(Run.Bound * 3);
        };
        run.Server.Channels.ChannelAdded += channel =>
        {
            if (channel.UniqueId != "a" || aApplying.IsSet) return;
            aApplying.Set();
            aGo.Wait(Run.Bound * 3);
        };

        var b = await StartDeferredAsync(run, peer, gate, bWritten);
        gate.Release();
        peer.StatusChannels = [new StatusChannel("a", "PJSIP/a", LinkedId: "call")];
        var a = Task.Run(async () => await run.Server.ReconcileChannelsAsync());
        aApplying.Wait(Run.Bound).Should().BeTrue("premise: A began to apply while B was held");
        bGo.Set();
        await b.WaitAsync(Run.Bound);
        var settledAfterB = watched.Settled.Count;
        aGo.Set();
        await a.WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            settledAfterB.Should().Be(0, "B left while A was counted: the table did not read covered");
            watched.Completions.Should().Equal([true], "A, leaving last, raises its own completion once");
            watched.Settled.Should().BeEmpty();
            run.Server.ChannelTableCovered.Should().BeTrue();
        }
    }

    [Fact]
    public async Task ChannelTableSettled_ShouldNotBeRaised_WhenADeferredApplyLeavesAlone()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        var watched = Watch(run.Server);
        using var gate = new DispatchGate();
        using var subscription = run.Connection.Subscribe(gate);
        using var written = new ManualResetEventSlim();
        run.Server.ChannelApplicationWritten = full =>
        {
            if (!full) written.Set();
        };

        var b = await StartDeferredAsync(run, peer, gate, written);
        await b.WaitAsync(Run.Bound);
        gate.Release();

        using (new AssertionScope())
        {
            run.Server.ChannelTableCovered.Should().BeFalse("a read that waited out its bound leaves the table incomplete");
            watched.Completions.Should().BeEmpty();
            watched.Settled.Should().BeEmpty("a deferred apply alone leaves no covered table to announce");
        }
    }

    // ---- The rig ----

    private sealed class Watched
    {
        public ConcurrentQueue<bool> Completions { get; } = new();

        public ConcurrentQueue<bool> Settled { get; } = new();

        public TaskCompletionSource Raised { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SettledOnce => Raised.Task;
    }

    /// <summary>Records, for each raise of either notice, whether the table read covered inside it.</summary>
    private static Watched Watch(VerbaraServer server)
    {
        var watched = new Watched();
        server.ChannelSnapshotCompleted += _ => watched.Completions.Enqueue(server.ChannelTableCovered);
        server.ChannelTableSettled += _ =>
        {
            watched.Settled.Enqueue(server.ChannelTableCovered);
            watched.Raised.TrySetResult();
        };
        return watched;
    }

    /// <summary>
    /// Subscribes a <c>Reconnected</c> handler after every server's: it returns once each server's reload has returned,
    /// and reports whether the reconnect was still pending then.
    /// </summary>
    private static Task<bool> LateHandlerWaitingFor(AmiConnection connection, params VerbaraServer[] servers)
    {
        var pendingAtReload = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Reconnected += () =>
        {
#pragma warning disable VSTHRD002 // The test's point: a handler that blocks the notification queue until the reloads returned
            var finished = servers.All(s => s.ReconnectReload.Wait(Run.Bound));
#pragma warning restore VSTHRD002
            pendingAtReload.TrySetResult(finished && servers.All(s => s.IsReconnectPending));
        };
        return pendingAtReload.Task;
    }

    // Internal to the connection and not part of what Live binds to, so it is read the way the reconnect tests read it.
    private static async Task PendingNotificationsAsync(AmiConnection connection)
    {
        var pending = (Task)typeof(AmiConnection)
            .GetProperty("PendingNotifications", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(connection)!;
        await pending.WaitAsync(Run.Bound);
    }

    /// <summary>
    /// Holds the dispatch, starts a channel read, runs its barrier out on the run's clock and returns once the read has
    /// written its verified flag (<paramref name="written"/> is set by the test's seam handler).
    /// </summary>
    private static async Task<Task> StartDeferredAsync(Run run, BootingAsterisk peer, DispatchGate gate,
        ManualResetEventSlim written)
    {
        await peer.SendEventAsync("UserEvent", DispatchGate.Frame());
        await gate.Held.WaitAsync(Run.Bound);
        while (run.Clock.TimersCreated.TryRead(out _))
        {
            // Only the barrier of this read is run out.
        }

        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Server.FenceWaitObserved = (_, _) => waiting.TrySetResult();
        var read = Task.Run(async () => await run.Server.ReconcileChannelsAsync());
        await waiting.Task.WaitAsync(Run.Bound);
        run.Server.FenceWaitObserved = null;
        await run.Clock.TimersCreated.ReadAsync().AsTask().WaitAsync(Run.Bound);
        _ = Task.Run(() => run.Clock.Advance(VerbaraServer.BarrierBound + TimeSpan.FromSeconds(1)));
        written.Wait(Run.Bound).Should().BeTrue("premise: the read whose barrier ran out wrote its flag");
        return read;
    }

    private static async Task<bool> CompletesWithinBoundAsync(Task task)
    {
        try
        {
            await task.WaitAsync(Run.Bound);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
