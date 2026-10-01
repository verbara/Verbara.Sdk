using System.Globalization;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Verbara.Sdk.Live.Diagnostics;

/// <summary>
/// Health check over a <see cref="VerbaraServerPool"/>. Each time it runs it reads the state of the AMI connection of
/// every server the pool holds, and aggregates them the way <see cref="LiveHealthCheck"/> reads a single connection:
/// <list type="bullet">
///   <item><description>every server <see cref="AmiConnectionState.Connected"/>, or a pool that holds no servers yet:
///   <see cref="HealthStatus.Healthy"/>.</description></item>
///   <item><description>every server <see cref="AmiConnectionState.Disconnecting"/> or
///   <see cref="AmiConnectionState.Disconnected"/>: <see cref="HealthStatus.Unhealthy"/> — nothing reconnects them
///   on its own.</description></item>
///   <item><description>anything else — at least one server not connected, and at least one connected or still
///   recovering (<see cref="AmiConnectionState.Initial"/>, <see cref="AmiConnectionState.Connecting"/>,
///   <see cref="AmiConnectionState.Reconnecting"/>): <see cref="HealthStatus.Degraded"/>. A pool whose only server is
///   reconnecting is therefore <see cref="HealthStatus.Degraded"/>, not <see cref="HealthStatus.Unhealthy"/>.</description></item>
/// </list>
/// The result's data holds one entry per server id, whose value is the name of that server's AMI connection state
/// (a string).
/// </summary>
/// <remarks>
/// <c>AddVerbaraMultiServer</c> registers this check once, under the name <c>verbara-pool</c> and with no tags.
/// </remarks>
public sealed class VerbaraServerPoolHealthCheck(VerbaraServerPool pool) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        var total = 0;
        var connected = 0;
        var ended = 0;

        foreach (var (serverId, server) in pool.Servers)
        {
            var state = server.Connection.State;
            // A string, never the boxed enum: a reflection-free JSON writer cannot serialize an arbitrary enum.
            data[serverId] = state.ToString();
            total++;
            if (state == AmiConnectionState.Connected)
                connected++;
            else if (state is AmiConnectionState.Disconnecting or AmiConnectionState.Disconnected)
                ended++;
        }

        HealthCheckResult result;
        if (total == 0)
        {
            result = HealthCheckResult.Healthy("The pool holds no servers", data);
        }
        else if (connected == total)
        {
            result = HealthCheckResult.Healthy(
                string.Create(CultureInfo.InvariantCulture, $"All {total} servers connected"), data);
        }
        else if (ended == total)
        {
            result = HealthCheckResult.Unhealthy(
                string.Create(CultureInfo.InvariantCulture, $"No server connected: all {total} AMI connections have ended"),
                data: data);
        }
        else
        {
            result = HealthCheckResult.Degraded(
                string.Create(CultureInfo.InvariantCulture, $"{total - connected} of {total} servers not connected"),
                data: data);
        }

        return Task.FromResult(result);
    }
}
