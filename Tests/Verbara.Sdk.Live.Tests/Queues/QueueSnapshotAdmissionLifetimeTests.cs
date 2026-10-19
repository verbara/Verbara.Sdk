using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami;
using Verbara.Sdk.Live.Tests.Harness;

namespace Verbara.Sdk.Live.Tests.Queues;

/// <summary>The buffered answer has one lifetime: an active load, then its deferred owner, then delivery or cancellation.</summary>
[Collection(LiveCountersGroup.Name)]
public sealed class QueueSnapshotAdmissionLifetimeTests
{
    [Fact]
    public async Task DeferredQueueReports_ShouldSelectTheirOwnCompletionOrdinal_WhenAnEarlierStatusFenceTimedOut()
    {
        await using var rig = await QueueAdmissionRun.PrepareAsync(holdStatus: true);
        var deferredFence = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lists = new System.Collections.Concurrent.ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        rig.Run.Server.Queues.OnCallerJoined("q-omitted", "PJSIP/held-omitted", "5552002", 1);
        rig.Run.Server.Queues.OnMemberAdded("q-omitted", "PJSIP/held-member", "Held member", 0, false, 1);
        var completions = 0;
        rig.Run.Server.Queues.QueueSnapshotCompleted += _ => Interlocked.Increment(ref completions);
        rig.Run.Server.FenceWaitObserved = (list, ordinal) =>
        {
            lists[list] = ordinal;
            if (list == "deferred")
                deferredFence.TrySetResult(ordinal);
        };

        await rig.ExpireStatusThenHoldTheLaterQueueFenceAsync();
        var selected = await deferredFence.Task.WaitAsync(Run.Bound);

        using var scope = new AssertionScope();
        lists["QueueStatus"].Should().BeGreaterThan(lists["Status"],
            "the live join and second held event arrived after StatusComplete but before QueueStatusComplete");
        selected.Should().Be(lists["QueueStatus"],
            "the real deferred owner must wait for the queue reports' own completion ordinal, not DeferredOn's earlier Status ordinal");
        rig.Run.Server.Queues.GetByName("q-omitted")!.EntryCount.Should().Be(1,
            "a queue fence that ran out removes no caller or omitted queue");
        rig.Run.Server.Queues.GetQueuesForMember("PJSIP/held-member").Should().Equal("q-omitted");
        completions.Should().Be(0, "timeout admission does not announce snapshot completion or remove omitted members");
    }

    [Fact]
    public async Task StartAsync_ShouldDiscardTheAnswer_WhenTheCallerCancelsAtItsQueueFence()
    {
        using var cancel = new CancellationTokenSource();
        await using var rig = await QueueAdmissionRun.PrepareAsync(reconnect: false, ct: cancel.Token);
        await rig.HoldQueueAsync(liveJoin: false);

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.ActiveLoad.WaitAsync(Run.Bound));
        var tail = rig.TailAsync("after-cancel");
        await rig.SendTailAsync("after-cancel");
        rig.Release();
        await tail;
        await rig.Run.Server.ScheduledReload.WaitAsync(Run.Bound);

        AssertNothingApplied(rig);
    }

    [Fact]
    public async Task StartAsync_ShouldKeepTheTransferredReports_WhenTheCallerCancelsAfterTheTimeoutLoadReturned()
    {
        using var cancel = new CancellationTokenSource();
        await using var rig = await QueueAdmissionRun.PrepareAsync(reconnect: false, ct: cancel.Token);
        var timer = await rig.HoldQueueAsync();
        await rig.ExpireAsync(timer);
        await cancel.CancelAsync();

        await rig.FinishAsync();
        await rig.Run.ServerLog.Logged(QueueAdmissionRun.StateLoaded, 2).WaitAsync(Run.Bound);
        await rig.Run.Server.ScheduledReload.WaitAsync(Run.Bound);

        rig.Visit().Should().BeEquivalentTo(QueueAdmissionRun.OneVisit,
            "after the load returned, transferred work belongs to the session/server lifetime, not the old caller token");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeferredQueueReports_ShouldCancelWithoutLateNotifications_WhenTheirSessionOrServerEnds(bool disposeServer)
    {
        var observed = await ObserveEndedLifetimeWhileDispatchIsHeldAsync(disposeServer, nested: false, expireSecondFence: false);

        using var scope = new AssertionScope();
        observed.MeasurementFailure.Should().BeNull("the session/disposal scenario must reach its held-dispatch observation");
        observed.DrainFailure.Should().BeNull("the released dispatch and its owner must drain during cleanup");
        observed.CleanupFailure.Should().BeNull("fixture failures must propagate after the gates have been released");
        observed.Held.Should().NotBeNull();
        if (observed.Held is not { } held)
            return;

        held.OwnerWaitFailure.Should().BeNull("session end or disposal must stop the owner while dispatch stays held");
        held.OwnerCompleted.Should().BeTrue("the ended lifetime cannot retain an observed owner until handler drainage");
        held.RetainedWindows.Should().Be(0, "session/disposal cleanup must release its queue window before handler drainage");
        held.ReportsBeforeEnd.Should().Be(0, "the first timeout batch has not delivered while dispatch stays held");
        held.Reports.Should().Be(held.ReportsBeforeEnd, "the ended lifetime cannot deliver its pending snapshot reports");
        held.QueuedBeforeEnd.Should().Be(0);
        held.Queued.Should().Be(held.QueuedBeforeEnd, "discarding a report opens no extra Sessions visit");
        held.HeldEntries.Should().Be(1, "ending deferred work does not roll back its previously admitted queue entry");
        held.Joined.Should().Be(1);
        held.Left.Should().Be(0);
        held.IsLoadInFlight.Should().BeFalse("the ended nested load must terminate even during its active fence wait");
        held.GateTimedOut.Should().BeFalse("the gate remained held until this test's finally released it");
        held.StatusRequests.Should().Be(1, "one deferred full reload is allowed, never a third load");
        held.QueueStatusRequests.Should().Be(1);
        observed.FinalReports.Should().Be(held.ReportsBeforeEnd, "releasing dispatch after session end delivers no discarded report");
        observed.FinalQueued.Should().Be(held.QueuedBeforeEnd, "final drainage opens no extra visit");
        observed.FinalRetainedWindows.Should().Be(0);
        observed.FinalGateTimedOut.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DeferredReload_ShouldCancelItsActiveOrTransferredQueueBatchWhileDispatchIsHeld_WhenItsSessionOrServerEnds(
        bool disposeServer, bool expireSecondFence)
    {
        var observed = await ObserveEndedLifetimeWhileDispatchIsHeldAsync(disposeServer, nested: true, expireSecondFence);

        using var scope = new AssertionScope();
        observed.MeasurementFailure.Should().BeNull("the session/disposal scenario must reach its held-dispatch observation");
        observed.DrainFailure.Should().BeNull("the released dispatch and its owner must drain during cleanup");
        observed.CleanupFailure.Should().BeNull("fixture failures must propagate after the gates have been released");
        observed.Held.Should().NotBeNull();
        if (observed.Held is not { } held)
            return;

        held.OwnerWaitFailure.Should().BeNull("session end or disposal must stop the owner while dispatch stays held");
        held.OwnerCompleted.Should().BeTrue("the ended lifetime cannot retain an observed owner until handler drainage");
        held.RetainedWindows.Should().Be(0, "session/disposal cleanup must release its queue window before handler drainage");
        held.ReportsBeforeEnd.Should().Be(1, "only the first batch of a nested load has already delivered");
        held.Reports.Should().Be(held.ReportsBeforeEnd, "the ended lifetime cannot deliver its pending snapshot reports");
        held.QueuedBeforeEnd.Should().Be(1);
        held.Queued.Should().Be(held.QueuedBeforeEnd, "discarding a report opens no extra Sessions visit");
        held.HeldEntries.Should().Be(1, "ending deferred work does not roll back its previously admitted queue entry");
        held.Joined.Should().Be(1);
        held.Left.Should().Be(0);
        held.IsLoadInFlight.Should().BeFalse("the ended nested load must terminate even during its active fence wait");
        held.GateTimedOut.Should().BeFalse("the gate remained held until this test's finally released it");
        held.StatusRequests.Should().Be(2, "one deferred full reload is allowed, never a third load");
        held.QueueStatusRequests.Should().Be(2);
        observed.FinalReports.Should().Be(held.ReportsBeforeEnd, "releasing dispatch after session end delivers no discarded report");
        observed.FinalQueued.Should().Be(held.QueuedBeforeEnd, "final drainage opens no extra visit");
        observed.FinalRetainedWindows.Should().Be(0);
        observed.FinalGateTimedOut.Should().BeFalse();
    }

    [Fact]
    public async Task DeferredReload_ShouldDeliverItsOwnReports_WhenItsQueueFenceAlsoTimesOut()
    {
        await using var rig = await QueueAdmissionRun.PrepareAsync();
        var firstTimer = await rig.HoldQueueAsync();
        await rig.ExpireAsync(firstTimer);
        var secondAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Peer.AnsweredAfter = new Dictionary<string, Task> { ["QueueStatus"] = secondAnswer.Task };
        rig.Release();
        var secondTimer = await rig.HoldSecondQueueAsync(secondAnswer);
        rig.BarrierClock.Advance(secondTimer.DueTime);
        await rig.Run.ServerLog.Logged(QueueAdmissionRun.StateLoaded, 3).WaitAsync(Run.Bound);
        var reportsBeforeRelease = rig.Reports.Count(e => e.FromSnapshot);

        rig.ReleaseSecond();
        await rig.Run.Server.ScheduledReload.WaitAsync(Run.Bound);
        rig.Reports.Count(e => e.FromSnapshot).Should().Be(2,
            "the observed owner finishes only after both timeout batches delivered their eligible reports");
        var tail = rig.TailAsync("after-second-timeout");
        await rig.SendTailAsync("after-second-timeout");
        await tail;

        using var scope = new AssertionScope();
        reportsBeforeRelease.Should().Be(1,
            "the first transferred answer delivered after its fence; the second still waits for its own fence");
        rig.Visit().Should().BeEquivalentTo(QueueAdmissionRun.OneVisit);
        rig.Peer.Asked("Status").Should().Be(2,
            "a scheduled reload that also timed out owns its reports without scheduling a third load");
        rig.Run.Server.ScheduledReload.IsCompleted.Should().BeTrue("the deferred owner terminates after delivery");
        rig.Run.Server.Queues.ActiveSnapshotWindowCount.Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_ShouldApplyNoPartialRows_WhenTheQueueAnswerEndsInARefusal()
    {
        var rig = await QueueAdmissionRun.PrepareAsync(reconnect: false);
        DiscardedAnswer? answer = null;
        Exception? measurementFailure = null;
        Exception? cleanupFailure;
        try
        {
            rig.Peer.QueueStatusRefusalAfterRows = BootingAsterisk.PermissionDenied;
            rig.AnswerQueue();
            await rig.ActiveLoad.WaitAsync(Run.Bound);
            answer = new DiscardedAnswer(rig.Reports.Count, rig.Queued.Count,
                rig.Run.Server.Queues.GetByName(QueueAdmissionRun.Queue) is not null,
                rig.Counters.Joined, rig.Run.Server.IsLoadInFlight,
                rig.Run.Server.Queues.ActiveSnapshotWindowCount, rig.GateTimedOut);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            measurementFailure = ex;
        }
        finally
        {
            cleanupFailure = await Record.ExceptionAsync(() => rig.DisposeAsync().AsTask());
        }

        using var scope = new AssertionScope();
        measurementFailure.Should().BeNull("the rejected answer must reach its pre-disposal observation");
        cleanupFailure.Should().BeNull("fixture failures must propagate independently of the rejected answer");
        answer.Should().NotBeNull();
        if (answer is null)
            return;

        answer.Reports.Should().Be(0, "an answer that was not accepted to its end has no admission notifications");
        answer.Queued.Should().Be(0);
        answer.QueueExists.Should().BeFalse("not even QueueParams or QueueMember from a discarded answer is applied");
        answer.Joined.Should().Be(0);
        answer.IsLoadInFlight.Should().BeFalse();
        answer.RetainedWindows.Should().Be(0);
        answer.GateTimedOut.Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_ShouldApplyNoPartialRows_WhenTheSessionEndsBeforeQueueStatusComplete()
    {
        await using var rig = await QueueAdmissionRun.PrepareAsync(reconnect: false);
        rig.Peer.BeforeQueueStatusComplete = () =>
        {
            rig.Peer.CloseSession();
            return Task.CompletedTask;
        };
        rig.AnswerQueue();

        var failure = await Record.ExceptionAsync(() => rig.ActiveLoad.WaitAsync(Run.Bound));

        using var scope = new AssertionScope();
        failure.Should().BeAssignableTo<AmiNotConnectedException>();
        AssertNothingApplied(rig);
    }

    [Fact]
    public async Task StartAsync_ShouldReleaseTheUntransferredReports_WhenAgentsCancelsAfterQueueTimeout()
    {
        using var cancel = new CancellationTokenSource();
        await using var rig = await QueueAdmissionRun.PrepareAsync(reconnect: false, ct: cancel.Token);
        var timer = await rig.HoldQueueAsync(liveJoin: false);
        var agentsAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Peer.AnsweredAfter = new Dictionary<string, Task> { ["Agents"] = agentsAnswer.Task };
        try
        {
            rig.BarrierClock.Advance(timer.DueTime);
            await rig.Peer.AskedAtLeast("Agents", 1).WaitAsync(Run.Bound);
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.ActiveLoad.WaitAsync(Run.Bound));

            using var scope = new AssertionScope();
            rig.Reports.Should().BeEmpty("the load never transferred its pending reports to an owner");
            rig.Run.Server.Queues.ActiveSnapshotWindowCount.Should().Be(0);
            rig.Run.Server.Queues.GetByName(QueueAdmissionRun.Queue)!.EntryCount.Should().Be(1,
                "cancellation releases pending work without rolling back the already admitted table half");
            rig.Run.Server.ScheduledReload.IsCompleted.Should().BeTrue();
            rig.Peer.Asked("Status").Should().Be(1, "an unsuccessful active load schedules no reload");
        }
        finally
        {
            agentsAnswer.TrySetResult();
        }
    }

    private static async Task<EndedLifetime> ObserveEndedLifetimeWhileDispatchIsHeldAsync(bool disposeServer, bool nested,
        bool expireSecondFence)
    {
        var rig = await QueueAdmissionRun.PrepareAsync(reconnect: false);
        Task owner = Task.CompletedTask;
        HeldLifetime? held = null;
        Exception? measurementFailure = null;
        Exception? drainFailure;
        Exception? cleanupFailure;
        try
        {
            var firstTimer = await rig.HoldQueueAsync(liveJoin: false);
            await rig.ExpireAsync(firstTimer);
            owner = rig.Run.Server.ScheduledReload;
            if (nested)
            {
                var secondAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                rig.Peer.AnsweredAfter = new Dictionary<string, Task> { ["QueueStatus"] = secondAnswer.Task };
                rig.Release();
                var secondTimer = await rig.HoldSecondQueueAsync(secondAnswer);
                if (expireSecondFence)
                {
                    rig.BarrierClock.Advance(secondTimer.DueTime);
                    await rig.Run.ServerLog.Logged(QueueAdmissionRun.StateLoaded, 2).WaitAsync(Run.Bound);
                }
                // Otherwise this load is still in its active queue-fence wait. Do not advance its manual clock:
                // ending the session must stop that wait without reaching either the fence or its timeout.
            }

            var reportsBeforeEnd = rig.Reports.Count;
            var queuedBeforeEnd = rig.Queued.Count;
            if (disposeServer)
                await rig.Run.Server.DisposeAsync().AsTask().WaitAsync(Run.Bound);
            else
            {
                var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                rig.Run.Server.ConnectionLost += _ => lost.TrySetResult();
                rig.Peer.CloseSession();
                await lost.Task.WaitAsync(Run.Bound);
            }

            // This is only a liveness guard, shorter than DispatchGate.HoldBound. Capture every verdict before
            // releasing the still-blocked handler: cleanup must never turn a retained owner/window into a pass.
            var ownerWaitFailure = await Record.ExceptionAsync(() => owner.WaitAsync(Run.Bound));
            held = new HeldLifetime(ownerWaitFailure, owner.IsCompleted,
                rig.Run.Server.Queues.ActiveSnapshotWindowCount, reportsBeforeEnd, rig.Reports.Count,
                queuedBeforeEnd, rig.Queued.Count, rig.Run.Server.Queues.GetByName(QueueAdmissionRun.Queue)!.EntryCount,
                rig.Counters.Joined, rig.Counters.Left, rig.Run.Server.IsLoadInFlight, rig.GateTimedOut,
                rig.Peer.Asked("Status"), rig.Peer.Asked("QueueStatus"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            measurementFailure = ex;
        }
        finally
        {
            // Always release, drain and dispose before asserting the captured result. Each cleanup failure is kept
            // separately, so cleanup cannot hide either the pre-release regression or a peer/fixture failure.
            rig.Release();
            rig.ReleaseSecond();
            drainFailure = await Record.ExceptionAsync(() => owner.WaitAsync(Run.Bound));
            cleanupFailure = await Record.ExceptionAsync(() => rig.DisposeAsync().AsTask());
        }

        return new EndedLifetime(held, measurementFailure, drainFailure, cleanupFailure,
            rig.Reports.Count, rig.Queued.Count, rig.Run.Server.Queues.ActiveSnapshotWindowCount, rig.GateTimedOut);
    }

    private sealed record EndedLifetime(HeldLifetime? Held, Exception? MeasurementFailure, Exception? DrainFailure,
        Exception? CleanupFailure, int FinalReports, int FinalQueued, int FinalRetainedWindows, bool FinalGateTimedOut);

    private sealed record DiscardedAnswer(int Reports, int Queued, bool QueueExists, long Joined,
        bool IsLoadInFlight, int RetainedWindows, bool GateTimedOut);

    private sealed record HeldLifetime(Exception? OwnerWaitFailure, bool OwnerCompleted, int RetainedWindows,
        int ReportsBeforeEnd, int Reports, int QueuedBeforeEnd, int Queued, int HeldEntries, long Joined, long Left,
        bool IsLoadInFlight, bool GateTimedOut, int StatusRequests, int QueueStatusRequests);

    private static void AssertNothingApplied(QueueAdmissionRun rig)
    {
        using var scope = new AssertionScope();
        rig.Reports.Should().BeEmpty("an answer that was not accepted to its end has no admission notifications");
        rig.Queued.Should().BeEmpty();
        rig.Run.Server.Queues.GetByName(QueueAdmissionRun.Queue).Should().BeNull(
            "not even QueueParams or QueueMember from a discarded answer is applied");
        rig.Counters.Joined.Should().Be(0);
        rig.Run.Server.IsLoadInFlight.Should().BeFalse();
        rig.Run.Server.Queues.ActiveSnapshotWindowCount.Should().Be(0);
        rig.GateTimedOut.Should().BeFalse();
    }
}
