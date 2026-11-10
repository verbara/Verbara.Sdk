using System.Diagnostics;

namespace Verbara.Sdk.Live.Diagnostics;

/// <summary>
/// OpenTelemetry-compatible ActivitySource for distributed tracing of Live state operations.
/// Produces spans for state loads, channel reconciliations and originate requests.
/// <para>
/// To enable tracing, register the source name with your OpenTelemetry tracer:
/// <c>builder.AddSource("Verbara.Sdk.Live")</c>
/// </para>
/// </summary>
public static class LiveActivitySource
{
    public static readonly ActivitySource Source = new("Verbara.Sdk.Live", "1.0.0");

    internal static Activity? StartStateLoad(string serverIdentifier)
    {
        var activity = Source.StartActivity("live state-load", ActivityKind.Client);
        if (activity is not null)
        {
            activity.SetTag("live.server", serverIdentifier);
        }

        return activity;
    }

    internal static void SetStateLoadResult(Activity? activity, int channels, int queues, int agents)
    {
        if (activity is null) return;

        activity.SetTag("live.channels", channels);
        activity.SetTag("live.queues", queues);
        activity.SetTag("live.agents", agents);
        activity.SetStatus(ActivityStatusCode.Ok);
    }

    internal static Activity? StartChannelReconcile(string serverIdentifier)
    {
        var activity = Source.StartActivity("live channel-reconcile", ActivityKind.Client);
        activity?.SetTag("live.server", serverIdentifier);
        return activity;
    }

    internal static void SetChannelReconcileResult(Activity? activity, int channels)
    {
        if (activity is null) return;

        activity.SetTag("live.channels", channels);
        activity.SetStatus(ActivityStatusCode.Ok);
    }

    /// <summary>
    /// Tags a state load or a channel reconciliation whose <c>Status</c> Asterisk refused with Asterisk's message, so a
    /// trace shows why it left the channel table as it was.
    /// </summary>
    internal static void SetStatusRefused(Activity? activity, string message) =>
        activity?.SetTag("live.status.refused", message);

    /// <summary>
    /// Tags a state load or a channel reconciliation one of whose lists applied only its non-destructive half, because
    /// the event pump had not dispatched every event received before the list completed within the bound: the list's
    /// action name.
    /// </summary>
    internal static void SetReconcileDeferred(Activity? activity, string list) =>
        activity?.SetTag("live.reconcile.deferred", list);

    /// <summary>Tags a channel reconciliation with whether it covered every event received before its <c>Status</c>.</summary>
    internal static void SetReconcileCovered(Activity? activity, bool covered) =>
        activity?.SetTag("live.reconcile.covered", covered);

    /// <summary>
    /// Tags a channel reconciliation that applied its snapshot: whether it covered, and why not where a reason is its own —
    /// the channels it kept although their creation event was received before its <c>Status</c>, and another channel read
    /// that overlapped it.
    /// </summary>
    internal static void SetReconcileCoverage(Activity? activity, bool covered, int keptStale, bool overlapped)
    {
        if (activity is null)
            return;

        activity.SetTag("live.reconcile.covered", covered);
        if (keptStale > 0)
            activity.SetTag("live.reconcile.kept_stale", keptStale);
        if (overlapped)
            activity.SetTag("live.reconcile.overlapped", true);
    }

    internal static Activity? StartOriginate(string channel, string context, string extension)
    {
        var activity = Source.StartActivity($"live originate {channel}", ActivityKind.Client);
        if (activity is not null)
        {
            activity.SetTag("asterisk.channel.name", channel);
            activity.SetTag("dialplan.context", context);
            activity.SetTag("dialplan.extension", extension);
        }

        return activity;
    }

    internal static void SetOriginateResult(Activity? activity, bool success, string? message)
    {
        if (activity is null) return;

        activity.SetTag("originate.result", success ? "success" : "failure");

        if (success)
            activity.SetStatus(ActivityStatusCode.Ok);
        else
            activity.SetStatus(ActivityStatusCode.Error, message);
    }
}
