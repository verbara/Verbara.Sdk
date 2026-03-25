using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Events;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Live.Agents;
using Asterisk.Sdk.Live.Server;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Asterisk.Sdk.Live.Tests.Server;

public sealed class AsteriskServerEventRoutingTests : IAsyncDisposable
{
    private readonly IAmiConnection _connection;
    private readonly AsteriskServer _sut;
    private IObserver<ManagerEvent>? _observer;

    public AsteriskServerEventRoutingTests()
    {
        _connection = Substitute.For<IAmiConnection>();

        _connection.Subscribe(Arg.Do<IObserver<ManagerEvent>>(obs => _observer = obs))
            .Returns(Substitute.For<IDisposable>());

        _connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(EmptyAsyncEnumerable());

        _sut = new AsteriskServer(_connection, NullLogger<AsteriskServer>.Instance);
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
