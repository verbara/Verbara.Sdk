using Asterisk.Sdk.Live.Agents;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asterisk.Sdk.Live.Tests.Agents;

public class AgentManagerTests
{
    private readonly AgentManager _sut = new(NullLogger.Instance);

    [Fact]
    public void OnAgentLogin_ShouldAddAgent()
    {
        _sut.OnAgentLogin("1001", "PJSIP/1001");

        _sut.AgentCount.Should().Be(1);
        var agent = _sut.GetById("1001");
        agent.Should().NotBeNull();
        agent!.State.Should().Be(AgentState.Available);
        agent.Channel.Should().Be("PJSIP/1001");
    }

    [Fact]
    public void OnAgentLogoff_ShouldUpdateState()
    {
        _sut.OnAgentLogin("1001");
        _sut.OnAgentLogoff("1001");

        _sut.GetById("1001")!.State.Should().Be(AgentState.LoggedOff);
    }

    [Fact]
    public void OnAgentConnect_ShouldSetOnCall()
    {
        _sut.OnAgentLogin("1001");
        _sut.OnAgentConnect("1001", "PJSIP/5551234");

        var agent = _sut.GetById("1001");
        agent!.State.Should().Be(AgentState.OnCall);
        agent.TalkingTo.Should().Be("PJSIP/5551234");
    }

    [Fact]
    public void OnAgentComplete_ShouldReturnToAvailable()
    {
        _sut.OnAgentLogin("1001");
        _sut.OnAgentConnect("1001", "PJSIP/5551234");
        _sut.OnAgentComplete("1001");

        _sut.GetById("1001")!.State.Should().Be(AgentState.Available);
    }

    [Fact]
    public void OnAgentPaused_ShouldSetPaused()
    {
        _sut.OnAgentLogin("1001");
        _sut.OnAgentPaused("1001", true);

        _sut.GetById("1001")!.State.Should().Be(AgentState.Paused);
    }

    [Fact]
    public void FullLifecycle_Login_Call_Complete_Logoff()
    {
        AsteriskAgent? loggedIn = null;
        AsteriskAgent? loggedOff = null;
        _sut.AgentLoggedIn += a => loggedIn = a;
        _sut.AgentLoggedOff += a => loggedOff = a;

        _sut.OnAgentLogin("1001", "PJSIP/1001");
        loggedIn.Should().NotBeNull();

        _sut.OnAgentConnect("1001", "PJSIP/caller");
        _sut.GetById("1001")!.State.Should().Be(AgentState.OnCall);

        _sut.OnAgentComplete("1001");
        _sut.GetById("1001")!.State.Should().Be(AgentState.Available);

        _sut.OnAgentLogoff("1001");
        loggedOff.Should().NotBeNull();
        _sut.GetById("1001")!.State.Should().Be(AgentState.LoggedOff);
    }
}
