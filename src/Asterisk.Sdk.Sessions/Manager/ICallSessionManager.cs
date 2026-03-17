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
}
