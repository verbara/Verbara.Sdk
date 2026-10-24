namespace Verbara.Sdk.Sessions.Manager;

public sealed partial class CallSessionManager
{
    /// <summary>
    /// Detaches <paramref name="serverId"/> and, with <see cref="DetachedCalls.Release"/>, releases the calls this
    /// manager holds for it that have not ended; returns the calls released.
    /// </summary>
    /// <remarks>
    /// <para><see cref="DetachedCalls.Keep"/> is exactly <see cref="DetachFromServer(string)"/>: it unsubscribes, waits
    /// for nothing and returns no call.</para>
    /// <para><see cref="DetachedCalls.Release"/> detaches the server when it is attached, then lets go of every call
    /// held under <paramref name="serverId"/> that has not ended, in whatever state and however it came to be held,
    /// without ending it: the call leaves every lookup and the default store, its state, legs, timestamps and metadata
    /// stay as they were, and no ending is raised or measured for it. Calls of the server that have already ended are
    /// left to their retention period. It also works for an id that is not attached, and a second release of the same
    /// id releases nothing.</para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="serverId"/> is null or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="calls"/> is neither value of
    /// <see cref="DetachedCalls"/>.</exception>
    public IReadOnlyList<CallSession> DetachFromServer(string serverId, DetachedCalls calls)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverId);
        if (calls is not (DetachedCalls.Keep or DetachedCalls.Release))
            throw new ArgumentOutOfRangeException(nameof(calls), calls, "Neither DetachedCalls.Keep nor DetachedCalls.Release.");

        DetachFromServer(serverId);
        if (calls == DetachedCalls.Keep)
            return [];

        Step(HandlerStep.ReleaseWalkStarted, serverId, string.Empty);
        return ReleaseCallsOf(serverId);
    }

    /// <summary>
    /// The release walk: selects every call held under <paramref name="serverId"/> that has not ended, then lets go of
    /// each under its own lock, after checking again that the manager still holds that very object and that it has
    /// not ended since the selection — a call ended or replaced in between is left alone. Returns the calls let go of,
    /// in the order they were let go of.
    /// </summary>
    private List<CallSession> ReleaseCallsOf(string serverId)
    {
        var selected = new List<CallSession>();
        foreach (var held in _sessions)
        {
            if (string.Equals(held.Value.ServerId, serverId, StringComparison.Ordinal) && !HasEnded(held.Value))
                selected.Add(held.Value);
        }

        // Oldest first, so the order a caller sees does not depend on the dictionary's.
        selected.Sort(static (x, y) =>
        {
            var byCreation = x.CreatedAt.CompareTo(y.CreatedAt);
            return byCreation != 0 ? byCreation : string.CompareOrdinal(x.SessionId, y.SessionId);
        });

        var released = new List<CallSession>(selected.Count);
        foreach (var call in selected)
        {
            lock (call.SyncRoot)
            {
                if (!IsHeld(call) || HasEnded(call))
                    continue;

                ForgetEverywhere(call);
                released.Add(call);
            }
        }

        return released;
    }

    /// <summary>
    /// Lets go of <paramref name="call"/> everywhere this manager keeps it, without ending it. Called under the call's
    /// lock, once per call let go of.
    /// <para>
    /// The bridge entries go first. That index maps a bridge to a session id, not to a call, so a removal cannot tell
    /// this call's entry from a newer call's under the same session id; while the call still holds its session id no
    /// other call can be registered under it, so every bridge entry naming that id is this call's. Then each other
    /// index gives up its entry only while that entry is this very object — by session id, by <c>linkedid</c>, and by
    /// the channel id of every leg the call has had — so a newer call holding one of those ids keeps it.
    /// </para>
    /// <para>
    /// The legs an agent or a queue member took on the call come off the connected counts their wrap-ups read, once
    /// each, opening no wrap-up and raising nothing: the manager saw no end of those legs, as it saw no end of the
    /// call. The store is told last, once the call is no longer held, so no later save finds it held and writes it
    /// again; the default store lets go of the object, a durable store keeps its record.
    /// </para>
    /// </summary>
    private void ForgetEverywhere(CallSession call)
    {
        foreach (var bridge in _bridgeToSession)
        {
            if (string.Equals(bridge.Value, call.SessionId, StringComparison.Ordinal))
                _bridgeToSession.TryRemove(bridge);
        }

        _sessions.TryRemove(new KeyValuePair<string, CallSession>(call.SessionId, call));
        Step(HandlerStep.ReleaseForgotById, call.ServerId, call.SessionId);

        _byLinkedId.TryRemove(new KeyValuePair<string, CallSession>(call.LinkedId, call));
        foreach (var participant in call.Participants)
            _byChannelId.TryRemove(new KeyValuePair<string, CallSession>(participant.UniqueId, call));

        if (call.ConnectedAgentLegs is { } legs)
        {
            call.ConnectedAgentLegs = null;
            foreach (var leg in legs)
                UncountConnectedLeg(leg.Key);
        }

        _store.OnReleasedByManager(call);
    }

    /// <summary>
    /// Test support: what this manager still keeps of the calls in <paramref name="released"/>, each as one line —
    /// an index entry naming one of those objects (a bridge entry naming one of their session ids that no call holds
    /// any more), a connected-leg list left on one, and every wrap-up key whose connected count differs from the legs
    /// the calls held record for it. Empty when a release left nothing behind. Reads the indexes without a lock, so it
    /// is meaningful only once nothing else runs on the manager.
    /// </summary>
    internal IReadOnlyList<string> ReleaseLeftovers(IEnumerable<CallSession> released)
    {
        var calls = new HashSet<CallSession>(released, ReferenceEqualityComparer.Instance);
        var sessionIds = new HashSet<string>(calls.Select(c => c.SessionId), StringComparer.Ordinal);
        var leftovers = new List<string>();

        foreach (var call in calls)
        {
            if (IsHeld(call))
                leftovers.Add($"session id {call.SessionId} still names released call {call.SessionId}");
            if (call.ConnectedAgentLegs is { Count: > 0 } legs)
                leftovers.Add($"released call {call.SessionId} still lists {legs.Count} connected leg(s)");
        }

        foreach (var entry in _byLinkedId)
        {
            if (calls.Contains(entry.Value))
                leftovers.Add($"linkedid {entry.Key} still names released call {entry.Value.SessionId}");
        }

        foreach (var entry in _byChannelId)
        {
            if (calls.Contains(entry.Value))
                leftovers.Add($"channel id {entry.Key} still names released call {entry.Value.SessionId}");
        }

        foreach (var entry in _bridgeToSession)
        {
            if (sessionIds.Contains(entry.Value) && !_sessions.ContainsKey(entry.Value))
                leftovers.Add($"bridge {entry.Key} still names released session id {entry.Value}, which no call holds");
        }

        var recorded = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var held in _sessions)
        {
            if (held.Value.ConnectedAgentLegs is not { } legs)
                continue;
            foreach (var leg in legs)
                recorded[leg.Key] = recorded.GetValueOrDefault(leg.Key) + 1;
        }

        foreach (var count in _connectedAgentLegs)
        {
            if (recorded.GetValueOrDefault(count.Key) != count.Value)
            {
                leftovers.Add($"wrap-up key {count.Key.Replace('\n', '/')} counts {count.Value} connected leg(s), the "
                    + $"calls held record {recorded.GetValueOrDefault(count.Key)}");
            }
        }

        foreach (var legs in recorded)
        {
            if (!_connectedAgentLegs.ContainsKey(legs.Key))
                leftovers.Add($"wrap-up key {legs.Key.Replace('\n', '/')} counts nothing, the calls held record {legs.Value}");
        }

        return leftovers;
    }
}
