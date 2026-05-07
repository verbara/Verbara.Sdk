using Verbara.Sdk.VoiceAi;
using Verbara.Sdk.VoiceAi.TurnDetection;
using Verbara.Sdk.VoiceAi.TurnDetection.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.VoiceAi.TurnDetection;

/// <summary>DI extensions for registering ML-based smart turn detection.</summary>
public static class TurnDetectionServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SmartTurnDetector"/> as the <see cref="ITurnDetector"/> implementation,
    /// replacing the default <c>SilenceTurnDetector</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration action for <see cref="SmartTurnDetectorOptions"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSmartTurnDetection(
        this IServiceCollection services,
        Action<SmartTurnDetectorOptions>? configure = null)
    {
        services.AddOptions<SmartTurnDetectorOptions>()
            .Configure(configure ?? (_ => { }))
            .ValidateOnStart();
        services.TryAddSingleton<IValidateOptions<SmartTurnDetectorOptions>, SmartTurnDetectorOptionsValidator>();

        services.TryAddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<SmartTurnDetectorOptions>>().Value;
            return new OnnxSessionManager(opts.ExecutionProvider, opts.IntraOpThreads);
        });

        // Replace any previously registered ITurnDetector (e.g., SilenceTurnDetector)
        services.RemoveAll<ITurnDetector>();
        services.AddTransient<ITurnDetector>(sp =>
            new SmartTurnDetector(
                sp.GetRequiredService<IOptions<SmartTurnDetectorOptions>>(),
                sp.GetRequiredService<OnnxSessionManager>()));

        return services;
    }
}
