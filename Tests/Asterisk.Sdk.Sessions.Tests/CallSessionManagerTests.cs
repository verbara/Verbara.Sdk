using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Live.Agents;
using Asterisk.Sdk.Live.Bridges;
using Asterisk.Sdk.Live.Channels;
using Asterisk.Sdk.Live.Server;
using Asterisk.Sdk.Sessions;
using Asterisk.Sdk.Sessions.Manager;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Asterisk.Sdk.Sessions.Tests;

public sealed class CallSessionManagerTests : IAsyncDisposable
{
    private readonly CallSessionManager _sut;
    private readonly AsteriskServer _server;
    private readonly IAmiConnection _connection;

    public CallSessionManagerTests()
    {
        _connection = Substitute.For<IAmiConnection>();
        _connection.AsteriskVersion.Returns("20.0.0");
        _server = new AsteriskServer(_connection, NullLogger<AsteriskServer>.Instance);
        var options = Options.Create(new SessionOptions());
        _sut = new CallSessionManager(options, NullLogger<CallSessionManager>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _sut.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Fact]
    public void AttachToServer_ShouldAcceptServer()
    {
        _sut.AttachToServer(_server, "srv-1");
        // No throw = success
    }

    [Fact]
    public void NewChannel_ShouldCreateSession()
    {
        _sut.AttachToServer(_server, "srv-1");

        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring,
            linkedId: "linked-1", context: "from-trunk");

        var session = _sut.GetByLinkedId("linked-1");
        session.Should().NotBeNull();
        session!.Direction.Should().Be(CallDirection.Inbound);
        session.Participants.Should().HaveCount(1);
        session.Participants[0].Role.Should().Be(ParticipantRole.Caller);
    }

    [Fact]
    public void SecondChannel_ShouldAddAsDestination()
    {
        _sut.AttachToServer(_server, "srv-1");

        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring,
            linkedId: "linked-1");
        _server.Channels.OnNewChannel("uid-2", "PJSIP/200-001", ChannelState.Ring,
            linkedId: "linked-1");

        var session = _sut.GetByLinkedId("linked-1")!;
        session.Participants.Should().HaveCount(2);
        session.Participants[1].Role.Should().Be(ParticipantRole.Destination);
    }

    [Fact]
    public void LocalChannel_ShouldBeMarkedInternal()
    {
        _sut.AttachToServer(_server, "srv-1");

        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring,
            linkedId: "linked-1");
        _server.Channels.OnNewChannel("uid-2", "Local/100@default-001;1", ChannelState.Ring,
            linkedId: "linked-1");

        var session = _sut.GetByLinkedId("linked-1")!;
        session.Participants[1].Role.Should().Be(ParticipantRole.Internal);
    }

    [Fact]
    public void GetByChannelId_ShouldFindSession()
    {
        _sut.AttachToServer(_server, "srv-1");

        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring,
            linkedId: "linked-1");

        _sut.GetByChannelId("uid-1").Should().NotBeNull();
    }

    [Fact]
    public void ChannelHangup_ShouldCompleteSession_WhenAllParticipantsLeft()
    {
        _sut.AttachToServer(_server, "srv-1");

        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Up,
            linkedId: "linked-1");
        _server.Channels.OnHangup("uid-1");

        var session = _sut.GetByLinkedId("linked-1")!;
        session.State.Should().BeOneOf(CallSessionState.Completed, CallSessionState.Failed);
    }

    [Fact]
    public void ActiveSessions_ShouldReturnNonCompleted()
    {
        _sut.AttachToServer(_server, "srv-1");

        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring,
            linkedId: "linked-1");

        _sut.ActiveSessions.Should().HaveCount(1);
    }

    [Fact]
    public void DetachFromServer_ShouldUnsubscribe()
    {
        _sut.AttachToServer(_server, "srv-1");
        _sut.DetachFromServer("srv-1");

        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring,
            linkedId: "linked-1");

        _sut.GetByLinkedId("linked-1").Should().BeNull();
    }
}
