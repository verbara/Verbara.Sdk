using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using static Verbara.Sdk.Sessions.FunctionalTests.Infrastructure.QueueFrames;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// What a queue counts for each way a caller leaves it, written as typed AMI frames in the order Asterisk 20.20.1,
/// 22.9.0 and 23.4.1 sent them to a user reading the <c>agent</c> and <c>dialplan</c> classes (the same sequence on
/// the three versions). In every exit Asterisk counts abandoned, app_queue reports <c>QueueCallerAbandon</c> just
/// before the caller's <c>QueueCallerLeave</c>; after a timeout, an emptied queue or a withdrawal it then sets
/// <c>QUEUESTATUS</c> on the caller's channel. A caller that leaves by key gets a leave and no abandon report, and
/// Asterisk counts it neither answered nor abandoned. Each exit is read twice: after Asterisk's reports, while the
/// caller is still on the line (an announcement, an IVR), and after the caller hangs up.
/// </summary>
public sealed class QueueExitCountTests
{
    private const string Caller = "1790615290.92";
    private const string CallerChannel = "PJSIP/far-0000005c";
    private const string CallerNumber = "50300";
    private const string Member = "1790615290.93";
    private const string MemberChannel = "PJSIP/agent2-0000005d";
    private const string Bridge = "33333333-3333-3333-3333-333333333333";

    [Fact]
    public async Task QueueTimeout_ShouldCountTheVisitTimedOutAndAbandonedAndNotWaiting_BeforeAndAfterTheCallerHangsUp()
    {
        await using var rig = await QueueCallRig.StartAsync();

        rig.Deliver([.. CallerQueued("q-tmo"), .. MemberRingsAndIsCancelled("NOANSWER"), .. AbandonedAndLeft("q-tmo", holdTime: 4),
            VarSet(Caller, CallerChannel, "QUEUESTATUS", "TIMEOUT"), Hangup(Member, 0)]);
        var beforeHangup = rig.Tally("q-tmo");
        rig.Deliver([Hangup(Caller, 16)]);

        using var scope = new AssertionScope();
        beforeHangup.Should().Be(new QueueTally(Offered: 1, Answered: 0, Abandoned: 1, TimedOut: 1, Waiting: 0),
            "the queue's timeout ended the visit: Asterisk reported the abandon, the leave and QUEUESTATUS=TIMEOUT, and "
            + "the caller is hearing the announcement that follows");
        rig.Tally("q-tmo").Should().Be(new QueueTally(Offered: 1, Answered: 0, Abandoned: 1, TimedOut: 1, Waiting: 0),
            "the hang-up counts nothing twice");
    }

    [Fact]
    public async Task CallerHangingUpWhileWaiting_ShouldBeCountedAbandonedAndNotTimedOut_WhenAsteriskReportsTheAbandonAndTheLeave()
    {
        await using var rig = await QueueCallRig.StartAsync();

        rig.Deliver([.. CallerQueued("q-abn"), .. MemberRingsAndIsCancelled("CANCEL"), Hangup(Member, 0),
            .. AbandonedAndLeft("q-abn", holdTime: 3)]);
        var atTheLeave = rig.Tally("q-abn");
        rig.Deliver([Hangup(Caller, 16)]);

        using var scope = new AssertionScope();
        atTheLeave.Should().Be(new QueueTally(Offered: 1, Answered: 0, Abandoned: 1, TimedOut: 0, Waiting: 0),
            "Asterisk reported the abandon and the leave; the caller's own hang-up comes after them");
        rig.Tally("q-abn").Should().Be(new QueueTally(Offered: 1, Answered: 0, Abandoned: 1, TimedOut: 0, Waiting: 0),
            "a hang-up while waiting is an abandon, not a timeout");
    }

    [Theory]
    [InlineData("q-lwe", "LEAVEEMPTY")]
    [InlineData("q-wd", "WITHDRAW")]
    public async Task QueueLettingTheCallerGo_ShouldCountTheVisitAbandonedAndNotTimedOutBeforeTheHangUp_WhenQueueStatusIsNotATimeout(
        string queue, string queueStatus)
    {
        await using var rig = await QueueCallRig.StartAsync();

        rig.Deliver([.. CallerQueued(queue), .. MemberRingsAndIsCancelled("NOANSWER"), .. AbandonedAndLeft(queue, holdTime: 2),
            VarSet(Caller, CallerChannel, "QUEUESTATUS", queueStatus), Hangup(Member, 0)]);
        var beforeHangup = rig.Tally(queue);
        rig.Deliver([Hangup(Caller, 16)]);

        using var scope = new AssertionScope();
        beforeHangup.Should().Be(new QueueTally(Offered: 1, Answered: 0, Abandoned: 1, TimedOut: 0, Waiting: 0),
            $"Asterisk reported the abandon, the leave and QUEUESTATUS={queueStatus}, and the caller is still on the line");
        rig.Tally(queue).Should().Be(new QueueTally(Offered: 1, Answered: 0, Abandoned: 1, TimedOut: 0, Waiting: 0),
            "the hang-up counts nothing twice");
    }

    [Fact]
    public async Task RedirectOutOfTheQueue_ShouldCountTheVisitAbandonedAndNotWaiting_BeforeTheCallerHangsUp()
    {
        await using var rig = await QueueCallRig.StartAsync();

        rig.Deliver([.. CallerQueued("q-redir"), .. MemberRingsAndIsCancelled("CANCEL"), .. AbandonedAndLeft("q-redir", holdTime: 2),
            Hangup(Member, 0)]);
        var beforeHangup = rig.Tally("q-redir");
        rig.Deliver([Hangup(Caller, 16)]);

        using var scope = new AssertionScope();
        beforeHangup.Should().Be(new QueueTally(Offered: 1, Answered: 0, Abandoned: 1, TimedOut: 0, Waiting: 0),
            "an AMI Redirect took the caller out of the queue: Asterisk reported the abandon and the leave, with no QUEUESTATUS");
        rig.Tally("q-redir").Should().Be(new QueueTally(Offered: 1, Answered: 0, Abandoned: 1, TimedOut: 0, Waiting: 0),
            "the hang-up counts nothing twice");
    }

    [Fact]
    public async Task KeyExit_ShouldBeCountedNeitherAnsweredNorAbandonedAndNotWaiting_BeforeAndAfterTheCallerHangsUp()
    {
        await using var rig = await QueueCallRig.StartAsync();

        rig.Deliver([.. CallerQueued("q-key"), .. MemberRingsAndIsCancelled("CANCEL"), LeftByKey("q-key"), Hangup(Member, 0)]);
        var beforeHangup = rig.Tally("q-key");
        rig.Deliver([Hangup(Caller, 16)]);

        using var scope = new AssertionScope();
        beforeHangup.Should().Be(new QueueTally(Offered: 1, Answered: 0, Abandoned: 0, TimedOut: 0, Waiting: 0),
            "the caller pressed a key: Asterisk reported the leave with no abandon, and the caller is in the exit context");
        rig.Tally("q-key").Should().Be(new QueueTally(Offered: 1, Answered: 0, Abandoned: 0, TimedOut: 0, Waiting: 0),
            "Asterisk counts a key exit neither answered nor abandoned, and the hang-up does not make it one");
    }

    [Fact]
    public async Task KeyExit_ShouldNotBeCountedAbandoned_WhenTheCallerIsPutBackIntoTheSameQueueAndAnswered()
    {
        await using var rig = await QueueCallRig.StartAsync();

        rig.Deliver([.. CallerQueued("q-key"), .. MemberRingsAndIsCancelled("CANCEL"), LeftByKey("q-key"), Hangup(Member, 0),
            Join("q-key", CallerChannel, Caller, CallerNumber), .. MemberAnswers("q-key", "1790615290.94", "PJSIP/agent1-0000005e"),
            Hangup("1790615290.94", 16), Hangup(Caller, 16)]);

        rig.Tally("q-key").Should().Be(new QueueTally(Offered: 2, Answered: 1, Abandoned: 0, TimedOut: 0, Waiting: 0),
            "the key exit is neither answered nor abandoned, and the second visit was answered");
    }

    [Fact]
    public async Task KeyExit_ShouldNotBeCountedAbandonedInTheFirstQueue_WhenTheCallerThenJoinsAnotherQueueAndIsAnswered()
    {
        await using var rig = await QueueCallRig.StartAsync();

        rig.Deliver([.. CallerQueued("q-key"), .. MemberRingsAndIsCancelled("CANCEL"), LeftByKey("q-key"), Hangup(Member, 0),
            Join("q-sales", CallerChannel, Caller, CallerNumber), .. MemberAnswers("q-sales", "1790615290.94", "PJSIP/agent1-0000005e"),
            Hangup("1790615290.94", 16), Hangup(Caller, 16)]);

        using var scope = new AssertionScope();
        rig.Tally("q-key").Should().Be(new QueueTally(Offered: 1, Answered: 0, Abandoned: 0, TimedOut: 0, Waiting: 0),
            "\"press 1 for another department\": the first queue reported a leave with no abandon");
        rig.Tally("q-sales").Should().Be(new QueueTally(Offered: 1, Answered: 1, Abandoned: 0, TimedOut: 0, Waiting: 0),
            "the second queue answered the caller");
    }

    /// <summary>
    /// Three four-second visits of the same queue, each ended by the queue's timeout and followed by an announcement,
    /// then a fourth visit a member answers: the dialplan of the capture's loop.
    /// </summary>
    [Fact]
    public async Task TimeoutLoop_ShouldCountFourOfferedThreeTimedOutThreeAbandonedOneAnswered_AndNoneWaitingBetweenTimeouts()
    {
        await using var rig = await QueueCallRig.StartAsync();
        var waitingAfterEachTimeout = new List<int>();

        rig.Deliver(CallerQueued("q-loop"));
        for (var visit = 0; visit < 3; visit++)
        {
            var member = $"1790615290.{100 + visit}";
            var memberChannel = $"PJSIP/agent3-000000{60 + visit}";
            if (visit > 0)
                rig.Deliver([Join("q-loop", CallerChannel, Caller, CallerNumber)]);
            rig.Deliver([NewChannel(member, memberChannel, "0", Caller), DialBegin(Caller, CallerChannel, member, memberChannel),
                NewState(member, "5"), DialEnd(Caller, CallerChannel, member, memberChannel, "NOANSWER"),
                DialEnd(Caller, CallerChannel, member, memberChannel, "CANCEL"), .. AbandonedAndLeft("q-loop", holdTime: 4),
                VarSet(Caller, CallerChannel, "QUEUESTATUS", "TIMEOUT"), Hangup(member, 0), VarSet(Caller, CallerChannel, "ABANDONED", "")]);
            waitingAfterEachTimeout.Add(rig.Tally("q-loop").Waiting);
        }

        rig.Deliver([Join("q-loop", CallerChannel, Caller, CallerNumber), .. MemberAnswers("q-loop", "1790615290.110", "PJSIP/agent3-00000070"),
            Hangup(Caller, 16), Hangup("1790615290.110", 16)]);

        using var scope = new AssertionScope();
        waitingAfterEachTimeout.Should().Equal([0, 0, 0], "after each timeout the caller hears the announcement, out of the queue");
        rig.Tally("q-loop").Should().Be(new QueueTally(Offered: 4, Answered: 1, Abandoned: 3, TimedOut: 3, Waiting: 0),
            "four visits: three ended by the queue's timeout, which Asterisk counts abandoned, and one answered");
    }

    [Fact]
    public async Task QueueTimeout_ShouldBeCountedAbandonedAndNotTimedOut_WhenTheAmiUserDoesNotReadTheDialplanClass()
    {
        await using var rig = await QueueCallRig.StartAsync();

        rig.Deliver([.. CallerQueued("q-tmo"), .. MemberRingsAndIsCancelled("NOANSWER"), Abandon("q-tmo", CallerChannel, Caller, 4),
            Leave("q-tmo", CallerChannel, Caller), Hangup(Member, 0), Hangup(Caller, 16)]);

        rig.Tally("q-tmo").Should().Be(new QueueTally(Offered: 1, Answered: 0, Abandoned: 1, TimedOut: 0, Waiting: 0),
            "without the dialplan class no QUEUESTATUS arrives: the timeout is an abandon, as Asterisk counts it");
    }

    /// <summary>
    /// Every answered visit has the shape join, leave, connect: app_queue reports the caller's leave before it reports
    /// the connection, so the leave must not take the waiting count below zero and the answer still measures the wait
    /// from the visit's own join.
    /// </summary>
    [Fact]
    public async Task AnsweredVisit_ShouldNeverTakeWaitingBelowZeroAndMeasureItsWaitFromItsOwnJoin_WhenTheLeaveComesBeforeTheConnect()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 9, 28, 17, 8, 10, TimeSpan.Zero));
        await using var rig = await QueueCallRig.StartAsync(clock);
        var waiting = new List<int>();

        rig.Deliver([NewChannel(Caller, CallerChannel, "4", Caller, CallerNumber, "from-pstn", "5001"), NewState(Caller, "6")]);
        clock.Advance(TimeSpan.FromSeconds(2));
        rig.Deliver([Join("q-ans", CallerChannel, Caller, CallerNumber)]);
        clock.Advance(TimeSpan.FromSeconds(5));
        foreach (var frame in MemberAnswers("q-ans", Member, "PJSIP/agent1-0000005d"))
        {
            rig.Deliver([frame]);
            waiting.Add(rig.Tally("q-ans").Waiting);
        }

        rig.Deliver([Hangup(Caller, 16), Hangup(Member, 16)]);

        using var scope = new AssertionScope();
        waiting.Should().OnlyContain(w => w >= 0, "the leave and the connect close one visit, once");
        rig.Tally("q-ans").Should().Be(new QueueTally(Offered: 1, Answered: 1, Abandoned: 0, TimedOut: 0, Waiting: 0),
            "one visit, answered");
        rig.Tracker.GetByQueueName("q-ans")!.TotalWaitTime.Should().Be(TimeSpan.FromSeconds(5),
            "the wait runs from the visit's join to the connection, not from the call's creation 2 s earlier");
    }

    [Fact]
    public async Task QueueRates_ShouldBeAHundredPercentAbandonedAndZeroServiceLevel_WhenEveryCallTimedOut()
    {
        await using var rig = await QueueCallRig.StartAsync();

        for (var call = 0; call < 2; call++)
        {
            var caller = $"1790615300.{call}";
            var channel = $"PJSIP/far-0000008{call}";
            rig.Deliver([NewChannel(caller, channel, "4", caller, $"5030{call}", "from-pstn", "5003"), NewState(caller, "6"),
                Join("q-tmo", channel, caller, $"5030{call}"), VarSet(caller, channel, "ABANDONED", "TRUE"),
                Abandon("q-tmo", channel, caller, 4), Leave("q-tmo", channel, caller),
                VarSet(caller, channel, "QUEUESTATUS", "TIMEOUT"), Hangup(caller, 16)]);
        }

        var queue = rig.Tracker.GetByQueueName("q-tmo")!;
        using var scope = new AssertionScope();
        queue.AbandonRate.Should().Be(100.0, "every call the queue offered timed out, and Asterisk counts a timeout abandoned");
        queue.ServiceLevel.Should().Be(0.0, "no call was answered");
    }

    [Fact]
    public async Task QueueRates_ShouldKeepKeyExitsInTheOfferedDenominator_WhenFourCallsAreOneAnsweredOneAbandonedAndTwoKeyExits()
    {
        await using var rig = await QueueCallRig.StartAsync();

        // One answered within the service-level threshold.
        rig.Deliver([NewChannel("1790615400.1", "PJSIP/far-00000091", "4", "1790615400.1", "50701", "from-pstn", "5007"),
            NewState("1790615400.1", "6"), Join("q-mix", "PJSIP/far-00000091", "1790615400.1", "50701"),
            .. MemberAnswersCaller("q-mix", "1790615400.1", "PJSIP/far-00000091", "1790615400.2", "PJSIP/agent1-00000092"),
            Hangup("1790615400.1", 16), Hangup("1790615400.2", 16)]);
        // One caller hangs up while waiting.
        rig.Deliver([NewChannel("1790615400.3", "PJSIP/far-00000093", "4", "1790615400.3", "50702", "from-pstn", "5007"),
            NewState("1790615400.3", "6"), Join("q-mix", "PJSIP/far-00000093", "1790615400.3", "50702"),
            VarSet("1790615400.3", "PJSIP/far-00000093", "ABANDONED", "TRUE"), Abandon("q-mix", "PJSIP/far-00000093", "1790615400.3", 3),
            Leave("q-mix", "PJSIP/far-00000093", "1790615400.3"), Hangup("1790615400.3", 16)]);
        // Two callers leave by key and hang up in the exit context.
        foreach (var (uid, channel, number) in new[]
                 { ("1790615400.4", "PJSIP/far-00000094", "50703"), ("1790615400.5", "PJSIP/far-00000095", "50704") })
        {
            rig.Deliver([NewChannel(uid, channel, "4", uid, number, "from-pstn", "5007"), NewState(uid, "6"),
                Join("q-mix", channel, uid, number), Leave("q-mix", channel, uid), Hangup(uid, 16)]);
        }

        var queue = rig.Tracker.GetByQueueName("q-mix")!;
        using var scope = new AssertionScope();
        queue.AbandonRate.Should().Be(25.0, "one abandon over four offered calls: the key exits are offered, not abandoned");
        queue.ServiceLevel.Should().Be(25.0, "one answer within the threshold over four offered calls");
    }

    // --- The frames ---------------------------------------------------------------------------

    /// <summary>The caller's inbound call, answered by the dialplan, joining <paramref name="queue"/>.</summary>
    private static ManagerEvent[] CallerQueued(string queue) =>
    [
        NewChannel(Caller, CallerChannel, "4", Caller, CallerNumber, "from-pstn", "5003"),
        NewState(Caller, "6"),
        Join(queue, CallerChannel, Caller, CallerNumber),
    ];

    /// <summary>The member's leg is dialed and rings, and the queue stops ringing it with <paramref name="firstStatus"/>.</summary>
    private static ManagerEvent[] MemberRingsAndIsCancelled(string firstStatus) =>
    [
        NewChannel(Member, MemberChannel, "0", Caller),
        DialBegin(Caller, CallerChannel, Member, MemberChannel),
        NewState(Member, "5"),
        DialEnd(Caller, CallerChannel, Member, MemberChannel, firstStatus),
        DialEnd(Caller, CallerChannel, Member, MemberChannel, "CANCEL"),
    ];

    /// <summary>app_queue's abandon, in the order it reports it: <c>ABANDONED</c>, the abandon report, the leave.</summary>
    private static ManagerEvent[] AbandonedAndLeft(string queue, int holdTime) =>
    [
        VarSet(Caller, CallerChannel, "ABANDONED", "TRUE"),
        Abandon(queue, CallerChannel, Caller, holdTime),
        Leave(queue, CallerChannel, Caller),
    ];

    /// <summary>The caller pressed the queue's exit key: a leave, with no abandon report.</summary>
    private static QueueCallerLeaveEvent LeftByKey(string queue) => Leave(queue, CallerChannel, Caller);

    private static ManagerEvent[] MemberAnswers(string queue, string member, string memberChannel) =>
        MemberAnswersCaller(queue, Caller, CallerChannel, member, memberChannel);

    /// <summary>
    /// A member's leg is dialed, rings and answers, the caller leaves the queue, app_queue reports the connection, and
    /// member and caller enter a bridge.
    /// </summary>
    private static ManagerEvent[] MemberAnswersCaller(string queue, string caller, string callerChannel, string member,
        string memberChannel)
        =>
        [
            NewChannel(member, memberChannel, "0", caller),
            DialBegin(caller, callerChannel, member, memberChannel),
            NewState(member, "5"),
            NewState(member, "6"),
            DialEnd(caller, callerChannel, member, memberChannel, "ANSWER"),
            Leave(queue, callerChannel, caller),
            AgentConnect(queue, caller, callerChannel, caller, memberChannel.Split('-')[0], member, memberChannel, holdTime: 1),
            BridgeCreate(Bridge),
            BridgeEnter(Bridge, member),
            BridgeEnter(Bridge, caller),
        ];
}
