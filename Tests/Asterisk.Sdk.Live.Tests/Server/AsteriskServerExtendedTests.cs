using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Events;
using Asterisk.Sdk.Live;
using Asterisk.Sdk.Live.Server;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Asterisk.Sdk.Live.Tests.Server;

public sealed class AsteriskServerExtendedTests : IAsyncDisposable
{
    private readonly IAmiConnection _connection;
    private readonly AsteriskServer _sut;
    private IObserver<ManagerEvent>? _capturedObserver;

    public AsteriskServerExtendedTests()
    {
        _connection = Substitute.For<IAmiConnection>();
        _connection.Subscribe(Arg.Do<IObserver<ManagerEvent>>(obs => _capturedObserver = obs))
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

    private static async IAsyncEnumerable<ManagerEvent> ToAsyncEnumerable(ManagerEvent evt)
    {
        await Task.CompletedTask;
        yield return evt;
    }

    private static async IAsyncEnumerable<ManagerEvent> ToAsyncEnumerable(
        ManagerEvent evt1, ManagerEvent evt2, ManagerEvent evt3)
    {
        await Task.CompletedTask;
        yield return evt1;
        yield return evt2;
        yield return evt3;
    }

    // ── OriginateAsync edge cases ──

    [Fact]
    public async Task OriginateAsync_ShouldReturnFailure_WhenNoOriginateResponseReceived()
    {
        _connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(EmptyAsyncEnumerable());

        await _sut.StartAsync();
        var result = await _sut.OriginateAsync("PJSIP/2000", "default", "100");

        result.Success.Should().BeFalse();
        result.Message.Should().Be("No OriginateResponse received");
        result.ChannelId.Should().BeNull();
    }

    [Fact]
    public async Task OriginateAsync_ShouldReturnFailure_WhenResponseIsNotSuccess()
    {
        var failedResponse = new OriginateResponseEvent
        {
            Response = "Failure",
            Channel = null
        };
        _connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(failedResponse));

        await _sut.StartAsync();
        var result = await _sut.OriginateAsync("PJSIP/2000", "default", "100");

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Failure");
    }

    // ── EventObserver routing: Bridge events ──

    [Fact]
    public async Task EventObserver_ShouldRouteBridgeCreateEvent()
    {
        await _sut.StartAsync();

        _capturedObserver!.OnNext(new BridgeCreateEvent
        {
            BridgeUniqueid = "br-1",
            BridgeType = "mixing",
            BridgeTechnology = "simple_bridge",
            BridgeCreator = "test",
            BridgeName = "conf-1"
        });

        _sut.Bridges.GetById("br-1").Should().NotBeNull();
        _sut.Bridges.GetById("br-1")!.BridgeType.Should().Be("mixing");
    }

    [Fact]
    public async Task EventObserver_ShouldRouteBridgeDestroyEvent()
    {
        await _sut.StartAsync();

        _capturedObserver!.OnNext(new BridgeCreateEvent
        {
            BridgeUniqueid = "br-1", BridgeType = "mixing"
        });
        _capturedObserver.OnNext(new BridgeDestroyEvent { BridgeUniqueid = "br-1" });

        // BridgeManager marks DestroyedAt but keeps the bridge in state
        var bridge = _sut.Bridges.GetById("br-1");
        bridge.Should().NotBeNull();
        bridge!.DestroyedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task EventObserver_ShouldRouteBridgeEnterEvent_AndLinkChannels()
    {
        await _sut.StartAsync();

        // Create bridge and two channels
        _capturedObserver!.OnNext(new BridgeCreateEvent { BridgeUniqueid = "br-1" });
        _capturedObserver.OnNext(new NewChannelEvent
        {
            UniqueId = "uid-1", Channel = "PJSIP/100-001", ChannelState = "Up"
        });
        _capturedObserver.OnNext(new NewChannelEvent
        {
            UniqueId = "uid-2", Channel = "PJSIP/200-001", ChannelState = "Up"
        });

        // First channel enters bridge
        _capturedObserver.OnNext(new BridgeEnterEvent { BridgeUniqueid = "br-1", UniqueId = "uid-1" });
        // Second channel enters bridge (now 2 channels = link)
        _capturedObserver.OnNext(new BridgeEnterEvent { BridgeUniqueid = "br-1", UniqueId = "uid-2" });

        var ch1 = _sut.Channels.GetByUniqueId("uid-1");
        var ch2 = _sut.Channels.GetByUniqueId("uid-2");
        ch1!.LinkedChannel.Should().BeSameAs(ch2);
        ch2!.LinkedChannel.Should().BeSameAs(ch1);
    }

    [Fact]
    public async Task EventObserver_ShouldRouteBridgeLeaveEvent_AndUnlinkChannels()
    {
        await _sut.StartAsync();

        _capturedObserver!.OnNext(new BridgeCreateEvent { BridgeUniqueid = "br-1" });
        _capturedObserver.OnNext(new NewChannelEvent
        {
            UniqueId = "uid-1", Channel = "PJSIP/100-001", ChannelState = "Up"
        });
        _capturedObserver.OnNext(new NewChannelEvent
        {
            UniqueId = "uid-2", Channel = "PJSIP/200-001", ChannelState = "Up"
        });
        _capturedObserver.OnNext(new BridgeEnterEvent { BridgeUniqueid = "br-1", UniqueId = "uid-1" });
        _capturedObserver.OnNext(new BridgeEnterEvent { BridgeUniqueid = "br-1", UniqueId = "uid-2" });

        // Now leave
        _capturedObserver.OnNext(new BridgeLeaveEvent { BridgeUniqueid = "br-1", UniqueId = "uid-1" });

        var ch1 = _sut.Channels.GetByUniqueId("uid-1");
        var ch2 = _sut.Channels.GetByUniqueId("uid-2");
        ch1!.LinkedChannel.Should().BeNull();
        ch2!.LinkedChannel.Should().BeNull();
    }

    // ── EventObserver routing: Dial events ──

    [Fact]
    public async Task EventObserver_ShouldRouteDialBeginEvent()
    {
        await _sut.StartAsync();

        _capturedObserver!.OnNext(new NewChannelEvent
        {
            UniqueId = "uid-1", Channel = "PJSIP/100-001", ChannelState = "Ring"
        });
        _capturedObserver.OnNext(new DialBeginEvent
        {
            UniqueId = "uid-1", DestUniqueid = "uid-2",
            DestChannel = "PJSIP/200-001", DialString = "PJSIP/200"
        });

        _sut.Channels.GetByUniqueId("uid-1")!.DialedChannel.Should().Be("PJSIP/200-001");
    }

    [Fact]
    public async Task EventObserver_ShouldRouteDialEndEvent()
    {
        await _sut.StartAsync();

        _capturedObserver!.OnNext(new NewChannelEvent
        {
            UniqueId = "uid-1", Channel = "PJSIP/100-001", ChannelState = "Ring"
        });
        _capturedObserver.OnNext(new DialEndEvent
        {
            UniqueId = "uid-1", DialStatus = "ANSWER"
        });

        _sut.Channels.GetByUniqueId("uid-1")!.DialStatus.Should().Be("ANSWER");
    }

    // ── EventObserver routing: Hold events ──

    [Fact]
    public async Task EventObserver_ShouldRouteHoldEvent()
    {
        await _sut.StartAsync();

        _capturedObserver!.OnNext(new NewChannelEvent
        {
            UniqueId = "uid-1", Channel = "PJSIP/100-001", ChannelState = "Up"
        });
        _capturedObserver.OnNext(new HoldEvent
        {
            UniqueId = "uid-1", MusicClass = "jazz"
        });

        var ch = _sut.Channels.GetByUniqueId("uid-1")!;
        ch.IsOnHold.Should().BeTrue();
        ch.HoldMusicClass.Should().Be("jazz");
    }

    [Fact]
    public async Task EventObserver_ShouldRouteUnholdEvent()
    {
        await _sut.StartAsync();

        _capturedObserver!.OnNext(new NewChannelEvent
        {
            UniqueId = "uid-1", Channel = "PJSIP/100-001", ChannelState = "Up"
        });
        _capturedObserver.OnNext(new HoldEvent { UniqueId = "uid-1" });
        _capturedObserver.OnNext(new UnholdEvent { UniqueId = "uid-1" });

        _sut.Channels.GetByUniqueId("uid-1")!.IsOnHold.Should().BeFalse();
    }

    // ── EventObserver routing: Queue events ──

    [Fact]
    public async Task EventObserver_ShouldRouteQueueMemberRemovedEvent()
    {
        await _sut.StartAsync();

        _capturedObserver!.OnNext(new QueueMemberAddedEvent
        {
            Queue = "sales", Interface = "PJSIP/2000", MemberName = "Agent 2000"
        });
        _capturedObserver.OnNext(new QueueMemberRemovedEvent
        {
            Queue = "sales", Interface = "PJSIP/2000"
        });

        _sut.Queues.GetByName("sales")!.MemberCount.Should().Be(0);
    }

    [Fact]
    public async Task EventObserver_ShouldRouteQueueMemberPausedEvent()
    {
        await _sut.StartAsync();

        _capturedObserver!.OnNext(new QueueMemberAddedEvent
        {
            Queue = "sales", Interface = "PJSIP/2000", MemberName = "Agent 2000"
        });
        _capturedObserver.OnNext(new QueueMemberPausedEvent
        {
            Queue = "sales", Interface = "PJSIP/2000", Paused = true, Reason = "Break"
        });

        var member = _sut.Queues.GetByName("sales")!.Members["PJSIP/2000"];
        member.Paused.Should().BeTrue();
        member.PausedReason.Should().Be("Break");
    }

    [Fact]
    public async Task EventObserver_ShouldRouteQueueMemberStatusEvent()
    {
        await _sut.StartAsync();

        _capturedObserver!.OnNext(new QueueMemberAddedEvent
        {
            Queue = "sales", Interface = "PJSIP/2000", MemberName = "Agent 2000", Status = 1
        });
        _capturedObserver.OnNext(new QueueMemberStatusEvent
        {
            Queue = "sales", Interface = "PJSIP/2000", Status = 6
        });

        _sut.Queues.GetByName("sales")!.Members["PJSIP/2000"].Status
            .Should().Be(QueueMemberState.DeviceRinging);
    }

    // ── EventObserver routing: DeviceStateChange ──

    [Fact]
    public async Task EventObserver_ShouldRouteDeviceStateChangeEvent()
    {
        await _sut.StartAsync();

        _capturedObserver!.OnNext(new QueueMemberAddedEvent
        {
            Queue = "sales", Interface = "PJSIP/2000", MemberName = "Agent 2000"
        });
        _capturedObserver.OnNext(new DeviceStateChangeEvent
        {
            Device = "PJSIP/2000", State = "INUSE"
        });

        _sut.Queues.GetByName("sales")!.Members["PJSIP/2000"].Status
            .Should().Be(QueueMemberState.DeviceInUse);
    }

    // ── EventObserver routing: NewState ──

    [Fact]
    public async Task EventObserver_ShouldRouteNewStateEvent()
    {
        await _sut.StartAsync();

        _capturedObserver!.OnNext(new NewChannelEvent
        {
            UniqueId = "uid-1", Channel = "PJSIP/100-001", ChannelState = "Ringing"
        });
        _capturedObserver.OnNext(new NewStateEvent
        {
            UniqueId = "uid-1", ChannelState = "6"
        });

        _sut.Channels.GetByUniqueId("uid-1")!.State
            .Should().Be(Asterisk.Sdk.Enums.ChannelState.Up);
    }

    // ── EventObserver routing: Rename ──

    [Fact]
    public async Task EventObserver_ShouldRouteRenameEvent()
    {
        await _sut.StartAsync();

        _capturedObserver!.OnNext(new NewChannelEvent
        {
            UniqueId = "uid-1", Channel = "PJSIP/100-001", ChannelState = "Up"
        });
        _capturedObserver.OnNext(new RenameEvent
        {
            UniqueId = "uid-1",
            RawFields = new Dictionary<string, string> { ["Newname"] = "PJSIP/100-001<MASQ>" }
        });

        _sut.Channels.GetByUniqueId("uid-1")!.Name.Should().Be("PJSIP/100-001<MASQ>");
    }

    // ── EventObserver routing: Agent events ──

    [Fact]
    public async Task EventObserver_ShouldRouteAgentLogoffEvent()
    {
        await _sut.StartAsync();

        _capturedObserver!.OnNext(new AgentLoginEvent { Agent = "1001", Channel = "PJSIP/1001" });
        _capturedObserver.OnNext(new AgentLogoffEvent { Agent = "1001" });

        _sut.Agents.GetById("1001")!.State
            .Should().Be(Asterisk.Sdk.Live.Agents.AgentState.LoggedOff);
    }

    [Fact]
    public async Task EventObserver_ShouldRouteAgentConnectEvent()
    {
        await _sut.StartAsync();

        _capturedObserver!.OnNext(new AgentLoginEvent { Agent = "1001", Channel = "PJSIP/1001" });
        _capturedObserver.OnNext(new AgentConnectEvent
        {
            Agent = "1001", Channel = "PJSIP/1001-001", LinkedId = "linked-1"
        });

        _sut.Agents.GetById("1001")!.State
            .Should().Be(Asterisk.Sdk.Live.Agents.AgentState.OnCall);
    }

    [Fact]
    public async Task EventObserver_ShouldRouteAgentCompleteEvent()
    {
        await _sut.StartAsync();

        _capturedObserver!.OnNext(new AgentLoginEvent { Agent = "1001", Channel = "PJSIP/1001" });
        _capturedObserver.OnNext(new AgentConnectEvent { Agent = "1001", Channel = "PJSIP/1001-001" });
        _capturedObserver.OnNext(new AgentCompleteEvent
        {
            Agent = "1001", TalkTime = 120, HoldTime = 10
        });

        var agent = _sut.Agents.GetById("1001")!;
        agent.State.Should().Be(Asterisk.Sdk.Live.Agents.AgentState.Available);
        agent.TotalTalkTimeSecs.Should().BeGreaterThanOrEqualTo(120);
    }

    // ── AsteriskVersion property ──

    [Fact]
    public void AsteriskVersion_ShouldDelegateToConnection()
    {
        _connection.AsteriskVersion.Returns("Asterisk 22.0.0");

        _sut.AsteriskVersion.Should().Be("Asterisk 22.0.0");
    }

    // ── Connection property ──

    [Fact]
    public void Connection_ShouldReturnUnderlyingConnection()
    {
        _sut.Connection.Should().BeSameAs(_connection);
    }

    // ── StartAsync with initial agents (AGENT_LOGGEDOFF path) ──

    [Fact]
    public async Task StartAsync_ShouldRegisterLoggedOffAgents()
    {
        var agentEvent = new AgentsEvent
        {
            Agent = "3001",
            Name = "Agent Three",
            Status = "AGENT_LOGGEDOFF"
        };
        var callCount = 0;
        _connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                if (callCount == 3) return ToAsyncEnumerable(agentEvent);
                return EmptyAsyncEnumerable();
            });

        await _sut.StartAsync();

        _sut.Agents.AgentCount.Should().Be(1);
        _sut.Agents.GetById("3001")!.State
            .Should().Be(Asterisk.Sdk.Live.Agents.AgentState.LoggedOff);
    }

    // ── StartAsync with QueueMember and QueueEntry in initial load ──

    [Fact]
    public async Task StartAsync_ShouldPopulateQueueMembersAndEntries()
    {
        var queueParams = new QueueParamsEvent { Queue = "support", Max = 10, Strategy = "ringall" };
        var queueMember = new QueueMemberEvent
        {
            Queue = "support", Interface = "PJSIP/100", MemberName = "Agent 100",
            Penalty = 0, Paused = false, Status = 1
        };
        var queueEntry = new QueueEntryEvent
        {
            Queue = "support", Channel = "PJSIP/5551234-001", CallerId = "5551234", Position = 1
        };
        var callCount = 0;
        _connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                if (callCount == 2)
                    return ToAsyncEnumerable(queueParams, queueMember, queueEntry);
                return EmptyAsyncEnumerable();
            });

        await _sut.StartAsync();

        var queue = _sut.Queues.GetByName("support");
        queue.Should().NotBeNull();
        queue!.MemberCount.Should().Be(1);
        queue.EntryCount.Should().Be(1);
    }
}
