namespace Verbara.Sdk.Sessions.Manager;

/// <summary>
/// What <see cref="ICallSessionManager.DetachFromServer(string, DetachedCalls)"/> does with the calls the session
/// engine holds for the server it detaches.
/// </summary>
public enum DetachedCalls
{
    /// <summary>The calls stay held, exactly as with <see cref="ICallSessionManager.DetachFromServer(string)"/>.</summary>
    Keep = 0,

    /// <summary>The server's calls in progress are released without being ended.</summary>
    /// <remarks>
    /// <para><b>What the release waits for.</b> It closes the server's attachment to every event not yet handled and
    /// waits for the handlers of that attachment already running — at most a few seconds — so that a hangup or an
    /// arrival already being handled takes effect first: a call it ended is not released, and a call it opened is.
    /// It then waits at most a second for each call's lock; a call whose lock is still held past that is skipped, stays
    /// held under the server id and is released by a later release of it. Either limit expiring writes one Error entry
    /// naming the server, the handlers still in flight and the calls skipped. The wait covers only the attachment the
    /// release closes: for an id detached earlier with <see cref="Keep"/>, a handler of that attachment still running
    /// may complete just after the release notification. An attach or a second release of the same id asked for while
    /// a release runs waits for it to finish.</para>
    /// <para><b>A call-released subscriber must not block.</b> The limits bound the engine's own waits, not the
    /// subscribers of <see cref="CallReleasedEvent"/>: they run on the releasing thread while the call's lock is held,
    /// so one that blocks holds the release, and any attach or release of that id waiting for it, until it returns.
    /// Hand slow work to another thread.</para>
    /// <para><b>Not from inside a notification.</b> A release asked for from inside any notification of the session
    /// engine — and an attach that would have to wait for a release in progress — throws
    /// <see cref="System.InvalidOperationException"/> naming the server id, and detaches and releases nothing: it would
    /// wait for the thread delivering the notification. Handle that exception inside the subscriber, and ask from
    /// another thread. One the subscriber lets escape is reported like any subscriber failure, and the notification
    /// still reaches the later subscribers.</para>
    /// <para><b>A wrap-up already open keeps running.</b> A wrap-up opened for the call before its release closes as it
    /// would have without the release, by its duration or by the agent's next connection; its one
    /// <see cref="CallWrapUpEvent"/> carries the released session id and may follow the
    /// <see cref="CallReleasedEvent"/>.</para>
    /// <para><b>What is not seen again.</b> An agent whose current call is released keeps its state, on a call
    /// included, with no current call, until another event of that agent reaches this process — which, for a server it
    /// no longer watches, may never happen. A released call whose hangup falls while its server is away is never ended
    /// in this process; when the server id is attached again, a call still up there gets a new session only by the
    /// paths that open one.</para>
    /// </remarks>
    Release = 1,
}
