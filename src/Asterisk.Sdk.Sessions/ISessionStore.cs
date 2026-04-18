namespace Asterisk.Sdk.Sessions;

/// <summary>
/// Abstraction for persistent storage of <see cref="CallSession"/> instances.
/// Implementations may be in-memory, Redis, PostgreSQL, or any custom backing store.
/// </summary>
/// <remarks>
/// This interface mirrors the surface of <see cref="Extensions.SessionStoreBase"/>
/// to enable mocking (for example with NSubstitute) and to serve as the registration
/// contract for backend-specific extension packages such as
/// <c>Asterisk.Sdk.Sessions.Redis</c> and <c>Asterisk.Sdk.Sessions.Postgres</c>.
/// <para>
/// Existing consumers that depend on <see cref="Extensions.SessionStoreBase"/> continue
/// to work unchanged; the base class now implements this interface transitively.
/// </para>
/// </remarks>
public interface ISessionStore
{
    /// <summary>
    /// Persist the specified <paramref name="session"/> in the backing store.
    /// </summary>
    /// <param name="session">The call session to save.</param>
    /// <param name="ct">A <see cref="CancellationToken"/> to observe while waiting for the task to complete.</param>
    ValueTask SaveAsync(CallSession session, CancellationToken ct);

    /// <summary>
    /// Retrieve a session by its identifier.
    /// </summary>
    /// <param name="sessionId">The unique session identifier.</param>
    /// <param name="ct">A <see cref="CancellationToken"/> to observe while waiting for the task to complete.</param>
    /// <returns>The matching <see cref="CallSession"/>, or <c>null</c> when no session exists for the given id.</returns>
    ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct);

    /// <summary>
    /// Retrieve all sessions that are currently in a non-terminal state.
    /// </summary>
    /// <param name="ct">A <see cref="CancellationToken"/> to observe while waiting for the task to complete.</param>
    /// <returns>An enumerable of active sessions.</returns>
    ValueTask<IEnumerable<CallSession>> GetActiveAsync(CancellationToken ct);

    /// <summary>
    /// Remove the session with the specified identifier from the backing store.
    /// </summary>
    /// <param name="sessionId">The unique session identifier.</param>
    /// <param name="ct">A <see cref="CancellationToken"/> to observe while waiting for the task to complete.</param>
    ValueTask DeleteAsync(string sessionId, CancellationToken ct);

    /// <summary>
    /// Retrieve a session by its Asterisk LinkedID (the call's master channel id).
    /// </summary>
    /// <param name="linkedId">The Asterisk LinkedID correlating multiple channels of the same logical call.</param>
    /// <param name="ct">A <see cref="CancellationToken"/> to observe while waiting for the task to complete.</param>
    /// <returns>The matching <see cref="CallSession"/>, or <c>null</c> when no session carries that LinkedID.</returns>
    ValueTask<CallSession?> GetByLinkedIdAsync(string linkedId, CancellationToken ct);

    /// <summary>
    /// Persist a batch of sessions in a single operation. Implementations may optimize this
    /// using a transaction or pipelined write; the default implementation on
    /// <see cref="Extensions.SessionStoreBase"/> falls back to per-session <see cref="SaveAsync"/>.
    /// </summary>
    /// <param name="sessions">The sessions to save.</param>
    /// <param name="ct">A <see cref="CancellationToken"/> to observe while waiting for the task to complete.</param>
    ValueTask SaveBatchAsync(IReadOnlyList<CallSession> sessions, CancellationToken ct);
}
