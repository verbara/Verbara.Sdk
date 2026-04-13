using Asterisk.Sdk.Sessions.Manager;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Asterisk.Sdk.Sessions.Diagnostics;

/// <summary>
/// Health check for the session engine.
/// Always Healthy — sessions are in-memory with no external dependency.
/// Reports active and recent completed session counts.
/// </summary>
public sealed class SessionHealthCheck(ICallSessionManager manager) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var active = manager.ActiveSessions.Count();
        var recentCompleted = manager.GetRecentCompleted(100).Count();

        var data = new Dictionary<string, object>
        {
            ["activeSessions"] = active,
            ["recentCompleted"] = recentCompleted,
        };

        return Task.FromResult(HealthCheckResult.Healthy("Session engine running", data));
    }
}
