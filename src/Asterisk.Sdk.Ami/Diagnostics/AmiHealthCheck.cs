using Asterisk.Sdk;
using Asterisk.Sdk.Enums;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Asterisk.Sdk.Ami.Diagnostics;

/// <summary>
/// Health check for AMI connection state.
/// Reports Healthy when connected, Degraded when reconnecting, Unhealthy otherwise.
/// </summary>
public sealed class AmiHealthCheck(IAmiConnection connection) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(connection.State switch
        {
            AmiConnectionState.Connected => HealthCheckResult.Healthy("AMI connected"),
            AmiConnectionState.Reconnecting => HealthCheckResult.Degraded("AMI reconnecting"),
            _ => HealthCheckResult.Unhealthy($"AMI state: {connection.State}")
        });
    }
}
