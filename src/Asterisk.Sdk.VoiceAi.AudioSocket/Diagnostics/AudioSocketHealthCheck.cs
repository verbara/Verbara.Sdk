using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Asterisk.Sdk.VoiceAi.AudioSocket.Diagnostics;

/// <summary>
/// Health check for AudioSocket server state.
/// Reports Healthy when listening, Unhealthy when not started.
/// </summary>
public sealed class AudioSocketHealthCheck(AudioSocketServer server) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(server.BoundPort > 0
            ? HealthCheckResult.Healthy($"Listening on :{server.BoundPort}, {server.ActiveSessionCount} active sessions")
            : HealthCheckResult.Unhealthy("AudioSocket server not started"));
    }
}
