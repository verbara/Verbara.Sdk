namespace Verbara.Sdk.Push.AspNetCore;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Push.Hosting;

/// <summary>
/// DI registration for Verbara.Sdk.Push.AspNetCore.
/// </summary>
public static class PushAspNetCoreServiceExtensions
{
    /// <summary>
    /// Registers all Asterisk push services required by the SSE endpoint, and the SSE stream's
    /// <see cref="SsePushStreamOptions"/>, validated when the host starts.
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

        // AOT-safe manual validation, checked at start: an unusable bound stops the host instead of every stream.
        services.AddOptions<SsePushStreamOptions>().ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<SsePushStreamOptions>, SsePushStreamOptionsValidator>());

        return services;
    }
}
