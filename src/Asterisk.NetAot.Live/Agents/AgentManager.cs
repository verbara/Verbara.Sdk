using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Asterisk.NetAot.Live.Agents;

/// <summary>
/// Tracks Asterisk agents in real-time.
/// </summary>
public sealed class AgentManager
{
    private readonly ConcurrentDictionary<string, AsteriskAgent> _agents = new();
    private readonly ILogger _logger;

    public AgentManager(ILogger logger) => _logger = logger;

    public IReadOnlyCollection<AsteriskAgent> Agents => _agents.Values.ToList().AsReadOnly();
    public AsteriskAgent? GetById(string agentId) => _agents.GetValueOrDefault(agentId);
}

/// <summary>Represents a live Asterisk agent.</summary>
public sealed class AsteriskAgent
{
    public string AgentId { get; set; } = string.Empty;
    public string? Name { get; set; }
    public AgentState State { get; set; }
    public string? TalkingTo { get; set; }
}

/// <summary>Agent state.</summary>
public enum AgentState
{
    Unknown,
    LoggedOff,
    Available,
    OnCall,
    Paused
}
