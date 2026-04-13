using System.Diagnostics;

namespace Asterisk.Sdk.Live.Diagnostics;

/// <summary>
/// OpenTelemetry-compatible ActivitySource for distributed tracing of Live state operations.
/// Produces spans for initial state loading and originate requests.
/// <para>
/// To enable tracing, register the source name with your OpenTelemetry tracer:
/// <c>builder.AddSource("Asterisk.Sdk.Live")</c>
/// </para>
/// </summary>
public static class LiveActivitySource
{
    public static readonly ActivitySource Source = new("Asterisk.Sdk.Live", "1.0.0");

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

    internal static Activity? StartOriginate(string channel, string context, string extension)
    {
        var activity = Source.StartActivity($"live originate {channel}", ActivityKind.Client);
        if (activity is not null)
        {
            activity.SetTag("originate.channel", channel);
            activity.SetTag("originate.context", context);
            activity.SetTag("originate.extension", extension);
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
