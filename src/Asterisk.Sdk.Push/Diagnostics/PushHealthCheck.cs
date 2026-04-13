using Asterisk.Sdk.Push.Subscriptions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Asterisk.Sdk.Push.Diagnostics;

/// <summary>
/// Health check for the push event bus.
/// Reports Healthy with active subscriber count.
/// </summary>
public sealed class PushHealthCheck(ISubscriptionRegistry registry) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var subscribers = registry.ActiveCount;

        var data = new Dictionary<string, object>
        {
            ["activeSubscribers"] = subscribers,
        };

        return Task.FromResult(HealthCheckResult.Healthy("Push bus active", data));
    }
}
