using Asterisk.Sdk.Sessions;
using FluentAssertions;

namespace Asterisk.Sdk.Sessions.Tests;

public sealed class AgentSessionTests
{
    [Fact]
    public void AvgTalkTime_ShouldReturnZero_WhenNoCallsHandled()
    {
        var session = new AgentSession("agent-1");

        session.AvgTalkTime.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void AvgTalkTime_ShouldCalculateCorrectly_WhenCallsHandled()
    {
        var session = new AgentSession("agent-1");
        session.CallsHandled = 3;
        session.TotalTalkTime = TimeSpan.FromMinutes(9);

        session.AvgTalkTime.Should().Be(TimeSpan.FromMinutes(3));
    }

    [Fact]
    public void AvgHandleTime_ShouldIncludeAllTimeComponents()
    {
        var session = new AgentSession("agent-1");
        session.CallsHandled = 2;
        session.TotalTalkTime = TimeSpan.FromMinutes(10);
        session.TotalHoldTime = TimeSpan.FromMinutes(2);
        session.TotalWrapUpTime = TimeSpan.FromMinutes(4);

        // (10 + 2 + 4) / 2 = 8 minutes
        session.AvgHandleTime.Should().Be(TimeSpan.FromMinutes(8));
    }

    [Fact]
    public void IdleTime_ShouldReturnZero_WhenNotIdle()
    {
        var session = new AgentSession("agent-1");
        session.State = AgentSessionState.OnCall;
        session.LastCallEndedAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        session.IdleTime.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void IdleTime_ShouldReturnElapsed_WhenIdleWithLastCall()
    {
        var session = new AgentSession("agent-1");
        session.State = AgentSessionState.Idle;
        session.LastCallEndedAt = DateTimeOffset.UtcNow.AddSeconds(-10);

        session.IdleTime.Should().BeCloseTo(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void IdleTime_ShouldReturnZero_WhenIdleWithNoLastCall()
    {
        var session = new AgentSession("agent-1");
        session.State = AgentSessionState.Idle;

        session.IdleTime.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void AvgHandleTime_ShouldReturnZero_WhenNoCallsHandled()
    {
        var session = new AgentSession("agent-1");

        session.AvgHandleTime.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Constructor_ShouldSetTrackedSince()
    {
        var before = DateTimeOffset.UtcNow;
        var session = new AgentSession("agent-1");
        var after = DateTimeOffset.UtcNow;

        session.TrackedSince.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        session.AgentId.Should().Be("agent-1");
        session.State.Should().Be(AgentSessionState.Idle);
    }
}
