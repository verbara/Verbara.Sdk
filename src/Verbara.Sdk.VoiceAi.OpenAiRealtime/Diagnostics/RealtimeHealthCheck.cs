using System.Globalization;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.VoiceAi.OpenAiRealtime.Diagnostics;

/// <summary>
/// Health check for the OpenAI Realtime bridge: whether it is configured, and whether the vendor is serving its
/// sessions.
/// </summary>
/// <remarks>
/// <para>
/// Reports <see cref="HealthStatus.Unhealthy"/> when no API key is configured.
/// </para>
/// <para>
/// Registered by <c>AddOpenAiRealtimeBridge</c> (as <c>openai-realtime</c>), it also reads what the vendor did with
/// the bridge's last sessions. It reports <see cref="HealthStatus.Degraded"/> once the last 3 consecutive sessions
/// were rejected — refused at the upgrade with a 4xx status, or closed by the vendor with a failure code before any of
/// its audio reached the caller — and <see cref="HealthStatus.Healthy"/> again on the first session the vendor
/// served. Rejections never make it <see cref="HealthStatus.Unhealthy"/>: <c>Degraded</c> keeps a default health
/// endpoint answering 200, so a vendor that rejects sessions does not take the host's readiness down. A session the
/// host cancelled, or one that ended on a transport failure, counts neither way. There is no time window: an entry
/// that reads <c>Degraded</c> with no session since says when the last rejection happened.
/// </para>
/// <para>
/// The result's data carries <c>consecutiveRejections</c> (a number) and, once a rejection has happened,
/// <c>lastRejection</c> (the vendor's close code or the upgrade's HTTP status, a number) and <c>lastRejectionAt</c>
/// (when it happened, an ISO 8601 round-trip string).
/// </para>
/// <para>
/// Built by hand with the public constructor, the check reads the configuration only, as it always did: it reports
/// <c>Healthy</c> whenever a key is configured.
/// </para>
/// </remarks>
public sealed class RealtimeHealthCheck : IHealthCheck
{
    private readonly IOptions<OpenAiRealtimeOptions> _options;
    private readonly RealtimeRejectionTracker? _rejections;

    /// <summary>Creates a check that reads the configuration only.</summary>
    /// <param name="options">The bridge's options.</param>
    public RealtimeHealthCheck(IOptions<OpenAiRealtimeOptions> options)
        : this(options, rejections: null)
    {
    }

    /// <summary>Creates a check that also reads what the vendor did with a bridge's sessions.</summary>
    internal RealtimeHealthCheck(IOptions<OpenAiRealtimeOptions> options, RealtimeRejectionTracker? rejections)
    {
        _options = options;
        _rejections = rejections;
    }

    /// <inheritdoc/>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        if (string.IsNullOrEmpty(options.ApiKey))
            return Task.FromResult(HealthCheckResult.Unhealthy("OpenAI API key not configured"));

        var description = $"Model: {options.Model}";
        if (_rejections is null)
            return Task.FromResult(HealthCheckResult.Healthy(description));

        var (consecutive, lastRejection, lastRejectionAt) = _rejections.Read();
        var data = new Dictionary<string, object> { ["consecutiveRejections"] = consecutive };
        if (lastRejection is { } code)
            data["lastRejection"] = code;
        if (lastRejectionAt is { } at)
            data["lastRejectionAt"] = at.ToString("O", CultureInfo.InvariantCulture);

        return Task.FromResult(consecutive >= RealtimeRejectionTracker.DegradedAfter
            ? HealthCheckResult.Degraded(
                $"OpenAI rejected the last {consecutive.ToString(CultureInfo.InvariantCulture)} sessions. {description}",
                data: data)
            : HealthCheckResult.Healthy(description, data));
    }
}
