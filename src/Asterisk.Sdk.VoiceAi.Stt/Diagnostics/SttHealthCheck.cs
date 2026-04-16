using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Asterisk.Sdk.VoiceAi.Stt.Diagnostics;

/// <summary>
/// Health check that reports whether an STT provider is configured.
/// </summary>
public sealed class SttHealthCheck(SpeechRecognizer? recognizer) : IHealthCheck
{
    /// <inheritdoc/>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default) =>
        recognizer is not null
            ? Task.FromResult(HealthCheckResult.Healthy($"STT provider: {recognizer.GetType().Name}"))
            : Task.FromResult(HealthCheckResult.Unhealthy("No STT provider registered"));
}
