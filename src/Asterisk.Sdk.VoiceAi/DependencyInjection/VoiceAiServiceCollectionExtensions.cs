using System.Diagnostics.CodeAnalysis;
using Asterisk.Sdk.VoiceAi.Diagnostics;
using Asterisk.Sdk.VoiceAi.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asterisk.Sdk.VoiceAi.DependencyInjection;

/// <summary>Extension methods for registering Voice AI pipeline services.</summary>
public static class VoiceAiServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Voice AI pipeline, session broker, and conversation handler.
    /// </summary>
    /// <typeparam name="THandler">
    /// The <see cref="IConversationHandler"/> implementation, registered as Scoped
    /// so each pipeline session gets its own instance.
    /// </typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration action for <see cref="VoiceAiPipelineOptions"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddVoiceAiPipeline<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(
        this IServiceCollection services,
        Action<VoiceAiPipelineOptions>? configure = null)
        where THandler : class, IConversationHandler
    {
        services.TryAddScoped<IConversationHandler, THandler>();
        services.TryAddSingleton<VoiceAiPipeline>();
        services.TryAddSingleton<ISessionHandler>(sp => sp.GetRequiredService<VoiceAiPipeline>());
        services.TryAddSingleton<VoiceAiSessionBroker>();
        services.AddHostedService<VoiceAiSessionBroker>(sp => sp.GetRequiredService<VoiceAiSessionBroker>());

        services.AddHealthChecks().AddCheck<VoiceAiHealthCheck>("voiceai");

        if (configure is not null)
            services.Configure(configure);
        else
            services.AddOptions<VoiceAiPipelineOptions>();

        return services;
    }
}
