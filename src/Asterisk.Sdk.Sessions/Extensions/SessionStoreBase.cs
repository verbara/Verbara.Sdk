namespace Asterisk.Sdk.Sessions.Extensions;

/// <summary>
/// Base class for custom <see cref="ISessionStore"/> implementations.
/// Provides virtual default implementations for optional members while forcing
/// derived types to supply <see cref="SaveAsync"/> and <see cref="GetAsync"/>.
/// </summary>
public abstract class SessionStoreBase : ISessionStore
{
    /// <inheritdoc />
    public abstract ValueTask SaveAsync(CallSession session, CancellationToken ct);

    /// <inheritdoc />
    public abstract ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct);

    /// <inheritdoc />
    public virtual ValueTask<IEnumerable<CallSession>> GetActiveAsync(CancellationToken ct)
        => ValueTask.FromResult(Enumerable.Empty<CallSession>());

    /// <inheritdoc />
    public virtual ValueTask DeleteAsync(string sessionId, CancellationToken ct)
        => ValueTask.CompletedTask;

    /// <inheritdoc />
    public virtual ValueTask<CallSession?> GetByLinkedIdAsync(string linkedId, CancellationToken ct)
        => ValueTask.FromResult<CallSession?>(null);

    /// <inheritdoc />
    public virtual async ValueTask SaveBatchAsync(IReadOnlyList<CallSession> sessions, CancellationToken ct)
    {
        // Default: save one by one
        foreach (var session in sessions)
            await SaveAsync(session, ct);
    }
}
