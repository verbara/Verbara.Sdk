using System.Diagnostics;

namespace Verbara.Sdk.Push.Diagnostics;

/// <summary>
/// OpenTelemetry-compatible ActivitySource for distributed tracing of push operations.
/// Produces spans for event publish and fan-out delivery.
/// <para>
/// To enable tracing, register the source name with your OpenTelemetry tracer:
/// <c>builder.AddSource("Verbara.Sdk.Push")</c>
/// </para>
/// </summary>
public static class PushActivitySource
{
    public static readonly ActivitySource Source = new("Verbara.Sdk.Push", "1.0.0");

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

    /// <summary>
    /// Starts the <c>push deliver</c> span. When <paramref name="traceContext"/> parses as a W3C
    /// <c>traceparent</c> the span is its child, so the delivery and every span a subscriber starts
    /// inside it continue the publisher's trace; otherwise the span is a root.
    /// </summary>
    internal static Activity? StartDelivery(string eventType, int subscriberCount, string? traceContext = null)
    {
        // Parse only when someone listens to the source: the no-listener path stays allocation-free.
        if (!Source.HasListeners()) return null;

        var activity = traceContext is not null && ActivityContext.TryParse(traceContext, null, out var parent)
            ? Source.StartActivity($"push deliver {eventType}", ActivityKind.Internal, parent)
            : Source.StartActivity($"push deliver {eventType}", ActivityKind.Internal);
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
