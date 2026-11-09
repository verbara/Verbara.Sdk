using System.Globalization;
using Verbara.Sdk.Live.Agents;
using FluentAssertions;
using FluentAssertions.Execution;
using NSubstitute;
using Verbara.Sdk.Live.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace Verbara.Sdk.Live.Tests.Agents;

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

    // Pins the public OnAgentPaused toggle a host may call. The server never calls it: it derives an Agent/<id>
    // member's pause from that agent's queue memberships instead.
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

    [Fact]
    public async Task ReplaceWithSnapshot_ShouldKeepAnAgentALiveEventAdds_WhenTheTwoRace()
    {
        var manager = new AgentManager(NullLogger.Instance);
        using var start = new Barrier(2);
        for (var i = 0; i < 2000; i++)
        {
            var agentId = "race-" + i.ToString(CultureInfo.InvariantCulture);
            var listedFrom = manager.LiveEventMark();
            var live = Task.Run(() =>
            {
                start.SignalAndWait();
                manager.NoteLiveEvent(agentId);
                manager.OnAgentLogin(agentId);
            });
            var replacement = Task.Run(() =>
            {
                start.SignalAndWait();
                manager.ReplaceWithSnapshot([], listedFrom);
            });
            await Task.WhenAll(live, replacement);

            manager.GetById(agentId).Should().NotBeNull("the event that added it is newer than the list, whichever ran first");
        }
    }

    [Fact]
    public async Task OnAgentLogin_ShouldReadPaused_WhenEveryMembershipIsPaused()
    {
        var server = new VerbaraServer(Substitute.For<IAmiConnection>(), NullLogger<VerbaraServer>.Instance);
        await using (server)
        {
            server.Queues.OnMemberAdded("sales", "Agent/1001", "1001", 0, true, 1);
            server.Queues.OnMemberAdded("support", "Agent/1001", "1001", 0, true, 1);
            var announced = new List<AgentState>();
            server.Agents.AgentLoggedIn += a => announced.Add(a.State);

            server.Agents.OnAgentLogin("1001", "PJSIP/1001");

            using (new AssertionScope())
            {
                server.Agents.GetById("1001")!.State.Should().Be(AgentState.Paused);
                announced.Should().Equal([AgentState.Paused], "the login is announced with the state it derived");
            }
        }
    }
}
