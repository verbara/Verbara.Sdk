namespace Verbara.Sdk.Sessions.Extensions;

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

    /// <summary>
    /// Told by the session manager that it has released <paramref name="session"/>: the call ended
    /// more than <see cref="Manager.SessionOptions.CompletedRetention"/> ago, and the manager no longer
    /// holds it and will not save it again. Does nothing here. A store that provides durability keeps
    /// its own retention and has no business with the manager's (the Redis store expires its keys on
    /// its own option), which is also why the manager never calls <see cref="DeleteAsync"/>: that would
    /// delete the durable record the store exists to keep.
    /// <para>
    /// Internal, so no store outside this assembly sees it or has to override it. The default
    /// in-memory store overrides it, because it keeps the very object the manager held: without it,
    /// the manager's release would free a dictionary entry and leave the call held (<c>ADR-0063</c>,
    /// D5).
    /// </para>
    /// </summary>
    /// <param name="session">The session object the manager released.</param>
    internal virtual void OnReleasedByManager(CallSession session)
    {
        // A durable store keeps its own retention: nothing to do.
    }

    /// <inheritdoc />
    public virtual async ValueTask SaveBatchAsync(IReadOnlyList<CallSession> sessions, CancellationToken ct)
    {
        // Default: save one by one
        foreach (var session in sessions)
            await SaveAsync(session, ct);
    }
}
