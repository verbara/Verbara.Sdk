using System.Reactive.Linq;
using FluentAssertions;
using NSubstitute;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Tests.Shared.Sessions;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

[Collection(SweepCounterGroup.Name)]
public sealed class WiredPromotionGuardTests
{
    public static TheoryData<bool, bool, string, bool> Cases
    {
        get
        {
            var data = new TheoryData<bool, bool, string, bool>();
            foreach (var outsideLinkedId in new[] { false, true })
            foreach (var singleServer in new[] { false, true })
            foreach (var skip in new[] { "no-candidate", "not-connected", "outcome-not-reported" })
            foreach (var observedY in new[] { false, true })
                data.Add(outsideLinkedId, singleServer, skip, observedY);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Sweep_ShouldEndTheLeftCall_WhenItsReplacedDepartureFinishedAfterReattach(
        bool outsideLinkedId, bool singleServer, string skip, bool observedY)
    {
        await using var rig = new PoolSweepRig();
        var server = rig.AddServer("a", attach: false);
        await server.Server.ReconcileChannelsAsync();
        ScopedSweepDecisionTests.Complete(server.Server).Should().BeTrue();
        server.Server.Channels.OnNewChannel("X", "PJSIP/X", ChannelState.Up,
            linkedId: outsideLinkedId ? "L-prime" : "L");
        var call = new CallSession("promotion-C", "L", "a", CallDirection.Inbound);
        call.AddParticipant(new SessionParticipant
        {
            UniqueId = "X", Channel = "PJSIP/X", Technology = "PJSIP",
            Role = ParticipantRole.Caller, JoinedAt = DateTimeOffset.UtcNow,
        });
        rig.Manager.RegisterReconstructedSession(call).Should().BeTrue();
        rig.Manager.AttachToServer(server.Server, "a");
        call.Unverified.Should().BeFalse("the initial complete attachment observes the seeded present leg");
        call.State = CallSessionState.Connected;

        // An ordinary candidate makes the named Status skip real, rather than being hidden by no-candidate.
        if (skip != "no-candidate")
        {
            server.Server.Channels.OnNewChannel("ordinary", "PJSIP/ordinary", ChannelState.Up, linkedId: "ordinary-L");
            var ordinary = rig.Manager.GetByLinkedId("a", "ordinary-L")!;
            SweepRig.CreatedAtField.SetValue(ordinary, DateTimeOffset.UtcNow - TimeSpan.FromDays(1));
        }

        using var parked = StepParker.Install(rig.Manager, HandlerStep.RemovalFound, "a", "X");
        var oldRemoval = Task.Run(() => server.Server.Channels.OnHangup("X", HangupCause.NormalClearing));
        var pendingAfterAttach = false;
        var entriesBeforeOldReturn = 0;
        try
        {
            parked.WaitUntilParked();
            server.Server.Channels.GetByUniqueId("X").Should().BeNull();
            rig.Manager.DetachFromServer("a", DetachedCalls.Keep);
            rig.Manager.AttachToServer(server.Server, "a");
            pendingAfterAttach = call.Unverified;
            rig.EndingsOf(call).Should().Be(0);
            call.Participants.Single(p => p.UniqueId == "X").LeftAt.Should().BeNull();
            if (observedY)
            {
                server.Server.Channels.OnNewChannel("Y", "PJSIP/Y", ChannelState.Up, linkedId: "L");
                server.Server.Channels.OnHangup("Y", HangupCause.UserBusy);
            }
            entriesBeforeOldReturn = call.Events.Count;
        }
        finally
        {
            parked.LetGo();
            await oldRemoval.WaitAsync(SweepRig.Bound);
        }
        parked.ThrowIfFaulted();
        // OnHangup's return includes the posted removal's finally: X has left Live's departing set.
        call.Events.Count.Should().Be(entriesBeforeOldReturn, "the old handler must record nothing");
        rig.EndingsOf(call).Should().Be(0, "the old handler must notify nothing");
        if (skip == "not-connected") server.ConnectionState = AmiConnectionState.Disconnected;
        if (skip == "outcome-not-reported") server.Server.Connection.ReportsEventActionOutcome.Returns(false);
        ScopedSweepDecisionTests.Complete(server.Server).Should().BeTrue();
        var requests = server.StatusRequests;
        var markerInsideEnding = false;
        using var ending = rig.Manager.Events.OfType<CallEndedEvent>().Subscribe(e =>
        {
            if (e.SessionId == call.SessionId)
                markerInsideEnding = observedY ? !call.Metadata.ContainsKey("cause")
                    : call.Metadata.GetValueOrDefault("cause") == "reload";
        });

        await ScopedSweepDecisionTests.Pass(rig, server.Server, singleServer ? null : "a");

        // First required guard-absent red MUST land here, after the old producer was drained and real vi ran.
        rig.EndingsOf(call).Should().Be(1,
            "a complete-table reattach must not promote across the old unrecorded departure and hide the call from vi");
        pendingAfterAttach.Should().BeTrue();
        markerInsideEnding.Should().BeTrue();
        if (observedY)
        {
            call.HangupCause.Should().Be(HangupCause.UserBusy);
            call.Metadata.Should().NotContainKey("cause");
        }
        else
        {
            call.HangupCause.Should().BeNull();
            call.Metadata["cause"].Should().Be("reload");
        }
        call.Events.Count(e => e.Type == CallSessionEventType.ParticipantLeft
            && e.SourceChannel == "PJSIP/X" && e.Detail == "reload").Should().Be(1);
        call.Events.Should().NotContain(e => e.Type == CallSessionEventType.ParticipantLeft
            && e.SourceChannel == "PJSIP/X" && e.Detail == HangupCause.NormalClearing.ToString());
        server.StatusRequests.Should().Be(requests);
        ScopedSweepDecisionTests.Count(rig, "Unverifiable").Should().Be(0);
        var row = rig.Log.Entries.Last(e => e.Values.ContainsKey("Unverifiable"));
        row.Values["Verification"].Should().Be("skipped:" + skip);
        await ScopedSweepDecisionTests.Pass(rig, server.Server, singleServer ? null : "a");
        rig.EndingsOf(call).Should().Be(1);
        ScopedSweepDecisionTests.Count(rig, "Unverifiable").Should().Be(0);
        SessionIndexInvariant.Assert(rig.Manager);
    }
}
