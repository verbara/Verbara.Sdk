namespace Verbara.Sdk.Push.AspNetCore;

using Microsoft.Extensions.DependencyInjection;
using Verbara.Sdk.Push.Hosting;

/// <summary>
/// DI registration for Verbara.Sdk.Push.AspNetCore.
/// </summary>
public static class PushAspNetCoreServiceExtensions
{
    /// <summary>
    /// Registers all Asterisk push services required by the SSE endpoint.
    /// Calls <c>AddVerbaraPush()</c> internally — safe to call multiple times.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configurePush">Optional delegate to configure <see cref="Verbara.Sdk.Push.Bus.PushEventBusOptions"/>.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddVerbaraPushAspNetCore(
        this IServiceCollection services,
        Action<Verbara.Sdk.Push.Bus.PushEventBusOptions>? configurePush = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddVerbaraPush(configurePush);

        return services;
    }
}
