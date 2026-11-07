using Verbara.Sdk.Live.Server;

namespace Verbara.Sdk.Sessions.Manager;

public interface ICallSessionManager : IAsyncDisposable
{
    CallSession? GetById(string sessionId);
    CallSession? GetByLinkedId(string linkedId);
    CallSession? GetByChannelId(string uniqueId);
    CallSession? GetByBridgeId(string bridgeId);

    /// <summary>Returns the unscoped linked answer only when it belongs to the named server.</summary>
    /// <remarks>The default can return null for an ambiguous id even when the engine's scoped implementation finds it.</remarks>
    CallSession? GetByLinkedId(string serverId, string linkedId)
    {
        var call = GetByLinkedId(linkedId);
        return call?.ServerId == serverId ? call : null;
    }

    /// <summary>Returns the unscoped channel answer only when it belongs to the named server.</summary>
    /// <remarks>The default can return null for an ambiguous id even when the engine's scoped implementation finds it.</remarks>
    CallSession? GetByChannelId(string serverId, string uniqueId)
    {
        var call = GetByChannelId(uniqueId);
        return call?.ServerId == serverId ? call : null;
    }

    /// <summary>Returns the unscoped bridge answer only when it belongs to the named server.</summary>
    /// <remarks>The default can return null for an ambiguous id even when the engine's scoped implementation finds it.</remarks>
    CallSession? GetByBridgeId(string serverId, string bridgeId)
    {
        var call = GetByBridgeId(bridgeId);
        return call?.ServerId == serverId ? call : null;
    }
    IEnumerable<CallSession> ActiveSessions { get; }
    IEnumerable<CallSession> GetRecentCompleted(int count = 100);

    /// <summary>
    /// The session engine's notifications about the calls it tracks, and the stream's completion when the engine is
    /// disposed.
    /// </summary>
    /// <remarks>
    /// <para>Each subscriber runs on the thread that raised the notification — the thread delivering the server's
    /// event, a reload, a sweep, a release or the disposal. Every notification of a call — its start, queueing,
    /// connection, hold, resumption, transfer, ending and release — is raised while the engine holds that call's lock;
    /// a <see cref="CallWrapUpEvent"/> is the agent's and is not. A subscriber that blocks holds them up.</para>
    /// <para>Do not attach a server or release its calls from a subscriber: either request throws
    /// <see cref="InvalidOperationException"/> before it changes the attachment. Handle the refusal here and schedule
    /// the request on another thread without waiting for it: attach takes left calls' locks, including this one.
    /// A preceding Keep detach stands; it is allowed and marks nothing. The refusal also covers another manager's
    /// notifications and a same-instance attach.</para>
    /// <para>An exception a subscriber throws is caught at that subscriber, logged once at Warning with the
    /// notification's type and session id (a failure on the stream's completion is logged naming <c>completion</c>, with
    /// no session id), and counted on <c>sessions.events.subscriber_faults</c>; an <c>OutOfMemoryException</c> is not
    /// caught. The other
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

    /// <summary>Attaches this instance under the id; an admitted same-instance attach changes nothing.</summary>
    /// <remarks>
    /// Reconciles left calls against an already complete table after subscribing, on this thread. It waits at most
    /// one second per busy call lock; each skipped call stays pending and an Error names it. Its subscribers also run
    /// on this thread. This acquisition limit bounds neither subscriber execution nor waiting for a prior release.
    /// Do not call from an engine notification or attachment handler: handle the refusal inside the subscriber and
    /// schedule attach elsewhere without waiting here. A Keep detach already made stands.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A different instance is already attached under this id, or attach
    /// was requested inside an engine notification or attachment handler, including a same-instance no-op.</exception>
    void AttachToServer(VerbaraServer server, string serverId);
    void DetachFromServer(string serverId);

    /// <summary>Admits calls already held by the instance attached under <paramref name="serverId"/>.</summary>
    /// <remarks>
    /// Call after attach and reconstruction. Only an incomplete table requests a channel snapshot; admission preserves
    /// channel owners and is idempotent. A refused snapshot leaves unverified calls waiting for a later completed one.
    /// The operation is bound to the captured instance and attachment generation, and stops normally on detach.
    /// Its walk never runs on the caller's thread. A session-event subscriber must return without waiting for it:
    /// the subscriber holds a call lock, while the walk takes channel locks before call locks.
    /// The default implementation admits nothing and completes without checking the id.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The engine implementation has no instance attached under the id.</exception>
    ValueTask AdmitExistingCalls(string serverId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    /// <summary>
    /// Detaches <paramref name="serverId"/> and, with <see cref="DetachedCalls.Release"/>, releases the calls the
    /// engine holds for it; returns the calls released.
    /// </summary>
    /// <remarks>
    /// The default implementation calls <see cref="DetachFromServer(string)"/>, releases nothing and returns no call:
    /// an implementation that does not override this member releases nothing.
    /// </remarks>
    IReadOnlyList<CallSession> DetachFromServer(string serverId, DetachedCalls calls)
    {
        DetachFromServer(serverId);
        return [];
    }

    /// <summary>
    /// Registers a reconstructed call within its own server's indexes without announcing a new call start.
    /// </summary>
    /// <remarks>
    /// <para>A held session id, a null or empty linkedid, a linkedid already held on the same server, or a leg that
    /// server maps to another call refuses the registration and is logged at Information. A refusal leaves existing
    /// entries unchanged and neither joins, persists nor announces the refused call. Registering the same instance
    /// again returns false without changing indexes, notifications or store. A bridge already mapped on that server
    /// is kept and logged at Debug.</para>
    /// <para>Joins held legs of the attached server. Do not call synchronously from a session-event subscriber:
    /// registration takes channel locks, while notifications run under a call lock. Schedule it outside the callback.</para>
    /// </remarks>
    bool RegisterReconstructedSession(CallSession session);
}
