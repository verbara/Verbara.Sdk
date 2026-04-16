using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Asterisk.Sdk.VoiceAi.Diagnostics;

/// <summary>
/// Health check for the Voice AI pipeline.
/// Always Healthy — confirms the pipeline is configured and a session handler is registered.
/// </summary>
public sealed class VoiceAiHealthCheck(ISessionHandler handler) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>
        {
            ["handler"] = handler.GetType().Name,
        };

        return Task.FromResult(HealthCheckResult.Healthy("Voice AI pipeline configured", data));
    }
}
