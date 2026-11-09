using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Sdk.Ami;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;

namespace Verbara.Sdk.Live.Tests.Server;

public sealed class ChannelTableCompletenessTests
{
    [Fact]
    public async Task Snapshot_ShouldCompleteOnItsBarrierAndCarryInstanceAndSession_WhenReadBeganBeforeSubscription()
    {
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusAnsweredAfter = answer.Task };
        await using var run = await Run.ConnectAsync(peer);
        var snapshot = run.Server.ReconcileChannelsAsync().AsTask();
        try
        {
            await peer.AskedAtLeast("Status", 1).WaitAsync(Run.Bound);
            run.Server.ChannelTableComplete.Should().BeFalse();
            var completed = new ConcurrentQueue<ChannelSnapshotCompletion>();
            run.Server.ChannelSnapshotCompleted += completed.Enqueue;
            answer.SetResult();
            await snapshot.WaitAsync(Run.Bound);
            run.Server.ChannelTableComplete.Should().BeTrue();
            completed.Should().ContainSingle();
            completed.Single().Server.Should().BeSameAs(run.Server);
            completed.Single().SessionIdentity.Should().NotBeNull();
            completed.Single().SessionIdentity!.IsCompletedSuccessfully.Should().BeTrue();
            completed.Single().SessionGeneration.Should().Be(0);
        }
        finally
        {
            answer.TrySetResult();
            await snapshot.WaitAsync(Run.Bound);
        }
    }

    [Fact]
    public async Task Snapshot_ShouldWaitForBarrierBeforeCompletion_WhenDispatchHasNotReachedStatusComplete()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        using var gate = new DispatchGate();
        using var subscription = run.Connection.Subscribe(gate);
        await peer.SendEventAsync("UserEvent", DispatchGate.Frame());
        await gate.Held.WaitAsync(Run.Bound);
        var completed = 0;
        run.Server.ChannelSnapshotCompleted += _ => Interlocked.Increment(ref completed);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Server.FenceWaitObserved = (_, _) => waiting.TrySetResult();
        var snapshot = run.Server.ReconcileChannelsAsync().AsTask();
        try
        {
            await waiting.Task.WaitAsync(Run.Bound);
            completed.Should().Be(0);
            snapshot.IsCompleted.Should().BeFalse();
        }
        finally { gate.Release(); }
        await snapshot.WaitAsync(Run.Bound);
        completed.Should().Be(1);
        run.Server.ChannelTableComplete.Should().BeTrue();
    }

    [Fact]
    public async Task Snapshot_ShouldClearCompleteAndRaiseNone_WhenItsBarrierIsDeferred()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        run.Server.ChannelTableComplete.Should().BeTrue();
        using var gate = new DispatchGate();
        using var subscription = run.Connection.Subscribe(gate);
        await peer.SendEventAsync("UserEvent", DispatchGate.Frame());
        await gate.Held.WaitAsync(Run.Bound);
        var completed = 0;
        run.Server.ChannelSnapshotCompleted += _ => Interlocked.Increment(ref completed);
        var snapshot = run.Server.ReconcileChannelsAsync().AsTask();
        try
        {
            // Consume startup barrier timers too; only this blocked barrier's timer can end the work.
            while (!snapshot.IsCompleted)
            {
                using var stop = new CancellationTokenSource();
                var next = run.Clock.TimersCreated.ReadAsync(stop.Token).AsTask();
                var first = await Task.WhenAny(snapshot, next).WaitAsync(Run.Bound);
                await stop.CancelAsync();
                if (first == next && next.IsCompletedSuccessfully) run.Clock.Advance((await next).DueTime);
            }
            await snapshot.WaitAsync(Run.Bound);
            run.Server.ChannelTableComplete.Should().BeFalse();
            completed.Should().Be(0);
        }
        finally { gate.Release(); }
    }

    [Fact]
    public async Task Snapshot_ShouldReadIncompleteUntilEveryApplicationEnds_WhenTwoSnapshotsApplyTogether()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusChannels = [] };
        await using var run = await Run.StartAsync(peer);
        using var firstApplied = new ManualResetEventSlim();
        using var secondApplied = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var releaseSecond = new ManualResetEventSlim();
        var completed = 0;
        run.Server.ChannelSnapshotCompleted += _ => Interlocked.Increment(ref completed);
        run.Server.Channels.ChannelAdded += c =>
        {
            run.Server.ChannelTableComplete.Should().BeFalse("the snapshot already changed the table");
            var first = c.UniqueId == "first";
            (first ? firstApplied : secondApplied).Set();
            (first ? releaseFirst : releaseSecond).Wait(Run.Bound).Should().BeTrue();
        };
        peer.StatusChannels = [new StatusChannel("first", "PJSIP/first", LinkedId: "call")];
        var one = Task.Run(async () => await run.Server.ReconcileChannelsAsync());
        Task? two = null;
        try
        {
            firstApplied.Wait(Run.Bound).Should().BeTrue();
            peer.StatusChannels = [new StatusChannel("second", "PJSIP/second", LinkedId: "call")];
            two = Task.Run(async () => await run.Server.ReconcileChannelsAsync());
            secondApplied.Wait(Run.Bound).Should().BeTrue();
            releaseSecond.Set();
            await two.WaitAsync(Run.Bound);
            run.Server.ChannelTableComplete.Should().BeFalse("the first application still owns its window");
            completed.Should().Be(0);
        }
        finally
        {
            releaseFirst.Set();
            releaseSecond.Set();
            await one.WaitAsync(Run.Bound);
            if (two is not null) await two.WaitAsync(Run.Bound);
        }
        run.Server.ChannelTableComplete.Should().BeTrue();
        completed.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Snapshot_ShouldRaiseNoCompletion_WhenStatusIsRefusedOrCutShort(bool cutShort)
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        var completed = 0;
        run.Server.ChannelSnapshotCompleted += _ => Interlocked.Increment(ref completed);
        if (cutShort) peer.Close = PeerClose.DuringStatus;
        else peer.StatusRefusedFromAsk = 2;
        var outcome = await Record.ExceptionAsync(() => run.Server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound));
        if (cutShort) outcome.Should().BeOfType<AmiNotConnectedException>();
        else
        {
            outcome.Should().BeNull();
            run.Server.ChannelTableComplete.Should().BeFalse();
        }
        completed.Should().Be(0);
    }

    [Fact]
    public async Task Reconnect_ShouldClearCompleteUntilNewSnapshotAndChangeIdentity_WhenTheSessionReturns()
    {
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new BootingAsterisk { BootedAtLogin = true };
        var second = new BootingAsterisk { BootedAtLogin = true, StatusAnsweredAfter = answer.Task };
        await using var run = await Run.ConnectAsync(first, second, autoReconnect: true);
        ChannelSnapshotCompletion initial = default;
        run.Server.ChannelSnapshotCompleted += c => initial = c;
        await run.StartServerAsync();
        var oldIdentity = initial.SessionIdentity;
        oldIdentity.Should().NotBeNull();
        var completed = new ConcurrentQueue<ChannelSnapshotCompletion>();
        run.Server.ChannelSnapshotCompleted += completed.Enqueue;
        run.ReleaseSecondPeer();
        first.CloseSession();
        try
        {
            await second.AskedAtLeast("Status", 1).WaitAsync(Run.Bound);
            run.Server.ChannelTableComplete.Should().BeFalse();
            completed.Should().BeEmpty();
            var signal = new TaskCompletionSource<ChannelSnapshotCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
            run.Server.ChannelSnapshotCompleted += c => signal.TrySetResult(c);
            answer.SetResult();
            var completion = await signal.Task.WaitAsync(Run.Bound);
            completion.Server.Should().BeSameAs(run.Server);
            completion.SessionGeneration.Should().Be(1);
            completion.SessionIdentity.Should().NotBeNull().And.NotBeSameAs(oldIdentity);
            completion.SessionIdentity!.IsCompletedSuccessfully.Should().BeTrue();
            run.Server.ChannelTableComplete.Should().BeTrue();
        }
        finally { answer.TrySetResult(); }
    }

    [Fact]
    public async Task Snapshot_ShouldRejectOldSessionCompletion_WhenReconnectOvertakesItsApplication()
    {
        var first = new BootingAsterisk { BootedAtLogin = true, StatusChannels = [] };
        var second = new BootingAsterisk { BootedAtLogin = true, StatusRefusedFromAsk = 1 };
        await using var run = await Run.StartAsync(first, second, autoReconnect: true);
        using var applying = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var completed = 0;
        run.Server.ChannelSnapshotCompleted += _ => Interlocked.Increment(ref completed);
        run.Server.Channels.ChannelAdded += _ =>
        {
            applying.Set();
            release.Wait(Run.Bound).Should().BeTrue();
        };
        first.StatusChannels = [new StatusChannel("old", "PJSIP/old", LinkedId: "call")];
        var snapshot = Task.Run(async () => await run.Server.ReconcileChannelsAsync());
        try
        {
            applying.Wait(Run.Bound).Should().BeTrue();
            run.ReleaseSecondPeer();
            first.CloseSession();
            await second.AskedAtLeast("Agents", 1).WaitAsync(Run.Bound);
            run.Server.ChannelTableComplete.Should().BeFalse();
            release.Set();
            await snapshot.WaitAsync(Run.Bound);
            run.Server.ChannelTableComplete.Should().BeFalse("the old session cannot verify the returning session");
            completed.Should().Be(0);
        }
        finally
        {
            release.Set();
            await snapshot.WaitAsync(Run.Bound);
        }
    }

    [Fact]
    public async Task Snapshot_ShouldRaiseNone_WhenTheReadIsCancelled()
    {
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusAnsweredAfter = answer.Task };
        await using var run = await Run.ConnectAsync(peer);
        using var stop = new CancellationTokenSource();
        var completed = 0;
        run.Server.ChannelSnapshotCompleted += _ => Interlocked.Increment(ref completed);
        var snapshot = run.Server.ReconcileChannelsAsync(stop.Token).AsTask();
        try
        {
            await peer.AskedAtLeast("Status", 1).WaitAsync(Run.Bound);
            await stop.CancelAsync();
            answer.SetResult();
            var outcome = await Record.ExceptionAsync(() => snapshot.WaitAsync(Run.Bound));
            outcome.Should().BeAssignableTo<OperationCanceledException>();
            completed.Should().Be(0);
            run.Server.ChannelTableComplete.Should().BeFalse();
        }
        finally { answer.TrySetResult(); }
    }

    [Fact]
    public async Task RequestSnapshot_ShouldAskOnlyWhileIncomplete_WhenAdmissionRequestsItTwice()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.ConnectAsync(peer);
        await run.Server.RequestChannelSnapshotAsync();
        run.Server.ChannelTableComplete.Should().BeTrue();
        await run.Server.RequestChannelSnapshotAsync();
        peer.Asked("Status").Should().Be(1);
        peer.Asked("QueueStatus").Should().Be(0);
        peer.Asked("Agents").Should().Be(0);
    }

    [Fact]
    public async Task Pool_ShouldAssignInternalIdOnBothAddPaths_WhenItAcceptsServers()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.ConnectAsync(peer);
        var factory = Substitute.For<IAmiConnectionFactory>();
#pragma warning disable CA2012 // NSubstitute configures the ValueTask-returning factory.
        factory.CreateAndConnectAsync(Arg.Any<AmiConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IAmiConnection>(run.Connection));
#pragma warning restore CA2012
        await using var pool = new VerbaraServerPool(factory, NullLoggerFactory.Instance);
        var created = await pool.AddServerAsync("created", new AmiConnectionOptions { Username = "test", Password = "test" });
        created.PoolId.Should().Be("created");
        run.Server.PoolId.Should().BeNull();
        pool.AddExistingServer("existing", run.Server);
        run.Server.PoolId.Should().Be("existing");
        var rejected = new VerbaraServer(run.Connection, NullLogger<VerbaraServer>.Instance);
        await using (rejected)
        {
            var duplicate = () => pool.AddExistingServer("existing", rejected);
            duplicate.Should().Throw<InvalidOperationException>();
            rejected.PoolId.Should().BeNull();
        }
    }
}
