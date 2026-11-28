using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Sessions.Tests.Harness;
using static Verbara.Sdk.Sessions.Tests.Manager.WrapUpFrames;

namespace Verbara.Sdk.Sessions.Tests.Manager;

/// <summary>
/// app_queue's report that it rang a member for a waiting caller and the member did not answer
/// (<c>AgentRingNoAnswer</c>) as the session manager reports it: one <see cref="CallRingNoAnswerEvent"/> per report,
/// for a call it holds and has not ended, naming the queue and the member the report names.
/// </summary>
/// <remarks>
/// Runs on the real manager over <see cref="WrapUpRig"/>'s frames, delivered synchronously through the server's own
/// observer. The frame shapes are those of the captures (<c>queue-ring-no-answer-*</c>): no <c>Agent</c> header unless
/// a test gives one, a shape no capture holds.
/// </remarks>
[Collection(SessionSubscriberFaultsGroup.Name)]
public sealed class CallSessionManagerRingNoAnswerTests
{
    private const string Queue = "sales";
    private const string Missed = "PJSIP/agent1";
    private const string Answered = "PJSIP/agent2";

    [Fact]
    public async Task AgentRingNoAnswer_ShouldRaiseOneEventNamingTheQueueAndTheMember_BeforeTheNextMemberConnects()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await WrapUpRig.StartAsync(clock);
        var frames = QueueCallConnected(Queue, "c1", "m2", Answered);
        var connectAt = Array.FindIndex(frames, f => f is AgentConnectEvent);
        rig.Deliver(frames[..connectAt]);
        var missedAt = clock.GetUtcNow();
        rig.Deliver(RingNoAnswer(Queue, "c1", "c1", Missed));
        rig.Deliver(frames[connectAt..]);
        var sessionId = rig.Session("c1").SessionId;

        rig.Events.OfType<CallRingNoAnswerEvent>().Should().Equal(
            [new CallRingNoAnswerEvent(sessionId, ServerId, missedAt, Missed, Queue) { AgentInterface = Missed }],
            "one report of a member that did not answer raises one notification for the call, naming the frame's "
            + "queue and the member; app_queue names no agent for it, so the member is its interface");
        rig.Events.Where(e => e is CallRingNoAnswerEvent or CallConnectedEvent).Select(e => e.GetType().Name)
            .Should().Equal([nameof(CallRingNoAnswerEvent), nameof(CallConnectedEvent)],
                "the missed ring is raised when Asterisk reports it, before the next member's connect");
    }

    [Fact]
    public async Task AgentRingNoAnswer_ShouldRaiseOneEventPerReport_WhenTheSameMemberIsMissedTwice()
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(QueueCallConnected(Queue, "c1", "m2", Answered, withConnect: false));
        rig.Deliver(RingNoAnswer(Queue, "c1", "c1", Missed), RingNoAnswer(Queue, "c1", "c1", Missed));

        rig.Events.OfType<CallRingNoAnswerEvent>().Should().HaveCount(2, "app_queue reported two missed rings");
    }

    [Fact]
    public async Task AgentRingNoAnswer_ShouldNameTheAgent_WhenTheReportNamesOne()
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(QueueCallConnected(Queue, "c1", "m2", Answered, withConnect: false));
        rig.Deliver(RingNoAnswer(Queue, "c1", "c1", Missed, agent: "agent-7"));

        rig.Events.OfType<CallRingNoAnswerEvent>().Select(e => (e.AgentId, e.AgentInterface))
            .Should().Equal([("agent-7", Missed)], "the agent the report names wins; the interface is still carried");
    }

    [Fact]
    public async Task AgentRingNoAnswer_ShouldNameTheCallsQueue_WhenTheReportNamesNone()
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(QueueCallConnected(Queue, "c1", "m2", Answered, withConnect: false));
        rig.Deliver(RingNoAnswer(queue: null, "c1", "c1", Missed));

        rig.Events.OfType<CallRingNoAnswerEvent>().Select(e => e.QueueName).Should().Equal([Queue]);
    }

    [Fact]
    public async Task AgentRingNoAnswer_ShouldFindTheCallByItsLinkedid_WhenTheCallersChannelIsNotOneTheManagerHolds()
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(QueueCallConnected(Queue, "c1", "m2", Answered, withConnect: false));
        rig.Deliver(RingNoAnswer(Queue, callerUniqueId: "unknown-1", linkedId: "c1", Missed));

        rig.Events.OfType<CallRingNoAnswerEvent>().Select(e => e.SessionId)
            .Should().Equal([rig.Session("c1").SessionId]);
    }

    [Fact]
    public async Task AgentRingNoAnswer_ShouldRaiseNothing_WhenTheManagerHoldsNoSuchCall()
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(QueueCallConnected(Queue, "c1", "m2", Answered, withConnect: false));
        var before = rig.Events.Count;
        rig.Deliver(RingNoAnswer(Queue, callerUniqueId: "unknown-1", linkedId: "unknown-1", Missed));

        rig.Events.Skip(before).Should().BeEmpty("a report for no held call raises nothing");
    }

    [Fact]
    public async Task AgentRingNoAnswer_ShouldRaiseNothing_WhenTheReportNamesNeitherAgentNorInterface()
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(QueueCallConnected(Queue, "c1", "m2", Answered, withConnect: false));
        var before = rig.Events.Count;
        rig.Deliver(RingNoAnswer(Queue, "c1", "c1", member: null));

        rig.Events.Skip(before).Should().BeEmpty("a report that names no member names nobody who missed");
    }

    [Fact]
    public async Task AgentRingNoAnswer_ShouldRaiseNothing_WhenTheCallHasEnded()
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(QueueCallConnected(Queue, "c1", "m2", Answered, withConnect: false));
        rig.Deliver(Hangup("m2"), Hangup("c1"));
        var before = rig.Events.Count;
        rig.Deliver(RingNoAnswer(Queue, "c1", "c1", Missed));

        rig.Events.Skip(before).OfType<CallRingNoAnswerEvent>().Should().BeEmpty("an ended call misses nothing more");
    }

    [Fact]
    public async Task AgentRingNoAnswer_ShouldBeCountedAsAMissedCallOfTheMember_ByTheAgentTracker()
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        using var tracker = new AgentSessionTracker(rig.Manager, Options.Create(new SessionOptions()),
            NullLogger<AgentSessionTracker>.Instance);
        rig.Deliver(QueueCallConnected(Queue, "c1", "m2", Answered, withConnect: false));
        rig.Deliver(RingNoAnswer(Queue, "c1", "c1", Missed));

        var agent = tracker.GetByAgentId(Missed);
        agent.Should().NotBeNull("the member that missed is tracked under its interface");
        new { agent!.CallsMissed, agent.CallsHandled, agent.State }.Should().BeEquivalentTo(
            new { CallsMissed = 1, CallsHandled = 0, State = AgentSessionState.Idle },
            "a missed ring adds one missed call and changes nothing else");
    }

    [Fact]
    public async Task AgentRingNoAnswer_ShouldRaiseForTheDeliveringServersCall_WhenTwoServersHoldACallUnderTheSameIds()
    {
        const string other = "srv-2";
        const string otherMissed = "PJSIP/agent3";
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider(), null, ServerId, other);
        rig.Deliver(QueueCallConnected(Queue, "c1", "m2", Answered, withConnect: false));
        rig.DeliverTo(other, QueueCallConnected(Queue, "c1", "m2", Answered, withConnect: false));
        var first = rig.Manager.GetByLinkedId(ServerId, "c1")
            ?? throw new InvalidOperationException("premise: the first server holds a call under linkedid c1");
        var second = rig.Manager.GetByLinkedId(other, "c1")
            ?? throw new InvalidOperationException("premise: the second server holds a call under linkedid c1");
        first.SessionId.Should().NotBe(second.SessionId, "premise: each server holds its own call");

        rig.Deliver(RingNoAnswer(Queue, "c1", "c1", Missed));
        rig.DeliverTo(other, RingNoAnswer(Queue, "c1", "c1", otherMissed));

        rig.Events.OfType<CallRingNoAnswerEvent>().Select(e => (e.SessionId, e.ServerId, e.AgentId)).Should().Equal(
            [(first.SessionId, ServerId, Missed), (second.SessionId, other, otherMissed)],
            "a report is attributed on the server that delivered it: the same channel id and linkedid on another "
            + "server name another call");
    }

    [Fact]
    public async Task AgentRingNoAnswer_ShouldFindTheCallByTheCallersChannelFirst_WhenTheLinkedidNamesAnotherHeldCall()
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(QueueCallConnected(Queue, "c1", "m1", Answered, withConnect: false));
        rig.Deliver(QueueCallConnected(Queue, "c2", "m2", Answered, withConnect: false));

        rig.Deliver(RingNoAnswer(Queue, callerUniqueId: "c2", linkedId: "c1", Missed));

        rig.Events.OfType<CallRingNoAnswerEvent>().Select(e => e.SessionId).Should().Equal(
            [rig.Session("c2").SessionId],
            "the caller's own channel names the call; the Linkedid is looked up only when that channel is not held");
    }

    [Fact]
    public async Task AgentRingNoAnswer_ShouldNameTheReportsQueue_WhenItDiffersFromTheCallsQueue()
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(QueueCallConnected(Queue, "c1", "m2", Answered, withConnect: false));
        rig.Session("c1").QueueName.Should().Be(Queue, "premise: the call joined the sales queue");

        rig.Deliver(RingNoAnswer("support", "c1", "c1", Missed));

        rig.Events.OfType<CallRingNoAnswerEvent>().Select(e => e.QueueName).Should().Equal(
            ["support"], "the queue whose member missed is the one the report names; the call's queue is a fallback");
    }

    [Fact]
    public async Task AgentRingNoAnswer_ShouldRaiseForTheCall_WhenTheCallHasNoQueue()
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(NewChannel("c1", CallerChannel("c1"), "Ring", "c1", context: "from-trunk", exten: "500"));
        rig.Session("c1").QueueName.Should().BeNull("premise: the manager never saw the caller join a queue");

        rig.Deliver(RingNoAnswer(Queue, "c1", "c1", Missed), RingNoAnswer(queue: null, "c1", "c1", Missed));

        rig.Events.OfType<CallRingNoAnswerEvent>().Select(e => (e.SessionId, e.QueueName)).Should().Equal(
            [(rig.Session("c1").SessionId, Queue), (rig.Session("c1").SessionId, null)],
            "a missed ring is reported whether or not the call joined a queue: it names the report's queue, and "
            + "none when neither the report nor the call names one");
    }

    private static AgentRingNoAnswerEvent RingNoAnswer(string? queue, string callerUniqueId, string linkedId,
        string? member, string? agent = null)
    {
        var fields = new Dictionary<string, string>
        {
            ["Channel"] = CallerChannel(callerUniqueId), ["Uniqueid"] = callerUniqueId, ["Linkedid"] = linkedId,
            ["RingTime"] = "2000",
        };
        if (queue is not null)
            fields["Queue"] = queue;
        if (member is not null)
        {
            fields["Interface"] = member;
            fields["MemberName"] = member;
        }

        if (agent is not null)
            fields["Agent"] = agent;

        return new()
        {
            EventType = "AgentRingNoAnswer", UniqueId = callerUniqueId, Channel = CallerChannel(callerUniqueId),
            LinkedId = linkedId, Interface = member, Agent = agent, Ringtime = 2000, RawFields = fields,
        };
    }
}
