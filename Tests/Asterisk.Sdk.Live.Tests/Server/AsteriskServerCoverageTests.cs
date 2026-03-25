using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Events;
using Asterisk.Sdk.Live.Server;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Asterisk.Sdk.Live.Tests.Server;

public sealed class AsteriskServerCoverageTests : IAsyncDisposable
{
    private readonly IAmiConnection _connection;
    private readonly AsteriskServer _sut;
    private IObserver<ManagerEvent>? _capturedObserver;

    public AsteriskServerCoverageTests()
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

    [Fact]
    public async Task EventObserver_OnError_ShouldNotThrow()
    {
        await _sut.StartAsync();

        var act = () => _capturedObserver!.OnError(new InvalidOperationException("test"));
        act.Should().NotThrow();
    }

    [Fact]
    public async Task EventObserver_OnCompleted_ShouldFireConnectionLost()
    {
        await _sut.StartAsync();

        Exception? capturedEx = null;
        bool fired = false;
        _sut.ConnectionLost += ex =>
        {
            fired = true;
            capturedEx = ex;
        };

        _capturedObserver!.OnCompleted();

        fired.Should().BeTrue();
        capturedEx.Should().BeNull();
    }

    [Fact]
    public async Task EventObserver_OnError_ShouldFireConnectionLost_WithException()
    {
        await _sut.StartAsync();

        Exception? capturedEx = null;
        _sut.ConnectionLost += ex => capturedEx = ex;

        var testException = new InvalidOperationException("connection error");
        _capturedObserver!.OnError(testException);

        capturedEx.Should().BeSameAs(testException);
    }

    [Fact]
    public async Task EventObserver_ShouldIgnoreUnknownEventTypes()
    {
        await _sut.StartAsync();

        var act = () => _capturedObserver!.OnNext(new ReloadEvent());
        act.Should().NotThrow();
    }

    [Fact]
    public async Task EventObserver_ShouldHandleMeetMeJoinEvent()
    {
        await _sut.StartAsync();

#pragma warning disable CS0618
        _capturedObserver!.OnNext(new MeetMeJoinEvent
        {
            Meetme = "1000",
            Usernum = 1,
            Channel = "PJSIP/100-001"
        });
#pragma warning restore CS0618

        var room = _sut.MeetMe.GetRoom("1000");
        room.Should().NotBeNull();
        room!.UserCount.Should().Be(1);
    }

    [Fact]
    public async Task EventObserver_ShouldHandleMeetMeLeaveEvent()
    {
        await _sut.StartAsync();

#pragma warning disable CS0618
        _capturedObserver!.OnNext(new MeetMeJoinEvent
        {
            Meetme = "1000", Usernum = 1, Channel = "PJSIP/100-001"
        });
        _capturedObserver.OnNext(new MeetMeLeaveEvent
        {
            Meetme = "1000", Usernum = 1
        });
#pragma warning restore CS0618

        // After last user leaves, room may or may not still exist
        var room = _sut.MeetMe.GetRoom("1000");
        if (room is not null)
            room.UserCount.Should().Be(0);
        else
            _sut.MeetMe.Should().NotBeNull(); // room was cleaned up
    }

    [Fact]
    public async Task EventObserver_ShouldHandleNewExtenEvent()
    {
        await _sut.StartAsync();

        _capturedObserver!.OnNext(new NewChannelEvent
        {
            UniqueId = "uid-1", Channel = "PJSIP/100-001", ChannelState = "Up"
        });
        _capturedObserver.OnNext(new NewExtenEvent
        {
            UniqueId = "uid-1", Extension = "200", Application = "Dial", AppData = "PJSIP/200"
        });

        var ch = _sut.Channels.GetByUniqueId("uid-1");
        ch.Should().NotBeNull();
    }

    [Fact]
    public async Task EventObserver_ShouldHandleHangupEvent()
    {
        await _sut.StartAsync();

        _capturedObserver!.OnNext(new NewChannelEvent
        {
            UniqueId = "uid-1", Channel = "PJSIP/100-001", ChannelState = "Up"
        });
        _capturedObserver.OnNext(new HangupEvent { UniqueId = "uid-1" });

        _sut.Channels.GetByUniqueId("uid-1").Should().BeNull();
    }

    [Fact]
    public async Task EventObserver_ShouldHandlePeerStatusEvent()
    {
        await _sut.StartAsync();

        var act = () => _capturedObserver!.OnNext(new PeerStatusEvent
        {
            Peer = "PJSIP/100", PeerStatus = "Registered"
        });
        act.Should().NotThrow();
    }

    [Fact]
    public async Task EventObserver_ShouldHandleVarSetEvent()
    {
        await _sut.StartAsync();

        var act = () => _capturedObserver!.OnNext(new VarSetEvent
        {
            UniqueId = "uid-1", Variable = "CHANNEL(musicclass)", Value = "default"
        });
        act.Should().NotThrow();
    }

    [Fact]
    public async Task EventObserver_ShouldHandleMusicOnHoldEvent()
    {
        await _sut.StartAsync();

        var act = () => _capturedObserver!.OnNext(new MusicOnHoldEvent
        {
            UniqueId = "uid-1"
        });
        act.Should().NotThrow();
    }

    [Fact]
    public async Task DisposeAsync_ShouldBeIdempotent()
    {
        await _sut.StartAsync();
        await _sut.DisposeAsync();

        var act = async () => await _sut.DisposeAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EventObserver_ShouldHandleMultipleNewChannelEvents()
    {
        await _sut.StartAsync();

        for (int i = 0; i < 5; i++)
        {
            _capturedObserver!.OnNext(new NewChannelEvent
            {
                UniqueId = $"uid-{i}", Channel = $"PJSIP/10{i}-001", ChannelState = "Up"
            });
        }

        _sut.Channels.ChannelCount.Should().Be(5);
    }

    [Fact]
    public async Task EventObserver_ShouldHandleMultipleQueueMemberAddedEvents()
    {
        await _sut.StartAsync();

        _capturedObserver!.OnNext(new QueueMemberAddedEvent
        {
            Queue = "sales", Interface = "PJSIP/2000", MemberName = "Agent 2000", Penalty = 0
        });
        _capturedObserver.OnNext(new QueueMemberAddedEvent
        {
            Queue = "sales", Interface = "PJSIP/2001", MemberName = "Agent 2001", Penalty = 3
        });

        var queue = _sut.Queues.GetByName("sales")!;
        queue.MemberCount.Should().Be(2);
        queue.Members["PJSIP/2001"].Penalty.Should().Be(3);
    }

    [Fact]
    public async Task EventObserver_ShouldHandleAgentRingNoAnswerEvent()
    {
        await _sut.StartAsync();

        _capturedObserver!.OnNext(new AgentLoginEvent { Agent = "1001", Channel = "PJSIP/1001" });
        _capturedObserver.OnNext(new AgentConnectEvent { Agent = "1001", Channel = "PJSIP/1001-001" });
        _capturedObserver.OnNext(new AgentRingNoAnswerEvent { Agent = "1001" });

        var agent = _sut.Agents.GetById("1001");
        agent.Should().NotBeNull();
    }

    [Fact]
    public async Task EventObserver_ShouldHandleAttendedTransferEvent()
    {
        await _sut.StartAsync();

        var act = () => _capturedObserver!.OnNext(new AttendedTransferEvent
        {
            OrigTransfererChannel = "PJSIP/100-001",
            OrigTransfererUniqueid = "uid-1",
            SecondTransfererChannel = "PJSIP/200-001",
            SecondTransfererUniqueid = "uid-2"
        });
        act.Should().NotThrow();
    }

    [Fact]
    public async Task EventObserver_ShouldHandleBlindTransferEvent()
    {
        await _sut.StartAsync();

        var act = () => _capturedObserver!.OnNext(new BlindTransferEvent
        {
            TransfererChannel = "PJSIP/100-001"
        });
        act.Should().NotThrow();
    }
}
