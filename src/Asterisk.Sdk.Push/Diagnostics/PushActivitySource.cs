using System.Diagnostics;

namespace Asterisk.Sdk.Push.Diagnostics;

/// <summary>
/// OpenTelemetry-compatible ActivitySource for distributed tracing of push operations.
/// Produces spans for event publish and fan-out delivery.
/// <para>
/// To enable tracing, register the source name with your OpenTelemetry tracer:
/// <c>builder.AddSource("Asterisk.Sdk.Push")</c>
/// </para>
/// </summary>
public static class PushActivitySource
{
    public static readonly ActivitySource Source = new("Asterisk.Sdk.Push", "1.0.0");

    internal static Activity? StartPublish(string eventType)
    {
        var activity = Source.StartActivity($"push publish {eventType}", ActivityKind.Producer);
        if (activity is not null)
        {
            activity.SetTag("push.event_type", eventType);
        }

        return activity;
    }

    internal static void SetPublished(Activity? activity)
    {
        if (activity is null) return;
        activity.SetStatus(ActivityStatusCode.Ok);
    }

    internal static Activity? StartDelivery(string eventType, int subscriberCount)
    {
        var activity = Source.StartActivity($"push deliver {eventType}", ActivityKind.Internal);
        if (activity is not null)
        {
            activity.SetTag("push.event_type", eventType);
            activity.SetTag("push.subscriber_count", subscriberCount);
        }

        return activity;
    }

    internal static void SetDeliveryResult(Activity? activity, int delivered, int dropped)
    {
        if (activity is null) return;

        activity.SetTag("push.delivered", delivered);
        activity.SetTag("push.dropped", dropped);

        if (dropped > 0)
            activity.SetStatus(ActivityStatusCode.Error, $"{dropped} subscriber(s) failed");
        else
            activity.SetStatus(ActivityStatusCode.Ok);
    }
}
