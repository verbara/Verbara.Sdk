using System.Runtime.CompilerServices;
using FluentAssertions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Tests.Shared.Sessions;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

[Collection(SweepCounterGroup.Name)]
public sealed class D5AttachmentLifetimeTests
{
    [Fact]
    public async Task Completion_ShouldLeaveTheCallForTheReplacement_WhenTheOldReadFinishesAfterARefusedReattach()
    {
        await using var rig = new PoolSweepRig();
        var first = rig.AddServer("a");
        first.Server.Channels.OnNewChannel("X", "PJSIP/X", ChannelState.Up, linkedId: "L");
        var call = rig.Manager.GetByLinkedId("a", "L")!;
        rig.Manager.DetachFromServer("a");
        first.Server.Channels.OnHangup("X", HangupCause.UserBusy);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.StatusReply = token => HeldEmpty(opened, answer, token);
        var oldRead = first.Server.ReconcileChannelsAsync().AsTask();
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusRefusedFromAsk = 1 };
        await using var replacement = await Run.ConnectAsync(peer);
        try
        {
            await opened.Task.WaitAsync(SweepRig.Bound);
            rig.Manager.AttachToServer(first.Server, "a");
            rig.Manager.IsPendingDecision(call).Should().BeTrue();
            rig.Manager.DetachFromServer("a");
            rig.Manager.AttachToServer(replacement.Server, "a");
            await replacement.StartServerAsync();
            await rig.Manager.AdmitExistingCalls("a");
            replacement.Server.LastStatusRefusal.Should().NotBeNull();
            answer.TrySetResult();
            await oldRead.WaitAsync(SweepRig.Bound);
            rig.EndingsOf(call).Should().Be(0, "the old instance's completed read cannot decide against the replacement's refused table");
            call.Participants.Single().LeftAt.Should().BeNull();
            await ScopedSweepDecisionTests.Pass(rig, replacement.Server, "a");
            ScopedSweepDecisionTests.Count(rig, "Unverifiable").Should().Be(1);
            rig.EndingsOf(call).Should().Be(0);
            SessionIndexInvariant.Assert(rig.Manager);
            peer.StatusRefusedFromAsk = 0;
            await replacement.Server.ReconcileChannelsAsync();
            rig.EndingsOf(call).Should().Be(1);
            call.Metadata["cause"].Should().Be("reload");
            call.HangupCause.Should().BeNull();
            rig.Manager.GetByChannelId("a", "X").Should().BeNull();
            SessionIndexInvariant.Assert(rig.Manager);
        }
        finally { answer.TrySetResult(); await oldRead.WaitAsync(SweepRig.Bound); }
    }

    [Fact]
    public async Task AttachToServer_ShouldWaitForTheCurrentReload_WhenTheSessionReconnectsAfterItsPriorLoad()
    {
        await using var rig = await ReconnectingSweepRig.StartAsync(sessions: 2);
        var call = rig.Manager.GetByLinkedId(SweepRig.ServerId, ReconnectingSweepRig.CallUid)!;
        rig.Manager.DetachFromServer(SweepRig.ServerId);
        rig.Server.Channels.OnHangup(ReconnectingSweepRig.CallUid, HangupCause.UserBusy);
        rig.Peers[1].ListsCall = false;
        rig.HoldFirstLoss();
        try
        {
            rig.Peers[0].CloseSession();
            await rig.LoggedIn(2).WaitAsync(SweepRig.Bound);
            rig.Manager.AttachToServer(rig.Server, SweepRig.ServerId);
            call.EndingDelivered.Should().BeFalse("the first load belongs to the old AMI session, so iii must wait");
            ScopedSweepDecisionTests.Complete(rig.Server).Should().BeFalse();
            rig.Peers[1].Asked("Status").Should().Be(0);
            await SweepRig.SweepOnceAsync(rig.BuildSweep());
            call.EndingDelivered.Should().BeFalse();
            rig.Peers[1].Asked("Status").Should().Be(0);
        }
        finally { rig.ReleaseLoss(); }
        await rig.ServerLog.Logged("[LIVE] State loaded", 2).WaitAsync(SweepRig.Bound);
        await rig.ReconnectHandled();
        call.EndingDelivered.Should().BeTrue();
        call.Metadata["cause"].Should().Be("reload");
        call.HangupCause.Should().BeNull();
        rig.Manager.GetByChannelId(SweepRig.ServerId, ReconnectingSweepRig.CallUid).Should().BeNull();
        SessionIndexInvariant.Assert(rig.Manager);
    }

    [Fact]
    public async Task AttachAndCompletion_ShouldNeitherMarkNorEndTheReleasedCall_WhenItsReturningTableIsComplete()
    {
        await using var rig = new PoolSweepRig();
        var server = rig.AddServer("a");
        await server.Server.ReconcileChannelsAsync();
        server.Server.Channels.OnNewChannel("X", "PJSIP/X", ChannelState.Up, linkedId: "L");
        var call = rig.Manager.GetByLinkedId("a", "L")!;
        server.Server.Bridges.OnBridgeCreated("B", "mixing", "simple_bridge", null, null);
        server.Server.Bridges.OnChannelEntered("B", "X");
        var before = SweepRig.Look(call);
        var stamp = call.AttachmentGeneration;
        var unverified = call.Unverified;
        rig.Manager.DetachFromServer("a", DetachedCalls.Release).Should().ContainSingle().Which.Should().BeSameAs(call);
        server.Server.Channels.OnHangup("X", HangupCause.UserBusy);
        rig.Manager.AttachToServer(server.Server, "a");
        await server.Server.ReconcileChannelsAsync();
        await ScopedSweepDecisionTests.Pass(rig, server.Server, "a");
        rig.EndingsOf(call).Should().Be(0);
        ScopedSweepDecisionTests.Count(rig, "Unverifiable").Should().Be(0);
        call.AttachmentGeneration.Should().Be(stamp);
        call.Unverified.Should().Be(unverified);
        SweepRig.Look(call).Should().BeEquivalentTo(before);
        rig.Manager.GetById(call.SessionId).Should().BeNull();
        rig.Manager.GetByChannelId("a", "X").Should().BeNull();
        rig.Manager.GetByBridgeId("a", "B").Should().BeNull();
        SessionIndexInvariant.Assert(rig.Manager);
    }

    private static async IAsyncEnumerable<ManagerEvent> HeldEmpty(TaskCompletionSource opened, TaskCompletionSource answer,
        [EnumeratorCancellation] CancellationToken token)
    {
        opened.TrySetResult();
        await answer.Task.WaitAsync(token);
        yield break;
    }
}
