using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Asterisk.NetAot.Live.Agents;

/// <summary>
/// Tracks Asterisk agents in real-time from AMI events.
/// </summary>
public sealed class AgentManager
{
    private readonly ConcurrentDictionary<string, AsteriskAgent> _agents = new();
    private readonly ILogger _logger;

    public event Action<AsteriskAgent>? AgentLoggedIn;
    public event Action<AsteriskAgent>? AgentLoggedOff;
    public event Action<AsteriskAgent>? AgentStateChanged;

    public AgentManager(ILogger logger) => _logger = logger;

    public IReadOnlyCollection<AsteriskAgent> Agents => _agents.Values.ToList().AsReadOnly();

    public AsteriskAgent? GetById(string agentId) => _agents.GetValueOrDefault(agentId);

    /// <summary>Handle AgentLogin event.</summary>
    public void OnAgentLogin(string agentId, string? channel = null)
    {
        var agent = _agents.GetOrAdd(agentId, _ => new AsteriskAgent { AgentId = agentId });
        agent.State = AgentState.Available;
        agent.Channel = channel;
        agent.LoggedInAt = DateTimeOffset.UtcNow;
        AgentLoggedIn?.Invoke(agent);
    }

    /// <summary>Handle AgentLogoff event.</summary>
    public void OnAgentLogoff(string agentId)
    {
        if (_agents.TryGetValue(agentId, out var agent))
        {
            agent.State = AgentState.LoggedOff;
            agent.Channel = null;
            AgentLoggedOff?.Invoke(agent);
        }
    }

    /// <summary>Handle AgentConnect event (agent answered a queue call).</summary>
    public void OnAgentConnect(string agentId, string? talkingTo = null)
    {
        if (_agents.TryGetValue(agentId, out var agent))
        {
            agent.State = AgentState.OnCall;
            agent.TalkingTo = talkingTo;
            AgentStateChanged?.Invoke(agent);
        }
    }

    /// <summary>Handle AgentComplete event (agent finished a queue call).</summary>
    public void OnAgentComplete(string agentId)
    {
        if (_agents.TryGetValue(agentId, out var agent))
        {
            agent.State = AgentState.Available;
            agent.TalkingTo = null;
            AgentStateChanged?.Invoke(agent);
        }
    }

    /// <summary>Handle agent paused/unpaused.</summary>
    public void OnAgentPaused(string agentId, bool paused)
    {
        if (_agents.TryGetValue(agentId, out var agent))
        {
            agent.State = paused ? AgentState.Paused : AgentState.Available;
            AgentStateChanged?.Invoke(agent);
        }
    }

    public void Clear() => _agents.Clear();
}

/// <summary>Represents a live Asterisk agent.</summary>
public sealed class AsteriskAgent : LiveObjectBase
{
    public override string Id => AgentId;
    public string AgentId { get; init; } = string.Empty;
    public string? Name { get; set; }
    public AgentState State { get; set; } = AgentState.LoggedOff;
    public string? Channel { get; set; }
    public string? TalkingTo { get; set; }
    public DateTimeOffset? LoggedInAt { get; set; }
}

/// <summary>Agent state.</summary>
public enum AgentState { Unknown, LoggedOff, Available, OnCall, Paused }
