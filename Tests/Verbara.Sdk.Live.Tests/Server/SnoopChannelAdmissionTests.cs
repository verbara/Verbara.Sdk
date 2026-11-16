using FluentAssertions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// Existing-call admission walks every channel the attached server holds. A snoop among them is a tap on a call, not a
/// call: it opens nothing and joins nothing, and the walk leaves it out of the indexes, so its hangup leaves nothing
/// behind. The snoop carries the shape Asterisk gives a real ARI snoop (<c>Snoop/&lt;target uniqueid&gt;-&lt;n&gt;</c>
/// and a linkedid of its own); the joining case gives it the call's linkedid instead, which reaches the call by that
/// linkedid alone.
/// </summary>
public sealed class SnoopChannelAdmissionTests
{
    private const string SnoopName = "Snoop/X-00000000";

    [Fact]
    public async Task AdmitExistingCalls_ShouldOpenNoCallForASnoop_WhenTheSnoopCarriesALinkedIdOfItsOwn()
    {
        await using var rig = await AdmissionRig.CreateAsync();
        rig.Add("X");
        AddSnoop(rig, "S1", linkedId: "S1");
        rig.Attach();
        await rig.Manager.AdmitExistingCalls("server");

        rig.Manager.ActiveSessions.Should().ContainSingle().Which.LinkedId.Should().Be("L");
        rig.Manager.GetByChannelId("server", "S1").Should().BeNull();
        rig.Manager.GetByLinkedId("server", "S1").Should().BeNull();
        rig.Events.OfType<CallStartedEvent>().Should().ContainSingle();
        rig.AssertIndexes();

        rig.Hangup("S1"); rig.Hangup("X");
        rig.Manager.ActiveSessions.Should().BeEmpty();
        rig.Events.OfType<CallEndedEvent>().Should().ContainSingle();
        rig.AssertIndexes();
    }

    [Fact]
    public async Task AdmitExistingCalls_ShouldJoinNoSnoopToTheCall_WhenTheSnoopCarriesTheCallsLinkedId()
    {
        await using var rig = await AdmissionRig.CreateAsync();
        rig.Add("X"); rig.Add("Y");
        AddSnoop(rig, "S1", linkedId: "L");
        rig.Attach();
        await rig.Manager.AdmitExistingCalls("server");

        var call = rig.Manager.GetByLinkedId("server", "L")!;
        rig.Manager.ActiveSessions.Should().ContainSingle().Which.Should().BeSameAs(call);
        call.Participants.Select(p => p.UniqueId).Should().BeEquivalentTo(["X", "Y"]);
        call.Events.Should().NotContain(e => e.SourceChannel == SnoopName || e.TargetChannel == SnoopName);
        rig.Manager.GetByChannelId("server", "S1").Should().BeNull();
        rig.AssertIndexes();

        rig.Hangup("S1");
        rig.Manager.ActiveSessions.Should().ContainSingle().Which.Should().BeSameAs(call);
        rig.Ends(call).Should().Be(0);
        rig.Hangup("X"); rig.Hangup("Y");
        rig.Ends(call).Should().Be(1);
        rig.Manager.ActiveSessions.Should().BeEmpty();
        rig.AssertIndexes();
    }

    [Fact]
    public async Task AdmitExistingCalls_ShouldOpenACallForAnOrdinaryLeg_WhenItCarriesALinkedIdOfItsOwn()
    {
        // Positive control for the first test: the same shape with an ordinary channel name is a call of its own.
        await using var rig = await AdmissionRig.CreateAsync();
        rig.Add("X"); rig.Add("S1", linked: "S1");
        rig.Attach();
        await rig.Manager.AdmitExistingCalls("server");

        rig.Manager.ActiveSessions.Should().HaveCount(2);
        rig.Manager.GetByChannelId("server", "S1").Should().NotBeNull();
        rig.Events.OfType<CallStartedEvent>().Should().HaveCount(2);
        rig.Hangup("S1"); rig.Hangup("X");
        rig.Manager.ActiveSessions.Should().BeEmpty();
        rig.AssertIndexes();
    }

    [Fact]
    public async Task OnChannelRemoved_ShouldEndAReconstructedSnoopSession_WhenItsSnoopHangsUp()
    {
        // An earlier version wrote a session for a snoop to the store. Restored, it holds the snoop as a participant, so
        // the snoop is indexed by its channel id; its hangup must still end that session, as it did before.
        await using var rig = await AdmissionRig.CreateAsync();
        var restored = new CallSession("restored-snoop", "S1", "server", CallDirection.Inbound);
        restored.AddParticipant(new SessionParticipant
        {
            UniqueId = "S1", Channel = SnoopName, Technology = "Snoop", Role = ParticipantRole.Caller,
            JoinedAt = DateTimeOffset.UtcNow
        });
        rig.Manager.RegisterReconstructedSession(restored).Should().BeTrue();
        rig.Add("X");
        AddSnoop(rig, "S1", linkedId: "S1");
        rig.Attach();
        rig.Manager.GetByChannelId("server", "S1").Should().BeSameAs(restored);

        rig.Hangup("S1");
        rig.Ends(restored).Should().Be(1);
        rig.Manager.GetByChannelId("server", "S1").Should().BeNull();
        rig.Manager.ActiveSessions.Should().NotContain(restored);
        rig.AssertIndexes();
    }

    private static void AddSnoop(AdmissionRig rig, string uniqueId, string linkedId) =>
        rig.Run.Server.Channels.OnNewChannel(uniqueId, SnoopName, ChannelState.Up, linkedId: linkedId);
}
