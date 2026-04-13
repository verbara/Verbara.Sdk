using Asterisk.Sdk.Push.Bus;
using Asterisk.Sdk.Push.Delivery;
using Asterisk.Sdk.Push.Diagnostics;
using Asterisk.Sdk.Push.Subscriptions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.Push.Hosting;

/// <summary>
/// DI extension methods to register Asterisk.Sdk.Push services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the in-memory push event bus, subscription registry, default delivery filter,
    /// and diagnostics meter. Safe to call multiple times — subsequent calls reapply the
    /// configuration delegate but do not duplicate registrations.
    /// </summary>
    public static IServiceCollection AddAsteriskPush(
        this IServiceCollection services,
        Action<PushEventBusOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<PushEventBusOptions>()
            .Configure(configure ?? (static _ => { }));

        // AOT-safe manual validation — DataAnnotations runtime path is not trim-safe.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<PushEventBusOptions>, PushEventBusOptionsValidator>());

        services.AddSingleton<PushMetrics>();
        services.AddSingleton<IPushEventBus, RxPushEventBus>();
        services.AddSingleton<IEventDeliveryFilter, DefaultDeliveryFilter>();
        services.AddSingleton<ISubscriptionRegistry, InMemorySubscriptionRegistry>();

        return services;
    }

    private sealed class PushEventBusOptionsValidator : IValidateOptions<PushEventBusOptions>
    {
        public ValidateOptionsResult Validate(string? name, PushEventBusOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (options.BufferCapacity < 1)
            {
                return ValidateOptionsResult.Fail(
                    $"{nameof(PushEventBusOptions.BufferCapacity)} must be >= 1 (was {options.BufferCapacity}).");
            }
            return ValidateOptionsResult.Success;
        }
    }
}
