using Asterisk.Sdk.Live.Server;

namespace Asterisk.Sdk.Sessions.Manager;

public interface ICallSessionManager : IAsyncDisposable
{
    CallSession? GetById(string sessionId);
    CallSession? GetByLinkedId(string linkedId);
    CallSession? GetByChannelId(string uniqueId);
    CallSession? GetByBridgeId(string bridgeId);
    IEnumerable<CallSession> ActiveSessions { get; }
    IEnumerable<CallSession> GetRecentCompleted(int count = 100);

    IObservable<SessionDomainEvent> Events { get; }

    void AttachToServer(AsteriskServer server, string serverId);
    void DetachFromServer(string serverId);

    /// <summary>
    /// Registers a session reconstructed during cluster failover.
    /// Adds to all indices without firing creation events.
    /// Skips if a session with the same LinkedId already exists.
    /// </summary>
    bool RegisterReconstructedSession(CallSession session);
}
