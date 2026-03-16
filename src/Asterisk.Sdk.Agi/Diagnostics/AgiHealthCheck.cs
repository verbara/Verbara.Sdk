using Asterisk.Sdk.Enums;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Asterisk.Sdk.Agi.Diagnostics;

/// <summary>
/// Health check for AGI server state.
/// Reports Healthy when listening, Degraded when starting, Unhealthy otherwise.
/// </summary>
public sealed class AgiHealthCheck(IAgiServer server) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(server.State switch
        {
            AgiServerState.Listening => HealthCheckResult.Healthy("AGI server listening"),
            AgiServerState.Starting => HealthCheckResult.Degraded("AGI server starting"),
            _ => HealthCheckResult.Unhealthy($"AGI state: {server.State}")
        });
    }
}
