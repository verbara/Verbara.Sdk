using System.Reactive.Linq;
using Asterisk.Sdk.Sessions.Internal;
using Asterisk.Sdk.Sessions.Manager;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.Sessions.Tests.Manager;

public sealed class CallSessionManagerReconstructionTests : IAsyncDisposable
{
    private readonly CallSessionManager _sut;

    public CallSessionManagerReconstructionTests()
    {
        var options = Options.Create(new SessionOptions());
        _sut = new CallSessionManager(options, NullLogger<CallSessionManager>.Instance, new InMemorySessionStore());
    }

    public async ValueTask DisposeAsync()
    {
        await _sut.DisposeAsync();
    }

    [Fact]
    public void RegisterReconstructedSession_ShouldAddToAllIndices()
    {
        var session = new CallSession("sess-1", "linked-1", "srv-1", CallDirection.Inbound)
        {
            BridgeId = "bridge-1"
        };
        session.AddParticipant(new SessionParticipant
        {
            UniqueId = "uid-1",
            Channel = "PJSIP/100-001",
            Technology = "PJSIP",
            Role = ParticipantRole.Caller
        });
        session.AddParticipant(new SessionParticipant
        {
            UniqueId = "uid-2",
            Channel = "PJSIP/200-001",
            Technology = "PJSIP",
            Role = ParticipantRole.Destination
        });

        var result = _sut.RegisterReconstructedSession(session);

        result.Should().BeTrue();
        _sut.GetById("sess-1").Should().BeSameAs(session);
        _sut.GetByLinkedId("linked-1").Should().BeSameAs(session);
        _sut.GetByChannelId("uid-1").Should().BeSameAs(session);
        _sut.GetByChannelId("uid-2").Should().BeSameAs(session);
        _sut.GetByBridgeId("bridge-1").Should().BeSameAs(session);
    }

    [Fact]
    public void RegisterReconstructedSession_ShouldNotFireSessionCreatedEvent()
    {
        var session = new CallSession("sess-2", "linked-2", "srv-1", CallDirection.Outbound);
        session.AddParticipant(new SessionParticipant
        {
            UniqueId = "uid-3",
            Channel = "PJSIP/300-001",
            Technology = "PJSIP",
            Role = ParticipantRole.Caller
        });

        var eventFired = false;
        using var sub = _sut.Events.Subscribe(_ => eventFired = true);

        _sut.RegisterReconstructedSession(session);

        eventFired.Should().BeFalse("reconstructed sessions should not fire domain events");
    }

    [Fact]
    public void RegisterReconstructedSession_ShouldNotOverwriteExistingSession()
    {
        var original = new CallSession("sess-orig", "linked-dup", "srv-1", CallDirection.Inbound);
        original.AddParticipant(new SessionParticipant
        {
            UniqueId = "uid-orig",
            Channel = "PJSIP/100-001",
            Technology = "PJSIP",
            Role = ParticipantRole.Caller
        });

        var duplicate = new CallSession("sess-dup", "linked-dup", "srv-2", CallDirection.Outbound);
        duplicate.AddParticipant(new SessionParticipant
        {
            UniqueId = "uid-dup",
            Channel = "PJSIP/200-001",
            Technology = "PJSIP",
            Role = ParticipantRole.Caller
        });

        _sut.RegisterReconstructedSession(original).Should().BeTrue();
        _sut.RegisterReconstructedSession(duplicate).Should().BeFalse();

        _sut.GetByLinkedId("linked-dup").Should().BeSameAs(original);
        _sut.GetById("sess-dup").Should().BeNull("duplicate session should not be registered");
    }
}
