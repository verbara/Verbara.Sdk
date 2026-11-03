using System.Collections.Concurrent;
using System.Reactive.Linq;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Tests.Shared.Sessions;
using Xunit.Abstractions;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

[Collection(SweepCounterGroup.Name)]
public sealed class StaleJoinRefusalTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnChannelAdded_ShouldRefuseTheJoin_WhenAReplacedAttachmentDeliversItIntoACallANewerAttachmentStamped(bool xHangsUpFirst)
    {
        await using var rig = new PoolSweepRig();
        var first = rig.AddServer("a", inPool: false);
        var starts = new ConcurrentQueue<CallStartedEvent>();
        using var subscription = rig.Manager.Events.OfType<CallStartedEvent>().Subscribe(starts.Enqueue);
        using var parked = StepParker.Install(rig.Manager, HandlerStep.ArrivalBuilt, "a", "X");
        var arrival = Task.Run(() => first.Server.Channels.OnNewChannel("X", "PJSIP/X", ChannelState.Up, linkedId: "L"));
        CallSession call;
        long generation;
        try
        {
            parked.WaitUntilParked();
            first.Server.Channels.GetByUniqueId("X").Should().NotBeNull();
            rig.Manager.GetByLinkedId("a", "L").Should().BeNull("X is stopped before its lookup and publication");
            rig.Manager.DetachFromServer("a", DetachedCalls.Keep);
            var replacement = rig.AddServer("a");
            ScopedSweepDecisionTests.Complete(replacement.Server).Should().BeFalse();
            replacement.Server.Channels.GetByUniqueId("X").Should().BeNull();
            replacement.Server.Channels.OnNewChannel("Y", "PJSIP/Y", ChannelState.Up, linkedId: "L");
            call = rig.Manager.GetByLinkedId("a", "L")!;
            call.Should().NotBeNull();
            call.Unverified.Should().BeFalse();
            rig.Manager.IsPendingDecision(call).Should().BeFalse();
            generation = call.AttachmentGeneration;
            generation.Should().Be(2, "the one replacement installed generation g2");
        }
        finally
        {
            parked.LetGo();
            await arrival.WaitAsync(SweepRig.Bound);
        }
        parked.ThrowIfFaulted();
        var current = rig["a"];
        var mappedX = rig.Manager.GetByChannelId("a", "X");
        var participants = call.Participants.Select(p => p.UniqueId).ToArray();
        var joinsOfX = call.Events.Count(e => e.Type == CallSessionEventType.ParticipantJoined && e.SourceChannel == "PJSIP/X");
        var startsAfterArrival = starts.Count;
        var pendingAfterArrival = rig.Manager.IsPendingDecision(call);
        var stampAfterArrival = call.AttachmentGeneration;
        var markOfX = call.GetParticipantMark("X");
        var debug = rig.Log.Entries.Where(e => e.Level == LogLevel.Debug
            && e.Values.ContainsKey("DeliveryGeneration") && Equals(e.Values.GetValueOrDefault("ChannelId"), "X")).ToArray();
        var entriesBeforeX = call.Events.Count;
        int endingsAfterY;
        bool xChangedNothing;
        if (xHangsUpFirst)
        {
            first.Server.Channels.OnHangup("X", HangupCause.NormalClearing);
            xChangedNothing = call.Events.Count == entriesBeforeX && rig.EndingsOf(call) == 0;
            current.Server.Channels.OnHangup("Y", HangupCause.UserBusy);
            endingsAfterY = rig.EndingsOf(call);
        }
        else
        {
            current.Server.Channels.OnHangup("Y", HangupCause.UserBusy);
            endingsAfterY = rig.EndingsOf(call);
            var beforeX = call.Events.Count;
            first.Server.Channels.OnHangup("X", HangupCause.NormalClearing);
            xChangedNothing = call.Events.Count == beforeX && rig.EndingsOf(call) == endingsAfterY;
        }
        ScopedSweepDecisionTests.Complete(current.Server).Should().BeFalse("Y's hangup, not a completed table, must end C");
        var causeAtY = call.HangupCause;
        var reloadAtY = call.Metadata.GetValueOrDefault("cause");
        await current.Server.ReconcileChannelsAsync().AsTask().WaitAsync(SweepRig.Bound);
        ScopedSweepDecisionTests.Complete(current.Server).Should().BeTrue();
        current.Server.Channels.ActiveChannels.Should().BeEmpty();
        SweepRig.CreatedAtField.SetValue(call, DateTimeOffset.UtcNow - TimeSpan.FromDays(1));
        var requests = current.StatusRequests;
        await ScopedSweepDecisionTests.Pass(rig, current.Server, "a");
        var unverifiable = ScopedSweepDecisionTests.Count(rig, "Unverifiable");
        output.WriteLine($"mappedX={mappedX?.SessionId ?? "null"}; participants=[{string.Join(',', participants)}]; joinsX={joinsOfX}; Starts={startsAfterArrival}; pending={pendingAfterArrival}; stamp={stampAfterArrival}; endingsAtY={endingsAfterY}; finalEndings={rig.EndingsOf(call)}; unverifiable={unverifiable}; StatusDelta={current.StatusRequests - requests}");
        using var assertions = new AssertionScope();
        mappedX.Should().BeNull("a stale join must write no channel index entry");
        participants.Should().Equal("Y");
        joinsOfX.Should().Be(0);
        startsAfterArrival.Should().Be(1);
        pendingAfterArrival.Should().BeFalse();
        stampAfterArrival.Should().Be(generation);
        markOfX.Should().Be(0, "a refused arrival must not install its old channel-manager mark");
        xChangedNothing.Should().BeTrue();
        endingsAfterY.Should().Be(1, "Y's observed hangup must end C without a phantom X");
        causeAtY.Should().Be(HangupCause.UserBusy);
        reloadAtY.Should().BeNull();
        rig.EndingsOf(call).Should().Be(1);
        call.HangupCause.Should().Be(HangupCause.UserBusy);
        call.Metadata.Should().NotContainKey("cause");
        current.StatusRequests.Should().Be(requests);
        unverifiable.Should().Be(0);
        debug.Should().ContainSingle("one refusal Debug identifies this stale join");
        if (debug.Length == 1)
        {
            debug[0].Values["ServerId"].Should().Be("a");
            debug[0].Values["CallGeneration"].Should().Be(generation);
            Convert.ToInt64(debug[0].Values["DeliveryGeneration"], System.Globalization.CultureInfo.InvariantCulture).Should().Be(1, "X was captured by the first attachment g1");
        }
        SessionIndexInvariant.Assert(rig.Manager);
    }

    [Fact]
    public async Task OnChannelAdded_ShouldKeepItsCapturedStamp_WhenTheNewerCallEndedBeforeTheOldArrivalResumed()
    {
        await using var rig = new PoolSweepRig();
        var first = rig.AddServer("a", inPool: false);
        var starts = new ConcurrentQueue<CallStartedEvent>();
        using var subscription = rig.Manager.Events.OfType<CallStartedEvent>().Subscribe(starts.Enqueue);
        using var parked = StepParker.Install(rig.Manager, HandlerStep.ArrivalBuilt, "a", "X");
        var arrival = Task.Run(() => first.Server.Channels.OnNewChannel("X", "PJSIP/X", ChannelState.Up, linkedId: "L"));
        CallSession newer;
        try
        {
            parked.WaitUntilParked();
            rig.Manager.DetachFromServer("a", DetachedCalls.Keep);
            var replacement = rig.AddServer("a");
            replacement.Server.Channels.OnNewChannel("Y", "PJSIP/Y", ChannelState.Up, linkedId: "L");
            newer = rig.Manager.GetByLinkedId("a", "L")!;
            replacement.Server.Channels.OnHangup("Y", HangupCause.UserBusy);
            rig.EndingsOf(newer).Should().Be(1, "the stale open is allowed only after this call already ended");
        }
        finally
        {
            parked.LetGo();
            await arrival.WaitAsync(SweepRig.Bound);
        }
        parked.ThrowIfFaulted();
        var stale = rig.Manager.GetByLinkedId("a", "L")!;
        stale.Should().NotBeSameAs(newer);
        stale.AttachmentGeneration.Should().BeLessThan(newer.AttachmentGeneration);
        starts.Should().HaveCount(2);
        rig.EndingsOf(stale).Should().Be(0);
        rig.Manager.IsPendingDecision(stale).Should().BeTrue();
        await rig["a"].Server.ReconcileChannelsAsync().AsTask().WaitAsync(SweepRig.Bound);
        rig.EndingsOf(stale).Should().Be(1);
        stale.Metadata["cause"].Should().Be("reload");
        stale.HangupCause.Should().BeNull();
        newer.HangupCause.Should().Be(HangupCause.UserBusy);
        newer.Metadata.Should().NotContainKey("cause");
        starts.Should().HaveCount(2);
        output.WriteLine($"stale-open boundary: Starts={starts.Count}; originalEndings={rig.EndingsOf(newer)}; staleEndings={rig.EndingsOf(stale)}; staleCause={stale.Metadata["cause"]}");
        SessionIndexInvariant.Assert(rig.Manager);
    }

}
