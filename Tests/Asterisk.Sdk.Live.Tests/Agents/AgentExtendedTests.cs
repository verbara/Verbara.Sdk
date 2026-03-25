using Asterisk.Sdk.Live.Agents;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asterisk.Sdk.Live.Tests.Agents;

public sealed class AgentExtendedTests
{
    private readonly AgentManager _sut = new(NullLogger.Instance);

    // ── RegisterAgent ──────────────────────────────────────────────────

    [Fact]
    public void RegisterAgent_ShouldSetStateToLoggedOff_WhenNewAgent()
    {
        _sut.RegisterAgent("1001", "Alice");

        var agent = _sut.GetById("1001");
        agent.Should().NotBeNull();
        agent!.State.Should().Be(AgentState.LoggedOff);
        agent.Name.Should().Be("Alice");
    }

    [Fact]
    public void RegisterAgent_ShouldNotOverrideState_WhenAlreadyLoggedIn()
    {
        _sut.OnAgentLogin("1001", "PJSIP/1001");
        _sut.GetById("1001")!.State.Should().Be(AgentState.Available);

        // Register same agent -- should NOT reset state to LoggedOff
        _sut.RegisterAgent("1001", "Alice");

        _sut.GetById("1001")!.State.Should().Be(AgentState.Available);
        _sut.GetById("1001")!.Name.Should().Be("Alice");
    }

    [Fact]
    public void RegisterAgent_ShouldUpdateName_WhenCalledAgain()
    {
        _sut.RegisterAgent("1001", "Alice");
        _sut.RegisterAgent("1001", "Bob");

        _sut.GetById("1001")!.Name.Should().Be("Bob");
    }

    [Fact]
    public void RegisterAgent_ShouldPreserveExistingName_WhenNameIsNull()
    {
        _sut.RegisterAgent("1001", "Alice");
        _sut.RegisterAgent("1001");

        _sut.GetById("1001")!.Name.Should().Be("Alice");
    }

    // ── State transitions ──────────────────────────────────────────────

    [Fact]
    public void FullLifecycle_LoggedOff_Available_OnCall_Available_Paused_Available_LoggedOff()
    {
        _sut.OnAgentLogin("1001");
        _sut.GetById("1001")!.State.Should().Be(AgentState.Available);

        _sut.OnAgentConnect("1001", "PJSIP/caller-001");
        _sut.GetById("1001")!.State.Should().Be(AgentState.OnCall);

        _sut.OnAgentComplete("1001", talkTimeSecs: 60);
        _sut.GetById("1001")!.State.Should().Be(AgentState.Available);

        _sut.OnAgentPaused("1001", true);
        _sut.GetById("1001")!.State.Should().Be(AgentState.Paused);

        _sut.OnAgentPaused("1001", false);
        _sut.GetById("1001")!.State.Should().Be(AgentState.Available);

        _sut.OnAgentLogoff("1001");
        _sut.GetById("1001")!.State.Should().Be(AgentState.LoggedOff);
    }

    // ── GetAgentsByState ───────────────────────────────────────────────

    [Fact]
    public void GetAgentsByState_ShouldFilterCorrectly()
    {
        _sut.OnAgentLogin("1001");
        _sut.OnAgentLogin("1002");
        _sut.OnAgentLogin("1003");
        _sut.OnAgentConnect("1002", "caller");
        _sut.OnAgentPaused("1003", true);

        _sut.GetAgentsByState(AgentState.Available).Should().HaveCount(1)
            .And.Contain(a => a.AgentId == "1001");

        _sut.GetAgentsByState(AgentState.OnCall).Should().HaveCount(1)
            .And.Contain(a => a.AgentId == "1002");

        _sut.GetAgentsByState(AgentState.Paused).Should().HaveCount(1)
            .And.Contain(a => a.AgentId == "1003");

        _sut.GetAgentsByState(AgentState.LoggedOff).Should().BeEmpty();
    }

    [Fact]
    public void GetAgentsByState_ShouldReturnEmpty_WhenNoMatch()
    {
        _sut.OnAgentLogin("1001");

        _sut.GetAgentsByState(AgentState.OnCall).Should().BeEmpty();
    }

    // ── GetAgentsWhere ─────────────────────────────────────────────────

    [Fact]
    public void GetAgentsWhere_ShouldApplyPredicate()
    {
        _sut.OnAgentLogin("1001");
        _sut.OnAgentLogin("1002");
        _sut.OnAgentConnect("1001", "caller1");
        _sut.OnAgentComplete("1001", talkTimeSecs: 200);
        _sut.OnAgentConnect("1002", "caller2");
        _sut.OnAgentComplete("1002", talkTimeSecs: 50);

        var highTalk = _sut.GetAgentsWhere(a => a.TotalTalkTimeSecs > 100).ToList();
        highTalk.Should().HaveCount(1);
        highTalk[0].AgentId.Should().Be("1001");
    }

    // ── AgentConnected event ───────────────────────────────────────────

    [Fact]
    public void OnAgentConnect_ShouldFireAgentConnectedEvent()
    {
        _sut.OnAgentLogin("1001");

        string? firedAgentId = null;
        string? firedLinkedId = null;
        string? firedInterface = null;
        _sut.AgentConnected += (agentId, linkedId, iface) =>
        {
            firedAgentId = agentId;
            firedLinkedId = linkedId;
            firedInterface = iface;
        };

        _sut.OnAgentConnect("1001", "PJSIP/caller", "linked-123", "PJSIP/1001");

        firedAgentId.Should().Be("1001");
        firedLinkedId.Should().Be("linked-123");
        firedInterface.Should().Be("PJSIP/1001");
    }

    [Fact]
    public void OnAgentConnect_ShouldFireStateChangedEvent()
    {
        _sut.OnAgentLogin("1001");

        AsteriskAgent? changed = null;
        _sut.AgentStateChanged += a => changed = a;

        _sut.OnAgentConnect("1001", "caller");

        changed.Should().NotBeNull();
        changed!.State.Should().Be(AgentState.OnCall);
    }

    [Fact]
    public void OnAgentComplete_ShouldFireStateChangedEvent()
    {
        _sut.OnAgentLogin("1001");
        _sut.OnAgentConnect("1001", "caller");

        AsteriskAgent? changed = null;
        _sut.AgentStateChanged += a => changed = a;

        _sut.OnAgentComplete("1001", talkTimeSecs: 30, holdTimeSecs: 5);

        changed.Should().NotBeNull();
        changed!.State.Should().Be(AgentState.Available);
    }

    [Fact]
    public void OnAgentComplete_ShouldClearTalkingTo()
    {
        _sut.OnAgentLogin("1001");
        _sut.OnAgentConnect("1001", "PJSIP/caller");
        _sut.GetById("1001")!.TalkingTo.Should().Be("PJSIP/caller");

        _sut.OnAgentComplete("1001");

        _sut.GetById("1001")!.TalkingTo.Should().BeNull();
    }

    // ── Unknown agent handling ─────────────────────────────────────────

    [Fact]
    public void OnAgentLogoff_ShouldBeNoOp_WhenAgentUnknown()
    {
        // Should not throw
        _sut.OnAgentLogoff("nonexistent");

        _sut.AgentCount.Should().Be(0);
    }

    [Fact]
    public void OnAgentConnect_ShouldBeNoOp_WhenAgentUnknown()
    {
        _sut.OnAgentConnect("nonexistent", "caller");

        _sut.AgentCount.Should().Be(0);
    }

    [Fact]
    public void OnAgentComplete_ShouldBeNoOp_WhenAgentUnknown()
    {
        _sut.OnAgentComplete("nonexistent");

        _sut.AgentCount.Should().Be(0);
    }

    [Fact]
    public void OnAgentPaused_ShouldBeNoOp_WhenAgentUnknown()
    {
        _sut.OnAgentPaused("nonexistent", true);

        _sut.AgentCount.Should().Be(0);
    }

    // ── Agent properties ───────────────────────────────────────────────

    [Fact]
    public void SetName_ShouldUpdateName()
    {
        var agent = new AsteriskAgent { AgentId = "1001" };

        agent.SetName("Alice");

        agent.Name.Should().Be("Alice");
    }

    [Fact]
    public void DefaultState_ShouldBeLoggedOff()
    {
        var agent = new AsteriskAgent { AgentId = "1001" };

        agent.State.Should().Be(AgentState.LoggedOff);
    }

    // ── Clear ──────────────────────────────────────────────────────────

    [Fact]
    public void Clear_ShouldRemoveAllAgents()
    {
        _sut.OnAgentLogin("1001");
        _sut.OnAgentLogin("1002");
        _sut.AgentCount.Should().Be(2);

        _sut.Clear();

        _sut.AgentCount.Should().Be(0);
        _sut.Agents.Should().BeEmpty();
    }

    // ── LoggedInAt ─────────────────────────────────────────────────────

    [Fact]
    public void OnAgentLogin_ShouldSetLoggedInAt()
    {
        var before = DateTimeOffset.UtcNow;
        _sut.OnAgentLogin("1001");
        var after = DateTimeOffset.UtcNow;

        var agent = _sut.GetById("1001")!;
        agent.LoggedInAt.Should().NotBeNull();
        agent.LoggedInAt.Should().BeOnOrAfter(before);
        agent.LoggedInAt.Should().BeOnOrBefore(after);
    }

    [Fact]
    public void OnAgentLogoff_ShouldClearChannel()
    {
        _sut.OnAgentLogin("1001", "PJSIP/1001");
        _sut.GetById("1001")!.Channel.Should().Be("PJSIP/1001");

        _sut.OnAgentLogoff("1001");

        _sut.GetById("1001")!.Channel.Should().BeNull();
    }
}
