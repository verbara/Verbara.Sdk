using Asterisk.Sdk.Live.Server;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Asterisk.Sdk.Live.Diagnostics;

/// <summary>
/// Health check for Live state tracking layer.
/// Reports Healthy when state is loaded, Degraded when collections are empty.
/// </summary>
public sealed class LiveHealthCheck(AsteriskServer server) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var channels = server.Channels.ChannelCount;
        var queues = server.Queues.QueueCount;
        var agents = server.Agents.AgentCount;

        var data = new Dictionary<string, object>
        {
            ["channels"] = channels,
            ["queues"] = queues,
            ["agents"] = agents,
        };

        var hasState = channels > 0 || queues > 0 || agents > 0;

        return Task.FromResult(hasState
            ? HealthCheckResult.Healthy("Live state loaded", data)
            : HealthCheckResult.Degraded("Live state empty — server may not have loaded yet", data: data));
    }
}
