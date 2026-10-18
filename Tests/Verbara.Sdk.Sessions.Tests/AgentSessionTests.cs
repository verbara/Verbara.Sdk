using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Tests.Harness;
using FluentAssertions;

namespace Verbara.Sdk.Sessions.Tests;

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

    // The three IdleTime cases run on the fake clock through the session's internal clock constructor, and assert the
    // exact value: idle time is counted from the instant the agent became Idle (StateChangedAt), on the tracker's
    // clock, not from the end of its last call on the wall clock.

    [Fact]
    public void IdleTime_ShouldReturnZero_WhenNotIdle()
    {
        var clock = new FakeTimeProvider();
        var session = new AgentSession("agent-1", clock);
        session.State = AgentSessionState.OnCall;
        session.StateChangedAt = clock.GetUtcNow().AddMinutes(-5);
        session.LastCallEndedAt = clock.GetUtcNow().AddMinutes(-5);

        session.IdleTime.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void IdleTime_ShouldReturnTheTimeSinceTheAgentBecameIdle_WhenIdle()
    {
        var clock = new FakeTimeProvider();
        var session = new AgentSession("agent-1", clock);
        session.State = AgentSessionState.Idle;
        session.LastCallEndedAt = clock.GetUtcNow().AddSeconds(-40);
        session.StateChangedAt = clock.GetUtcNow().AddSeconds(-10);

        session.IdleTime.Should().Be(TimeSpan.FromSeconds(10),
            "the agent became Idle 10 s ago on its clock, when its wrap-up ended; its call ended 40 s ago");
    }

    [Fact]
    public void IdleTime_ShouldReturnZero_WhenIdleAndNeverChangedState()
    {
        var clock = new FakeTimeProvider();
        var session = new AgentSession("agent-1", clock);
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
