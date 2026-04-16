using Asterisk.Sdk.VoiceAi.Stt.Deepgram;
using Asterisk.Sdk.VoiceAi.Stt.Diagnostics;
using Asterisk.Sdk.VoiceAi.Stt.Google;
using Asterisk.Sdk.VoiceAi.Stt.Whisper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asterisk.Sdk.VoiceAi.Stt.DependencyInjection;

/// <summary>Extension methods for registering STT providers in the DI container.</summary>
public static class SttServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Deepgram WebSocket streaming STT provider as the
    /// <see cref="SpeechRecognizer"/> singleton.
    /// </summary>
    public static IServiceCollection AddDeepgramSpeechRecognizer(
        this IServiceCollection services,
        Action<DeepgramOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);

        services.TryAddSingleton<SpeechRecognizer, DeepgramSpeechRecognizer>();
        services.AddHealthChecks().AddCheck<SttHealthCheck>("stt");
        return services;
    }

    /// <summary>
    /// Registers the OpenAI Whisper REST STT provider as the
    /// <see cref="SpeechRecognizer"/> singleton.
    /// </summary>
    public static IServiceCollection AddWhisperSpeechRecognizer(
        this IServiceCollection services,
        Action<WhisperOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);

        services.AddHttpClient<WhisperSpeechRecognizer>();
        services.TryAddSingleton<SpeechRecognizer>(sp => sp.GetRequiredService<WhisperSpeechRecognizer>());
        services.AddHealthChecks().AddCheck<SttHealthCheck>("stt");
        return services;
    }

    /// <summary>
    /// Registers the Azure OpenAI Whisper REST STT provider as the
    /// <see cref="SpeechRecognizer"/> singleton.
    /// </summary>
    public static IServiceCollection AddAzureWhisperSpeechRecognizer(
        this IServiceCollection services,
        Action<AzureWhisperOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);

        services.AddHttpClient<AzureWhisperSpeechRecognizer>();
        services.TryAddSingleton<SpeechRecognizer>(sp => sp.GetRequiredService<AzureWhisperSpeechRecognizer>());
        services.AddHealthChecks().AddCheck<SttHealthCheck>("stt");
        return services;
    }

    /// <summary>
    /// Registers the Google Cloud Speech-to-Text REST STT provider as the
    /// <see cref="SpeechRecognizer"/> singleton.
    /// </summary>
    public static IServiceCollection AddGoogleSpeechRecognizer(
        this IServiceCollection services,
        Action<GoogleSpeechOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);

        services.AddHttpClient<GoogleSpeechRecognizer>();
        services.TryAddSingleton<SpeechRecognizer>(sp => sp.GetRequiredService<GoogleSpeechRecognizer>());
        services.AddHealthChecks().AddCheck<SttHealthCheck>("stt");
        return services;
    }
}
