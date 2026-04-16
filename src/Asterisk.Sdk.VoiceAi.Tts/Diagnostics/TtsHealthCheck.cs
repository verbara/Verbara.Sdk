using Asterisk.Sdk.VoiceAi;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Asterisk.Sdk.VoiceAi.Tts.Diagnostics;

/// <summary>
/// Health check that reports whether a TTS provider is configured.
/// </summary>
public sealed class TtsHealthCheck(SpeechSynthesizer? synthesizer) : IHealthCheck
{
    /// <inheritdoc/>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        synthesizer is not null
            ? Task.FromResult(HealthCheckResult.Healthy($"TTS provider: {synthesizer.GetType().Name}"))
            : Task.FromResult(HealthCheckResult.Unhealthy("No TTS provider registered"));
}
