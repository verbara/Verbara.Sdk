using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using static Verbara.Sdk.Sessions.FunctionalTests.Infrastructure.QueueFrames;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// Which member a queue call records as having taken it (<see cref="CallSession.AgentInterface"/>) when more than
/// one report could name it: a second queue visit, the connect of an agent the SDK knows by name, and the save the
/// manager makes once the call is connected.
/// </summary>
/// <remarks>
/// The frames follow a call Asterisk 22.9.0 sent live (an endpoint member answers the caller in <c>q-pjsip</c> and
/// blind-transfers it into <c>q-pjsip3</c>, where another member answers), written as typed events with the fields
/// <see cref="Live.Server.VerbaraServer"/>'s observer reads.
/// </remarks>
public sealed class QueueMemberRecordTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private const string Caller = "1790567054.11";
    private const string CallerChannel = "PJSIP/pstn-00000007";
    private const string FirstMember = "PJSIP/agent1";
    private const string SecondMember = "PJSIP/agent3";
    private const string PooledAgent = "1001";
    private const string PooledAgentInterface = "Local/1001@agent-request/n";

    /// <summary>
    /// The call is answered in one queue and transferred into another, the shape a transfer to a queue takes. A
    /// consumer reads the call's member when each visit is announced connected; both announcements name the first
    /// member, and so does the call once it has ended.
    /// </summary>
    [Fact]
    public async Task AgentInterface_ShouldKeepTheFirstMember_WhenTheCallIsAnsweredInOneQueueAndThenInAnother()
    {
        await using var rig = await QueueCallRig.StartAsync();
        var atDelivery = new[]
        {
            new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var delivered = 0;
        using var reader = rig.Manager.Events.Subscribe(evt =>
        {
            if (evt is not CallConnectedEvent connected)
                return;

            var index = Interlocked.Increment(ref delivered) - 1;
            if (index < atDelivery.Length)
                atDelivery[index].TrySetResult(rig.Manager.GetById(connected.SessionId)?.AgentInterface);
        });

        rig.Deliver(AnsweredInOneQueueThenInAnother(secondVisitAgent: null));

        var first = await atDelivery[0].Task.WaitAsync(Bound);
        var second = await atDelivery[1].Task.WaitAsync(Bound);

        using var scope = new AssertionScope();
        rig.Connected.Select(e => e.QueueName).Should().Equal(["q-pjsip", "q-pjsip3"], "premise: each queue connected the call once");
        first.Should().Be(FirstMember, "the first queue connected the call to its member, recorded before the call is announced");
        second.Should().Be(FirstMember, "the second queue's report does not replace the member the call already records");
        var call = rig.Manager.GetById(rig.Connected[0].SessionId);
        call.Should().NotBeNull("the manager keeps the ended call");
        call!.AgentInterface.Should().Be(FirstMember, "the call keeps the first member once it has ended");
        call.AgentId.Should().BeNull("no known agent took the call");
    }

    /// <summary>
    /// No-regression pin, green before and after the queue's own report records a member: an agent the SDK knows
    /// by name takes the call, and its connect, which runs first, records the interface and the agent. app_queue's
    /// report of the same connection carries the same interface and keeps both.
    /// </summary>
    [Fact]
    public async Task AgentInterface_ShouldBeTheKnownAgentsInterfaceAndAgentId_WhenAnAgentTheSdkKnowsByNameTakesTheCall()
    {
        await using var rig = await QueueCallRig.StartAsync();

        rig.Deliver([
            AgentLogin(PooledAgent, "PJSIP/agentphone-00000000", "login-1001"),
            .. OneVisit("q-agent", PooledAgentInterface, "1790567060.20", "Local/1001@agent-request-00000001;1", "b-agent", PooledAgent),
            Hangup(Caller, 16),
        ]);

        using var scope = new AssertionScope();
        var call = rig.Manager.GetById(rig.Connected.Should().ContainSingle("premise: the queue connected the call once").Subject.SessionId);
        call.Should().NotBeNull("the manager keeps the ended call");
        call!.AgentInterface.Should().Be(PooledAgentInterface, "the known agent's connect recorded its interface, as before");
        call.AgentId.Should().Be(PooledAgent, "the known agent's connect recorded the agent, as before");
    }

    /// <summary>
    /// Pin of the known agent's exception, green before and after: the first visit is taken by a member the SDK does
    /// not know by name, the second by a known agent. The known agent's connect writes its own interface on every
    /// connect, so the call records the agent of the later visit.
    /// </summary>
    [Fact]
    public async Task AgentInterface_ShouldBeTheKnownAgents_WhenAKnownAgentTakesALaterVisit()
    {
        await using var rig = await QueueCallRig.StartAsync();

        rig.Deliver([
            AgentLogin(PooledAgent, "PJSIP/agentphone-00000000", "login-1001"),
            .. AnsweredInOneQueueThenInAnother(secondVisitAgent: PooledAgent),
        ]);

        using var scope = new AssertionScope();
        rig.Connected.Select(e => e.QueueName).Should().Equal(["q-pjsip", "q-pjsip3"], "premise: each queue connected the call once");
        var call = rig.Manager.GetById(rig.Connected[0].SessionId);
        call.Should().NotBeNull("the manager keeps the ended call");
        call!.AgentInterface.Should().Be(PooledAgentInterface, "a known agent's connect records its interface, as before");
        call.AgentId.Should().Be(PooledAgent, "a known agent's connect records the agent, as before");
    }

    /// <summary>The save the manager makes once app_queue has connected the call carries the member.</summary>
    [Fact]
    public async Task Save_ShouldCarryTheMember_WhenTheQueueConnectsTheCall()
    {
        var store = new RecordingSessionStore();
        await using var rig = await QueueCallRig.StartAsync(store: store);
        var frames = new Queue<ManagerEvent>(AnsweredInOneQueueThenInAnother(secondVisitAgent: null));

        rig.DeliverThrough(frames, f => f is QueueCallerLeaveEvent);
        var savesBeforeTheConnect = store.Saves.Count;
        rig.DeliverThrough(frames, f => f is AgentConnectEvent);

        var sessionId = rig.Connected.Should().ContainSingle("premise: the queue connected the call").Subject.SessionId;
        var save = store.Saves.Skip(savesBeforeTheConnect).Where(s => s.SessionId == sessionId)
            .Should().ContainSingle("the connection is saved once").Subject;

        using var scope = new AssertionScope();
        save.State.Should().Be(CallSessionState.Connected, "premise: the save is the connected call's");
        save.AgentInterface.Should().Be(FirstMember, "the member is persisted with the call like every other field of it");
    }

    /// <summary>
    /// The caller is answered in <c>q-pjsip</c> by <see cref="FirstMember"/>, blind-transferred into
    /// <c>q-pjsip3</c>, and answered there by <see cref="SecondMember"/>, or, with
    /// <paramref name="secondVisitAgent"/>, by that agent, known by name, on <see cref="PooledAgentInterface"/>.
    /// </summary>
    private static ManagerEvent[] AnsweredInOneQueueThenInAnother(string? secondVisitAgent)
    {
        const string m1 = "1790567054.12", m1Ch = "PJSIP/agent1-00000008";
        const string m2 = "1790567058.13";
        var m2Ch = secondVisitAgent is null ? "PJSIP/agent3-00000009" : "Local/1001@agent-request-00000009;1";
        var secondMember = secondVisitAgent is null ? SecondMember : PooledAgentInterface;
        return
        [
            .. OneVisit("q-pjsip", FirstMember, m1, m1Ch, "33333333-3333-3333-3333-333333333333", agent: null, newCaller: true),
            BlindTransfer("33333333-3333-3333-3333-333333333333", Caller, CallerChannel, "from-pstn", "4015"),
            AgentComplete(Caller, CallerChannel, FirstMember, holdTime: 2, talkTime: 2),
            BridgeLeave("33333333-3333-3333-3333-333333333333", m1),
            BridgeLeave("33333333-3333-3333-3333-333333333333", Caller),
            BridgeDestroy("33333333-3333-3333-3333-333333333333"),
            Hangup(m1, 16),
            .. OneVisit("q-pjsip3", secondMember, m2, m2Ch, "44444444-4444-4444-4444-444444444444", secondVisitAgent, newCaller: false),
            AgentComplete(Caller, CallerChannel, secondMember, holdTime: 2, talkTime: 16),
            BridgeLeave("44444444-4444-4444-4444-444444444444", Caller),
            BridgeLeave("44444444-4444-4444-4444-444444444444", m2),
            BridgeDestroy("44444444-4444-4444-4444-444444444444"),
            Hangup(m2, 16),
            Hangup(Caller, 16),
        ];
    }

    /// <summary>
    /// One queue visit of the caller that <paramref name="member"/> takes: the join (after the caller's own channel
    /// when <paramref name="newCaller"/>), the member's leg ringing and answering, app_queue's connect, and the bridge.
    /// </summary>
    private static ManagerEvent[] OneVisit(string queue, string member, string memberUniqueId, string memberChannel,
        string bridgeId, string? agent, bool newCaller = true) =>
    [
        .. newCaller ? new ManagerEvent[] { NewChannel(Caller, CallerChannel, "4", Caller, "5551402", "from-pstn", "4002") } : [],
        Join(queue, CallerChannel, Caller, "5551402"),
        NewChannel(memberUniqueId, memberChannel, "0", Caller, "<unknown>", "from-agents", "s"),
        DialBegin(Caller, CallerChannel, memberUniqueId, memberChannel, member),
        NewState(memberUniqueId, "5"),
        NewState(memberUniqueId, "6"),
        DialEnd(Caller, CallerChannel, memberUniqueId, memberChannel, "ANSWER"),
        Leave(queue, CallerChannel, Caller),
        AgentConnect(queue, Caller, CallerChannel, Caller, member, memberUniqueId, memberChannel, holdTime: 2, agent: agent),
        .. newCaller ? new ManagerEvent[] { NewState(Caller, "6") } : [],
        BridgeCreate(bridgeId),
        BridgeEnter(bridgeId, memberUniqueId),
        BridgeEnter(bridgeId, Caller),
    ];
}
