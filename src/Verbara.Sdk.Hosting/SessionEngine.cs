using Verbara.Sdk.Sessions.Manager;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Sdk.Hosting;

/// <summary>
/// Finds the SDK's session engine behind the host's <see cref="ICallSessionManager"/>, for the services that act on the
/// engine itself: the persistence token and the reconciliation sweep. It looks the engine up and never creates one.
/// </summary>
internal static class SessionEngine
{
    /// <summary>
    /// The resolved <see cref="ICallSessionManager"/> when it is a <see cref="CallSessionManager"/>; else the
    /// <see cref="CallSessionManager"/> the host registered as itself, the shape a decorator takes (the engine registered
    /// as <see cref="CallSessionManager"/>, the decorator over it as <see cref="ICallSessionManager"/>); else
    /// <see langword="null"/>: no engine is reachable, as for a manager of the host's own or a decorator over an engine
    /// the container does not know.
    /// </summary>
    /// <remarks>
    /// The interface comes first: a host that registers <see cref="CallSessionManager"/> as itself beside the SDK's
    /// default interface holds two engines, and the one the interface resolves is the one the SDK attaches. The SDK
    /// registers no <see cref="CallSessionManager"/> of its own, so <c>GetService</c> builds only an engine the host
    /// asked for.
    /// </remarks>
    public static CallSessionManager? Resolve(IServiceProvider services) =>
        services.GetRequiredService<ICallSessionManager>() as CallSessionManager
        ?? services.GetService<CallSessionManager>();
}
