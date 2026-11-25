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
        session.AgentInterface.Should().Be(Second, "the call records the member of its most recent connection");
        session.AgentId.Should().BeNull("neither connect named an agent");
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

    /// <summary>
    /// The connect of an agent known by name that finds the call already connected: the agent's leg is reported
    /// answered before the connect, the order Asterisk sends a leg's answer and app_queue's connect report in. The leg's
    /// answer made the connection and announced it with no agent; the connect that follows makes none, so it records
    /// no agent, and the call says what its one announcement said. The frames are synthetic: no capture holds an
    /// <c>AgentConnect</c> naming an agent.
    /// </summary>
    [Fact]
    public async Task AgentConnected_ShouldRecordNoAgent_WhenAKnownAgentsConnectFindsTheCallAlreadyConnectedByItsLegsAnswer()
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(AgentLogin("agent-4", "PJSIP/agent4", "login-4"));
        rig.Deliver(
            NewChannel("n1", CallerChannel("n1"), "Down", "n1", context: "from-trunk", exten: "600"),
            NewState("n1", "Ring"),
            NewChannel("nm1", MemberChannel("PJSIP/agent4", "nm1"), "Down", "n1", context: "agents"),
            new DialBeginEvent
            {
                EventType = "DialBegin", UniqueId = "n1", Channel = CallerChannel("n1"), DestUniqueid = "nm1",
                DestChannel = MemberChannel("PJSIP/agent4", "nm1"), DialString = "PJSIP/agent4",
            },
            NewState("nm1", "Up"));
        var session = rig.Session("n1");
        var beforeConnect = session.State;
        rig.Deliver(
            AgentConnect("", "n1", CallerChannel("n1"), "n1", "PJSIP/agent4", "nm1", MemberChannel("PJSIP/agent4", "nm1"),
                "agent-4"),
            NewState("n1", "Up"),
            BridgeCreate(Bridge("n1")),
            BridgeEnter(Bridge("n1"), "nm1"),
            BridgeEnter(Bridge("n1"), "n1"));

        var connected = rig.Events.OfType<CallConnectedEvent>().Where(e => e.SessionId == session.SessionId).ToList();

        beforeConnect.Should().Be(CallSessionState.Connected, "premise: the leg's answer connected the call");
        connected.Should().ContainSingle("only the leg's answer made a connection")
            .Which.AgentId.Should().BeNull("the leg's answer names no agent");
        (session.AgentId, session.AgentInterface).Should().Be((connected[0].AgentId, connected[0].AgentInterface),
            "a connect that makes no connection records neither its agent nor its interface, so the call says what its "
            + "one announcement said");
    }

    [Fact]
    public async Task BridgeEntered_ShouldNameNoMember_WhenACallThatNeverJoinedAQueueConnectsAgainAfterATransfer()
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

        (string? Queue, string? Member)[] expected = [(null, "PJSIP/agent4"), (null, null)];

        Connected(rig, session.SessionId).Should().Equal(expected,
            "the agent's connect announces the call with the agent's interface; after the transfer the call's "
            + "bridge announces it connected again, and no member's connect made that connection");
        (session.AgentId, session.AgentInterface).Should().Be(((string?)null, (string?)null),
            "the bridge entry made the call's most recent connection and names no agent and no member, so the call "
            + "no longer records agent-4");
    }

    [Fact]
    public async Task LegAnswered_ShouldClearTheAgentAndTheMember_WhenAQueuedCallAKnownAgentTookReturnsFromABlindTransfer()
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(AgentLogin("agent-1", First, "login-1"));
        rig.Deliver(QueueCallConnected("sales", "c1", "m1", First, agent: "agent-1"));
        var session = rig.Session("c1");
        var afterConnect = (session.AgentId, session.AgentInterface);
        (string? AgentId, string? AgentInterface)? atReturn = null;
        using var reader = rig.Manager.Events.Subscribe(evt =>
        {
            if (evt is CallConnectedEvent c && c.SessionId == session.SessionId)
                atReturn = (session.AgentId, session.AgentInterface);
        });
        rig.Deliver(MemberBlindTransfers("sales", "c1", "m1", First, "from-trunk", "700", agent: "agent-1"));
        rig.Deliver(
            NewChannel("t1", MemberChannel("PJSIP/ext700", "t1"), "Down", "c1", context: "from-trunk"),
            NewState("t1", "Up"),
            BridgeCreate("bridge-c1-2"),
            BridgeEnter("bridge-c1-2", "t1"),
            BridgeEnter("bridge-c1-2", "c1"));

        afterConnect.Should().Be(((string?)"agent-1", (string?)First), "premise: agent-1's connect recorded the agent");
        Connected(rig, session.SessionId).Should().Equal([("sales", First), (null, null)],
            "premise: the extension's answer returns the call to connected, a connection no member made");
        atReturn.Should().Be(((string?)null, (string?)null),
            "the extension's answer made the call's most recent connection, which names no agent and no member, so "
            + "a consumer of the return's announcement no longer reads agent-1");
        (session.AgentId, session.AgentInterface).Should().Be(((string?)null, (string?)null),
            "the call still records no agent and no member after the return");
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
