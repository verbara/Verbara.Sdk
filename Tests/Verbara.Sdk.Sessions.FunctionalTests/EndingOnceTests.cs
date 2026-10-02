using FluentAssertions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// Binds that a call's ending is reported once and queued for release once, however many times its
/// participants are later found to have all left.
///
/// <para>Three routes bring a leg carrying an ended call's <c>linkedid</c> to the manager after the
/// call ended: a leg that arrives with that <c>linkedid</c> and then hangs up; a reconnect reload that
/// ends a call and admits, from the same snapshot, a leg of it the SDK never saw, which then hangs up;
/// and such a leg still up when the ended call is released, hanging up afterwards. The leg opens a
/// call of its own — an ended call's state is terminal, so it could never report that leg's ending —
/// and its hangup ends that call. None of them may deliver the ended call's ending again: a second
/// <see cref="CallEndedEvent"/>, a second queue entry, and — because the agent statistics count on
/// that event — a second handled call for the agent.</para>
///
/// <para>The guards pass today and must keep passing: a normal hangup, a duplicate <c>Hangup</c>, a
/// reload's ending followed by late hangups, and a call the timeout sweep made terminal while its
/// legs were still up — which has had no ending delivered and must get exactly one when they leave,
/// so the once-only rule cannot be keyed on the call's state.</para>
/// </summary>
public sealed class EndingOnceTests
{
    // --- the routes that once delivered an ending twice -------------------------------------------

    [Fact]
    public async Task CallEnded_ShouldBeRaisedAndQueuedOnce_WhenALegReusingTheEndedCallsLinkedIdHangsUp()
    {
        await using var rig = new ResidencyRig();
        var call = rig.Call("g");

        rig.LegJoins("x-g", "g");
        var lateCall = rig.Manager.GetByChannelId("x-g")?.SessionId ?? string.Empty;
        lateCall.Should().NotBeEmpty().And.NotBe(call.SessionId,
            "premise: a leg carrying an ended call's linkedid opens a call of its own while the ended call is retained");
        rig.LegLeaves("x-g");

        new
        {
            Endings = rig.EndingsFor(call.SessionId),
            QueueEntries = rig.QueueEntriesFor(call.SessionId),
            LateCallEndings = rig.EndingsFor(lateCall),
            LateCallQueueEntries = rig.QueueEntriesFor(lateCall),
        }.Should().BeEquivalentTo(
                new { Endings = 1, QueueEntries = 1, LateCallEndings = 1, LateCallQueueEntries = 1 },
                "the call ended once; a leg that arrived afterwards with its linkedid and left ends its own "
                + $"call, once, and not the ended one again. Measured: {rig.Describe()}");
    }

    [Fact]
    public async Task CallEnded_ShouldBeRaisedAndQueuedOnce_WhenAReloadEndedTheCallAndAnUnseenLegItAdmittedHangsUp()
    {
        // This route's second ending is a regression of the reconnect-reload change (#315), not a
        // released behaviour: before it, the reload did not end the call at all.
        await using var rig = new ResidencyRig();
        await rig.StartAsync();
        var call = rig.OpenAnsweredCall("f");

        await rig.ReconnectAsync(ResidencyRig.StatusLeg("x-f", "f"));
        var afterReload = new { Endings = rig.EndingsFor(call.SessionId), QueueEntries = rig.QueueEntriesFor(call.SessionId) };
        var unseenLegsCall = rig.Manager.GetByChannelId("x-f")?.SessionId;
        new { call.State, OpenedItsOwnCall = unseenLegsCall is not null && unseenLegsCall != call.SessionId }
            .Should().BeEquivalentTo(
                new { State = CallSessionState.Completed, OpenedItsOwnCall = true },
                $"premise: the reload ended the call and the unseen leg it admitted opened a call of its own. Measured: {rig.Describe()}");
        rig.LegLeaves("x-f");

        new
        {
            Endings = rig.EndingsFor(call.SessionId),
            QueueEntries = rig.QueueEntriesFor(call.SessionId),
            UnseenLegsCallEndings = rig.EndingsFor(unseenLegsCall!),
        }.Should().BeEquivalentTo(
                new { Endings = 1, QueueEntries = 1, UnseenLegsCallEndings = 1 },
                $"the reload delivered the call's ending (after it: {afterReload.Endings} endings, {afterReload.QueueEntries} queue entries); the unseen leg's "
                + $"hangup ends its own call and does not deliver the ended call's again. Measured: {rig.Describe()}");
    }

    [Fact]
    public async Task CallEnded_ShouldNotBeRaisedOrHeldAgain_WhenALegReusingTheEndedCallsLinkedIdLeavesAfterItsRelease()
    {
        await using var rig = new ResidencyRig();
        var call = rig.Call("v");
        rig.LegJoins("x-v", "v");
        var lateCall = rig.Manager.GetByChannelId("x-v")?.SessionId ?? string.Empty;
        lateCall.Should().NotBeEmpty().And.NotBe(call.SessionId,
            "premise: the leg opened a call of its own while the ended call was retained");

        rig.MovePastRetention();
        rig.Call("n");
        rig.Manager.GetById(call.SessionId).Should().BeNull(
            $"premise: the call was released while the leg was still up. Measured: {rig.Describe()}");

        rig.LegLeaves("x-v");

        new
        {
            Endings = rig.EndingsFor(call.SessionId),
            QueueEntries = rig.QueueEntriesFor(call.SessionId),
            HeldById = rig.Manager.GetById(call.SessionId) is not null,
            HeldByLinkedId = rig.Manager.GetByLinkedId(ResidencyRig.LinkedIdOf("v"))?.SessionId == call.SessionId,
        }.Should().BeEquivalentTo(
            new { Endings = 1, QueueEntries = 0, HeldById = false, HeldByLinkedId = false },
            "a call already ended and released is not ended again, nor queued again, by a leg that "
            + $"outlived it. Measured: {rig.Describe()}");
    }

    [Fact]
    public async Task AgentStatistics_ShouldCountTheCallOnce_WhenALegReusingItsLinkedIdHangsUpAfterItEnded()
    {
        await using var rig = new ResidencyRig();
        using var tracker = new AgentSessionTracker(rig.Manager, Microsoft.Extensions.Options.Options.Create(rig.Options));
        rig.Server.Agents.OnAgentLogin("agent-1", "PJSIP/100");
        var call = rig.OpenAnsweredCall("g");
        rig.Server.Agents.OnAgentConnect("agent-1", "PJSIP/100", linkedId: ResidencyRig.LinkedIdOf("g"),
            memberInterface: "PJSIP/100");

        // A fixed 30 s of talk, so a second count shows whatever the wall clock did between the
        // answer and the hangup.
        call.ConnectedAt = call.ConnectedAt!.Value - TimeSpan.FromSeconds(30);
        rig.HangUp("g");

        var agent = tracker.GetByAgentId("agent-1")!;
        var talkTime = call.TalkTime!.Value;
        new { agent.CallsHandled, agent.TotalTalkTime }.Should().BeEquivalentTo(
            new { CallsHandled = 1, TotalTalkTime = talkTime },
            "premise: the tracker counted the call when it ended");

        rig.LegJoins("x-g", "g");
        rig.LegLeaves("x-g");

        new { agent.CallsHandled, agent.TotalTalkTime }.Should().BeEquivalentTo(
            new { CallsHandled = 1, TotalTalkTime = talkTime },
            "the agent handled one call with one talk time; a leg reusing its linkedid after it ended does "
            + $"not make it two. Measured: {rig.Describe()}");
    }

    // --- guards: routes that deliver one ending today --------------------------------------------

    [Fact]
    public async Task CallEnded_ShouldBeRaisedAndQueuedOnce_WhenBothLegsHangUp()
    {
        await using var rig = new ResidencyRig();
        var call = rig.Call("a");

        new { Endings = rig.EndingsFor(call.SessionId), QueueEntries = rig.QueueEntriesFor(call.SessionId) }
            .Should().BeEquivalentTo(
                new { Endings = 1, QueueEntries = 1 },
                $"one call, one ending. Measured: {rig.Describe()}");
    }

    [Fact]
    public async Task CallEnded_ShouldBeRaisedAndQueuedOnce_WhenEachLegsHangupArrivesTwice()
    {
        await using var rig = new ResidencyRig();
        var call = rig.Call("b");
        rig.HangUp("b");

        new { Endings = rig.EndingsFor(call.SessionId), QueueEntries = rig.QueueEntriesFor(call.SessionId) }
            .Should().BeEquivalentTo(
                new { Endings = 1, QueueEntries = 1 },
                $"a repeated Hangup is about a channel that is already gone. Measured: {rig.Describe()}");
    }

    [Fact]
    public async Task CallEnded_ShouldBeRaisedAndQueuedOnce_WhenAReloadEndedTheCallAndItsHangupsArriveLate()
    {
        await using var rig = new ResidencyRig();
        await rig.StartAsync();
        var call = rig.OpenAnsweredCall("c");

        await rig.ReconnectAsync();
        call.State.Should().Be(CallSessionState.Completed,
            $"premise: the reload found neither leg and ended the call. Measured: {rig.Describe()}");
        rig.HangUp("c");

        new { Endings = rig.EndingsFor(call.SessionId), QueueEntries = rig.QueueEntriesFor(call.SessionId) }
            .Should().BeEquivalentTo(
                new { Endings = 1, QueueEntries = 1 },
                $"the reload delivered the ending; late hangups do not add one. Measured: {rig.Describe()}");
    }

    [Fact]
    public async Task CallEnded_ShouldBeRaisedAndQueuedOnce_WhenTheTimeoutSweepEndedTheCallWhileItsLegsWereUp()
    {
        await using var rig = new ResidencyRig();
        rig.Server.Channels.OnNewChannel("c-i", "PJSIP/trunk-c-i", ChannelState.Ring,
            context: "from-trunk", linkedId: ResidencyRig.LinkedIdOf("i"));
        rig.Server.Channels.OnNewChannel("a-i", "PJSIP/100-a-i", ChannelState.Ring,
            linkedId: ResidencyRig.LinkedIdOf("i"));
        rig.Server.Channels.OnDialBegin("c-i", "a-i", "PJSIP/100-a-i", null);
        var call = rig.Manager.GetByLinkedId(ResidencyRig.LinkedIdOf("i"))!;

        SessionReconciler.TryMarkTimedOut(call).Should().BeTrue(
            "premise: the sweep moves a call still dialling to TimedOut");
        rig.HangUp("i");

        new { Endings = rig.EndingsFor(call.SessionId), QueueEntries = rig.QueueEntriesFor(call.SessionId) }
            .Should().BeEquivalentTo(
                new { Endings = 1, QueueEntries = 1 },
                "the sweep made the call terminal without delivering its ending, so it is delivered "
                + "once when the legs leave — the once-only rule is about delivery, not about the "
                + $"state the call was already in. Measured: {rig.Describe()}");
    }
}
