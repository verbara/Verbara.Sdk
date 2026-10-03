using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using static Verbara.Sdk.Sessions.FunctionalTests.Infrastructure.QueueFrames;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// The session manager marks a call's current queue visit left when Asterisk reports the caller leaving that visit's
/// queue. Each test delivers Asterisk's own <c>QueueCallerLeave</c> frame to the server's event observer, so the mark
/// is read from the report it names, whichever internal path carries it.
/// </summary>
public sealed class QueueVisitLeftMarkTests
{
    private const string Caller = "1790615600.1";
    private const string CallerChannel = "PJSIP/far-000000c0";

    [Fact]
    public async Task QueueCallerLeave_ShouldMarkTheVisitLeft_WhenAsteriskReportsTheCallerLeavingItsQueue()
    {
        await using var rig = await QueueCallRig.StartAsync();
        rig.Deliver([NewChannel(Caller, CallerChannel, "4", Caller, "100"), Join("sales", CallerChannel, Caller, "100")]);
        var session = rig.Manager.GetByChannelId(Caller)!;
        session.QueueVisitLeft.Should().BeFalse("premise: a visit the manager opens starts not left");

        rig.Deliver([Leave("sales", CallerChannel, Caller)]);

        session.QueueVisitLeft.Should().BeTrue("Asterisk reported the caller leaving the queue of its visit");
    }

    [Fact]
    public async Task QueueCallerLeave_ShouldNotMarkTheVisitLeft_WhenTheLeaveIsForAnotherQueue()
    {
        await using var rig = await QueueCallRig.StartAsync();
        rig.Deliver([NewChannel(Caller, CallerChannel, "4", Caller, "100"), Join("first", CallerChannel, Caller, "100"),
            Join("second", CallerChannel, Caller, "100")]);

        rig.Deliver([Leave("first", CallerChannel, Caller)]);

        rig.Manager.GetByChannelId(Caller)!.QueueVisitLeft.Should().BeFalse(
            "the leave is for a queue the call's current visit is not in");
    }

    /// <summary>
    /// Asterisk renamed the caller's channel before it joined the queue (a masquerade, or a technology rename), so the
    /// queue's reports name the channel by a name the call's participant never carried. They carry its Uniqueid, which
    /// a rename does not change.
    /// </summary>
    [Fact]
    public async Task QueueJoinAndLeave_ShouldFindTheCallByItsUniqueid_WhenTheChannelWasRenamedBeforeItJoined()
    {
        const string renamed = "PJSIP/far-000000c0<MASQ>";
        await using var rig = await QueueCallRig.StartAsync();
        rig.Deliver([NewChannel(Caller, CallerChannel, "4", Caller, "100"), NewState(Caller, "6"),
            new RenameEvent
            {
                EventType = "Rename", UniqueId = Caller,
                RawFields = new Dictionary<string, string> { ["Channel"] = CallerChannel, ["Newname"] = renamed, ["Uniqueid"] = Caller },
            },
            Join("sales", renamed, Caller, "100")]);
        var queuedAfterTheJoin = rig.Queued.Count;

        rig.Deliver([Abandon("sales", renamed, Caller, 3), Leave("sales", renamed, Caller)]);

        using var scope = new AssertionScope();
        queuedAfterTheJoin.Should().Be(1, "the join names the renamed channel, and carries the caller's Uniqueid");
        rig.Manager.GetByChannelId(Caller)!.QueueVisitLeft.Should().BeTrue("the leave is found by the same Uniqueid");
        rig.Tally("sales").Should().Be(new QueueTally(Offered: 1, Answered: 0, Abandoned: 1, TimedOut: 0, Waiting: 0));
    }

    [Fact]
    public async Task QueueCallerLeave_ShouldNotMarkTheVisitLeft_WhenTheManagerWasDetachedFromTheServer()
    {
        await using var rig = await QueueCallRig.StartAsync();
        rig.Deliver([NewChannel(Caller, CallerChannel, "4", Caller, "100"), Join("sales", CallerChannel, Caller, "100")]);
        var session = rig.Manager.GetByChannelId(Caller)!;
        rig.Manager.DetachFromServer(QueueCallRig.ServerId);

        rig.Deliver([Leave("sales", CallerChannel, Caller)]);

        session.QueueVisitLeft.Should().BeFalse("a detached manager no longer hears the server's queue departures");
    }
}
