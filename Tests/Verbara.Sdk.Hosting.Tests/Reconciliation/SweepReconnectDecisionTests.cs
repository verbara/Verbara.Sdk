using System.Reflection;
using System.Reactive.Linq;
using FluentAssertions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Tests.Shared.Sessions;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

[Collection(SweepCounterGroup.Name)]
public sealed class SweepReconnectDecisionTests
{
    [Fact]
    public async Task Sweep_ShouldLeaveThePendingCallUntilReconnectIsHandled_WhenTheCurrentSessionTableIsAlreadyComplete()
    {
        await using var rig = await ReconnectingSweepRig.StartAsync(sessions: 2);
        rig.Server.Channels.OnNewChannel("ghost", "PJSIP/ghost", ChannelState.Up, linkedId: "gone");
        var call = rig.Manager.GetByLinkedId(SweepRig.ServerId, "gone")!;
        call.State = CallSessionState.Connected;
        using var parked = new SweepDecisionGate();
        using var observer = rig.Manager.Events.OfType<CallHeldEvent>().Subscribe(e =>
        {
            if (e.SessionId == call.SessionId) parked.Hold();
        });
        var hold = Task.Run(() => rig.Server.Channels.OnHold("ghost", null));
        rig.HoldFirstLoss();
        try
        {
            await parked.Entered.Task.WaitAsync(SweepRig.Bound);
            rig.Manager.DetachFromServer(SweepRig.ServerId);
            rig.Server.Channels.OnHangup("ghost", HangupCause.UserBusy);
            rig.Peers[0].CloseSession();
            await rig.LoggedIn(2).WaitAsync(SweepRig.Bound);
            await rig.Server.ReconcileChannelsAsync().AsTask().WaitAsync(SweepRig.Bound);
            ScopedSweepDecisionTests.Complete(rig.Server).Should().BeTrue("the new fully-booted AMI session supplied this complete table");
            Flag(rig.Server, "IsReconnectPending").Should().BeTrue();
            await Task.Run(() => rig.Manager.AttachToServer(rig.Server, SweepRig.ServerId)).WaitAsync(SweepRig.Bound);
        }
        finally { parked.Release(); await hold.WaitAsync(SweepRig.Bound); }
        try
        {
            rig.Manager.IsPendingDecision(call).Should().BeTrue("the bounded iii was genuinely busy");
            var asks = rig.Peers[1].Asked("Status");
            await SweepRig.SweepOnceAsync(rig.BuildSweep());
            rig.Peers[1].Asked("Status").Should().Be(asks);
            call.EndingDelivered.Should().BeFalse("vi is suppressed while this actual current-session reconnect announcement is queued");
            rig.Manager.IsPendingDecision(call).Should().BeTrue();
            call.Participants.Single().LeftAt.Should().BeNull();
        }
        finally { rig.ReleaseLoss(); }
        await rig.ServerLog.Logged("[LIVE] State loaded", 2).WaitAsync(SweepRig.Bound);
        await rig.ReconnectHandled();
        call.EndingDelivered.Should().BeTrue();
        call.Metadata["cause"].Should().Be("reload");
        call.HangupCause.Should().BeNull();
        SessionIndexInvariant.Assert(rig.Manager);
    }

    private static bool Flag(VerbaraServer server, string name) =>
        (bool)typeof(VerbaraServer).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
}
