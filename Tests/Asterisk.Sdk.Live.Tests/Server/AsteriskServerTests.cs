using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Events;
using Asterisk.Sdk.Live.Server;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Asterisk.Sdk.Live.Tests.Server;

public sealed class AsteriskServerTests : IAsyncDisposable
{
    private readonly IAmiConnection _connection;
    private readonly AsteriskServer _sut;
    private IObserver<ManagerEvent>? _capturedObserver;

    public AsteriskServerTests()
    {
        _connection = Substitute.For<IAmiConnection>();

        // Capture the observer when Subscribe is called
        _connection.Subscribe(Arg.Do<IObserver<ManagerEvent>>(obs => _capturedObserver = obs))
            .Returns(Substitute.For<IDisposable>());

        // Return empty event streams for initial state load
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

    [Fact]
    public async Task StartAsync_ShouldSubscribeToConnection()
    {
        await _sut.StartAsync();

        _connection.Received(1).Subscribe(Arg.Any<IObserver<ManagerEvent>>());
        _capturedObserver.Should().NotBeNull();
    }

    [Fact]
    public async Task StartAsync_ShouldPopulateChannelsFromStatusAction()
    {
        // Return a StatusEvent for initial state
        var statusEvent = new Asterisk.Sdk.Ami.Events.StatusEvent
        {
            UniqueId = "123.1",
            Channel = "PJSIP/2000-001",
            State = "Up"
        };
        var callCount = 0;
        _connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                if (callCount == 1) // StatusAction
                    return ToAsyncEnumerable(statusEvent);
                return EmptyAsyncEnumerable();
            });

        await _sut.StartAsync();

        _sut.Channels.ChannelCount.Should().Be(1);
    }

    [Fact]
    public async Task EventObserver_ShouldRouteNewChannelEvent_ToChannelManager()
    {
        await _sut.StartAsync();
        _capturedObserver.Should().NotBeNull();

        var evt = new NewChannelEvent
        {
            UniqueId = "999.1",
            Channel = "PJSIP/5000-001",
            ChannelState = "Up",
            CallerIdNum = "5551234"
        };
        _capturedObserver!.OnNext(evt);

        _sut.Channels.ChannelCount.Should().Be(1);
        _sut.Channels.GetByUniqueId("999.1").Should().NotBeNull();
    }

    [Fact]
    public async Task EventObserver_ShouldRouteHangupEvent_ToChannelManager()
    {
        await _sut.StartAsync();

        // Add channel first
        _capturedObserver!.OnNext(new NewChannelEvent
        {
            UniqueId = "999.1", Channel = "PJSIP/5000-001", ChannelState = "Up"
        });
        _sut.Channels.ChannelCount.Should().Be(1);

        // Hangup
        _capturedObserver.OnNext(new HangupEvent
        {
            UniqueId = "999.1", Cause = 16
        });
        _sut.Channels.ChannelCount.Should().Be(0);
    }

    [Fact]
    public async Task EventObserver_ShouldRouteQueueMemberAddedEvent_ToQueueManager()
    {
        await _sut.StartAsync();

        _capturedObserver!.OnNext(new QueueMemberAddedEvent
        {
            Queue = "support",
            Interface = "PJSIP/2000",
            MemberName = "Agent 2000",
            Penalty = 0,
            Paused = false,
            Status = 1
        });

        _sut.Queues.QueueCount.Should().Be(1);
    }

    [Fact]
    public async Task EventObserver_ShouldRouteAgentLoginEvent_ToAgentManager()
    {
        await _sut.StartAsync();

        _capturedObserver!.OnNext(new AgentLoginEvent
        {
            Agent = "1001",
            Channel = "PJSIP/1001"
        });

        _sut.Agents.AgentCount.Should().Be(1);
        _sut.Agents.GetById("1001").Should().NotBeNull();
    }

    [Fact]
    public async Task DisposeAsync_ShouldUnsubscribeFromConnection()
    {
        var subscription = Substitute.For<IDisposable>();
        _connection.Subscribe(Arg.Any<IObserver<ManagerEvent>>()).Returns(subscription);

        await _sut.StartAsync();
        await _sut.DisposeAsync();

        subscription.Received(1).Dispose();
    }

    [Fact]
    public async Task ConnectionLost_ShouldFire_WhenObserverOnError()
    {
        await _sut.StartAsync();

        Exception? firedWith = null;
        _sut.ConnectionLost += ex => firedWith = ex;

        var testEx = new InvalidOperationException("connection lost");
        _capturedObserver!.OnError(testEx);

        firedWith.Should().BeSameAs(testEx);
    }

    [Fact]
    public async Task ConnectionLost_ShouldFire_WhenObserverOnCompleted()
    {
        await _sut.StartAsync();

        var fired = false;
        _sut.ConnectionLost += _ => fired = true;

        _capturedObserver!.OnCompleted();

        fired.Should().BeTrue();
    }

    [Fact]
    public async Task OriginateAsync_ShouldDelegateToConnection()
    {
        var originateResponse = new Asterisk.Sdk.Ami.Events.OriginateResponseEvent
        {
            Response = "Success",
            Channel = "PJSIP/2000-001"
        };
        _connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(originateResponse));

        await _sut.StartAsync();

        var result = await _sut.OriginateAsync(
            "PJSIP/2000", "default", "100");

        result.Success.Should().BeTrue();
        result.ChannelId.Should().Be("PJSIP/2000-001");
    }

    [Fact]
    public async Task StartAsync_ShouldPopulateQueuesFromQueueStatusAction()
    {
        var queueParams = new Asterisk.Sdk.Ami.Events.QueueParamsEvent
        {
            Queue = "sales",
            Max = 10,
            Strategy = "ringall"
        };
        var callCount = 0;
        _connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                if (callCount == 2) // QueueStatusAction
                    return ToAsyncEnumerable(queueParams);
                return EmptyAsyncEnumerable();
            });

        await _sut.StartAsync();

        _sut.Queues.QueueCount.Should().Be(1);
    }

    [Fact]
    public async Task StartAsync_ShouldPopulateAgentsFromAgentsAction()
    {
        var agentEvent = new Asterisk.Sdk.Ami.Events.AgentsEvent
        {
            Agent = "2001",
            Status = "AGENT_IDLE"
        };
        var callCount = 0;
        _connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                if (callCount == 3) // AgentsAction
                    return ToAsyncEnumerable(agentEvent);
                return EmptyAsyncEnumerable();
            });

        await _sut.StartAsync();

        _sut.Agents.AgentCount.Should().Be(1);
    }

    private static async IAsyncEnumerable<ManagerEvent> ToAsyncEnumerable(ManagerEvent evt)
    {
        await Task.CompletedTask;
        yield return evt;
    }
}
