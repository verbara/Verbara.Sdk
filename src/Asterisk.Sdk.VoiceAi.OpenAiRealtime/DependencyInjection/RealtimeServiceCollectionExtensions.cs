using System.Diagnostics.CodeAnalysis;
using Asterisk.Sdk.VoiceAi.OpenAiRealtime.FunctionCalling;
using Asterisk.Sdk.VoiceAi.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.DependencyInjection;

/// <summary>Extension methods for registering the OpenAI Realtime bridge in the DI container.</summary>
public static class RealtimeServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="OpenAiRealtimeBridge"/> as the <see cref="ISessionHandler"/> singleton
    /// and starts <see cref="VoiceAiSessionBroker"/> as a hosted service.
    /// </summary>
    /// <remarks>
    /// Prerequisite: <c>services.AddAudioSocketServer()</c> must be called before this method.
    /// </remarks>
    /// <returns>The service collection for chaining <c>AddFunction&lt;T&gt;()</c> calls.</returns>
    public static IServiceCollection AddOpenAiRealtimeBridge(
        this IServiceCollection services,
        Action<OpenAiRealtimeOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);
        else
            services.AddOptions<OpenAiRealtimeOptions>();

        services.AddSingleton<IValidateOptions<OpenAiRealtimeOptions>, OpenAiRealtimeOptionsValidator>();

        // Factory lambdas required: both types have internal constructors.
        services.TryAddSingleton<RealtimeFunctionRegistry>(sp =>
            new RealtimeFunctionRegistry(sp.GetServices<IRealtimeFunctionHandler>()));

        services.TryAddSingleton<OpenAiRealtimeBridge>(sp =>
            new OpenAiRealtimeBridge(
                sp.GetRequiredService<IOptions<OpenAiRealtimeOptions>>(),
                sp.GetRequiredService<RealtimeFunctionRegistry>(),
                sp.GetRequiredService<ILogger<OpenAiRealtimeBridge>>()));

        services.TryAddSingleton<ISessionHandler>(
            sp => sp.GetRequiredService<OpenAiRealtimeBridge>());

        services.TryAddSingleton<VoiceAiSessionBroker>();
        services.AddHostedService<VoiceAiSessionBroker>(
            sp => sp.GetRequiredService<VoiceAiSessionBroker>());

        return services;
    }

    /// <summary>
    /// Registers a function tool that can be invoked by the OpenAI Realtime model.
    /// Multiple calls to <c>AddFunction</c> add multiple handlers.
    /// </summary>
    /// <typeparam name="THandler">
    /// The <see cref="IRealtimeFunctionHandler"/> implementation. Must be singleton-safe.
    /// </typeparam>
    public static IServiceCollection AddFunction<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(
        this IServiceCollection services)
        where THandler : class, IRealtimeFunctionHandler
    {
        // Intentionally NOT TryAddSingleton -- allows multiple different handlers.
        services.AddSingleton<IRealtimeFunctionHandler, THandler>();
        return services;
    }
}
