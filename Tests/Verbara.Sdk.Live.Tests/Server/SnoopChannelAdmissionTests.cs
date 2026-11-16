using FluentAssertions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// Existing-call admission walks every channel the attached server holds. A snoop among them is a tap on a call, not a
/// call: it opens nothing and joins nothing, and the walk leaves it out of the indexes, so its hangup leaves nothing
/// behind. The snoop carries the shape Asterisk gives a real ARI snoop (<c>Snoop/&lt;target uniqueid&gt;-&lt;n&gt;</c>
/// and a linkedid of its own); the joining case gives it the call's linkedid instead, which reaches the call by that
/// linkedid alone. The ending-order tests at the end run on the three routes a call reaches the manager by (a live
/// arrival after attach, existing-call admission, a reconstructed registration): a snoop carrying the call's linkedid
/// never ends the call early and never keeps it open after its last real leg leaves, whatever order the hangups take.
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

    [Fact]
    public async Task AdmitExistingCalls_ShouldEndTheCallFromItsLegs_WhenTheyHangUpWhileASnoopCarryingItsLinkedIdIsStillHeld()
    {
        // The order Asterisk gives a real snoop: it ends after the channel it taps.
        await using var rig = await AdmissionRig.CreateAsync();
        rig.Add("X"); rig.Add("Y");
        AddSnoop(rig, "S1", linkedId: "L");
        rig.Attach();
        await rig.Manager.AdmitExistingCalls("server");
        var call = rig.Manager.GetByLinkedId("server", "L")!;

        rig.Hangup("X"); rig.Hangup("Y");
        rig.Ends(call).Should().Be(1, "the call's real legs are gone; the held snoop is not one of its channels");
        rig.Manager.ActiveSessions.Should().BeEmpty();
        rig.Hangup("S1");
        rig.Ends(call).Should().Be(1);
        rig.Events.Count(e => e is CallEndedEvent or CallFailedEvent).Should().Be(1);
        rig.AssertIndexes();
    }

    [Fact]
    public async Task OnChannelRemoved_ShouldEndAReconstructedSnoopSession_WhenItIsRegisteredAfterAttachAndItsSnoopHangsUp()
    {
        // The other order of the restore: the host attaches first and registers the stored session afterwards.
        await using var rig = await AdmissionRig.CreateAsync();
        rig.Add("X");
        AddSnoop(rig, "S1", linkedId: "S1");
        rig.Attach();
        var restored = RestoredSnoopSession("restored-snoop", "S1");
        rig.Manager.RegisterReconstructedSession(restored).Should().BeTrue();
        rig.Manager.GetByChannelId("server", "S1").Should().BeSameAs(restored);

        rig.Hangup("S1");
        rig.Ends(restored).Should().Be(1);
        rig.Manager.GetByChannelId("server", "S1").Should().BeNull();
        rig.Manager.ActiveSessions.Should().NotContain(restored);
        rig.AssertIndexes();
    }

    /// <summary>
    /// A mixed restored call: 2.10.0 joined a snoop carrying a call's linkedid to that call and stored it so. Restored,
    /// the snoop is one of the call's own participants, so its hangup is recorded on that call like any participant's
    /// (the third requirement, not the second: the second is about calls a snoop is not a participant of), and the call
    /// ends once its last participant, snoop or leg, has left.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnChannelRemoved_ShouldEndAMixedRestoredCallAtItsLastParticipant_WhenItHoldsLegsAndASnoop(bool snoopFirst)
    {
        await using var rig = await AdmissionRig.CreateAsync();
        rig.Add("X"); rig.Add("Y");
        AddSnoop(rig, "S1", linkedId: "L");
        rig.Attach();
        var restored = new CallSession("restored-mixed", "L", "server", CallDirection.Inbound);
        foreach (var (id, name, role) in new[]
                 {
                     ("X", "PJSIP/X", ParticipantRole.Caller), ("Y", "PJSIP/Y", ParticipantRole.Agent),
                     ("S1", SnoopName, ParticipantRole.Agent),
                 })
            restored.AddParticipant(new SessionParticipant
            {
                UniqueId = id, Channel = name, Technology = TechnologyOf(name), Role = role,
                JoinedAt = DateTimeOffset.UtcNow
            });
        rig.Manager.RegisterReconstructedSession(restored).Should().BeTrue();
        rig.Manager.GetByChannelId("server", "S1").Should().BeSameAs(restored);

        if (snoopFirst)
        {
            rig.Hangup("S1");
            rig.Ends(restored).Should().Be(0, "its legs X and Y are still up");
            restored.Participants.Single(p => p.UniqueId == "S1").LeftAt.Should().NotBeNull(
                "the snoop is this call's own participant, so its departure is recorded on it");
            rig.Hangup("X"); rig.Hangup("Y");
        }
        else
        {
            rig.Hangup("X"); rig.Hangup("Y");
            rig.Ends(restored).Should().Be(0, "the snoop is still one of this call's present participants");
            rig.Hangup("S1");
        }

        rig.Ends(restored).Should().Be(1);
        rig.Manager.ActiveSessions.Should().BeEmpty();
        rig.AssertIndexes();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RegisterReconstructedSession_ShouldEndTheRestoredCallFromItsLegs_WhenTheyHangUpWhileASnoopCarryingItsLinkedIdIsStillHeld(
        bool registerBeforeAttach)
    {
        // A restored call that holds only its real legs, while a snoop carrying its linkedid is still held: the
        // registration walk does not take the snoop in, and the legs' hangups end the call without waiting for it.
        await using var rig = await AdmissionRig.CreateAsync();
        rig.Add("X"); rig.Add("Y");
        AddSnoop(rig, "S1", linkedId: "L");
        var restored = new CallSession("restored-legs", "L", "server", CallDirection.Inbound);
        foreach (var (id, role) in new[] { ("X", ParticipantRole.Caller), ("Y", ParticipantRole.Agent) })
            restored.AddParticipant(new SessionParticipant
            {
                UniqueId = id, Channel = "PJSIP/" + id, Technology = "PJSIP", Role = role, JoinedAt = DateTimeOffset.UtcNow
            });
        if (registerBeforeAttach) rig.Manager.RegisterReconstructedSession(restored).Should().BeTrue();
        rig.Attach();
        if (!registerBeforeAttach) rig.Manager.RegisterReconstructedSession(restored).Should().BeTrue();
        await rig.Manager.AdmitExistingCalls("server");
        restored.Participants.Select(p => p.UniqueId).Should().BeEquivalentTo(["X", "Y"]);

        rig.Hangup("X");
        Endings(rig).Should().Be(0, "leg Y is still up");
        rig.Hangup("Y");
        rig.Ends(restored).Should().Be(1, "the restored call's real legs are gone; the held snoop is not one of its channels");
        rig.Hangup("S1");
        Endings(rig).Should().Be(1);
        rig.Manager.ActiveSessions.Should().BeEmpty();
        rig.AssertIndexes();
    }

    [Fact]
    public async Task OnChannelRemoved_ShouldKeepTheCallOpen_WhenAThirdLegIsStillUpAfterTwoLegsAndTheSnoopLeave()
    {
        await using var rig = await LiveRig();
        rig.Add("X"); rig.Add("Y"); rig.Add("Z");
        AddSnoop(rig, "S1", linkedId: "L");
        var call = rig.Manager.GetByLinkedId("server", "L")!;

        rig.Hangup("X"); rig.Hangup("Y");
        Endings(rig).Should().Be(0, "leg Z is still up");
        rig.Hangup("S1");
        Endings(rig).Should().Be(0, "leg Z is still up after the snoop leaves");
        rig.Manager.ActiveSessions.Should().ContainSingle().Which.Should().BeSameAs(call);
        rig.Hangup("Z");
        rig.Ends(call).Should().Be(1);
        Endings(rig).Should().Be(1);
        rig.AssertIndexes();
    }

    [Fact]
    public async Task AdmitExistingCalls_ShouldKeepTheCallOpen_WhenAThirdLegIsStillUpAfterTwoLegsAndASnoopLeave()
    {
        await using var rig = await AdmissionRig.CreateAsync();
        rig.Add("X"); rig.Add("Y"); rig.Add("Z");
        AddSnoop(rig, "S1", linkedId: "L");
        AddSnoop(rig, "S2", linkedId: "L", name: "Snoop/Z-00000001");
        rig.Attach();
        await rig.Manager.AdmitExistingCalls("server");
        var call = rig.Manager.GetByLinkedId("server", "L")!;

        rig.Hangup("X"); rig.Hangup("S1"); rig.Hangup("Y");
        Endings(rig).Should().Be(0, "leg Z is still up");
        rig.Hangup("Z");
        rig.Ends(call).Should().Be(1, "the last real leg is gone; snoop S2 on it is still held");
        rig.Hangup("S2");
        Endings(rig).Should().Be(1);
        rig.Manager.ActiveSessions.Should().BeEmpty();
        rig.AssertIndexes();
    }

    [Fact]
    public async Task OnChannelRemoved_ShouldEndTheCallAtItsLastLeg_WhenTheLegsAndTheSnoopHangUpInterleaved()
    {
        await using var rig = await LiveRig();
        rig.Add("X"); rig.Add("Y");
        AddSnoop(rig, "S1", linkedId: "L");
        var call = rig.Manager.GetByLinkedId("server", "L")!;

        rig.Hangup("X");
        Endings(rig).Should().Be(0);
        rig.Hangup("S1");
        Endings(rig).Should().Be(0, "leg Y is still up");
        rig.Hangup("Y");
        rig.Ends(call).Should().Be(1);
        Endings(rig).Should().Be(1);
        rig.Manager.ActiveSessions.Should().BeEmpty();
        rig.AssertIndexes();
    }

    [Fact]
    public async Task OnChannelRemoved_ShouldEndTheCallFromItsLegs_WhenTwoSnoopsCarryingItsLinkedIdAreStillHeld()
    {
        await using var rig = await LiveRig();
        rig.Add("X"); rig.Add("Y");
        AddSnoop(rig, "S1", linkedId: "L");
        AddSnoop(rig, "S2", linkedId: "L", name: "Snoop/Y-00000001");
        var call = rig.Manager.GetByLinkedId("server", "L")!;

        rig.Hangup("X"); rig.Hangup("Y");
        rig.Ends(call).Should().Be(1, "neither held snoop is one of the call's channels");
        rig.Hangup("S1"); rig.Hangup("S2");
        Endings(rig).Should().Be(1);
        rig.Manager.ActiveSessions.Should().BeEmpty();
        rig.AssertIndexes();
    }

    [Fact]
    public async Task OnChannelRemoved_ShouldEndTheCallAtItsLastLeg_WhenOneOfTwoSnoopsHangsUpFirst()
    {
        await using var rig = await LiveRig();
        rig.Add("X"); rig.Add("Y");
        AddSnoop(rig, "S1", linkedId: "L");
        AddSnoop(rig, "S2", linkedId: "L", name: "Snoop/Y-00000001");
        var call = rig.Manager.GetByLinkedId("server", "L")!;

        rig.Hangup("S1");
        Endings(rig).Should().Be(0);
        rig.Hangup("X");
        Endings(rig).Should().Be(0, "leg Y is still up");
        rig.Hangup("Y");
        rig.Ends(call).Should().Be(1, "the last real leg is gone; snoop S2 is still held");
        rig.Hangup("S2");
        Endings(rig).Should().Be(1);
        rig.Manager.ActiveSessions.Should().BeEmpty();
        rig.AssertIndexes();
    }

    [Fact]
    public async Task OnChannelRemoved_ShouldEndTheCallFromItsLegs_WhenTheSnoopArrivedBeforeTheLegs()
    {
        await using var rig = await LiveRig();
        AddSnoop(rig, "S1", linkedId: "L");
        rig.Add("X"); rig.Add("Y");
        var call = rig.Manager.GetByLinkedId("server", "L")!;
        call.Participants.Select(p => p.UniqueId).Should().BeEquivalentTo(["X", "Y"]);

        rig.Hangup("X"); rig.Hangup("Y");
        rig.Ends(call).Should().Be(1);
        rig.Hangup("S1");
        Endings(rig).Should().Be(1);
        rig.Manager.ActiveSessions.Should().BeEmpty();
        rig.AssertIndexes();
    }

    /// <summary>The live route: attach to an empty server, so every channel after it arrives as a live Newchannel.</summary>
    private static async Task<AdmissionRig> LiveRig()
    {
        var rig = await AdmissionRig.CreateAsync();
        rig.Attach();
        await rig.Manager.AdmitExistingCalls("server");
        return rig;
    }

    private static int Endings(AdmissionRig rig) => rig.Events.Count(e => e is CallEndedEvent or CallFailedEvent);

    private static string TechnologyOf(string channel) => channel[..channel.IndexOf('/', StringComparison.Ordinal)];

    private static CallSession RestoredSnoopSession(string sessionId, string snoopId)
    {
        var restored = new CallSession(sessionId, snoopId, "server", CallDirection.Inbound);
        restored.AddParticipant(new SessionParticipant
        {
            UniqueId = snoopId, Channel = SnoopName, Technology = "Snoop", Role = ParticipantRole.Caller,
            JoinedAt = DateTimeOffset.UtcNow
        });
        return restored;
    }

    private static void AddSnoop(AdmissionRig rig, string uniqueId, string linkedId, string name = SnoopName) =>
        rig.Run.Server.Channels.OnNewChannel(uniqueId, name, ChannelState.Up, linkedId: linkedId);
}
