namespace Verbara.Sdk.Sessions.Manager;

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
    /// Observable stream of agent session state changes, and its completion when the tracker is disposed.
    /// </summary>
    /// <remarks>
    /// <para>Each subscriber runs on the thread that delivered the session notification causing the change, while the
    /// tracker holds that agent's lock. A subscriber that blocks holds them up.</para>
    /// <para>An exception a subscriber throws is caught at that subscriber, logged once at Warning with the agent id and
    /// the previous and new states, and counted on <c>sessions.events.subscriber_faults</c>. The other subscribers still
    /// receive the change, the tracker's state is unchanged by the failure, and it is not reported as a failure of a
    /// subscriber of the session notifications. A subscriber subscribed directly stays subscribed and receives later
    /// changes.</para>
    /// <para>A subscription composed through an operator (<c>Where</c>, <c>Select</c>, <c>OfType</c>, …) is ended by
    /// the composition library on the first exception from the operator's function or from the handler behind it, so
    /// one Warning can mean that subscriber is gone for good; subscribe directly and filter inside the handler to stay
    /// subscribed.</para>
    /// <para>Only what throws on the delivering thread is caught. A subscriber that moves its work elsewhere —
    /// <c>ObserveOn</c> or <c>SubscribeOn</c>, an <c>async void</c> handler, work pushed to <c>Task.Run</c> — throws on
    /// another thread, and that exception is not caught here.</para>
    /// </remarks>
    IObservable<AgentSessionStateChanged> StateChanges { get; }
}
