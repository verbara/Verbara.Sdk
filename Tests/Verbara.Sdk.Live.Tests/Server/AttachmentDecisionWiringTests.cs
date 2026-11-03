using FluentAssertions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Live.Tests.Server;

public sealed class AttachmentDecisionWiringTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachToServer_ShouldRefuse_WhenRequestedFromInsideANotification(bool sameInstance)
    {
        await using var rig = await EndingBaselineRig.CreateAsync();
        await using var other = await EndingBaselineRig.CreateAsync();
        await using var replacement = await EndingBaselineRig.CreateAsync();
        rig.Attach();
        rig.Add("X");
        var call = rig.Manager.GetByLinkedId("server", "L")!;
        call.State = CallSessionState.Connected;
        rig.Manager.AttachToServer(other.Run.Server, "b");
        other.Add("B", "LB");
        var kept = rig.Manager.GetByLinkedId("b", "LB")!;
        Exception? refusal = null;
        var delivered = 0;
        var requestedId = sameInstance ? "server" : "b";
        using var requester = rig.Manager.Events.Subscribe(new NotificationObserver(value =>
        {
            if (value is CallHeldEvent)
            {
                if (!sameInstance) rig.Manager.DetachFromServer("b", DetachedCalls.Keep);
                refusal = Record.Exception(() => rig.Manager.AttachToServer(
                    sameInstance ? rig.Run.Server : replacement.Run.Server, requestedId));
            }
        }));
        using var follower = rig.Manager.Events.Subscribe(new NotificationObserver(value =>
        {
            if (value is CallHeldEvent) delivered++;
        }));

        rig.Run.Server.Channels.OnHold("X", null);

        refusal.Should().BeOfType<InvalidOperationException>("every attach requested inside engine work must be refused, including a same-instance no-op")
            .Which.Message.Should().Contain($"'{requestedId}'");
        delivered.Should().Be(1, "catching the refusal must let later subscribers receive the original notification");
        call.State.Should().Be(CallSessionState.OnHold);
        rig.Run.Server.Channels.GetByUniqueId("X").Should().NotBeNull();
        rig.Manager.GetByChannelId("server", "X").Should().BeSameAs(call);
        kept.Unverified.Should().BeFalse("a Keep detach must not walk or change its call");
        rig.Manager.GetByChannelId("b", "B").Should().BeSameAs(kept);
        if (!sameInstance)
        {
            replacement.Add("not-subscribed", "not-subscribed");
            rig.Manager.GetByChannelId("b", "not-subscribed").Should().BeNull(
                "the refused replacement must have no installed subscriptions");
            rig.Manager.AttachToServer(replacement.Run.Server, "b");
            replacement.Add("outside-attach", "outside-attach");
            rig.Manager.GetByChannelId("b", "outside-attach").Should().NotBeNull(
                "the ordinary outside attach must install the subscriptions refused inside the callback");
        }
        rig.Run.Server.Channels.OnUnhold("X");
        call.State.Should().Be(CallSessionState.Connected, "the original attachment must still deliver its events");
    }

    [Fact]
    public async Task OnChannelRemoved_ShouldEndItsCall_WhenAKeepDetachClosedTheAttachmentDuringTheDelivery()
    {
        await using var rig = await EndingBaselineRig.CreateAsync();
        rig.Run.Server.Channels.ChannelRemoved += _ => rig.Manager.DetachFromServer("server", DetachedCalls.Keep);
        rig.Attach();
        rig.Add("X");
        var call = rig.Manager.GetByLinkedId("server", "L")!;

        rig.Hangup("X");

        rig.Ends(call).Should().Be(1, "the captured closed and unreplaced attachment must finish the departure it delivers");
        call.HangupCause.Should().Be(HangupCause.NormalClearing);
        call.Metadata.Should().NotContainKey("cause");
        call.Events.Should().ContainSingle(e => e.Type == CallSessionEventType.ParticipantLeft);
    }

    private sealed class NotificationObserver(Action<SessionDomainEvent> onNext) : IObserver<SessionDomainEvent>
    {
        public void OnNext(SessionDomainEvent value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    [Fact]
    public async Task DetachFromServer_ShouldKeepObservationUnchanged_WhenTheAttachmentKeepsItsCalls()
    {
        await using var rig = await EndingBaselineRig.CreateAsync();
        rig.Attach();
        rig.Add("X");
        var call = rig.Manager.GetByLinkedId("server", "L")!;
        call.Unverified.Should().BeFalse();
        rig.Manager.DetachFromServer("server", DetachedCalls.Keep);
        call.Unverified.Should().BeFalse("Keep takes no call lock and changes no observation record");
    }

    [Fact]
    public async Task AttachToServer_ShouldEndTheKeptCall_WhenItsCompleteTableNoLongerHoldsItsLastLeg()
    {
        await using var rig = await EndingBaselineRig.CreateAsync();
        rig.Attach();
        rig.Add("X");
        var call = rig.Manager.GetByLinkedId("server", "L")!;
        rig.Manager.DetachFromServer("server", DetachedCalls.Keep);
        rig.Hangup("X");
        rig.Attach();
        rig.Ends(call).Should().Be(1, "the complete-table attach must decide the call it inherited");
        call.HangupCause.Should().BeNull();
        call.Metadata["cause"].Should().Be("reload");
        call.Events.Should().ContainSingle(e => e.Type == CallSessionEventType.ParticipantLeft && e.Detail == "reload");
    }

    [Fact]
    public async Task AttachToServer_ShouldPreserveOrdinaryObservation_WhenTheCurrentInstanceIsAttachedAgain()
    {
        await using var rig = await EndingBaselineRig.CreateAsync();
        rig.Attach();
        rig.Add("X");
        var call = rig.Manager.GetByLinkedId("server", "L")!;
        rig.Attach();
        call.Unverified.Should().BeFalse("an attach of the current instance is a no-op");
        rig.Hangup("X");
        rig.Ends(call).Should().Be(1);
        call.HangupCause.Should().Be(HangupCause.NormalClearing);
        call.Metadata.Should().NotContainKey("cause");
    }
}
