using System.Collections.Concurrent;
using Verbara.Sdk.Sessions.Extensions;

namespace Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;

/// <summary>
/// A store that keeps nothing and records every save as the session read at that moment: its id, its
/// state, its connected time and its agent interface. A session is mutable, so the record is taken inside the save, not read
/// back afterwards.
/// </summary>
/// <remarks>
/// The manager hands a save to the store synchronously, on the thread that changed the session, and this
/// store completes it at once, so a save is recorded before the call that caused it returns.
/// </remarks>
internal sealed class RecordingSessionStore : SessionStoreBase
{
    private readonly ConcurrentQueue<SavedSession> _saves = new();

    /// <summary>Every save, in the order the manager made them.</summary>
    public IReadOnlyList<SavedSession> Saves => [.. _saves];

    /// <summary>How many saves the manager made of the session <paramref name="sessionId"/>.</summary>
    public int SavesOf(string sessionId) => _saves.Count(s => s.SessionId == sessionId);

    public override ValueTask SaveAsync(CallSession session, CancellationToken ct)
    {
        _saves.Enqueue(new SavedSession(session.SessionId, session.State, session.ConnectedAt, session.AgentInterface));
        return ValueTask.CompletedTask;
    }

    public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) =>
        ValueTask.FromResult<CallSession?>(null);
}

/// <summary>A session as a store was handed it: its id, its state, its connected time and its agent interface.</summary>
internal sealed record SavedSession(string SessionId, CallSessionState State, DateTimeOffset? ConnectedAt, string? AgentInterface);
