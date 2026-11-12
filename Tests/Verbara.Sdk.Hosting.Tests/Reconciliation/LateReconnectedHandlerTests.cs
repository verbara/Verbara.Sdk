using System.Reflection;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

/// <summary>
/// An application handler on the connection's <c>Reconnected</c> that runs after the live server's keeps the reconnect
/// pending until it returns. A reload whose covering channel read completes inside that window must still decide the
/// calls that wait on it, with no sweep tick to do it: the end of the pending reconnect decides them.
/// </summary>
/// <remarks>
/// Over <see cref="LossRig"/> with automatic reconnection: a handler subscribed after the server's blocks the connection's
/// notification queue until the server's reload has returned, so the reload's channel read completes while the reconnect
/// is still pending, by construction rather than by timing. No wait in these tests ends on the clock: each ends on the
/// decision it asserts, and its bound is reached only when that decision never comes.
/// </remarks>
[Collection(SweepCounterGroup.Name)]
public sealed class LateReconnectedHandlerTests
{
    [Fact]
    public async Task Reload_ShouldVerifyTheWaitingCall_WhenAReconnectedHandlerAfterTheServersOutlivesTheReload()
    {
        await using var rig = await LossRig.StartAsync(attach: false, autoReconnect: true);
        var run = await ReconnectWithALateHandlerAsync(rig, endDuringTheOutage: false);

        using (new AssertionScope())
        {
            run.Unverified.Should().BeTrue("premise: the walk admits while the reconnect is pending");
            run.Pending.Should().BeTrue("premise: the reload returned while the late handler kept the reconnect pending");
            rig.Server.ChannelTableCovered.Should().BeTrue("premise: the reload's read covers the table once nothing is pending");
            run.Decided.Should().BeTrue("the end of the pending reconnect decides the call that waits on the reload's read");
            run.Call.Unverified.Should().BeFalse("the call waits on the reload's covering read, X and Y present. " + rig.Describe(run.Call));
            rig.EndingsOf(run.Call).Should().Be(0);
        }
    }

    [Fact]
    public async Task Reload_ShouldEndTheWaitingCall_WhenItsLegsHungUpInTheOutageAndAReconnectedHandlerAfterTheServersOutlivesTheReload()
    {
        await using var rig = await LossRig.StartAsync(attach: false, autoReconnect: true);
        var run = await ReconnectWithALateHandlerAsync(rig, endDuringTheOutage: true);

        using (new AssertionScope())
        {
            run.Unverified.Should().BeTrue("premise: the walk admits while the reconnect is pending");
            run.Pending.Should().BeTrue("premise: the reload returned while the late handler kept the reconnect pending");
            rig.Server.Channels.ActiveChannels.Should().BeEmpty("premise: the reload's Status listed neither leg");
            run.Decided.Should().BeTrue("the end of the pending reconnect decides the call that waits on the reload's read");
            rig.EndingsOf(run.Call).Should().Be(1, "both legs left in the outage and the reload's read proves it. " + rig.Describe(run.Call));
        }
    }

    /// <summary>
    /// The call the end of the pending reconnect decided is decided again — by a sweep tick and by the host's own channel
    /// read — and nothing changes: one ending at most, and no second notification.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reload_ShouldNotifyNothingMore_WhenTheSweepAndTheHostsReadDecideTheSettledCallAgain(bool endDuringTheOutage)
    {
        await using var rig = await LossRig.StartAsync(attach: false, autoReconnect: true);
        var notified = new NotificationCounter();
        using var subscription = rig.Manager.Events.Subscribe(notified);
        var run = await ReconnectWithALateHandlerAsync(rig, endDuringTheOutage);
        var atTheDecision = notified.Of(run.Call.SessionId);
        var endingsAtTheDecision = rig.EndingsOf(run.Call);

        await rig.TickAsync(pool: false);
        await rig.ReconcileAsync();

        using (new AssertionScope())
        {
            run.Decided.Should().BeTrue("premise: the end of the pending reconnect decided the call");
            endingsAtTheDecision.Should().Be(endDuringTheOutage ? 1 : 0, "premise: " + rig.Describe(run.Call));
            rig.EndingsOf(run.Call).Should().Be(endingsAtTheDecision, "a call is ended at most once");
            notified.Of(run.Call.SessionId).Should().Be(atTheDecision, "a later decision of a decided call notifies nothing");
            if (!endDuringTheOutage)
                run.Call.Unverified.Should().BeFalse("premise: the settle verified the call. " + rig.Describe(run.Call));
        }
    }

    /// <summary>
    /// The caller's own route: a connection without automatic reconnection whose lost session the caller's
    /// <c>ConnectAsync</c> brought back, and a <c>CallerReconnected</c> handler subscribed after the server's that outlives
    /// the reload. The end of that pending reconnect decides the waiting call as on the loop's route.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reload_ShouldDecideTheWaitingCall_WhenACallerReconnectedHandlerAfterTheServersOutlivesTheReload(bool endDuringTheOutage)
    {
        await using var rig = await LossRig.StartAsync(attach: false, autoReconnect: false);
        var run = await CallerReconnectWithALateHandlerAsync(rig, endDuringTheOutage);

        using (new AssertionScope())
        {
            run.Unverified.Should().BeTrue("premise: the walk admits while the caller's reconnect is pending");
            run.Pending.Should().BeTrue("premise: the reload returned while the late handler kept the caller's reconnect pending");
            run.Decided.Should().BeTrue("the end of the caller's pending reconnect decides the call that waits on the reload's read");
            if (endDuringTheOutage)
            {
                rig.Server.Channels.ActiveChannels.Should().BeEmpty("premise: the reload's Status listed neither leg");
                rig.EndingsOf(run.Call).Should().Be(1, "both legs left in the outage and the reload's read proves it. " + rig.Describe(run.Call));
                run.Call.Metadata.GetValueOrDefault("cause").Should().Be("reload", rig.Describe(run.Call));
            }
            else
            {
                run.Call.Unverified.Should().BeFalse("the call waits on the reload's covering read, X and Y present. " + rig.Describe(run.Call));
                rig.EndingsOf(run.Call).Should().Be(0);
            }
        }
    }

    /// <summary>
    /// Two live servers on one connection, each attached under its own server id and each holding a waiting call. The end
    /// of the pending reconnect reaches the first server's notice first: right after the session engine's handler of that
    /// notice has returned, the first server's call is decided and the second's is exactly as it was; the second server's
    /// own notice then decides its call. No sweep tick and no channel read after the two reloads take part.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Settle_ShouldDecideOnlyItsOwnServersWaitingCall_WhenTwoAttachedServersShareThePendingReconnect(bool endDuringTheOutage)
    {
        const string OtherId = "loss-other";
        await using var rig = await LossRig.StartAsync(attach: false, autoReconnect: true);
        await using var other = new VerbaraServer(rig.Connection, NullLogger<VerbaraServer>.Instance) { TimeProvider = rig.LiveClock };
        await other.StartAsync().WaitAsync(LossRig.Bound);
        await rig.NewChannelAsync("X", "L");
        await rig.NewChannelAsync("Y", "L");
        rig.Attach();
        rig.Manager.AttachToServer(other, OtherId);

        // Subscribed after both servers' handlers: it returns only once both reloads have returned.
        var pendingAtReloads = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Late()
        {
#pragma warning disable VSTHRD002 // The test's point: a handler that blocks the notification queue until both reloads returned
            var finished = rig.Server.ReconnectReload.Wait(LossRig.Bound) && other.ReconnectReload.Wait(LossRig.Bound);
#pragma warning restore VSTHRD002
            pendingAtReloads.TrySetResult(finished && rig.Server.IsReconnectPending);
        }
        rig.Connection.Reconnected += Late;

        await rig.HoldAsync();
        await rig.BufferAsync("PeerStatus", [new("Peer", "FILL/lost"), new("PeerStatus", "Reachable")], "FILL/lost");
        await rig.FenceAsync();
        rig.CloseSession();
        if (endDuringTheOutage)
        {
            rig.ForgetChannel("X");
            rig.ForgetChannel("Y");
        }
        await rig.LoggedInAsync(2);
        await rig.AdmitAsync();
        await rig.RunAsync(() => rig.Manager.AdmitExistingCalls(OtherId).AsTask());
        var own = rig.Manager.GetByLinkedId(LossRig.ServerId, "L")!;
        var others = rig.Manager.GetByLinkedId(OtherId, "L")!;
        var admitted = (Own: Read(own), Other: Read(others));

        // The session engine subscribed to the first server's notice when it attached; this runs right after it, on the
        // same delivery and before the second server hears the end of the reconnect.
        var atFirstSettle = new TaskCompletionSource<(Snapshot Own, Snapshot Other)>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFirstSettled(ChannelSnapshotCompletion _) => atFirstSettle.TrySetResult((Read(own), Read(others)));
        rig.Server.ChannelTableSettled += OnFirstSettled;

        var otherDecided = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStep(HandlerStep step, string serverId, string id)
        {
            if (step == HandlerStep.DecisionOracleCaptured && id == others.SessionId && !other.IsReconnectPending)
                otherDecided.TrySetResult();
        }
        rig.Manager.StepHook += OnStep;
        try
        {
            rig.Release();
            var pending = await pendingAtReloads.Task.WaitAsync(LossRig.Bound);
            var statusAtTheReloads = rig.StatusAsked;
            var spansAtTheReloads = rig.SpanMark;
            var first = await atFirstSettle.Task.WaitAsync(LossRig.Bound);
            var decidedOther = await EndsWithinBoundAsync(otherDecided.Task);
            using (others.SyncRoot.EnterScope())
            {
                // The decision the hook saw has returned.
            }

            using (new AssertionScope())
            {
                admitted.Own.Unverified.Should().BeTrue("premise: the first server's walk admits while the reconnect is pending");
                admitted.Other.Unverified.Should().BeTrue("premise: the second server's walk admits while the reconnect is pending");
                pending.Should().BeTrue("premise: both reloads returned while the late handler kept the reconnect pending");
                if (endDuringTheOutage)
                    first.Own.Ended.Should().BeTrue("the first server's notice decides its own call, whose legs left. " + rig.Describe(own));
                else
                    first.Own.Unverified.Should().BeFalse("the first server's notice decides its own call, X and Y present. " + rig.Describe(own));
                first.Other.Should().Be(admitted.Other,
                    "the first server's notice leaves the call attached under the other server id as it was. " + rig.Describe(others));
                decidedOther.Should().BeTrue("the second server's own notice decides its call");
                if (endDuringTheOutage)
                {
                    rig.EndingsOf(own).Should().Be(1, rig.Describe(own));
                    rig.EndingsOf(others).Should().Be(1, rig.Describe(others));
                }
                else
                {
                    others.Unverified.Should().BeFalse("the second server's notice verifies its call. " + rig.Describe(others));
                    rig.EndingsOf(own).Should().Be(0);
                    rig.EndingsOf(others).Should().Be(0);
                }
                rig.StatusAsked.Should().Be(statusAtTheReloads, "no channel read after the two reloads took part");
                rig.ReconcileSpansSince(spansAtTheReloads).Should().BeEmpty("no sweep tick and no reconcile took part");
            }
        }
        finally
        {
            rig.Manager.StepHook -= OnStep;
            rig.Server.ChannelTableSettled -= OnFirstSettled;
        }
    }

    internal sealed record Snapshot(CallSessionState State, bool Unverified, bool Ended);

    private static Snapshot Read(CallSession call)
    {
        using (call.SyncRoot.EnterScope())
            return new Snapshot(call.State, call.Unverified, call.State is CallSessionState.Completed or CallSessionState.Failed);
    }

    private sealed class NotificationCounter : IObserver<SessionDomainEvent>
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

        public int Of(string sessionId) => _counts.GetValueOrDefault(sessionId);

        public void OnNext(SessionDomainEvent value) => _counts.AddOrUpdate(value.SessionId, 1, static (_, n) => n + 1);

        public void OnError(Exception error)
        {
            // The manager's subject never faults; nothing to record.
        }

        public void OnCompleted()
        {
            // Completed when the manager is disposed; nothing to record.
        }
    }

    internal sealed record Run(CallSession Call, bool Unverified, bool Pending, bool Decided);

    /// <summary>
    /// Admits a call of two legs while the reconnect is pending, then lets the reload run with a late handler that keeps
    /// the reconnect pending until the reload has returned, and waits for a decision of that call that reads the
    /// reconnect no longer pending. <c>Decided</c> is false when no such decision came within the bound.
    /// </summary>
    internal static async Task<Run> ReconnectWithALateHandlerAsync(LossRig rig, bool endDuringTheOutage)
    {
        await rig.NewChannelAsync("X", "L");
        await rig.NewChannelAsync("Y", "L");
        rig.Attach();

        // Subscribed after the server's own handler: it returns only once the reload the server started has returned,
        // so the reload's channel read completes while the reconnect is still pending.
        var pendingAtReload = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Late()
        {
#pragma warning disable VSTHRD002 // The test's point: a handler that blocks the notification queue until the reload returned
            var finished = rig.Server.ReconnectReload.Wait(LossRig.Bound);
#pragma warning restore VSTHRD002
            pendingAtReload.TrySetResult(finished && rig.Server.IsReconnectPending);
        }
        rig.Connection.Reconnected += Late;

        await rig.HoldAsync();
        await rig.BufferAsync("PeerStatus", [new("Peer", "FILL/lost"), new("PeerStatus", "Reachable")], "FILL/lost");
        await rig.FenceAsync();
        rig.CloseSession();
        if (endDuringTheOutage)
        {
            rig.ForgetChannel("X");
            rig.ForgetChannel("Y");
        }
        await rig.LoggedInAsync(2);
        await rig.AdmitAsync();
        var call = rig.Manager.GetByLinkedId("L")!;
        var unverified = call.Unverified;
        var run = await WaitForTheSettledDecisionAsync(rig, call, () =>
        {
            rig.Release();
            return pendingAtReload.Task;
        });
        return run with { Unverified = unverified };
    }

    /// <summary>
    /// The caller's route: the session is lost on a connection without automatic reconnection, and the caller's own
    /// <c>ConnectAsync</c> brings it back. A <c>CallerReconnected</c> handler subscribed after the server's keeps that
    /// reconnect pending until the reload has returned; the reload's <c>Status</c> is answered by hand, after a walk has
    /// admitted the call while the reconnect is pending, so the reload's read completes while it is still pending.
    /// </summary>
    internal static async Task<Run> CallerReconnectWithALateHandlerAsync(LossRig rig, bool endDuringTheOutage)
    {
        await rig.NewChannelAsync("X", "L");
        await rig.NewChannelAsync("Y", "L");
        rig.Attach();

        var pendingAtReload = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<long> late = _ =>
        {
#pragma warning disable VSTHRD002 // The test's point: a handler that blocks the notification queue until the reload returned
            var finished = rig.Server.ReconnectReload.Wait(LossRig.Bound);
#pragma warning restore VSTHRD002
            pendingAtReload.TrySetResult(finished && rig.Server.IsReconnectPending);
        };
        // Internal to the connection and outside this suite's view of it, so it is subscribed the way Live's own handler
        // is, after the server's.
        typeof(AmiConnection).GetEvent("CallerReconnected", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetAddMethod(nonPublic: true)!.Invoke(rig.Connection, [late]);

        rig.CloseSession();
        if (endDuringTheOutage)
        {
            rig.ForgetChannel("X");
            rig.ForgetChannel("Y");
        }
        await rig.ConnectionLog.Logged("[AMI] Disconnected").WaitAsync(LossRig.Bound);
        rig.AnswerNextStatusByHand();
        await rig.Connection.ConnectAsync().AsTask().WaitAsync(LossRig.Bound);
        var reloadStatus = await rig.StatusIdAsync(1);
        await rig.AdmitAsync();
        var call = rig.Manager.GetByLinkedId("L")!;
        var unverified = call.Unverified;
        var run = await WaitForTheSettledDecisionAsync(rig, call, async () =>
        {
            string[] listed = endDuringTheOutage ? [] : ["X", "Y"];
            await rig.AnswerStatusAsync(reloadStatus, listed);
            return await pendingAtReload.Task.WaitAsync(LossRig.Bound);
        });
        return run with { Unverified = unverified };
    }

    /// <summary>
    /// Runs <paramref name="letTheReloadRun"/>, which returns whether the reload returned while the reconnect was still
    /// pending, then waits for a decision of <paramref name="call"/> that reads the reconnect no longer pending.
    /// <c>Decided</c> is false when no such decision came within the bound.
    /// </summary>
    private static async Task<Run> WaitForTheSettledDecisionAsync(LossRig rig, CallSession call, Func<Task<bool>> letTheReloadRun)
    {
        // A decision of this call reaches this step holding the call's lock, and reads the table after it: one that finds
        // the reconnect no longer pending here reads it settled. Taking the lock afterwards waits for it to finish.
        var settledDecision = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStep(HandlerStep step, string serverId, string id)
        {
            if (step == HandlerStep.DecisionOracleCaptured && id == call.SessionId && !rig.Server.IsReconnectPending)
                settledDecision.TrySetResult();
        }
        rig.Manager.StepHook += OnStep;
        try
        {
            var pending = await letTheReloadRun().WaitAsync(LossRig.Bound);
            await rig.ReconnectHandledAsync();
            var decided = await EndsWithinBoundAsync(settledDecision.Task);
            using (call.SyncRoot.EnterScope())
            {
                // The decision the hook saw has returned.
            }
            return new Run(call, Unverified: false, pending, decided);
        }
        finally
        {
            rig.Manager.StepHook -= OnStep;
        }
    }

    private static async Task<bool> EndsWithinBoundAsync(Task signal)
    {
        try
        {
            await signal.WaitAsync(LossRig.Bound);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
