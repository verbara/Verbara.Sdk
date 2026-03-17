namespace Asterisk.Sdk.Sessions.Extensions;

public abstract class SessionStoreBase
{
    public abstract ValueTask SaveAsync(CallSession session, CancellationToken ct);
    public abstract ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct);
    public virtual ValueTask<IEnumerable<CallSession>> GetActiveAsync(CancellationToken ct)
        => ValueTask.FromResult(Enumerable.Empty<CallSession>());
    public virtual ValueTask DeleteAsync(string sessionId, CancellationToken ct)
        => ValueTask.CompletedTask;
}
