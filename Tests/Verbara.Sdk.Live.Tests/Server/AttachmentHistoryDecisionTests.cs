using FluentAssertions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Live.Tests.Server;

public sealed class AttachmentHistoryDecisionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task OnChannelRemoved_ShouldIgnoreTheOldDelivery_WhenANewerAttachmentIsActive(bool sameInstance, bool aba)
        => VerifyOldDelivery(sameInstance, secondDetach: false, aba);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task OnChannelRemoved_ShouldIgnoreTheOldDelivery_WhenANewerAttachmentWasKeptAgain(bool sameInstance, bool aba)
        => VerifyOldDelivery(sameInstance, secondDetach: true, aba);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachToServer_ShouldEndTheKeptCall_WhenAnotherInstancesTableIsComplete(bool completeAtAttach)
    {
        await using var rig = await EndingBaselineRig.CreateAsync();
        await using var replacement = await Run.ConnectAsync(new BootingAsterisk { BootedAtLogin = true });
        if (completeAtAttach)
            await replacement.Server.ReconcileChannelsAsync().AsTask().WaitAsync(EndingBaselineRig.Bound);
        rig.Attach();
        rig.Add("X");
        var call = rig.Manager.GetByLinkedId("server", "L")!;
        call.State = CallSessionState.Connected;
        using var gate = new NotificationGate();
        rig.Manager.StepHook = (step, _, id) =>
        {
            if (step == HandlerStep.RemovalFound && id == "X") gate.Hold();
        };
        var workers = new List<Task>();
        await NotificationGate.RunAsync([gate], workers, async () =>
        {
            workers.Add(Task.Run(() => rig.Hangup("X")));
            await gate.Entered.Task.WaitAsync(EndingBaselineRig.Bound);
            rig.Manager.DetachFromServer("server", DetachedCalls.Keep);
            rig.Manager.AttachToServer(replacement.Server, "server");
            if (!completeAtAttach)
            {
                rig.Ends(call).Should().Be(0);
                await replacement.Server.ReconcileChannelsAsync().AsTask().WaitAsync(EndingBaselineRig.Bound);
            }
            rig.Ends(call).Should().Be(1, "the returning instance has neither held nor departing X");
        });
        rig.Manager.StepHook = null;
        rig.Ends(call).Should().Be(1);
        call.HangupCause.Should().BeNull();
        call.Metadata["cause"].Should().Be("reload");
        call.Events.Should().ContainSingle(e => e.Type == CallSessionEventType.ParticipantLeft && e.Detail == "reload");
    }

    private static async Task VerifyOldDelivery(bool sameInstance, bool secondDetach, bool aba)
    {
        await using var rig = await EndingBaselineRig.CreateAsync();
        await using var replacement = await Run.ConnectAsync(new BootingAsterisk { BootedAtLogin = true });
        await using var collision = await EndingBaselineRig.CreateAsync();
        rig.Attach();
        rig.Add("X");
        var call = rig.Manager.GetByLinkedId("server", "L")!;
        call.State = CallSessionState.Connected;
        rig.Run.Server.Bridges.OnBridgeCreated("bridge", "mixing", "simple_bridge", null, null);
        rig.Run.Server.Bridges.OnChannelEntered("bridge", "X");
        rig.Manager.GetByBridgeId("server", "bridge").Should().BeSameAs(call);
        rig.Manager.AttachToServer(collision.Run.Server, "other");
        collision.Add("X", "other-L");
        var unrelated = rig.Manager.GetByChannelId("other", "X")!;
        unrelated.State = CallSessionState.Connected;
        collision.Run.Server.Bridges.OnBridgeCreated("bridge", "mixing", "simple_bridge", null, null);
        collision.Run.Server.Bridges.OnChannelEntered("bridge", "X");
        using var gate = new NotificationGate();
        rig.Manager.StepHook = (step, _, id) =>
        {
            if (step == HandlerStep.RemovalFound && id == "X") gate.Hold();
        };
        var workers = new List<Task>();
        var before = 0;
        var returning = sameInstance ? rig.Run.Server : replacement.Server;
        await NotificationGate.RunAsync([gate], workers, async () =>
        {
            workers.Add(Task.Run(() => rig.Hangup("X")));
            await gate.Entered.Task.WaitAsync(EndingBaselineRig.Bound);
            rig.Manager.DetachFromServer("server", DetachedCalls.Keep);
            if (aba)
            {
                rig.Manager.AttachToServer(replacement.Server, "server");
                rig.Manager.DetachFromServer("server", DetachedCalls.Keep);
            }
            rig.Manager.AttachToServer(returning, "server");
            if (secondDetach) rig.Manager.DetachFromServer("server", DetachedCalls.Keep);
            rig.Ends(call).Should().Be(0, "the newer attachment must leave a live witness for the old handler");
            rig.Manager.GetById(call.SessionId).Should().BeSameAs(call);
            call.Participants.Single(p => p.UniqueId == "X").LeftAt.Should().BeNull();
            before = call.Events.Count;
            gate.Release();
            await Task.WhenAll(workers).WaitAsync(EndingBaselineRig.Bound);
        });
        rig.Manager.StepHook = null;
        workers.Should().OnlyContain(worker => worker.IsCompletedSuccessfully);
        rig.Ends(call).Should().Be(0, "a replaced delivery must not end the still-held call, even after another Keep");
        call.Events.Count.Should().Be(before, "a replaced delivery must record no old cause or departure");
        call.Participants.Single(p => p.UniqueId == "X").LeftAt.Should().BeNull();
        call.HangupCause.Should().BeNull();
        call.Metadata.Should().NotContainKey("cause");
        rig.Manager.GetById(call.SessionId).Should().BeSameAs(call);
        if (secondDetach) rig.Manager.AttachToServer(returning, "server");
        await returning.ReconcileChannelsAsync().AsTask().WaitAsync(EndingBaselineRig.Bound);
        rig.Ends(call).Should().Be(1);
        call.HangupCause.Should().BeNull();
        call.Metadata["cause"].Should().Be("reload");
        call.Events.Should().ContainSingle(e => e.Type == CallSessionEventType.ParticipantLeft && e.Detail == "reload");
        rig.Manager.GetByChannelId("server", "X").Should().BeNull();
        rig.Manager.GetByBridgeId("server", "bridge").Should().BeNull();
        rig.Manager.GetByChannelId("other", "X").Should().BeSameAs(unrelated);
        rig.Manager.GetByBridgeId("other", "bridge").Should().BeSameAs(unrelated);
        unrelated.State.Should().Be(CallSessionState.Connected);
        unrelated.Unverified.Should().BeFalse();
        rig.Ends(unrelated).Should().Be(0);
    }
}
