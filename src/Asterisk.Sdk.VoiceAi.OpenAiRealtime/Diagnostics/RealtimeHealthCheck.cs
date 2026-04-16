using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.Diagnostics;

/// <summary>
/// Health check that verifies the OpenAI Realtime bridge is properly configured.
/// Reports Healthy when an API key is present, Unhealthy otherwise.
/// </summary>
public sealed class RealtimeHealthCheck(IOptions<OpenAiRealtimeOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(!string.IsNullOrEmpty(options.Value.ApiKey)
            ? HealthCheckResult.Healthy($"Model: {options.Value.Model}")
            : HealthCheckResult.Unhealthy("OpenAI API key not configured"));
    }
}
