using Asterisk.Sdk.VoiceAi.Tts.Azure;
using Asterisk.Sdk.VoiceAi.Tts.Cartesia;
using Asterisk.Sdk.VoiceAi.Tts.Diagnostics;
using Asterisk.Sdk.VoiceAi.Tts.ElevenLabs;
using Asterisk.Sdk.VoiceAi.Tts.Speechmatics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.VoiceAi.Tts.DependencyInjection;

/// <summary>Extension methods for registering TTS providers in the DI container.</summary>
public static class TtsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the ElevenLabs WebSocket streaming TTS provider as the
    /// <see cref="SpeechSynthesizer"/> singleton.
    /// </summary>
    public static IServiceCollection AddElevenLabsSpeechSynthesizer(
        this IServiceCollection services,
        Action<ElevenLabsOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);

        services.TryAddSingleton<SpeechSynthesizer, ElevenLabsSpeechSynthesizer>();
        services.AddHealthChecks().AddCheck<TtsHealthCheck>("tts");
        return services;
    }

    /// <summary>
    /// Registers the Azure Cognitive Services REST TTS provider as the
    /// <see cref="SpeechSynthesizer"/> singleton.
    /// </summary>
    public static IServiceCollection AddAzureTtsSpeechSynthesizer(
        this IServiceCollection services,
        Action<AzureTtsOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);

        services.AddHttpClient<AzureTtsSpeechSynthesizer>();
        services.TryAddSingleton<SpeechSynthesizer>(sp => sp.GetRequiredService<AzureTtsSpeechSynthesizer>());
        services.AddHealthChecks().AddCheck<TtsHealthCheck>("tts");
        return services;
    }

    /// <summary>
    /// Registers the Cartesia Sonic-3 WebSocket streaming TTS provider as the
    /// <see cref="SpeechSynthesizer"/> singleton.
    /// </summary>
    public static IServiceCollection AddCartesiaSpeechSynthesizer(
        this IServiceCollection services,
        Action<CartesiaOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);
        else
            services.AddOptions<CartesiaOptions>();

        services.AddSingleton<IValidateOptions<CartesiaOptions>, CartesiaOptionsValidator>();
        services.TryAddSingleton<SpeechSynthesizer, CartesiaSpeechSynthesizer>();
        services.AddHealthChecks().AddCheck<TtsHealthCheck>("tts");
        return services;
    }

    /// <summary>
    /// Registers the Speechmatics REST TTS provider as the
    /// <see cref="SpeechSynthesizer"/> singleton.
    /// </summary>
    public static IServiceCollection AddSpeechmaticsTts(
        this IServiceCollection services,
        Action<SpeechmaticsOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);
        else
            services.AddOptions<SpeechmaticsOptions>();

        services.AddSingleton<IValidateOptions<SpeechmaticsOptions>, SpeechmaticsOptionsValidator>();
        services.TryAddSingleton<SpeechSynthesizer, SpeechmaticsSpeechSynthesizer>();
        services.AddHealthChecks().AddCheck<TtsHealthCheck>("tts");
        return services;
    }
}
