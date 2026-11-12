using FluentAssertions;
using FluentAssertions.Execution;
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
            rig.Release();
            var pending = await pendingAtReload.Task.WaitAsync(LossRig.Bound);
            await rig.ReconnectHandledAsync();
            var decided = await EndsWithinBoundAsync(settledDecision.Task);
            using (call.SyncRoot.EnterScope())
            {
                // The decision the hook saw has returned.
            }
            return new Run(call, unverified, pending, decided);
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
