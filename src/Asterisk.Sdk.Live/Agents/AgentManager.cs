using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Asterisk.Sdk.Live.Agents;

/// <summary>
/// Tracks Asterisk agents in real-time from AMI events.
/// All state mutations are protected by per-entity locks for atomic updates.
/// </summary>
public sealed class AgentManager
{
    private readonly ConcurrentDictionary<string, AsteriskAgent> _agents = new();
    private readonly ILogger _logger;

    public event Action<AsteriskAgent>? AgentLoggedIn;
    public event Action<AsteriskAgent>? AgentLoggedOff;
    public event Action<AsteriskAgent>? AgentStateChanged;

    public AgentManager(ILogger logger) => _logger = logger;

    public IEnumerable<AsteriskAgent> Agents => _agents.Values;

    public int AgentCount => _agents.Count;

    public AsteriskAgent? GetById(string agentId) => _agents.GetValueOrDefault(agentId);

    /// <summary>Handle AgentLogin event.</summary>
    public void OnAgentLogin(string agentId, string? channel = null)
    {
        var agent = _agents.GetOrAdd(agentId, _ => new AsteriskAgent { AgentId = agentId });
        lock (agent.SyncRoot)
        {
            agent.State = AgentState.Available;
            agent.Channel = channel;
            agent.LoggedInAt = DateTimeOffset.UtcNow;
            agent.LastStateChangeAt = DateTimeOffset.UtcNow;
            agent.CallsTaken = 0;
            agent.TotalTalkTimeSecs = 0;
            agent.TotalHoldTimeSecs = 0;
            agent.LastCallTalkTimeSecs = 0;
        }
        AgentLoggedIn?.Invoke(agent);
    }

    /// <summary>Handle AgentLogoff event.</summary>
    public void OnAgentLogoff(string agentId)
    {
        if (_agents.TryGetValue(agentId, out var agent))
        {
            lock (agent.SyncRoot)
            {
                agent.State = AgentState.LoggedOff;
                agent.Channel = null;
                agent.LastStateChangeAt = DateTimeOffset.UtcNow;
            }
            AgentLoggedOff?.Invoke(agent);
        }
    }

    /// <summary>Handle AgentConnect event (agent answered a queue call).</summary>
    public void OnAgentConnect(string agentId, string? talkingTo = null)
    {
        if (_agents.TryGetValue(agentId, out var agent))
        {
            lock (agent.SyncRoot)
            {
                agent.State = AgentState.OnCall;
                agent.TalkingTo = talkingTo;
                agent.LastStateChangeAt = DateTimeOffset.UtcNow;
            }
            AgentStateChanged?.Invoke(agent);
        }
    }

    /// <summary>Handle AgentComplete event (agent finished a queue call).</summary>
    public void OnAgentComplete(string agentId, long talkTimeSecs = 0, long holdTimeSecs = 0)
    {
        if (_agents.TryGetValue(agentId, out var agent))
        {
            lock (agent.SyncRoot)
            {
                agent.State = AgentState.Available;
                agent.TalkingTo = null;
                agent.LastStateChangeAt = DateTimeOffset.UtcNow;
                agent.CallsTaken++;
                agent.LastCallTalkTimeSecs = talkTimeSecs;
                agent.TotalTalkTimeSecs += talkTimeSecs;
                agent.TotalHoldTimeSecs += holdTimeSecs;
            }
            AgentStateChanged?.Invoke(agent);
        }
    }

    /// <summary>Handle agent paused/unpaused.</summary>
    public void OnAgentPaused(string agentId, bool paused)
    {
        if (_agents.TryGetValue(agentId, out var agent))
        {
            lock (agent.SyncRoot)
            {
                agent.State = paused ? AgentState.Paused : AgentState.Available;
                agent.LastStateChangeAt = DateTimeOffset.UtcNow;
            }
            AgentStateChanged?.Invoke(agent);
        }
    }

    /// <summary>Get agents filtered by state (lazy, zero-alloc).</summary>
    public IEnumerable<AsteriskAgent> GetAgentsByState(AgentState state) =>
        _agents.Values.Where(a => a.State == state);

    /// <summary>Get agents matching a predicate (lazy, zero-alloc).</summary>
    public IEnumerable<AsteriskAgent> GetAgentsWhere(Func<AsteriskAgent, bool> predicate) =>
        _agents.Values.Where(predicate);

    public void Clear() => _agents.Clear();
}

/// <summary>Represents a live Asterisk agent.</summary>
public sealed class AsteriskAgent : LiveObjectBase
{
    internal readonly Lock SyncRoot = new();

    public override string Id => AgentId;
    public string AgentId { get; init; } = string.Empty;
    public string? Name { get; set; }
    public AgentState State { get; set; } = AgentState.LoggedOff;
    public string? Channel { get; set; }
    public string? TalkingTo { get; set; }
    public DateTimeOffset? LoggedInAt { get; set; }

    /// <summary>Timestamp of the last state transition.</summary>
    public DateTimeOffset? LastStateChangeAt { get; set; }

    /// <summary>Total calls answered by this agent since login.</summary>
    public int CallsTaken { get; set; }

    /// <summary>Total talk time in seconds accumulated since login.</summary>
    public long TotalTalkTimeSecs { get; set; }

    /// <summary>Total hold time in seconds accumulated since login.</summary>
    public long TotalHoldTimeSecs { get; set; }

    /// <summary>Talk time of the last completed call in seconds.</summary>
    public long LastCallTalkTimeSecs { get; set; }

    /// <summary>Duration the agent has been in the current state.</summary>
    public TimeSpan StateElapsed => LastStateChangeAt.HasValue
        ? DateTimeOffset.UtcNow - LastStateChangeAt.Value
        : TimeSpan.Zero;

    /// <summary>Average talk time per call in seconds.</summary>
    public double AvgTalkTimeSecs => CallsTaken > 0
        ? (double)TotalTalkTimeSecs / CallsTaken
        : 0;
}

/// <summary>Agent state.</summary>
public enum AgentState { Unknown, LoggedOff, Available, OnCall, Paused }
