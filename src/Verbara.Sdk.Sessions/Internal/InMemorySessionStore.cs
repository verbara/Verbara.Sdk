using System.Collections.Concurrent;
using Verbara.Sdk.Sessions.Extensions;

namespace Verbara.Sdk.Sessions.Internal;

internal sealed class InMemorySessionStore : SessionStoreBase
{
    private readonly ConcurrentDictionary<string, CallSession> _store = new();

    public override ValueTask SaveAsync(CallSession session, CancellationToken ct)
    {
        _store[session.SessionId] = session;
        return ValueTask.CompletedTask;
    }

    public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct)
        => ValueTask.FromResult(_store.GetValueOrDefault(sessionId));

    public override ValueTask<IEnumerable<CallSession>> GetActiveAsync(CancellationToken ct)
        => ValueTask.FromResult(_store.Values.Where(s =>
            s.State is not CallSessionState.Completed
            and not CallSessionState.Failed
            and not CallSessionState.TimedOut));

    public override ValueTask DeleteAsync(string sessionId, CancellationToken ct)
    {
        _store.TryRemove(sessionId, out _);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The newest session under <paramref name="linkedId"/> when exactly one server holds it; <c>null</c> when none
    /// or two or more servers do.
    /// </summary>
    public override ValueTask<CallSession?> GetByLinkedIdAsync(string linkedId, CancellationToken ct)
    {
        CallSession? newest = null;
        foreach (var session in _store.Values.Where(s => string.Equals(s.LinkedId, linkedId, StringComparison.Ordinal)))
        {
            if (newest is not null && !string.Equals(newest.ServerId, session.ServerId, StringComparison.Ordinal))
                return ValueTask.FromResult<CallSession?>(null);
            if (newest is null || session.CreatedAt > newest.CreatedAt) newest = session;
        }

        return ValueTask.FromResult(newest);
    }

    /// <summary>The newest session the named server holds under <paramref name="linkedId"/>.</summary>
    public override ValueTask<CallSession?> GetByLinkedIdAsync(string serverId, string linkedId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(serverId);
        ArgumentNullException.ThrowIfNull(linkedId);
        return ValueTask.FromResult(_store.Values
            .Where(s => string.Equals(s.LinkedId, linkedId, StringComparison.Ordinal)
                && string.Equals(s.ServerId, serverId, StringComparison.Ordinal))
            .MaxBy(s => s.CreatedAt));
    }

    /// <summary>
    /// Lets go of the session the manager released. This store keeps the manager's own object, so
    /// holding it past the manager's release would keep the whole call reachable for the life of the
    /// process. The entry is removed only while it is that same object: a different session saved
    /// under the id since is not removed on the released one's account.
    /// </summary>
    internal override void OnReleasedByManager(CallSession session)
        => _store.TryRemove(new KeyValuePair<string, CallSession>(session.SessionId, session));
}
