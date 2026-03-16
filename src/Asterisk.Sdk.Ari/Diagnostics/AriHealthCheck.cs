using Asterisk.Sdk.Enums;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Asterisk.Sdk.Ari.Diagnostics;

/// <summary>
/// Health check for ARI client connection state.
/// Reports Healthy when connected, Degraded when reconnecting, Unhealthy otherwise.
/// </summary>
public sealed class AriHealthCheck(IAriClient client) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(client.State switch
        {
            AriConnectionState.Connected => HealthCheckResult.Healthy("ARI connected"),
            AriConnectionState.Reconnecting => HealthCheckResult.Degraded("ARI reconnecting"),
            _ => HealthCheckResult.Unhealthy($"ARI state: {client.State}")
        });
    }
}
