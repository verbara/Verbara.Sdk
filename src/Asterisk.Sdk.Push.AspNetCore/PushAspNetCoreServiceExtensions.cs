namespace Asterisk.Sdk.Push.AspNetCore;

using Microsoft.Extensions.DependencyInjection;
using Asterisk.Sdk.Push.Hosting;

/// <summary>
/// DI registration for Asterisk.Sdk.Push.AspNetCore.
/// </summary>
public static class PushAspNetCoreServiceExtensions
{
    /// <summary>
    /// Registers all Asterisk push services required by the SSE endpoint.
    /// Calls <c>AddAsteriskPush()</c> internally — safe to call multiple times.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configurePush">Optional delegate to configure <see cref="Asterisk.Sdk.Push.Bus.PushEventBusOptions"/>.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddAsteriskPushAspNetCore(
        this IServiceCollection services,
        Action<Asterisk.Sdk.Push.Bus.PushEventBusOptions>? configurePush = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAsteriskPush(configurePush);

        return services;
    }
}
