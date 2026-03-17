using System.Collections.Concurrent;
using Asterisk.Sdk.Sessions.Extensions;

namespace Asterisk.Sdk.Sessions.Internal;

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
}
