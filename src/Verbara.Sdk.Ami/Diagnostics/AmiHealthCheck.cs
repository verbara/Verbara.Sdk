using Verbara.Sdk;
using Verbara.Sdk.Enums;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Verbara.Sdk.Ami.Diagnostics;

/// <summary>
/// Health check for the AMI connection's state. It reads the state with the same table as the <c>live</c> check
/// (<c>LiveHealthCheck</c>), so the two never disagree on a state:
/// <list type="bullet">
///   <item><description><see cref="AmiConnectionState.Connected"/>: <see cref="HealthStatus.Healthy"/>.</description></item>
///   <item><description><see cref="AmiConnectionState.Reconnecting"/> and <see cref="AmiConnectionState.Connecting"/>:
///   <see cref="HealthStatus.Degraded"/> — not connected, but the connection may still recover on its own.</description></item>
///   <item><description><see cref="AmiConnectionState.Initial"/>: <see cref="HealthStatus.Degraded"/> — nobody has
///   connected it yet, and it does not connect on its own.</description></item>
///   <item><description><see cref="AmiConnectionState.Disconnecting"/> and
///   <see cref="AmiConnectionState.Disconnected"/>: <see cref="HealthStatus.Unhealthy"/> — nothing reconnects it until a
///   new connect.</description></item>
/// </list>
/// The result's data carries <c>amiState</c>, the state's name as a string.
/// </summary>
public sealed class AmiHealthCheck(IAmiConnection connection) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var state = connection.State;
        var stateName = state.ToString();
        var data = new Dictionary<string, object>
        {
            // A string, never the boxed enum: a reflection-free JSON writer cannot serialize an arbitrary enum.
            ["amiState"] = stateName,
        };

        return Task.FromResult(state switch
        {
            AmiConnectionState.Connected => HealthCheckResult.Healthy("AMI connected", data),
            AmiConnectionState.Disconnecting or AmiConnectionState.Disconnected =>
                HealthCheckResult.Unhealthy("AMI " + stateName + ": nothing reconnects it until a new connect", data: data),
            AmiConnectionState.Initial => HealthCheckResult.Degraded("AMI not connected yet", data: data),
            _ => HealthCheckResult.Degraded("AMI " + stateName + ": the connection may still recover on its own", data: data),
        });
    }
}
