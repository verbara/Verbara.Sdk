namespace Asterisk.Sdk.Sessions.Manager;

public interface IAgentSessionTracker
{
    /// <summary>
    /// Gets the agent session for the specified agent, or null if not tracked.
    /// </summary>
    AgentSession? GetByAgentId(string agentId);

    /// <summary>
    /// Returns all currently tracked agent sessions.
    /// </summary>
    IEnumerable<AgentSession> ActiveAgents { get; }

    /// <summary>
    /// Returns agent sessions filtered by the specified state.
    /// </summary>
    IEnumerable<AgentSession> GetByState(AgentSessionState state);

    /// <summary>
    /// Observable stream of agent session state changes.
    /// </summary>
    IObservable<AgentSessionStateChanged> StateChanges { get; }
}
