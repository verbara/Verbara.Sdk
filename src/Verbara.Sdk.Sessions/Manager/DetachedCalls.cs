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
    Release = 1,
}
