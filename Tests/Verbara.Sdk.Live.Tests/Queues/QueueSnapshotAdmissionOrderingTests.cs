using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Live.Tests.Harness;

namespace Verbara.Sdk.Live.Tests.Queues;

/// <summary>
/// A queue answer received while a live join waits in the AMI pump must reach Sessions after that join. Unlike a
/// replacement-counter pin, these tests observe CallQueued and the real queue session tracker as well as Live.
/// </summary>
[Collection(LiveCountersGroup.Name)]
public sealed class QueueSnapshotAdmissionOrderingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reload_ShouldOpenExactlyOneSessionVisit_WhenALiveJoinWaitsAtTheQueueSnapshotFence(bool expire)
    {
        await using var rig = await QueueAdmissionRun.PrepareAsync();
        var timer = await rig.HoldQueueAsync();
        rig.Run.Clock.Advance(TimeSpan.FromSeconds(8));
        rig.Peer.QueueEntries = [new QueueCaller(QueueAdmissionRun.Caller, QueueAdmissionRun.Uid, Position: 7, Wait: 50)];
        if (expire)
            await rig.ExpireAsync(timer);
        var notificationsWhileHeld = rig.Reports.Count;
        var queuedWhileHeld = rig.Queued.Count;
        var windowsWhileHeld = rig.Run.Server.Queues.ActiveSnapshotWindowCount;
        var heldBeforeRelease = rig.Run.Server.Queues.GetByName(QueueAdmissionRun.Queue)?.EntryCount ?? 0;

        await rig.FinishAsync();
        if (expire)
        {
            await rig.Run.ServerLog.Logged(QueueAdmissionRun.StateLoaded, 3).WaitAsync(Run.Bound);
            await rig.Run.Server.ScheduledReload.WaitAsync(Run.Bound);
        }

        using var scope = new AssertionScope();
        rig.Visit().Should().BeEquivalentTo(QueueAdmissionRun.OneVisit,
            "the channel already existed; its live join was queued only after the earlier list barriers. A queue "
            + "snapshot must not open a visit before that join: one CallQueued, one offer and no false abandon");
        new { Notifications = notificationsWhileHeld, CallQueued = queuedWhileHeld }.Should().BeEquivalentTo(
            new { Notifications = 0, CallQueued = 0 }, "queue admission notifications wait for their own dispatch fence");
        windowsWhileHeld.Should().Be(1,
            "the eligible pending snapshot keeps its read window through the fence, including after timeout transfers ownership");
        heldBeforeRelease.Should().Be(expire ? 1 : 0,
            "timeout admits the table silently; a reached load applies its answer after dispatch catches up");
        rig.Run.Server.Queues.GetByName(QueueAdmissionRun.Queue)!.Entries[QueueAdmissionRun.Caller].Position.Should().Be(7,
            "the live join after the request supplied position 7; the older snapshot supplied position 1");
        rig.Peer.Asked("Status").Should().Be(expire ? 2 : 1,
            "only an expired load schedules exactly one additional full reload");
    }

    [Fact]
    public async Task Reload_ShouldOpenOneVisit_WhenOnlyTheLiveJoinReportsTheCaller()
    {
        await using var rig = await QueueAdmissionRun.PrepareAsync();
        rig.Peer.QueueEntries = [];
        await rig.HoldQueueAsync();
        await rig.FinishAsync();

        rig.Visit().Should().BeEquivalentTo(QueueAdmissionRun.OneVisit,
            "with no row for this caller the live join alone opens one visit");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reload_ShouldKeepTheRowsReadInstantForTheEntryAndVisit_WhenOnlyTheSnapshotReportsTheCaller(bool expire)
    {
        await using var rig = await QueueAdmissionRun.PrepareAsync();
        var timer = await rig.HoldQueueAsync(liveJoin: false);
        rig.Run.Clock.Advance(TimeSpan.FromSeconds(8));
        // A later fresh snapshot must report the same visit eight seconds older, rather than manufacture a rejoin.
        rig.Peer.QueueEntries = [new QueueCaller(QueueAdmissionRun.Caller, QueueAdmissionRun.Uid, Position: 1, Wait: 50)];
        if (expire)
            await rig.ExpireAsync(timer);
        await rig.FinishAsync(liveJoin: false);
        if (expire)
        {
            await rig.Run.ServerLog.Logged(QueueAdmissionRun.StateLoaded, 3).WaitAsync(Run.Bound);
            await rig.Run.Server.ScheduledReload.WaitAsync(Run.Bound);
        }

        var entry = rig.Run.Server.Queues.GetByName(QueueAdmissionRun.Queue)!.Entries[QueueAdmissionRun.Caller];
        using var scope = new AssertionScope();
        rig.Visit().Should().BeEquivalentTo(QueueAdmissionRun.OneVisit);
        entry.JoinedAt.Should().Be(rig.RowReadAt,
            "JoinedAt dates the row read on the server's clock; the eight-second delivery delay does not move it");
        rig.Queued.Should().ContainSingle().Which.Timestamp.Should().Be(rig.RowReadAt - TimeSpan.FromSeconds(42),
            "Wait was measured when the row was read, not eight seconds later when the notification was delivered");
        rig.Manager.GetByLinkedId(QueueAdmissionRun.Uid)!.QueueVisitStartedAt.Should().Be(
            rig.RowReadAt - TimeSpan.FromSeconds(42));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reload_ShouldPreserveTheLivePosition_WhenARejoinAfterTheRequestHasANewerPosition(bool expire)
    {
        await using var rig = await QueueAdmissionRun.PrepareAsync();
        // The existing visit is live. Its next live join is a genuine new visit, not snapshot deduplication.
        // Seed through the public table with the existing channel; the rejoin itself travels over AMI.
        rig.Run.Server.Queues.OnCallerJoined(QueueAdmissionRun.Queue, QueueAdmissionRun.Caller, "5552101", 4);
        var timer = await rig.HoldQueueAsync(livePosition: 7);
        rig.Peer.QueueEntries = [new QueueCaller(QueueAdmissionRun.Caller, QueueAdmissionRun.Uid, Position: 7, Wait: 42)];
        if (expire)
            await rig.ExpireAsync(timer);
        await rig.FinishAsync();
        if (expire)
        {
            await rig.Run.ServerLog.Logged(QueueAdmissionRun.StateLoaded, 3).WaitAsync(Run.Bound);
            await rig.Run.Server.ScheduledReload.WaitAsync(Run.Bound);
        }

        rig.Run.Server.Queues.GetByName(QueueAdmissionRun.Queue)!.Entries[QueueAdmissionRun.Caller].Position.Should().Be(7,
            "the live rejoin after the snapshot request supersedes its older position");
        rig.Queued.Should().HaveCount(2, "the two actual live joins are two visits; the snapshot adds no third visit");
        rig.Tracker.GetByQueueName(QueueAdmissionRun.Queue)!.CallsAbandoned.Should().Be(1,
            "only the first actual visit was superseded by a live rejoin");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reload_ShouldRejectAWaitBeforeTheRowsEpoch_WhenDeliveryAdvancesBeyondThatWait(bool expire)
    {
        await using var rig = await QueueAdmissionRun.PrepareAsync(reconnect: false);
        var rowClock = new FakeTimeProvider(DateTimeOffset.MinValue + TimeSpan.FromSeconds(10));
        rig.Run.Server.TimeProvider = rowClock;
        rig.Peer.QueueEntries = [new QueueCaller(QueueAdmissionRun.Caller, QueueAdmissionRun.Uid, 1, 15)];
        var timer = await rig.HoldQueueAsync(liveJoin: false);
        rowClock.Advance(TimeSpan.FromSeconds(30));
        rig.Run.Clock.Advance(TimeSpan.FromSeconds(8));
        if (expire)
            await rig.ExpireAsync(timer);
        await rig.FinishAsync(liveJoin: false);
        if (expire)
            await rig.Run.Server.ScheduledReload.WaitAsync(Run.Bound);

        rig.Queued.Should().ContainSingle().Which.Timestamp.Should().Be(rig.Run.Clock.GetUtcNow(),
            "Wait=15 predates the captured epoch+10, even though later delivery would make it arithmetically valid");
        rig.Run.Server.Queues.ActiveSnapshotWindowCount.Should().Be(0);
    }

    [Fact]
    public async Task Reload_ShouldKeepTheAlreadyDispatchedPosition_WhenTimeoutReplayWaitsBeforeItsCallerRow()
    {
        await using var rig = await QueueAdmissionRun.PrepareAsync();
        var timer = await rig.HoldQueueAsync(livePosition: 7);
        rig.Peer.QueueEntries = [new QueueCaller(QueueAdmissionRun.Caller, QueueAdmissionRun.Uid, 7, 42)];
        var agents = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Peer.AnsweredAfter = new Dictionary<string, Task> { ["Agents"] = agents.Task };
        var tail = rig.TailAsync("position-delivered-before-replay");
        await rig.SendTailAsync("position-delivered-before-replay");
        rig.Run.Server.Queues.SnapshotMemberApplying = () =>
        {
            rig.Run.Server.Queues.SnapshotMemberApplying = null;
            rig.Release();
            tail.WaitAsync(Run.Bound).GetAwaiter().GetResult();
        };
        var positionAfterReplay = -1;
        try
        {
            rig.BarrierClock.Advance(timer.DueTime);
            await rig.Peer.AskedAtLeast("Agents", 1).WaitAsync(Run.Bound);
            positionAfterReplay = rig.Run.Server.Queues.GetByName(QueueAdmissionRun.Queue)!
                .Entries[QueueAdmissionRun.Caller].Position;
        }
        finally
        {
            rig.Run.Server.Queues.SnapshotMemberApplying = null;
            agents.TrySetResult();
        }
        await rig.ActiveLoad.WaitAsync(Run.Bound);
        await rig.Run.Server.ScheduledReload.WaitAsync(Run.Bound);
        positionAfterReplay.Should().Be(7,
            "the live join dispatched during timeout replay and is newer than the buffered caller row");
    }

}
