using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Tests.Shared.Sessions;

/// <summary>
/// Checks the engine's actual entries in both directions, after scenario-owned causal gates have completed.
/// Historical participants and retained ended calls do not require channel entries; all existing entries must name
/// the held instance carrying their key. Enumerating every entry also finds released instances with unknown keys.
/// </summary>
internal static class SessionIndexInvariant
{
    public static void Assert(CallSessionManager manager) => Assert(manager.InspectIndexes());

    public static void Assert(SessionIndexSnapshot snapshot)
    {
        static void Require(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException($"Session index invariant: {message}");
        }

        CallSession? Held(string id) => snapshot.Sessions.FirstOrDefault(e => e.Key == id).Value;

        void CheckHeld(string kind, string key, CallSession call) => Require(
            ReferenceEquals(Held(call.SessionId), call),
            $"{kind} '{key}' names an unheld instance '{call.SessionId}' on '{call.ServerId}'.");

        foreach (var (key, call) in snapshot.Sessions)
        {
            CheckHeld("session", key, call);
            Require(key == call.SessionId, $"session key '{key}' differs from instance id '{call.SessionId}'.");
        }

        foreach (var (key, call) in snapshot.LinkedIds)
        {
            CheckHeld("linkedid", key, call);
            Require(key == call.LinkedId, $"linkedid '{key}' is absent from '{call.SessionId}'.");
        }

        foreach (var (key, call) in snapshot.ChannelIds)
        {
            CheckHeld("channel", key, call);
            Require(call.Participants.Any(p => p.UniqueId == key), $"channel '{key}' is absent from '{call.SessionId}'.");
        }

        foreach (var (key, sessionId) in snapshot.Bridges)
        {
            var call = Held(sessionId);
            Require(call is not null, $"bridge '{key}' names unheld session id '{sessionId}'.");
            Require(call!.BridgeId == key, $"bridge '{key}' is absent from '{sessionId}'.");
        }

        foreach (var scoped in snapshot.ServerIndexes)
        {
            foreach (var entry in scoped.LinkedIds.Concat(scoped.ChannelIds))
                Require(entry.Value.ServerId == scoped.ServerId,
                    $"index on '{scoped.ServerId}' names call '{entry.Value.SessionId}' of '{entry.Value.ServerId}'.");
            foreach (var bridge in scoped.Bridges)
                Require(Held(bridge.Value)?.ServerId == scoped.ServerId,
                    $"bridge '{bridge.Key}' on '{scoped.ServerId}' names another server's call.");
        }
        if (snapshot.ServerIndexes.Count > 0)
        {
            Require(snapshot.ServerIndexes.SelectMany(s => s.LinkedIds).SequenceEqual(snapshot.LinkedIds), "flat linkedid view differs from actual server indexes.");
            Require(snapshot.ServerIndexes.SelectMany(s => s.ChannelIds).SequenceEqual(snapshot.ChannelIds), "flat channel view differs from actual server indexes.");
            Require(snapshot.ServerIndexes.SelectMany(s => s.Bridges).SequenceEqual(snapshot.Bridges), "flat bridge view differs from actual server indexes.");
        }

        foreach (var (_, call) in snapshot.Sessions)
        {
            if (call.EndingDelivered || call.State is CallSessionState.Completed or CallSessionState.Failed or CallSessionState.TimedOut)
                continue;

            foreach (var participant in call.Participants.Where(p => p.LeftAt is null))
            {
                // Live snapshots retain the actual dictionary's server key; legacy synthetic snapshots have no scopes.
                var entries = snapshot.ServerIndexes.Count > 0
                    ? snapshot.ServerIndexes.SingleOrDefault(s => s.ServerId == call.ServerId)?.ChannelIds ?? []
                    : snapshot.ChannelIds.Where(e => e.Value.ServerId == call.ServerId);
                var resolved = entries.FirstOrDefault(e => e.Key == participant.UniqueId).Value;
                Require(ReferenceEquals(resolved, call),
                    $"present leg '{participant.UniqueId}' on '{call.ServerId}' does not resolve to '{call.SessionId}'.");
            }
        }
    }
}
