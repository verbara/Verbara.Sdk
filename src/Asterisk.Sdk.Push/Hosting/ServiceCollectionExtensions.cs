using Asterisk.Sdk.Push.Authz;
using Asterisk.Sdk.Push.Bus;
using Asterisk.Sdk.Push.Delivery;
using Asterisk.Sdk.Push.Diagnostics;
using Asterisk.Sdk.Push.Subscriptions;
using Asterisk.Sdk.Push.Topics;

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

        services.AddHealthChecks()
            .AddCheck<Asterisk.Sdk.Push.Diagnostics.PushHealthCheck>("push");

        // Topic registry — thread-safe, singleton. Consumers register their event→topic
        // mappings at startup (e.g. inside a hosted service or IConfigureOptions).
        services.TryAddSingleton<ITopicRegistry, TopicRegistry>();

        // Default authorizer allows every subscription. Platform or other consumers
        // replace this with an RBAC-aware implementation via TryAddSingleton before
        // calling AddAsteriskPush, or by overriding after the call.
        services.TryAddSingleton<ISubscriptionAuthorizer, AllowAllSubscriptionAuthorizer>();

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
