using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Push.Webhooks;

/// <summary>
/// DI extensions for wiring up <see cref="WebhookDeliveryService"/>.
/// </summary>
public static class WebhookServiceCollectionExtensions
{
    /// <summary>
    /// Register the webhook delivery pipeline: in-memory subscription store, HMAC-SHA256
    /// signer, default JSON payload serializer, HttpClient, and a <see cref="WebhookDeliveryService"/>
    /// hosted service that consumes the Push bus.
    /// </summary>
    /// <remarks>
    /// Consumers can override any piece by registering a replacement BEFORE calling
    /// <c>AddVerbaraPushWebhooks</c>, or by calling <c>services.Replace(...)</c> after.
    /// The built-in registrations use <c>TryAdd*</c> semantics.
    /// <see cref="WebhookDeliveryOptions"/> is validated when it is first resolved (at the latest when the
    /// hosted service is built at host start); an unusable value fails with an
    /// <see cref="OptionsValidationException"/> naming the option.
    /// </remarks>
    public static IServiceCollection AddVerbaraPushWebhooks(
        this IServiceCollection services,
        Action<WebhookDeliveryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is not null)
            services.Configure(configure);

        services.AddOptions<WebhookDeliveryOptions>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<WebhookDeliveryOptions>, WebhookDeliveryOptionsValidator>());
        services.TryAddSingleton<IWebhookSubscriptionStore, InMemoryWebhookSubscriptionStore>();
        services.TryAddSingleton<IWebhookSigner, HmacSha256Signer>();
        services.TryAddSingleton<IWebhookPayloadSerializer, DefaultWebhookPayloadSerializer>();
        services.TryAddSingleton<WebhookMetrics>();
        services.AddHttpClient(nameof(WebhookDeliveryService));
        services.AddHostedService<WebhookDeliveryService>();

        return services;
    }
}
