using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

    /// <inheritdoc cref="ISessionStore.GetByLinkedIdAsync(string, string, CancellationToken)" />
    public virtual async ValueTask<CallSession?> GetByLinkedIdAsync(string serverId, string linkedId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(serverId);
        ArgumentNullException.ThrowIfNull(linkedId);
        var session = await GetByLinkedIdAsync(linkedId, ct).ConfigureAwait(false);
        return session is not null && string.Equals(session.ServerId, serverId, StringComparison.Ordinal) ? session : null;
    }

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
    /// the manager's release would free a dictionary entry and leave the call held. A manager that
    /// lets go of an object its default store still holds frees nothing.
    /// </para>
    /// </summary>
    /// <param name="session">The session object the manager released.</param>
    internal virtual void OnReleasedByManager(CallSession session)
    {
        // A durable store keeps its own retention: nothing to do.
    }

    /// <summary>
    /// How often the sessions host runs <see cref="MaintainAsync"/> on this store, or <see langword="null"/> when the
    /// store needs no maintenance (the default, and the value of an SDK store whose option for it is unset). Read once,
    /// when the host starts.
    /// <para>
    /// Internal, as <see cref="OnReleasedByManager"/>: no store outside this SDK sees it, and a store that wraps an SDK
    /// store is therefore never maintained.
    /// </para>
    /// </summary>
    internal virtual TimeSpan? MaintenanceInterval => null;

    /// <summary>
    /// Where a maintenance run reports what it removed. Set by the sessions host before
    /// <see cref="ArmMaintenance"/>; nothing is logged until then.
    /// </summary>
    internal ILogger MaintenanceLogger { get; set; } = NullLogger.Instance;

    /// <summary>
    /// Called by the sessions host, synchronously and before any hosted service starts, when
    /// <see cref="MaintenanceInterval"/> is not <see langword="null"/>: from then on the store may give the records it
    /// writes a deadline, because something will refresh them. A store nobody arms writes what it writes today. Does no
    /// I/O.
    /// </summary>
    internal virtual void ArmMaintenance()
    {
        // No maintenance by default.
    }

    /// <summary>
    /// One maintenance run: <paramref name="heldActive"/> is what the session manager holds at that moment. A store
    /// keeps those records alive and may remove the records of a process that stopped keeping its own. Runs
    /// never overlap. Does nothing by default.
    /// </summary>
    /// <param name="heldActive">The sessions the manager holds; ended ones among them are left alone.</param>
    /// <param name="ct">Cancelled when the host stops.</param>
    internal virtual ValueTask MaintainAsync(IReadOnlyList<CallSession> heldActive, CancellationToken ct) =>
        ValueTask.CompletedTask;

    /// <inheritdoc />
    public virtual async ValueTask SaveBatchAsync(IReadOnlyList<CallSession> sessions, CancellationToken ct)
    {
        // Default: save one by one
        foreach (var session in sessions)
            await SaveAsync(session, ct);
    }
}
