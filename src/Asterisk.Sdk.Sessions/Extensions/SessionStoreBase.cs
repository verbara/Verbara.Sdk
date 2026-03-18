namespace Asterisk.Sdk.Sessions.Extensions;

public abstract class SessionStoreBase
{
    public abstract ValueTask SaveAsync(CallSession session, CancellationToken ct);
    public abstract ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct);
    public virtual ValueTask<IEnumerable<CallSession>> GetActiveAsync(CancellationToken ct)
        => ValueTask.FromResult(Enumerable.Empty<CallSession>());
    public virtual ValueTask DeleteAsync(string sessionId, CancellationToken ct)
        => ValueTask.CompletedTask;

    public virtual ValueTask<CallSession?> GetByLinkedIdAsync(string linkedId, CancellationToken ct)
        => ValueTask.FromResult<CallSession?>(null);

    public virtual async ValueTask SaveBatchAsync(IReadOnlyList<CallSession> sessions, CancellationToken ct)
    {
        // Default: save one by one
        foreach (var session in sessions)
            await SaveAsync(session, ct);
    }
}
