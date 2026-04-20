using Asterisk.Sdk.Resilience.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asterisk.Sdk.Resilience.DependencyInjection;

/// <summary>
/// DI extensions for registering the resilience primitives.
/// </summary>
public static class ResilienceServiceCollectionExtensions
{
    /// <summary>
    /// Registers a singleton <see cref="ResiliencePolicy"/> built from the supplied
    /// <paramref name="configure"/> callback (or <see cref="ResiliencePolicy.NoOp"/>
    /// when <paramref name="configure"/> is <see langword="null"/>), plus a singleton
    /// <see cref="TimeProvider.System"/> if no <see cref="TimeProvider"/> is already
    /// registered.
    /// </summary>
    /// <remarks>
    /// The <see cref="ResilienceMetrics.Meter"/> is automatically enrolled for
    /// OpenTelemetry consumption when SDK's
    /// <c>AddAsteriskOpenTelemetry()</c> is used alongside this method.
    /// </remarks>
    public static IServiceCollection AddAsteriskResilience(
        this IServiceCollection services,
        Action<ResiliencePolicyBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        var builder = new ResiliencePolicyBuilder();
        if (configure is not null)
        {
            configure(builder);
        }

        var policy = configure is null ? ResiliencePolicy.NoOp : builder.Build();
        services.TryAddSingleton(policy);

        return services;
    }
}
