using Asterisk.Sdk.Live.Agents;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asterisk.Sdk.Live.Tests.Agents;

public class AgentStatisticsTests
{
    private readonly AgentManager _sut = new(NullLogger.Instance);

    [Fact]
    public void OnAgentLogin_ShouldResetCountersAndSetTimestamp()
    {
        _sut.OnAgentLogin("1001", "PJSIP/1001");

        var agent = _sut.GetById("1001")!;
        agent.CallsTaken.Should().Be(0);
        agent.TotalTalkTimeSecs.Should().Be(0);
        agent.TotalHoldTimeSecs.Should().Be(0);
        agent.LastCallTalkTimeSecs.Should().Be(0);
        agent.LastStateChangeAt.Should().NotBeNull();
        agent.LastStateChangeAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void OnAgentComplete_ShouldIncrementCallsTakenAndAccumulateTalkTime()
    {
        _sut.OnAgentLogin("1001");
        _sut.OnAgentConnect("1001", "caller1");
        _sut.OnAgentComplete("1001", talkTimeSecs: 120, holdTimeSecs: 10);

        var agent = _sut.GetById("1001")!;
        agent.CallsTaken.Should().Be(1);
        agent.TotalTalkTimeSecs.Should().Be(120);
        agent.TotalHoldTimeSecs.Should().Be(10);
        agent.LastCallTalkTimeSecs.Should().Be(120);
    }

    [Fact]
    public void OnAgentComplete_MultipleCalls_ShouldCalculateCorrectAverage()
    {
        _sut.OnAgentLogin("1001");

        _sut.OnAgentConnect("1001", "caller1");
        _sut.OnAgentComplete("1001", talkTimeSecs: 60, holdTimeSecs: 5);

        _sut.OnAgentConnect("1001", "caller2");
        _sut.OnAgentComplete("1001", talkTimeSecs: 120, holdTimeSecs: 15);

        _sut.OnAgentConnect("1001", "caller3");
        _sut.OnAgentComplete("1001", talkTimeSecs: 180, holdTimeSecs: 10);

        var agent = _sut.GetById("1001")!;
        agent.CallsTaken.Should().Be(3);
        agent.TotalTalkTimeSecs.Should().Be(360);
        agent.TotalHoldTimeSecs.Should().Be(30);
        agent.LastCallTalkTimeSecs.Should().Be(180);
        agent.AvgTalkTimeSecs.Should().Be(120.0);
    }

    [Fact]
    public void AvgTalkTimeSecs_ShouldReturnZero_WhenNoCallsTaken()
    {
        _sut.OnAgentLogin("1001");

        _sut.GetById("1001")!.AvgTalkTimeSecs.Should().Be(0);
    }

    [Fact]
    public void OnAgentConnect_ShouldUpdateLastStateChangeAt()
    {
        _sut.OnAgentLogin("1001");
        var loginTime = _sut.GetById("1001")!.LastStateChangeAt;

        Thread.Sleep(50);
        _sut.OnAgentConnect("1001", "caller");

        var agent = _sut.GetById("1001")!;
        agent.LastStateChangeAt.Should().BeAfter(loginTime!.Value);
    }

    [Fact]
    public void OnAgentPaused_ShouldUpdateLastStateChangeAt()
    {
        _sut.OnAgentLogin("1001");
        var loginTime = _sut.GetById("1001")!.LastStateChangeAt;

        Thread.Sleep(50);
        _sut.OnAgentPaused("1001", true);

        var agent = _sut.GetById("1001")!;
        agent.LastStateChangeAt.Should().BeAfter(loginTime!.Value);
        agent.State.Should().Be(AgentState.Paused);
    }

    [Fact]
    public void OnAgentLogoff_ShouldPreserveCounters()
    {
        _sut.OnAgentLogin("1001");
        _sut.OnAgentConnect("1001", "caller1");
        _sut.OnAgentComplete("1001", talkTimeSecs: 90, holdTimeSecs: 5);
        _sut.OnAgentLogoff("1001");

        var agent = _sut.GetById("1001")!;
        agent.State.Should().Be(AgentState.LoggedOff);
        agent.CallsTaken.Should().Be(1);
        agent.TotalTalkTimeSecs.Should().Be(90);
        agent.LastStateChangeAt.Should().NotBeNull();
    }

    [Fact]
    public void StateElapsed_ShouldReturnPositiveDuration()
    {
        _sut.OnAgentLogin("1001");

        var agent = _sut.GetById("1001")!;
        agent.StateElapsed.Should().BeGreaterOrEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public void StateElapsed_ShouldReturnZero_WhenNoStateChange()
    {
        var agent = new AsteriskAgent { AgentId = "1001" };
        agent.StateElapsed.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void OnAgentLogin_ShouldResetCounters_WhenRelogging()
    {
        _sut.OnAgentLogin("1001");
        _sut.OnAgentConnect("1001", "caller1");
        _sut.OnAgentComplete("1001", talkTimeSecs: 200, holdTimeSecs: 30);

        _sut.GetById("1001")!.CallsTaken.Should().Be(1);

        // Re-login should reset
        _sut.OnAgentLogin("1001", "PJSIP/1001");

        var agent = _sut.GetById("1001")!;
        agent.CallsTaken.Should().Be(0);
        agent.TotalTalkTimeSecs.Should().Be(0);
        agent.TotalHoldTimeSecs.Should().Be(0);
        agent.LastCallTalkTimeSecs.Should().Be(0);
    }
}
