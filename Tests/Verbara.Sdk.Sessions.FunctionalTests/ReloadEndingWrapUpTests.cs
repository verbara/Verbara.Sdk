using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// An agent's call whose ending the SDK did not observe — the AMI connection dropped while the call was up, and the
/// reload after reconnecting found its channels gone — opens no wrap-up: no <see cref="CallWrapUpEvent"/> is raised
/// for it, and the agent tracker reports the agent <see cref="AgentSessionState.Idle"/> with no wrap-up time added.
/// A wrap-up that started at some unknown point during the outage cannot be measured.
/// </summary>
/// <remarks>
/// The ending is recognised by the marker the session manager writes on such a session:
/// <c>Metadata["cause"] == "reload"</c>. This test pins that marker as a literal, so whatever reads it — the session
/// manager deciding not to open a wrap-up, the tracker deciding to go idle — reads the same contract a consumer reads.
/// </remarks>
public sealed class ReloadEndingWrapUpTests
{
    private const string Agent = "agent-1";
    private const string Member = "PJSIP/agent1";
    private static readonly DateTimeOffset Start = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The ending-provenance key and value a reload-ended session carries, as a consumer reads them.</summary>
    private const string EndingProvenanceKey = "cause";
    private const string EndingProvenanceReload = "reload";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reload_ShouldOpenNoWrapUpAndLeaveTheAgentIdleWithNothingAdded_WhenItEndsTheAgentsCall(bool queued)
    {
        var clock = new ManualClock(Start);
        await using var rig = await QueueCallRig.StartAsync(clock);
        using var tracker = new AgentSessionTracker(rig.Manager, Options.Create(new SessionOptions()),
            NullLogger<AgentSessionTracker>.Instance, clock);
        rig.Deliver([QueueFrames.AgentLogin(Agent, Member, "login-1")]);
        rig.Deliver(AnsweredCall(queued));
        var call = rig.Manager.GetByLinkedId("c1")
            ?? throw new InvalidOperationException("premise: the manager holds the agent's call");
        var onCall = tracker.GetByAgentId(Agent)?.State;

        clock.Advance(TimeSpan.FromMinutes(3));
        await rig.ReconnectAsync(status: [], queueStatus: []);
        clock.Advance(TimeSpan.FromHours(1));
        var agent = tracker.GetByAgentId(Agent);

        new
        {
            OnCall = onCall,
            Provenance = call.Metadata.GetValueOrDefault(EndingProvenanceKey),
            Ended = rig.Events.OfType<CallEndedEvent>().Select(e => e.SessionId).ToArray(),
            WrapUps = rig.Events.OfType<CallWrapUpEvent>().Count(),
            agent?.State,
            agent?.TotalWrapUpTime,
            agent?.CallsHandled,
        }.Should().BeEquivalentTo(
            new
            {
                OnCall = (AgentSessionState?)AgentSessionState.OnCall,
                Provenance = EndingProvenanceReload,
                Ended = new[] { call.SessionId },
                WrapUps = 0,
                State = (AgentSessionState?)AgentSessionState.Idle,
                TotalWrapUpTime = TimeSpan.Zero,
                CallsHandled = (int?)1,
            },
            $"a reload that finds the agent's {(queued ? "queue " : "")}call gone ends it with "
            + $"Metadata[\"{EndingProvenanceKey}\"] = \"{EndingProvenanceReload}\": no wrap-up opens for it, an hour "
            + "later still none has been raised, and the tracker counts the call and reports the agent Idle with no "
            + "wrap-up time. Measured events: ["
            + string.Join(", ", rig.Events.Select(e => $"{e.GetType().Name}({e.SessionId})")) + "]");
    }

    /// <summary>
    /// The agent's answered call: the caller's channel and the agent's leg, connected by an <c>AgentConnect</c> that
    /// names the agent, both legs up and bridged. With <paramref name="queued"/>, the caller first joins a queue.
    /// </summary>
    private static List<ManagerEvent> AnsweredCall(bool queued)
    {
        const string caller = "c1";
        const string agentLeg = "m1";
        const string callerChannel = "PJSIP/trunk-c1";
        const string agentChannel = "PJSIP/agent1-m1";
        List<ManagerEvent> frames = [QueueFrames.NewChannel(caller, callerChannel, "Ring", caller, "5551234", "from-trunk", "500")];
        frames.Add(queued
            ? QueueFrames.Join("sales", callerChannel, caller, "5551234")
            : QueueFrames.NewState(caller, "Ring"));

        frames.AddRange(
        [
            QueueFrames.NewChannel(agentLeg, agentChannel, "Down", caller, context: "agents"),
            QueueFrames.NewState(agentLeg, "Up"),
        ]);
        if (queued)
            frames.Add(QueueFrames.Leave("sales", callerChannel, caller));

        frames.AddRange(
        [
            QueueFrames.AgentConnect(queued ? "sales" : "", caller, callerChannel, caller, Member, agentLeg, agentChannel,
                holdTime: 0, agent: Agent),
            QueueFrames.NewState(caller, "Up"),
            QueueFrames.BridgeCreate("bridge-c1"),
            QueueFrames.BridgeEnter("bridge-c1", agentLeg),
            QueueFrames.BridgeEnter("bridge-c1", caller),
        ]);
        return frames;
    }
}
