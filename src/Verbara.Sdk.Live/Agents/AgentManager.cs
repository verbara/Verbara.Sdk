using System.Collections.Concurrent;
using Verbara.Sdk.Live.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.Live.Agents;

internal static partial class AgentManagerLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "[AGENT] Login: agent_id={AgentId} channel={Channel}")]
    public static partial void Login(ILogger logger, string agentId, string? channel);

    [LoggerMessage(Level = LogLevel.Information, Message = "[AGENT] Logoff: agent_id={AgentId}")]
    public static partial void Logoff(ILogger logger, string agentId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[AGENT] Connect: agent_id={AgentId} talking_to={TalkingTo}")]
    public static partial void Connect(ILogger logger, string agentId, string? talkingTo);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[AGENT] Complete: agent_id={AgentId} talk_secs={TalkTimeSecs} hold_secs={HoldTimeSecs} calls_taken={CallsTaken}")]
    public static partial void Complete(ILogger logger, string agentId, long talkTimeSecs, long holdTimeSecs, int callsTaken);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[AGENT] Paused: agent_id={AgentId} paused={Paused}")]
    public static partial void PauseChanged(ILogger logger, string agentId, bool paused);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[AGENT] Unknown agent: agent_id={AgentId}")]
    public static partial void UnknownAgent(ILogger logger, string agentId);
}

/// <summary>
/// Tracks Asterisk agents in real-time from AMI events.
/// All state mutations are protected by per-entity locks for atomic updates.
/// </summary>
public sealed class AgentManager
{
    // Replaced whole by ReplaceWithSnapshot, so that a reader sees the table before a reload or after it, never between.
    private volatile ConcurrentDictionary<string, AsteriskAgent> _agents = new();
    private readonly ILogger _logger;

    // The ordinal of the last live event that named each agent, and the gate a replacement shares with them: a list is
    // older than what was delivered after it was asked for, and the replacement must not slip between an event and its add.
    private readonly Lock _liveEvents = new();
    private readonly Dictionary<string, long> _lastLiveEvent = new(StringComparer.Ordinal);
    private long _liveEventCount;

    public event Action<AsteriskAgent>? AgentLoggedIn;
    public event Action<AsteriskAgent>? AgentLoggedOff;
    public event Action<AsteriskAgent>? AgentStateChanged;
    public event Action<string, string?, string?>? AgentConnected; // agentId, linkedId, interface

    /// <summary>
    /// Raised for every <c>AgentConnect</c>: app_queue connected a queue caller to a member, whether
    /// or not the member is an agent this manager knows by name. <see cref="AgentConnected"/> fires
    /// only for an agent named by the frame's <c>Agent</c> header, which the <c>AgentConnect</c>
    /// frames captured from Asterisk 20, 22 and 23 do not carry, so there an answered queue call
    /// reaches this event alone. Arguments: the caller's <c>Uniqueid</c>, the member's
    /// <c>MemberName</c> and its <c>Interface</c>. The caller is keyed by its own channel, not by its
    /// <c>Linkedid</c>: Asterisk rewrites a channel's <c>Linkedid</c> when it shares a bridge with an
    /// older call, so a caller that did so before it queued carries the other call's.
    /// </summary>
    internal event Action<string?, string?, string?>? QueueCallerConnected;

    /// <summary>
    /// Raised for every <c>AgentComplete</c>: app_queue reports that a member left the caller it connected,
    /// because the member hung up, the caller did, or the member transferred the caller away. It is raised in
    /// the same instant as the member leaves, while the caller's own channel is still up (a blind transfer to
    /// another context included), so it marks where the member's after-call work starts. Like
    /// <see cref="QueueCallerConnected"/> it fires whether or not the member is an agent this manager knows by
    /// name, and it carries the caller's <c>Uniqueid</c> to key the call by.
    /// </summary>
    internal event Action<QueueCallerCompletion>? QueueCallerCompleted;

    public AgentManager(ILogger logger) => _logger = logger;

    public IEnumerable<AsteriskAgent> Agents => _agents.Values;

    public int AgentCount => _agents.Count;

    public AsteriskAgent? GetById(string agentId) => _agents.GetValueOrDefault(agentId);

    /// <summary>Handle AgentLogin event.</summary>
    public void OnAgentLogin(string agentId, string? channel = null)
    {
        var agent = GetOrAddAgent(agentId);
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
        AgentManagerLog.Login(_logger, agentId, channel);
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
            AgentManagerLog.Logoff(_logger, agentId);
            AgentLoggedOff?.Invoke(agent);
        }
        else
        {
            AgentManagerLog.UnknownAgent(_logger, agentId);
        }
    }

    /// <summary>Handle AgentConnect event (agent answered a queue call).</summary>
    public void OnAgentConnect(string agentId, string? talkingTo = null,
        string? linkedId = null, string? memberInterface = null)
    {
        if (_agents.TryGetValue(agentId, out var agent))
        {
            lock (agent.SyncRoot)
            {
                agent.State = AgentState.OnCall;
                agent.TalkingTo = talkingTo;
                agent.LastStateChangeAt = DateTimeOffset.UtcNow;
            }
            AgentManagerLog.Connect(_logger, agentId, talkingTo);
            LiveMetrics.AgentStateChanges.Add(1);
            AgentStateChanged?.Invoke(agent);
            AgentConnected?.Invoke(agentId, linkedId, memberInterface);
        }
    }

    /// <summary>
    /// Raise <see cref="QueueCallerConnected"/> for an <c>AgentConnect</c>. The server calls it
    /// after <see cref="OnAgentConnect"/>, so a known agent's <see cref="AgentConnected"/> handlers
    /// run first and see the call as they always did.
    /// </summary>
    internal void OnQueueCallerConnected(string? callerUniqueId, string? memberName, string? memberInterface) =>
        QueueCallerConnected?.Invoke(callerUniqueId, memberName, memberInterface);

    /// <summary>
    /// Raise <see cref="QueueCallerCompleted"/> for an <c>AgentComplete</c>. The server calls it after
    /// <see cref="OnAgentComplete"/>, so a known agent's state handlers run first, as they always did.
    /// </summary>
    internal void OnQueueCallerCompleted(QueueCallerCompletion completion) =>
        QueueCallerCompleted?.Invoke(completion);

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
            AgentManagerLog.Complete(_logger, agentId, talkTimeSecs, holdTimeSecs, agent.CallsTaken);
            LiveMetrics.AgentStateChanges.Add(1);
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
            AgentManagerLog.PauseChanged(_logger, agentId, paused);
            LiveMetrics.AgentStateChanges.Add(1);
            AgentStateChanged?.Invoke(agent);
        }
    }

    /// <summary>Register an agent definition without logging it in (appears as LoggedOff).</summary>
    public void RegisterAgent(string agentId, string? name = null)
    {
        var agent = GetOrAddAgent(agentId);
        lock (agent.SyncRoot)
        {
            agent.Name = name ?? agent.Name;
            // Don't change state if already logged in
            if (agent.State == AgentState.Unknown)
                agent.State = AgentState.LoggedOff;
        }
    }

    /// <summary>Get agents filtered by state (lazy, zero-alloc).</summary>
    public IEnumerable<AsteriskAgent> GetAgentsByState(AgentState state) =>
        _agents.Values.Where(a => a.State == state);

    /// <summary>Get agents matching a predicate (lazy, zero-alloc).</summary>
    public IEnumerable<AsteriskAgent> GetAgentsWhere(Func<AsteriskAgent, bool> predicate) =>
        _agents.Values.Where(predicate);

    public void Clear() => _agents.Clear();

    private AsteriskAgent GetOrAddAgent(string agentId)
    {
        lock (_liveEvents)
            return _agents.GetOrAdd(agentId, static id => new AsteriskAgent { AgentId = id });
    }

    /// <summary>
    /// Records that an event Asterisk reported live names <paramref name="agentId"/>. The server calls it before it applies
    /// the event; a row of a list is not one, so a load never counts as a live event of its own.
    /// </summary>
    internal void NoteLiveEvent(string agentId)
    {
        lock (_liveEvents)
            _lastLiveEvent[agentId] = ++_liveEventCount;
    }

    /// <summary>Where the live events stand, read before an <c>Agents</c> list is asked for.</summary>
    internal long LiveEventMark()
    {
        lock (_liveEvents)
            return _liveEventCount;
    }

    /// <summary>Whether a live event named <paramref name="agentId"/> after <paramref name="mark"/>, so that a list asked for there is older.</summary>
    internal bool HasLiveEventSince(string agentId, long mark)
    {
        lock (_liveEvents)
            return IsNewerThanTheList(agentId, mark);
    }

    private bool IsNewerThanTheList(string agentId, long mark) =>
        _lastLiveEvent.TryGetValue(agentId, out var last) && last > mark;

    /// <summary>
    /// Replaces the table, in one step, with what a completed <c>Agents</c> list reports after a reconnect, and returns
    /// the agents to announce, in the list's order, for <see cref="AnnounceLogin"/>: a new <see cref="AsteriskAgent"/>
    /// each, with its counters at zero, as the load that clears the table and logs each one in leaves them. An agent the
    /// list omits leaves the table, and one it reports logged off is held logged off; neither raises
    /// <see cref="AgentLoggedOff"/>, since no logoff was observed. A row after the first for an agent is ignored.
    /// An agent a live event named after <paramref name="listedFrom"/> stays as that event left it, listed or not, since
    /// the list is older: its row is announced only while it reads logged in, and it is never announced as new.
    /// </summary>
    /// <param name="rows">The list, read to its end.</param>
    /// <param name="listedFrom">The <see cref="LiveEventMark"/> read before the list was asked for.</param>
    internal IReadOnlyList<AsteriskAgent> ReplaceWithSnapshot(IReadOnlyList<AgentSnapshotRow> rows, long listedFrom)
    {
        var now = DateTimeOffset.UtcNow;
        var table = new ConcurrentDictionary<string, AsteriskAgent>();
        var loggedIn = new List<AsteriskAgent>();
        lock (_liveEvents)
        {
            var held = _agents;
            foreach (var row in rows)
            {
                var newer = IsNewerThanTheList(row.AgentId, listedFrom);
                if (newer && held.TryGetValue(row.AgentId, out var kept))
                {
                    if (table.TryAdd(row.AgentId, kept) && row.LoggedIn && !IsLoggedOff(kept))
                        loggedIn.Add(kept);

                    continue;
                }

                var agent = new AsteriskAgent { AgentId = row.AgentId, Name = row.Name };
                if (!table.TryAdd(row.AgentId, agent) || !row.LoggedIn || newer)
                    continue;

                agent.State = AgentState.Available;
                agent.Channel = row.Channel;
                agent.LoggedInAt = now;
                agent.LastStateChangeAt = now;
                loggedIn.Add(agent);
            }

            // The agents the list could not have seen: an event after it is the only word on them.
            foreach (var (agentId, last) in _lastLiveEvent)
            {
                if (last > listedFrom && held.TryGetValue(agentId, out var unlisted))
                    table.TryAdd(agentId, unlisted);
            }

            _agents = table;
        }

        return loggedIn;
    }

    /// <summary>
    /// Logs and raises <see cref="AgentLoggedIn"/> for an agent <see cref="ReplaceWithSnapshot"/> reported logged in, unless
    /// a live logoff has been applied to it since: that logoff is newer than the list, and its <see cref="AgentLoggedOff"/>
    /// may already have been raised, so a login announced after it would leave subscribers holding the agent logged in.
    /// The state is read under the agent's lock, which the logoff's change holds: a logoff applied before the read skips
    /// the login, and one applied after it is raised after it, except in the instant between the read and the raise.
    /// </summary>
    internal void AnnounceLogin(AsteriskAgent agent)
    {
        if (IsLoggedOff(agent))
            return;

        AgentManagerLog.Login(_logger, agent.AgentId, agent.Channel);
        AgentLoggedIn?.Invoke(agent);
    }

    // Read under the agent's lock, so that a live change in progress is read before it or after it. _liveEvents may be
    // held around it; nothing takes _liveEvents inside an agent's lock, and the event pump never holds both.
    private static bool IsLoggedOff(AsteriskAgent agent)
    {
        lock (agent.SyncRoot)
            return agent.State == AgentState.LoggedOff;
    }
}

/// <summary>
/// One row of a completed <c>Agents</c> list, copied out of its event: the <c>Agent</c>, <c>Name</c> and
/// <c>LoggedInChan</c> headers, and whether its <c>Status</c> says the agent is logged in.
/// </summary>
internal readonly record struct AgentSnapshotRow(string AgentId, string? Name, string? Channel, bool LoggedIn);

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

    /// <summary>Set the agent name (thread-safe).</summary>
    public void SetName(string name)
    {
        lock (SyncRoot)
        {
            Name = name;
        }
    }

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

/// <summary>
/// One <c>AgentComplete</c> as <see cref="AgentManager.QueueCallerCompleted"/> delivers it: the caller's channel
/// (<c>Uniqueid</c>), the call's <c>Linkedid</c>, the member's <c>Interface</c>, the talk time app_queue
/// reports in whole seconds, and the frame's <c>Queue</c> header — the queue whose member finished, which can differ
/// from the last queue the caller joined; <see langword="null"/> when the frame carries none. Asterisk spells the
/// header <c>Linkedid</c>; AMI field names match without regard to case.
/// </summary>
internal readonly record struct QueueCallerCompletion(
    string? CallerUniqueId, string? LinkedId, string? MemberInterface, long TalkTimeSeconds, string? Queue);
