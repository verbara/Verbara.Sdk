using FluentAssertions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Sessions.Tests.Harness;
using static Verbara.Sdk.Sessions.Tests.Manager.WrapUpFrames;

namespace Verbara.Sdk.Sessions.Tests.Manager;

/// <summary>
/// The member a <see cref="CallConnectedEvent"/> names: the interface of the connect it announces, taken from that
/// connect at each of the three places the manager announces a connection (app_queue's connect report, the connect of
/// an agent known by name, and the bridge of a call that never joined a queue), whatever
/// <see cref="CallSession.AgentInterface"/> keeps.
/// </summary>
/// <remarks>
/// Runs on the real manager over <see cref="WrapUpRig"/>'s frames, on <see cref="FakeTimeProvider"/>. Each test reads
/// every <see cref="CallConnectedEvent"/> of its call and the call's recorded interface after the last frame.
/// </remarks>
[Collection(SessionSubscriberFaultsGroup.Name)]
public sealed class CallSessionManagerConnectedMemberTests
{
    private const string First = "PJSIP/agent1";
    private const string Second = "PJSIP/agent2";

    [Fact]
    public async Task QueueCallerConnected_ShouldNameTheSecondMember_WhenOneCallVisitsTwoQueuesTakenByTwoMembers()
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(QueueCallConnected("sales", "c1", "m1", First));
        rig.Deliver(MemberBlindTransfers("sales", "c1", "m1", First, "queues", "support"));
        rig.Deliver(SecondVisitConnected("support", "c1", "m2", Second));
        var session = rig.Session("c1");

        (string? Queue, string? Member)[] expected = [("sales", First), ("support", Second)];

        Connected(rig, session.SessionId).Should().Equal(expected,
            "each visit's announcement names the member app_queue reported connecting that visit");
        session.AgentInterface.Should().Be(First, "the call keeps recording its first member, as before");
    }

    [Fact]
    public async Task AgentConnected_ShouldNameTheAgentsInterface_WhenAnAgentKnownByNameConnectsAQueuedCall()
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(AgentLogin("agent-3", "PJSIP/agent3", "login-3"));
        rig.Deliver(QueueCallConnected("sales", "c1", "m1", "PJSIP/agent3", agent: "agent-3"));
        var session = rig.Session("c1");

        var connected = rig.Events.OfType<CallConnectedEvent>().Where(e => e.SessionId == session.SessionId).ToList();

        connected.Select(e => (e.AgentId, e.QueueName, e.AgentInterface)).Should().Equal(
            [("agent-3", "sales", "PJSIP/agent3")],
            "the connect of an agent known by name announces the visit once, naming the agent and the interface its "
            + "connect reported");
    }

    [Fact]
    public async Task AgentConnected_ShouldNameTheAgentsInterface_WhenAnAgentKnownByNameTakesACallOutsideAnyQueue()
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(AgentLogin("agent-4", "PJSIP/agent4", "login-4"));
        rig.Deliver(NamedAgentCallConnected("n1", "nm1", "PJSIP/agent4", "agent-4"));
        var session = rig.Session("n1");

        Connected(rig, session.SessionId).Should().Equal(
            [(null, "PJSIP/agent4")],
            "a call outside any queue is announced by the agent's connect, which names the agent's interface");
    }

    [Fact]
    public async Task BridgeEntered_ShouldNameTheInterfaceTheCallRecorded_WhenACallThatNeverJoinedAQueueConnectsAgain()
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(AgentLogin("agent-4", "PJSIP/agent4", "login-4"));
        rig.Deliver(DialingCaller("n1", "nm1", "PJSIP/agent4"));
        rig.Deliver(AgentConnect("", "n1", CallerChannel("n1"), "n1", "PJSIP/agent4", "nm1",
            MemberChannel("PJSIP/agent4", "nm1"), "agent-4"));
        rig.Deliver(
            BridgeCreate(Bridge("n1")),
            BridgeEnter(Bridge("n1"), "nm1"),
            BridgeEnter(Bridge("n1"), "n1"),
            BlindTransfer(Bridge("n1"), "n1", CallerChannel("n1"), "from-trunk", "700"),
            BridgeLeave(Bridge("n1"), "nm1"),
            BridgeLeave(Bridge("n1"), "n1"),
            Hangup("nm1"),
            BridgeCreate("bridge-n1-2"),
            BridgeEnter("bridge-n1-2", "n1"));
        var session = rig.Session("n1");

        (string? Queue, string? Member)[] expected = [(null, "PJSIP/agent4"), (null, "PJSIP/agent4")];

        Connected(rig, session.SessionId).Should().Equal(expected,
            "the agent's connect announces the call with the agent's interface; after the transfer the call's "
            + "bridge announces it connected again and names the interface the call recorded, the agent's");
        session.AgentInterface.Should().Be("PJSIP/agent4", "the agent's connect recorded its interface");
    }

    [Fact]
    public async Task BridgeEntered_ShouldNameNoMember_WhenACallThatNeverJoinedAQueueRecordedNoInterface()
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(DialingCaller("d1", "dm1", "PJSIP/phone1"));
        rig.Deliver(
            BridgeCreate(Bridge("d1")),
            BridgeEnter(Bridge("d1"), "dm1"),
            BridgeEnter(Bridge("d1"), "d1"));
        var session = rig.Session("d1");

        (string? Queue, string? Member)[] expected = [(null, null)];

        Connected(rig, session.SessionId).Should().Equal(expected,
            "a call that never joined a queue and recorded no interface is announced by its bridge with no member");
        session.AgentInterface.Should().BeNull("no connect recorded an interface");
    }

    [Fact]
    public void CallConnectedEvent_ShouldNameNoMember_WhenBuiltWithThePositionalConstructorAlone()
    {
        var at = DateTimeOffset.UnixEpoch;
        var positional = new CallConnectedEvent("s1", "srv-1", at, null, "sales", TimeSpan.Zero);
        var named = positional with { AgentInterface = First };

        (string, string, DateTimeOffset, string?, string?, TimeSpan) positionalMembers =
            ("s1", "srv-1", at, null, "sales", TimeSpan.Zero);

        positional.AgentInterface.Should().BeNull("the positional constructor alone names no member");
        named.AgentInterface.Should().Be(First, "the member is set with its init accessor");
        positional.Should().NotBe(named, "two notifications that differ only by the member are not equal");
        Deconstructed(named).Should().Be(positionalMembers,
            "the member is outside the positional ones: Deconstruct is as before");
    }

    private static (string, string, DateTimeOffset, string?, string?, TimeSpan) Deconstructed(CallConnectedEvent e)
    {
        var (sessionId, serverId, timestamp, agentId, queueName, waitTime) = e;
        return (sessionId, serverId, timestamp, agentId, queueName, waitTime);
    }

    /// <summary>A caller outside any queue that dials <paramref name="device"/>: its channel, and the dial's start.</summary>
    private static ManagerEvent[] DialingCaller(string caller, string calleeLeg, string device) =>
    [
        NewChannel(caller, CallerChannel(caller), "Ring", caller, context: "from-trunk", exten: "600"),
        NewChannel(calleeLeg, MemberChannel(device, calleeLeg), "Down", caller, context: "agents"),
        new DialBeginEvent
        {
            EventType = "DialBegin", UniqueId = caller, Channel = CallerChannel(caller), DestUniqueid = calleeLeg,
            DestChannel = MemberChannel(device, calleeLeg), DialString = device,
        },
    ];

    private static (string? Queue, string? Member)[] Connected(WrapUpRig rig, string sessionId) =>
    [
        .. rig.Events.OfType<CallConnectedEvent>()
            .Where(e => e.SessionId == sessionId)
            .Select(e => (e.QueueName, e.AgentInterface)),
    ];
}
