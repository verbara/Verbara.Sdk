using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using static Verbara.Sdk.Sessions.FunctionalTests.Infrastructure.QueueFrames;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// What a queue counts when the caller's exit and the SDK's view of the queues cross: a leave the SDK never saw, a
/// reconnect reload that re-reads the queues while the caller waits or leaves, a queue snapshot older than the leave,
/// and a call the SDK took over from another node. A reload clears Live's queue table without reporting anyone leaving,
/// reads <c>Status</c> first and <c>QueueStatus</c> second, and only a snapshot that completed is evidence of who is
/// still waiting.
/// </summary>
public sealed class QueueExitAcrossReloadTests
{
    private const string Queue = "q-wait";
    private const string Caller = "1790615500.20";
    private const string CallerChannel = "PJSIP/far-000000a0";
    private const string CallerNumber = "52600";

    [Fact]
    public async Task UnseenLeave_ShouldBeCountedAbandonedAtTheHangUp_WhenTheSdkNeverSawTheCallerLeave()
    {
        await using var rig = await QueueCallRig.StartAsync();

        rig.Deliver([.. CallerQueued(), Hangup(Caller, 16)]);

        rig.Tally(Queue).Should().Be(new QueueTally(Offered: 1, Answered: 0, Abandoned: 1, TimedOut: 0, Waiting: 0),
            "the visit ended without a connection, and no leave was observed");
    }

    [Fact]
    public async Task ReconnectReload_ShouldKeepCountingTheCallerWaitingOnce_WhenTheSnapshotFindsItStillWaiting()
    {
        await using var rig = await QueueCallRig.StartAsync();

        rig.Deliver(CallerQueued());
        await rig.ReconnectAsync([CallerStatus("Queue", Queue)], [QueueParams(Queue, calls: 1), CallerEntry(wait: 3)]);

        rig.Tally(Queue).Should().Be(new QueueTally(Offered: 1, Answered: 0, Abandoned: 0, TimedOut: 0, Waiting: 1),
            "the reload found the caller still waiting in the visit the SDK holds: one waiting, not zero and not two");
    }

    [Fact]
    public async Task LeaveDuringTheReload_ShouldEndTheWait_WhenItArrivesAfterTheQueueTableWasClearedAndBeforeTheQueueSnapshot()
    {
        await using var rig = await QueueCallRig.StartAsync();

        rig.Deliver(CallerQueued());
        await rig.ReconnectAsync([CallerStatus("Wait", "30")], [QueueParams(Queue, calls: 0)],
            new ReloadScript(WhileStatusIsAsked: () => rig.Deliver(AbandonedAndLeft())));

        rig.Tally(Queue).Should().Be(new QueueTally(Offered: 1, Answered: 0, Abandoned: 1, TimedOut: 0, Waiting: 0),
            "Asterisk reported the abandon and the leave while the reload was reading Status, and the caller is still on the line");
    }

    [Fact]
    public async Task StaleQueueSnapshot_ShouldNotBringTheCallerBack_WhenItIsAppliedAfterTheCallersLeaveWasProcessed()
    {
        await using var rig = await QueueCallRig.StartAsync();

        rig.Deliver(CallerQueued());
        await rig.ReconnectAsync([CallerStatus("Queue", Queue)], [QueueParams(Queue, calls: 1), CallerEntry(wait: 3)],
            new ReloadScript(WhileQueueStatusIsAsked: () => rig.Deliver(AbandonedAndLeft())));

        using var scope = new AssertionScope();
        rig.Queued.Should().ContainSingle("the snapshot, read before the leave, reports the same visit, not a new one");
        rig.Tally(Queue).Should().Be(new QueueTally(Offered: 1, Answered: 0, Abandoned: 1, TimedOut: 0, Waiting: 0),
            "the caller left after the snapshot was asked for; the older snapshot does not make it wait again");
    }

    /// <summary>
    /// The abandon report is lost (here: withheld) and the connection reconnected between the join and the leave, so the
    /// leave cannot be read as a key exit.
    /// </summary>
    [Fact]
    public async Task LostAbandonReport_ShouldStillCountTheVisitAbandoned_WhenTheConnectionReconnectedBetweenTheJoinAndTheLeave()
    {
        await using var rig = await QueueCallRig.StartAsync();

        rig.Deliver(CallerQueued());
        await rig.ReconnectAsync([CallerStatus("Queue", Queue)], [QueueParams(Queue, calls: 1), CallerEntry(wait: 3)]);
        rig.Deliver([VarSet(Caller, CallerChannel, "ABANDONED", "TRUE"), Leave(Queue, CallerChannel, Caller), Hangup(Caller, 16)]);

        rig.Tally(Queue).Should().Be(new QueueTally(Offered: 1, Answered: 0, Abandoned: 1, TimedOut: 0, Waiting: 0),
            "the SDK lost events since the visit opened, so a leave without an abandon report is not taken for a key exit");
    }

    [Fact]
    public async Task ReconstructedSession_ShouldCountNothing_WhenItHasAQueueButNoVisitStartAndAsteriskReportsATimeout()
    {
        await using var rig = await QueueCallRig.StartAsync();
        var session = new CallSession("reconstructed-1", Caller, QueueCallRig.ServerId, CallDirection.Inbound) { QueueName = Queue };
        session.AddParticipant(new SessionParticipant
        {
            UniqueId = Caller, Channel = CallerChannel, Technology = "PJSIP", Role = ParticipantRole.Caller,
        });
        rig.Manager.RegisterReconstructedSession(session).Should().BeTrue("premise: the call is new to this manager");

        rig.Deliver([.. AbandonedAndLeft(), VarSet(Caller, CallerChannel, "QUEUESTATUS", "TIMEOUT"), Hangup(Caller, 16)]);

        rig.Tally(Queue).Should().Be(new QueueTally(Offered: 0, Answered: 0, Abandoned: 0, TimedOut: 0, Waiting: 0),
            "a session taken over from another node carries its queue's name but no visit this manager opened");
    }

    [Fact]
    public async Task CompleteQueueSnapshot_ShouldEndTheWaitAndCountTheVisitAbandonedBeforeTheHangUp_WhenItNoLongerListsACallerWhoLeftDuringTheOutage()
    {
        await using var rig = await QueueCallRig.StartAsync();

        rig.Deliver(CallerQueued());
        // The caller's abandon and leave fall in the outage: the SDK never receives them.
        await rig.ReconnectAsync([CallerStatus("Wait", "30")], [QueueParams(Queue, calls: 0)]);
        var beforeHangup = rig.Tally(Queue);
        rig.Deliver([Hangup(Caller, 16)]);

        using var scope = new AssertionScope();
        beforeHangup.Should().Be(new QueueTally(Offered: 1, Answered: 0, Abandoned: 1, TimedOut: 0, Waiting: 0),
            "the reload's queue snapshot completed without the caller, who is still on the line in the dialplan");
        rig.Tally(Queue).Should().Be(new QueueTally(Offered: 1, Answered: 0, Abandoned: 1, TimedOut: 0, Waiting: 0),
            "the hang-up counts nothing twice");
    }

    [Fact]
    public async Task IncompleteQueueSnapshot_ShouldEndNoWait_WhenItWasCutOffBeforeItCompleted()
    {
        await using var rig = await QueueCallRig.StartAsync();

        rig.Deliver(CallerQueued());
        await rig.ReconnectAsync([CallerStatus("Wait", "30")], [QueueParams(Queue, calls: 0)],
            new ReloadScript(CutQueueStatusOff: true));
        var beforeHangup = rig.Tally(Queue);
        rig.Deliver([Hangup(Caller, 16)]);

        using var scope = new AssertionScope();
        beforeHangup.Should().Be(new QueueTally(Offered: 1, Answered: 0, Abandoned: 0, TimedOut: 0, Waiting: 1),
            "a snapshot that did not complete is no evidence that the caller left");
        rig.Tally(Queue).Should().Be(new QueueTally(Offered: 1, Answered: 0, Abandoned: 1, TimedOut: 0, Waiting: 0),
            "the visit ends at the hang-up, as one whose leave the SDK did not see");
    }

    [Fact]
    public async Task QueueSnapshot_ShouldNotEndTheWaitOfACaller_WhoJoinedAfterTheSnapshotWasAskedFor()
    {
        await using var rig = await QueueCallRig.StartAsync();
        const string late = "1790615500.30";
        const string lateChannel = "PJSIP/far-000000b0";

        await rig.ReconnectAsync([], [QueueParams(Queue, calls: 0)],
            new ReloadScript(WhileQueueStatusIsAsked: () => rig.Deliver([
                NewChannel(late, lateChannel, "4", late, "52601", "from-pstn", "5011"), NewState(late, "6"),
                Join(Queue, lateChannel, late, "52601")])));

        rig.Tally(Queue).Should().Be(new QueueTally(Offered: 1, Answered: 0, Abandoned: 0, TimedOut: 0, Waiting: 1),
            "the snapshot is older than the join: its silence about the caller says nothing about it");
    }

    // --- The frames ---------------------------------------------------------------------------

    private static ManagerEvent[] CallerQueued() =>
    [
        NewChannel(Caller, CallerChannel, "4", Caller, CallerNumber, "from-pstn", "5011"),
        NewState(Caller, "6"),
        Join(Queue, CallerChannel, Caller, CallerNumber),
    ];

    private static ManagerEvent[] AbandonedAndLeft() =>
    [
        VarSet(Caller, CallerChannel, "ABANDONED", "TRUE"),
        Abandon(Queue, CallerChannel, Caller, holdTime: 3),
        Leave(Queue, CallerChannel, Caller),
    ];

    /// <summary>The caller's channel in a <c>Status</c> answer: up, and running <paramref name="application"/>.</summary>
    private static StatusEvent CallerStatus(string application, string data) =>
        Status(Caller, CallerChannel, Caller, "6", CallerNumber, "from-pstn", "5011", application, data);

    private static QueueEntryEvent CallerEntry(long wait) => Entry(Queue, CallerChannel, Caller, CallerNumber, wait);
}
