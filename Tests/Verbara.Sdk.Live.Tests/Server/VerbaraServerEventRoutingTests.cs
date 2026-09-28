using Verbara.Sdk;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Agents;
using Verbara.Sdk.Live.Channels;
using Verbara.Sdk.Live.Server;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Sdk.Live.Tests.Server;

public sealed class VerbaraServerEventRoutingTests : IAsyncDisposable
{
    private readonly IAmiConnection _connection;
    private readonly VerbaraServer _sut;
    private IObserver<ManagerEvent>? _observer;

    public VerbaraServerEventRoutingTests()
    {
        _connection = Substitute.For<IAmiConnection>();

        _connection.Subscribe(Arg.Do<IObserver<ManagerEvent>>(obs => _observer = obs))
            .Returns(Substitute.For<IDisposable>());

        _connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(EmptyAsyncEnumerable());

        _sut = new VerbaraServer(_connection, NullLogger<VerbaraServer>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _sut.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private static async IAsyncEnumerable<ManagerEvent> EmptyAsyncEnumerable()
    {
        await Task.CompletedTask;
        yield break;
    }

    private async Task StartAndGetObserverAsync()
    {
        await _sut.StartAsync();
        _observer.Should().NotBeNull("StartAsync must capture the observer");
    }

    // --- Helper: create a channel first so state/rename/hold/unhold/dial events have a target ---
    private void CreateChannel(string uniqueId = "ch.1", string name = "PJSIP/2000-001")
    {
        _observer!.OnNext(new NewChannelEvent
        {
            UniqueId = uniqueId,
            Channel = name,
            ChannelState = "Up"
        });
    }

    // --- Helper: add a queue member so remove/pause events have a target ---
    private void CreateQueueWithMember(string queue = "support", string iface = "PJSIP/2000")
    {
        _observer!.OnNext(new QueueMemberAddedEvent
        {
            Queue = queue,
            Interface = iface,
            MemberName = "Agent 2000",
            Penalty = 0,
            Paused = false,
            Status = 1
        });
    }

    // --- Helper: login an agent so logoff events have a target ---
    private void LoginAgent(string agentId = "1001", string channel = "PJSIP/1001")
    {
        _observer!.OnNext(new AgentLoginEvent { Agent = agentId, Channel = channel });
    }

    // --- Helper: create a bridge ---
    private void CreateBridge(string bridgeId = "bridge-1")
    {
        _observer!.OnNext(new BridgeCreateEvent
        {
            BridgeUniqueid = bridgeId,
            BridgeType = "basic",
            BridgeTechnology = "simple_bridge"
        });
    }

    // ==========================================================================
    // NewStateEvent -> ChannelManager
    // ==========================================================================

    [Fact]
    public async Task EventObserver_ShouldRouteNewStateEvent_ToChannelManager()
    {
        await StartAndGetObserverAsync();
        CreateChannel("ch.1", "PJSIP/2000-001");

        _observer!.OnNext(new NewStateEvent
        {
            UniqueId = "ch.1",
            ChannelState = "Ringing"
        });

        var channel = _sut.Channels.GetByUniqueId("ch.1");
        channel.Should().NotBeNull();
        channel!.State.Should().Be(ChannelState.Ringing);
    }

    // ==========================================================================
    // RenameEvent -> ChannelManager
    // ==========================================================================

    [Fact]
    public async Task EventObserver_ShouldRouteRenameEvent_ToChannelManager()
    {
        await StartAndGetObserverAsync();
        CreateChannel("ch.1", "PJSIP/2000-001");

        _observer!.OnNext(new RenameEvent
        {
            UniqueId = "ch.1",
            RawFields = new Dictionary<string, string> { ["Newname"] = "PJSIP/2000-002" }
        });

        var channel = _sut.Channels.GetByUniqueId("ch.1");
        channel.Should().NotBeNull();
        channel!.Name.Should().Be("PJSIP/2000-002");
        _sut.Channels.GetByName("PJSIP/2000-002").Should().BeSameAs(channel);
    }

    // ==========================================================================
    // QueueMemberRemovedEvent -> QueueManager
    // ==========================================================================

    [Fact]
    public async Task EventObserver_ShouldRouteQueueMemberRemovedEvent_ToQueueManager()
    {
        await StartAndGetObserverAsync();
        CreateQueueWithMember("support", "PJSIP/2000");

        _sut.Queues.GetByName("support")!.MemberCount.Should().Be(1);

        _observer!.OnNext(new QueueMemberRemovedEvent
        {
            Queue = "support",
            Interface = "PJSIP/2000"
        });

        _sut.Queues.GetByName("support")!.MemberCount.Should().Be(0);
    }

    // ==========================================================================
    // QueueMemberPausedEvent -> QueueManager
    // ==========================================================================

    [Fact]
    public async Task EventObserver_ShouldRouteQueueMemberPausedEvent_ToQueueManager()
    {
        await StartAndGetObserverAsync();
        CreateQueueWithMember("support", "PJSIP/2000");

        _observer!.OnNext(new QueueMemberPausedEvent
        {
            Queue = "support",
            Interface = "PJSIP/2000",
            Paused = true,
            Reason = "lunch break"
        });

        var member = _sut.Queues.GetByName("support")!.Members["PJSIP/2000"];
        member.Paused.Should().BeTrue();
        member.PausedReason.Should().Be("lunch break");
    }

    // ==========================================================================
    // QueueCallerJoinEvent -> QueueManager
    // ==========================================================================

    [Fact]
    public async Task EventObserver_ShouldRouteQueueCallerJoinEvent_ToQueueManager()
    {
        await StartAndGetObserverAsync();

        _observer!.OnNext(new QueueCallerJoinEvent
        {
            Position = 1,
            RawFields = new Dictionary<string, string>
            {
                ["Queue"] = "sales",
                ["Channel"] = "PJSIP/3000-001",
                ["CallerIDNum"] = "5551234"
            }
        });

        var queue = _sut.Queues.GetByName("sales");
        queue.Should().NotBeNull();
        queue!.EntryCount.Should().Be(1);
        queue.Entries.Should().ContainKey("PJSIP/3000-001");
    }

    // ==========================================================================
    // QueueCallerLeaveEvent -> QueueManager
    // ==========================================================================

    [Fact]
    public async Task EventObserver_ShouldRouteQueueCallerLeaveEvent_ToQueueManager()
    {
        await StartAndGetObserverAsync();

        // First join, then leave
        _observer!.OnNext(new QueueCallerJoinEvent
        {
            Position = 1,
            RawFields = new Dictionary<string, string>
            {
                ["Queue"] = "sales",
                ["Channel"] = "PJSIP/3000-001",
                ["CallerIDNum"] = "5551234"
            }
        });

        _observer!.OnNext(new QueueCallerLeaveEvent
        {
            RawFields = new Dictionary<string, string>
            {
                ["Queue"] = "sales",
                ["Channel"] = "PJSIP/3000-001"
            }
        });

        var queue = _sut.Queues.GetByName("sales");
        queue.Should().NotBeNull();
        queue!.EntryCount.Should().Be(0);
    }

    // ==========================================================================
    // AgentLogoffEvent -> AgentManager
    // ==========================================================================

    [Fact]
    public async Task EventObserver_ShouldRouteAgentLogoffEvent_ToAgentManager()
    {
        await StartAndGetObserverAsync();
        LoginAgent("1001");

        _sut.Agents.GetById("1001")!.State.Should().Be(AgentState.Available);

        _observer!.OnNext(new AgentLogoffEvent { Agent = "1001" });

        _sut.Agents.GetById("1001")!.State.Should().Be(AgentState.LoggedOff);
    }

    // ==========================================================================
    // AgentConnectEvent -> AgentManager: app_queue's connect, agent or not
    // ==========================================================================

    // Copied from an AgentConnect captured on Asterisk 22.9.0. The caller is the ;2 half of an
    // originate to a Local channel that runs Queue(), so its Uniqueid and Linkedid differ. The
    // capture names its member by its interface; a member configured with a name carries that name
    // instead, and one is given here so the two arguments can be told apart.
    private const string QueueCallerUniqueId = "1790566855.1";
    private const string QueueCallerLinkedId = "1790566855.0";
    private const string QueueCallerChannel = "Local/qp@dialer-00000000;2";
    private const string QueueMemberInterface = "PJSIP/agent1";
    private const string QueueMemberName = "Agent One";

    // Like every captured AgentConnect, it carries no Agent header unless one is given.
    private static AgentConnectEvent QueueCallerAgentConnect(string? agent = null)
    {
        var fields = new Dictionary<string, string>
        {
            ["Event"] = "AgentConnect", ["Privilege"] = "agent,all",
            ["Channel"] = QueueCallerChannel, ["ChannelState"] = "4", ["ChannelStateDesc"] = "Ring",
            ["CallerIDNum"] = "5550099", ["CallerIDName"] = "LQ", ["Context"] = "dialer", ["Exten"] = "qp",
            ["Priority"] = "1", ["Uniqueid"] = QueueCallerUniqueId, ["Linkedid"] = QueueCallerLinkedId,
            ["DestChannel"] = "PJSIP/agent1-00000000", ["DestChannelState"] = "6", ["DestChannelStateDesc"] = "Up",
            ["DestContext"] = "from-agents", ["DestExten"] = "qp", ["DestPriority"] = "1",
            ["DestUniqueid"] = "1790566855.2", ["DestLinkedid"] = QueueCallerLinkedId,
            ["Queue"] = "q-pjsip", ["Interface"] = QueueMemberInterface, ["MemberName"] = QueueMemberName,
            ["HoldTime"] = "2", ["RingTime"] = "2",
        };
        if (agent is not null)
            fields["Agent"] = agent;

        return new AgentConnectEvent
        {
            EventType = "AgentConnect",
            Privilege = "agent,all",
            Channel = QueueCallerChannel,
            UniqueId = QueueCallerUniqueId,
            LinkedId = QueueCallerLinkedId,
            DestChannel = "PJSIP/agent1-00000000",
            DestChannelState = "6",
            DestChannelStateDesc = "Up",
            DestContext = "from-agents",
            DestExten = "qp",
            DestPriority = "1",
            DestUniqueId = "1790566855.2",
            DestLinkedId = QueueCallerLinkedId,
            Interface = QueueMemberInterface,
            HoldTime = 2,
            Ringtime = 2,
            Agent = agent,
            RawFields = fields,
        };
    }

    [Fact]
    public async Task EventObserver_ShouldRaiseQueueCallerConnected_WhenAgentConnectCarriesNoAgentHeader()
    {
        await StartAndGetObserverAsync();
        var callerConnects = new List<(string? CallerUniqueId, string? MemberName, string? MemberInterface)>();
        _sut.Agents.QueueCallerConnected += (uniqueId, memberName, memberInterface) =>
            callerConnects.Add((uniqueId, memberName, memberInterface));
        var agentConnects = 0;
        _sut.Agents.AgentConnected += (_, _, _) => agentConnects++;

        _observer!.OnNext(QueueCallerAgentConnect());

        callerConnects.Should().ContainSingle("app_queue connected the caller once, and the frame names no agent")
            .Which.Should().Be((QueueCallerUniqueId, QueueMemberName, QueueMemberInterface),
                "the caller is named by its own Uniqueid, not its Linkedid, and the member by its name and interface");
        agentConnects.Should().Be(0, "no agent the manager knows is named, so the public event stays silent as before");
    }

    [Fact]
    public async Task EventObserver_ShouldRaiseAgentConnectedAsBeforeAndThenQueueCallerConnected_WhenAgentConnectNamesAKnownAgent()
    {
        await StartAndGetObserverAsync();
        LoginAgent("1001", "PJSIP/1001");
        var raised = new List<(string Event, string? First, string? Second, string? Third)>();
        _sut.Agents.AgentConnected += (agentId, linkedId, memberInterface) =>
            raised.Add((nameof(AgentManager.AgentConnected), agentId, linkedId, memberInterface));
        _sut.Agents.QueueCallerConnected += (uniqueId, memberName, memberInterface) =>
            raised.Add((nameof(AgentManager.QueueCallerConnected), uniqueId, memberName, memberInterface));

        _observer!.OnNext(QueueCallerAgentConnect(agent: "1001"));

        raised.Should().Equal(
            [
                (nameof(AgentManager.AgentConnected), "1001", QueueCallerLinkedId, QueueMemberInterface),
                (nameof(AgentManager.QueueCallerConnected), QueueCallerUniqueId, QueueMemberName, QueueMemberInterface),
            ],
            "a known agent's handlers receive the Linkedid and interface they always did, and run before the internal event");
        var agent = _sut.Agents.GetById("1001")!;
        agent.State.Should().Be(AgentState.OnCall);
        agent.TalkingTo.Should().Be(QueueCallerChannel);
    }

    // ==========================================================================
    // MeetMeJoinEvent -> MeetMeManager
    // ==========================================================================

    [Fact]
    public async Task EventObserver_ShouldRouteMeetMeJoinEvent_ToMeetMeManager()
    {
        await StartAndGetObserverAsync();

#pragma warning disable CS0618 // MeetMe events are obsolete but still received from Asterisk 18-20
        _observer!.OnNext(new MeetMeJoinEvent
        {
            Meetme = "300",
            Usernum = 1,
            Channel = "PJSIP/2000-001"
        });
#pragma warning restore CS0618

        var room = _sut.MeetMe.GetRoom("300");
        room.Should().NotBeNull();
        room!.UserCount.Should().Be(1);
    }

    // ==========================================================================
    // MeetMeLeaveEvent -> MeetMeManager
    // ==========================================================================

    [Fact]
    public async Task EventObserver_ShouldRouteMeetMeLeaveEvent_ToMeetMeManager()
    {
        await StartAndGetObserverAsync();

#pragma warning disable CS0618
        _observer!.OnNext(new MeetMeJoinEvent
        {
            Meetme = "300",
            Usernum = 1,
            Channel = "PJSIP/2000-001"
        });

        _observer!.OnNext(new MeetMeLeaveEvent
        {
            Meetme = "300",
            Usernum = 1
        });
#pragma warning restore CS0618

        _sut.MeetMe.GetRoom("300").Should().BeNull("room should be removed when last user leaves");
    }

    // ==========================================================================
    // BridgeCreateEvent -> BridgeManager
    // ==========================================================================

    [Fact]
    public async Task EventObserver_ShouldRouteBridgeCreateEvent_ToBridgeManager()
    {
        await StartAndGetObserverAsync();

        _observer!.OnNext(new BridgeCreateEvent
        {
            BridgeUniqueid = "bridge-1",
            BridgeType = "basic",
            BridgeTechnology = "simple_bridge",
            BridgeCreator = "dialplan",
            BridgeName = "test-bridge"
        });

        _sut.Bridges.BridgeCount.Should().Be(1);
        var bridge = _sut.Bridges.GetById("bridge-1");
        bridge.Should().NotBeNull();
        bridge!.BridgeType.Should().Be("basic");
        bridge.Technology.Should().Be("simple_bridge");
    }

    // ==========================================================================
    // BridgeEnterEvent -> BridgeManager
    // ==========================================================================

    [Fact]
    public async Task EventObserver_ShouldRouteBridgeEnterEvent_ToBridgeManager()
    {
        await StartAndGetObserverAsync();
        CreateBridge("bridge-1");

        _observer!.OnNext(new BridgeEnterEvent
        {
            BridgeUniqueid = "bridge-1",
            UniqueId = "ch.1"
        });

        var bridge = _sut.Bridges.GetById("bridge-1");
        bridge.Should().NotBeNull();
        bridge!.Channels.Should().ContainKey("ch.1");
    }

    [Fact]
    public async Task EventObserver_ShouldLinkChannels_WhenSecondChannelEntersBridge()
    {
        await StartAndGetObserverAsync();
        CreateChannel("ch.1", "PJSIP/2000-001");
        CreateChannel("ch.2", "PJSIP/3000-001");
        CreateBridge("bridge-1");

        _observer!.OnNext(new BridgeEnterEvent { BridgeUniqueid = "bridge-1", UniqueId = "ch.1" });
        _observer!.OnNext(new BridgeEnterEvent { BridgeUniqueid = "bridge-1", UniqueId = "ch.2" });

        var ch1 = _sut.Channels.GetByUniqueId("ch.1");
        var ch2 = _sut.Channels.GetByUniqueId("ch.2");
        ch1!.LinkedChannel.Should().BeSameAs(ch2);
        ch2!.LinkedChannel.Should().BeSameAs(ch1);
    }

    // ==========================================================================
    // BridgeLeaveEvent -> BridgeManager
    // ==========================================================================

    [Fact]
    public async Task EventObserver_ShouldRouteBridgeLeaveEvent_ToBridgeManager()
    {
        await StartAndGetObserverAsync();
        CreateChannel("ch.1", "PJSIP/2000-001");
        CreateBridge("bridge-1");

        _observer!.OnNext(new BridgeEnterEvent { BridgeUniqueid = "bridge-1", UniqueId = "ch.1" });
        _sut.Bridges.GetById("bridge-1")!.Channels.Should().ContainKey("ch.1");

        _observer!.OnNext(new BridgeLeaveEvent { BridgeUniqueid = "bridge-1", UniqueId = "ch.1" });

        _sut.Bridges.GetById("bridge-1")!.Channels.Should().NotContainKey("ch.1");
    }

    [Fact]
    public async Task EventObserver_ShouldUnlinkChannels_WhenChannelLeavesBridge()
    {
        await StartAndGetObserverAsync();
        CreateChannel("ch.1", "PJSIP/2000-001");
        CreateChannel("ch.2", "PJSIP/3000-001");
        CreateBridge("bridge-1");

        _observer!.OnNext(new BridgeEnterEvent { BridgeUniqueid = "bridge-1", UniqueId = "ch.1" });
        _observer!.OnNext(new BridgeEnterEvent { BridgeUniqueid = "bridge-1", UniqueId = "ch.2" });

        // Verify linked
        _sut.Channels.GetByUniqueId("ch.1")!.LinkedChannel.Should().NotBeNull();

        _observer!.OnNext(new BridgeLeaveEvent { BridgeUniqueid = "bridge-1", UniqueId = "ch.1" });

        _sut.Channels.GetByUniqueId("ch.1")!.LinkedChannel.Should().BeNull();
        _sut.Channels.GetByUniqueId("ch.2")!.LinkedChannel.Should().BeNull();
    }

    // ==========================================================================
    // BridgeDestroyEvent -> BridgeManager
    // ==========================================================================

    [Fact]
    public async Task EventObserver_ShouldRouteBridgeDestroyEvent_ToBridgeManager()
    {
        await StartAndGetObserverAsync();
        CreateBridge("bridge-1");

        _sut.Bridges.BridgeCount.Should().Be(1);

        _observer!.OnNext(new BridgeDestroyEvent { BridgeUniqueid = "bridge-1" });

        var bridge = _sut.Bridges.GetById("bridge-1");
        bridge.Should().NotBeNull();
        bridge!.DestroyedAt.Should().NotBeNull();
    }

    // ==========================================================================
    // DialBeginEvent -> ChannelManager
    // ==========================================================================

    [Fact]
    public async Task EventObserver_ShouldRouteDialBeginEvent_ToChannelManager()
    {
        await StartAndGetObserverAsync();
        CreateChannel("ch.1", "PJSIP/2000-001");

        _observer!.OnNext(new DialBeginEvent
        {
            UniqueId = "ch.1",
            DestUniqueid = "ch.2",
            DestChannel = "PJSIP/3000-001",
            DialString = "3000"
        });

        var channel = _sut.Channels.GetByUniqueId("ch.1");
        channel.Should().NotBeNull();
        channel!.DialedChannel.Should().Be("PJSIP/3000-001");
    }

    // ==========================================================================
    // DialBeginEvent / DialEndEvent that name no calling channel (an AMI Originate)
    // ==========================================================================
    //
    // For an AMI Originate, Asterisk's DialBegin and DialEnd name only the dialed side: no Channel and
    // no Uniqueid. The field values below are copied from the S3 frames of the Asterisk 22.9.0 capture
    // (Tests/Verbara.Sdk.Sessions.FunctionalTests/Recordings/asterisk-ami/). The dialed leg is created
    // first, so attributing the event to DestUniqueid instead of skipping it would be caught as well.
    // A dial event that does name its calling channel still reaches ChannelManager:
    // EventObserver_ShouldRouteDialBeginEvent_ToChannelManager above binds that.

    private const string OriginatedLegUniqueId = "1790513796.2";
    private const string OriginatedLegChannel = "PJSIP/tocaller-00000002";

    private void CreateOriginatedLeg()
    {
        _observer!.OnNext(new NewChannelEvent
        {
            Privilege = "call,all",
            Channel = OriginatedLegChannel,
            ChannelState = "0",
            ChannelStateDesc = "Down",
            CallerIdNum = "<unknown>",
            CallerIdName = "<unknown>",
            Context = "from-external",
            Exten = "s",
            Priority = 1,
            UniqueId = OriginatedLegUniqueId,
            Linkedid = OriginatedLegUniqueId,
        });
    }

    [Fact]
    public async Task EventObserver_ShouldSkipDialBeginEvent_WhenItNamesNoCallingChannel()
    {
        await StartAndGetObserverAsync();
        CreateOriginatedLeg();
        var raised = new List<AsteriskChannel>();
        _sut.Channels.ChannelDialBegin += raised.Add;

        var originateDialBegin = new DialBeginEvent
        {
            Privilege = "call,all",
            DestChannel = OriginatedLegChannel,
            DestChannelState = "0",
            DestChannelStateDesc = "Down",
            DestCallerIdNum = "1003",
            DestCallerIdName = "N5",
            DestConnectedLineNum = "1003",
            DestConnectedLineName = "N5",
            DestLanguage = "en",
            DestAccountCode = "",
            DestContext = "from-external",
            DestExten = "s",
            DestPriority = 1,
            DestUniqueid = OriginatedLegUniqueId,
            DestLinkedid = OriginatedLegUniqueId,
            DialString = "400@tocaller",
        };

        var deliver = () => _observer!.OnNext(originateDialBegin);

        deliver.Should().NotThrow("an originate's DialBegin names no calling channel, so the observer skips it");
        raised.Should().BeEmpty("a dial event with no calling channel is attributed to no channel");
        _sut.Channels.GetByUniqueId(OriginatedLegUniqueId)!.DialedChannel.Should().BeNull();
    }

    [Fact]
    public async Task EventObserver_ShouldSkipDialEndEvent_WhenItNamesNoCallingChannel()
    {
        await StartAndGetObserverAsync();
        CreateOriginatedLeg();
        var raised = new List<AsteriskChannel>();
        _sut.Channels.ChannelDialEnd += raised.Add;

        var originateDialEnd = new DialEndEvent
        {
            Privilege = "call,all",
            DestChannel = OriginatedLegChannel,
            DestChannelState = "6",
            DestChannelStateDesc = "Up",
            DestCallerIdNum = "1003",
            DestCallerIdName = "N5",
            DestConnectedLineNum = "1003",
            DestConnectedLineName = "N5",
            DestLanguage = "en",
            DestAccountCode = "",
            DestContext = "from-external",
            DestExten = "s",
            DestPriority = 1,
            DestUniqueid = OriginatedLegUniqueId,
            DestLinkedid = OriginatedLegUniqueId,
            DialStatus = "ANSWER",
        };

        var deliver = () => _observer!.OnNext(originateDialEnd);

        deliver.Should().NotThrow("an originate's DialEnd names no calling channel, so the observer skips it");
        raised.Should().BeEmpty("a dial event with no calling channel is attributed to no channel");
        _sut.Channels.GetByUniqueId(OriginatedLegUniqueId)!.DialStatus.Should().BeNull();
    }

    // ==========================================================================
    // HoldEvent -> ChannelManager
    // ==========================================================================

    [Fact]
    public async Task EventObserver_ShouldRouteHoldEvent_ToChannelManager()
    {
        await StartAndGetObserverAsync();
        CreateChannel("ch.1", "PJSIP/2000-001");

        _observer!.OnNext(new HoldEvent
        {
            UniqueId = "ch.1",
            MusicClass = "default"
        });

        var channel = _sut.Channels.GetByUniqueId("ch.1");
        channel.Should().NotBeNull();
        channel!.IsOnHold.Should().BeTrue();
        channel.HoldMusicClass.Should().Be("default");
    }

    // ==========================================================================
    // UnholdEvent -> ChannelManager
    // ==========================================================================

    [Fact]
    public async Task EventObserver_ShouldRouteUnholdEvent_ToChannelManager()
    {
        await StartAndGetObserverAsync();
        CreateChannel("ch.1", "PJSIP/2000-001");

        // First hold
        _observer!.OnNext(new HoldEvent { UniqueId = "ch.1", MusicClass = "default" });
        _sut.Channels.GetByUniqueId("ch.1")!.IsOnHold.Should().BeTrue();

        // Then unhold
        _observer!.OnNext(new UnholdEvent { UniqueId = "ch.1" });

        var channel = _sut.Channels.GetByUniqueId("ch.1");
        channel!.IsOnHold.Should().BeFalse();
        channel.HoldMusicClass.Should().BeNull();
    }
}
