using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.Push.Nats;

/// <summary>
/// DI extensions that wire up the <see cref="NatsBridge"/> hosted service plus its
/// validated options and default payload serializer.
/// </summary>
public static class NatsServiceCollectionExtensions
{
    /// <summary>
    /// Register the NATS bridge: options (AOT-safe validated via source generator +
    /// custom scheme check), <see cref="NatsMetrics"/>, the default payload serializer,
    /// and the <see cref="NatsBridge"/> hosted service that consumes the Push bus.
    /// </summary>
    /// <remarks>
    /// Consumers can override the payload shape by registering a custom
    /// <see cref="INatsPayloadSerializer"/> before calling this method — the built-in
    /// registrations use <c>TryAdd*</c> semantics.
    /// </remarks>
    public static IServiceCollection AddPushNats(
        this IServiceCollection services,
        Action<NatsBridgeOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<NatsBridgeOptions>()
            .Configure(configure)
            .ValidateOnStart();

        // AOT-safe source-generated validator (DataAnnotations) + custom scheme/prefix checks.
        services.AddSingleton<IValidateOptions<NatsBridgeOptions>, NatsBridgeOptionsValidator>();
        services.AddSingleton<IValidateOptions<NatsBridgeOptions>, NatsBridgeOptionsCustomValidator>();

        services.TryAddSingleton<INatsPayloadSerializer, DefaultNatsPayloadSerializer>();
        services.TryAddSingleton<INatsPayloadDeserializer, DefaultNatsPayloadDeserializer>();
        services.TryAddSingleton<NatsMetrics>();

        services.AddSingleton<NatsBridge>();
        services.AddHostedService(sp => sp.GetRequiredService<NatsBridge>());

        return services;
    }
}
