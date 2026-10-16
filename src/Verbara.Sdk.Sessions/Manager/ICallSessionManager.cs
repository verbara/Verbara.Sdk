using Verbara.Sdk.Live.Server;

namespace Verbara.Sdk.Sessions.Manager;

public interface ICallSessionManager : IAsyncDisposable
{
    CallSession? GetById(string sessionId);
    CallSession? GetByLinkedId(string linkedId);
    CallSession? GetByChannelId(string uniqueId);
    CallSession? GetByBridgeId(string bridgeId);
    IEnumerable<CallSession> ActiveSessions { get; }
    IEnumerable<CallSession> GetRecentCompleted(int count = 100);

    /// <summary>
    /// The session engine's notifications about the calls it tracks, and the stream's completion when the engine is
    /// disposed.
    /// </summary>
    /// <remarks>
    /// <para>Each subscriber runs on the thread that raised the notification — the thread delivering the server's
    /// event, a reload, a sweep or the disposal — and, for some kinds of notification, while the engine holds that
    /// call's lock. A subscriber that blocks holds them up.</para>
    /// <para>An exception a subscriber throws is caught at that subscriber, logged once at Warning with the
    /// notification's type and session id, and counted on <c>sessions.events.subscriber_faults</c>. The other
    /// subscribers still receive the notification, and the engine's own work for the call, the live state's handlers
    /// and a reload or sweep that raised it complete as with no failing subscriber. A subscriber subscribed directly
    /// stays subscribed and receives later notifications.</para>
    /// <para>A subscription composed through an operator (<c>Where</c>, <c>Select</c>, <c>OfType</c>, …) is ended by
    /// the composition library on the first exception from the operator's function or from the handler behind it, so
    /// one Warning can mean that subscriber is gone for good; subscribe directly and filter inside the handler to stay
    /// subscribed.</para>
    /// <para>Only what throws on the delivering thread is caught. A subscriber that moves its work elsewhere —
    /// <c>ObserveOn</c> or <c>SubscribeOn</c>, an <c>async void</c> handler, work pushed to <c>Task.Run</c> — throws on
    /// another thread, and that exception is not caught here.</para>
    /// </remarks>
    IObservable<SessionDomainEvent> Events { get; }

    void AttachToServer(VerbaraServer server, string serverId);
    void DetachFromServer(string serverId);

    /// <summary>
    /// Registers a session reconstructed during cluster failover.
    /// Adds to all indices without firing creation events.
    /// Skips if a session with the same LinkedId already exists.
    /// </summary>
    bool RegisterReconstructedSession(CallSession session);
}
