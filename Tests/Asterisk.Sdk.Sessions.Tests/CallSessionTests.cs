// Tests/Asterisk.Sdk.Sessions.Tests/CallSessionTests.cs
using Asterisk.Sdk.Sessions;
using Asterisk.Sdk.Sessions.Exceptions;
using FluentAssertions;

namespace Asterisk.Sdk.Sessions.Tests;

public sealed class CallSessionTests
{
    private static CallSession CreateSession() => new("test-session", "linked-1", "server-1", CallDirection.Inbound);

    [Fact]
    public void NewSession_ShouldBeInCreatedState()
    {
        var session = CreateSession();
        session.State.Should().Be(CallSessionState.Created);
    }

    [Theory]
    [InlineData(CallSessionState.Dialing)]
    [InlineData(CallSessionState.Failed)]
    public void Created_ShouldAllowValidTransitions(CallSessionState target)
    {
        var session = CreateSession();
        session.TryTransition(target).Should().BeTrue();
        session.State.Should().Be(target);
    }

    [Theory]
    [InlineData(CallSessionState.Connected)]
    [InlineData(CallSessionState.OnHold)]
    [InlineData(CallSessionState.Completed)]
    public void Created_ShouldRejectInvalidTransitions(CallSessionState target)
    {
        var session = CreateSession();
        session.TryTransition(target).Should().BeFalse();
        session.State.Should().Be(CallSessionState.Created);
    }

    [Fact]
    public void Transition_ShouldThrowOnInvalid()
    {
        var session = CreateSession();
        var act = () => session.Transition(CallSessionState.Completed);
        act.Should().Throw<InvalidSessionStateTransitionException>();
    }

    [Fact]
    public void FullCallLifecycle_ShouldTransitionCorrectly()
    {
        var session = CreateSession();
        session.Transition(CallSessionState.Dialing);
        session.Transition(CallSessionState.Ringing);
        session.Transition(CallSessionState.Connected);
        session.Transition(CallSessionState.Completed);

        session.State.Should().Be(CallSessionState.Completed);
    }

    [Fact]
    public void Connected_ShouldAllowHoldCycle()
    {
        var session = CreateSession();
        session.Transition(CallSessionState.Dialing);
        session.Transition(CallSessionState.Connected);
        session.Transition(CallSessionState.OnHold);
        session.Transition(CallSessionState.Connected);
        session.Transition(CallSessionState.OnHold);
        session.Transition(CallSessionState.Connected);

        session.State.Should().Be(CallSessionState.Connected);
    }

    [Fact]
    public void Conference_ShouldAllowBackToConnected()
    {
        var session = CreateSession();
        session.Transition(CallSessionState.Dialing);
        session.Transition(CallSessionState.Connected);
        session.Transition(CallSessionState.Conference);
        session.Transition(CallSessionState.Connected);

        session.State.Should().Be(CallSessionState.Connected);
    }

    [Fact]
    public void HoldTime_ShouldAccumulate()
    {
        var session = CreateSession();
        session.Transition(CallSessionState.Dialing);
        session.Transition(CallSessionState.Connected);

        session.StartHold();
        Thread.Sleep(50);
        session.EndHold();

        session.StartHold();
        Thread.Sleep(50);
        session.EndHold();

        session.HoldTime.TotalMilliseconds.Should().BeGreaterThan(80);
    }

    [Fact]
    public void AddParticipant_ShouldAppearInList()
    {
        var session = CreateSession();
        session.AddParticipant(new SessionParticipant
        {
            UniqueId = "uid-1", Channel = "PJSIP/100-001",
            Technology = "PJSIP", Role = ParticipantRole.Caller,
            JoinedAt = DateTimeOffset.UtcNow
        });

        session.Participants.Should().HaveCount(1);
        session.Participants[0].Role.Should().Be(ParticipantRole.Caller);
    }

    [Fact]
    public void AddEvent_ShouldAppearInList()
    {
        var session = CreateSession();
        session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
            CallSessionEventType.Created, null, null, null));

        session.Events.Should().HaveCount(1);
    }

    [Fact]
    public void SetMetadata_ShouldBeReadable()
    {
        var session = CreateSession();
        session.SetMetadata("key1", "value1");

        session.Metadata.Should().ContainKey("key1");
        session.Metadata["key1"].Should().Be("value1");
    }

    [Fact]
    public void TerminalState_ShouldRejectAllTransitions()
    {
        var session = CreateSession();
        session.Transition(CallSessionState.Failed);

        session.TryTransition(CallSessionState.Dialing).Should().BeFalse();
        session.TryTransition(CallSessionState.Connected).Should().BeFalse();
    }

    [Fact]
    public void WaitTime_ShouldComputeCorrectly()
    {
        var session = CreateSession();
        session.DialingAt = session.CreatedAt.AddSeconds(1);
        session.ConnectedAt = session.CreatedAt.AddSeconds(5);

        session.WaitTime.Should().NotBeNull();
        session.WaitTime!.Value.TotalSeconds.Should().BeApproximately(5, 0.1);
    }
}
