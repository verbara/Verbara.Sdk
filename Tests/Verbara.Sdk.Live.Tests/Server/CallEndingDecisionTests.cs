using FluentAssertions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;
using Xunit;

namespace Verbara.Sdk.Live.Tests.Server;

public sealed class CallEndingDecisionTests
{
    [Fact]
    public async Task RegisterReconstructedSession_ShouldJoinHeldLegsWithoutStarting_WhenItsServerIsAttached()
    {
        await using var rig = await EndingBaselineRig.CreateAsync();
        rig.Add("X");
        rig.Add("Y");
        rig.Attach();
        var call = NewCall();
        rig.Manager.RegisterReconstructedSession(call).Should().BeTrue();
        call.Participants.Select(p => p.UniqueId).Should().BeEquivalentTo("X", "Y");
        rig.Starts().Should().Be(0);
        rig.Hangup("X");
        rig.Ends(call).Should().Be(0);
        rig.Hangup("Y");
        rig.Ends(call).Should().Be(1);
        call.HangupCause.Should().Be(HangupCause.NormalClearing);
        call.Metadata.Should().NotContainKey("cause");
    }

    [Fact]
    public async Task RegisterReconstructedSession_ShouldEndAtRegistration_WhenItsCompleteTableHoldsNoChannel()
    {
        await using var rig = await EndingBaselineRig.CreateAsync();
        rig.Attach();
        var call = NewCall();
        rig.Manager.RegisterReconstructedSession(call).Should().BeTrue();
        rig.Ends(call).Should().Be(1);
        call.HangupCause.Should().BeNull();
        call.Metadata["cause"].Should().Be("reload");
        rig.Starts().Should().Be(0);
    }

    [Theory]
    [InlineData(CallSessionState.Completed)]
    [InlineData(CallSessionState.Failed)]
    public async Task RegisterReconstructedSession_ShouldPreserveEnding_WhenTheCallerSuppliesAnEndedCall(CallSessionState state)
    {
        await using var rig = await EndingBaselineRig.CreateAsync();
        rig.Attach();
        var call = NewCall();
        call.State = state;
        call.HangupCause = HangupCause.UserBusy;
        call.SetMetadata("cause", "preserved");
        rig.Manager.RegisterReconstructedSession(call).Should().BeTrue();
        await rig.Run.Server.ReconcileChannelsAsync();
        rig.Ends(call).Should().Be(0);
        call.State.Should().Be(state);
        call.HangupCause.Should().Be(HangupCause.UserBusy);
        call.Metadata["cause"].Should().Be("preserved");
    }

    [Fact]
    public async Task OnHangup_ShouldFindLeglessCallByLinkedId_WhenItWasRegisteredBeforeAttach()
    {
        await using var rig = await EndingBaselineRig.CreateAsync();
        var call = NewCall();
        rig.Manager.RegisterReconstructedSession(call).Should().BeTrue();
        rig.Add("X");
        rig.Add("Y");
        rig.Attach();
        rig.Hangup("X");
        rig.Ends(call).Should().Be(0);
        rig.Run.Server.Channels.OnHangup("Y", HangupCause.UserBusy);
        rig.Ends(call).Should().Be(1);
        call.Participants.Should().BeEmpty();
        call.HangupCause.Should().Be(HangupCause.UserBusy);
        call.Metadata.Should().NotContainKey("cause");
        call.Events.Count(e => e.Type == CallSessionEventType.ParticipantLeft).Should().Be(2);
    }

    [Fact]
    public async Task OnChannelRemoved_ShouldIgnoreOldAttachment_WhenTheSameInstanceIsDetachedAndAttachedBeforeDelivery()
    {
        await using var rig = await EndingBaselineRig.CreateAsync();
        using var gate = new NotificationGate();
        rig.Run.Server.Channels.ChannelRemoved += _ => gate.Hold();
        rig.Attach();
        rig.Add("X");
        var call = rig.Manager.GetByLinkedId("L")!;
        var originalEvents = call.Events.Count;
        var workers = new List<Task>();
        await NotificationGate.RunAsync([gate], workers, async () =>
        {
            workers.Add(Task.Run(() => rig.Hangup("X")));
            await gate.Entered.Task.WaitAsync(EndingBaselineRig.Bound);
            rig.Manager.DetachFromServer("server");
            rig.Attach();
            gate.Release();
            await workers[0].WaitAsync(EndingBaselineRig.Bound);
            call.Events.Count.Should().Be(originalEvents, "the captured old subscription cannot record on a new attachment");
            rig.Ends(call).Should().Be(0);
        });
    }

    [Fact]
    public async Task RegisterReconstructedSession_ShouldSkipCompetingOwner_WhenAnotherRegistrationClaimsAfterItsJoinLookup()
    {
        await using var rig = await EndingBaselineRig.CreateAsync();
        rig.Add("X");
        rig.Attach();
        var call = NewCall();
        var competing = new CallSession("competing", "other", "server", CallDirection.Inbound);
        competing.AddParticipant(new SessionParticipant
        {
            UniqueId = "X", Channel = "PJSIP/X", Technology = "PJSIP", Role = ParticipantRole.Caller,
        });
        using var gate = new NotificationGate();
        rig.Manager.StepHook = (step, _, _) =>
        {
            if (step == HandlerStep.RegistrationJoinReady) gate.Hold();
        };
        var workers = new List<Task>();
        await NotificationGate.RunAsync([gate], workers, async () =>
        {
            workers.Add(Task.Run(() => rig.Manager.RegisterReconstructedSession(call).Should().BeTrue()));
            await gate.Entered.Task.WaitAsync(EndingBaselineRig.Bound);
            rig.Manager.RegisterReconstructedSession(competing).Should().BeTrue();
            gate.Release();
            await workers[0].WaitAsync(EndingBaselineRig.Bound);
            rig.Manager.GetByChannelId("server", "X").Should().BeSameAs(competing);
            call.Participants.Should().BeEmpty("a skipped join changes no participant, event or admission mark");
            call.Events.Should().BeEmpty();
        });
    }

    private static CallSession NewCall() => new("reconstructed", "L", "server", CallDirection.Inbound);
}
