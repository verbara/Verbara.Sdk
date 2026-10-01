using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Verbara.Sdk.Live.Diagnostics;

/// <summary>
/// Health check for the Live state tracking layer. It reads the state of the AMI connection the server runs on
/// before it looks at the live collections, because a table nothing updates is not healthy:
/// <list type="bullet">
///   <item><description><see cref="AmiConnectionState.Connected"/>: <see cref="HealthStatus.Healthy"/> when a
///   live collection is non-empty, <see cref="HealthStatus.Degraded"/> when every collection is empty.</description></item>
///   <item><description><see cref="AmiConnectionState.Reconnecting"/>, <see cref="AmiConnectionState.Connecting"/>
///   and <see cref="AmiConnectionState.Initial"/>: <see cref="HealthStatus.Degraded"/> — the live state is not being
///   updated, but the connection may still recover on its own.</description></item>
///   <item><description><see cref="AmiConnectionState.Disconnecting"/> and
///   <see cref="AmiConnectionState.Disconnected"/>: <see cref="HealthStatus.Unhealthy"/> — nothing updates the
///   live state until a new connect.</description></item>
/// </list>
/// The result's data carries <c>amiState</c> (the state's name, a string) and the <c>channels</c>,
/// <c>queues</c> and <c>agents</c> counts.
/// </summary>
public sealed class LiveHealthCheck(VerbaraServer server) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var amiState = server.Connection.State;
        var amiStateName = amiState.ToString();
        var channels = server.Channels.ChannelCount;
        var queues = server.Queues.QueueCount;
        var agents = server.Agents.AgentCount;

        var data = new Dictionary<string, object>
        {
            // A string, never the boxed enum: a reflection-free JSON writer cannot serialize an arbitrary enum.
            ["amiState"] = amiStateName,
            ["channels"] = channels,
            ["queues"] = queues,
            ["agents"] = agents,
        };

        return Task.FromResult(amiState switch
        {
            AmiConnectionState.Connected => channels > 0 || queues > 0 || agents > 0
                ? HealthCheckResult.Healthy("Live state loaded", data)
                : HealthCheckResult.Degraded("Live state empty — server may not have loaded yet", data: data),
            AmiConnectionState.Disconnecting or AmiConnectionState.Disconnected =>
                HealthCheckResult.Unhealthy("Live state is not being updated: AMI " + amiStateName, data: data),
            _ => HealthCheckResult.Degraded("Live state is not being updated: AMI " + amiStateName, data: data),
        });
    }
}
