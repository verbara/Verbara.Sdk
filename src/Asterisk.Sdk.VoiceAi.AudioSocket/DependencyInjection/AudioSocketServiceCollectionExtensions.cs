using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asterisk.Sdk.VoiceAi.AudioSocket.DependencyInjection;

/// <summary>Extension methods for registering AudioSocket services.</summary>
public static class AudioSocketServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="AudioSocketServer"/> as a hosted service.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration action for <see cref="AudioSocketOptions"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAudioSocketServer(
        this IServiceCollection services,
        Action<AudioSocketOptions>? configure = null)
    {
        var options = new AudioSocketOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<AudioSocketServer>();
        services.AddHostedService<AudioSocketServer>(sp => sp.GetRequiredService<AudioSocketServer>());
        return services;
    }
}
