using System.Diagnostics;

namespace Asterisk.Sdk.Ami.Diagnostics;

/// <summary>
/// OpenTelemetry-compatible ActivitySource for distributed tracing of AMI operations.
/// Produces spans for action send/response roundtrips and event-generating actions.
/// <para>
/// To enable tracing, register the source name with your OpenTelemetry tracer:
/// <c>builder.AddSource("Asterisk.Sdk.Ami")</c>
/// </para>
/// </summary>
public static class AmiActivitySource
{
    public static readonly ActivitySource Source = new("Asterisk.Sdk.Ami", "1.0.0");

    internal static Activity? StartAction(string actionName, string actionId)
    {
        var activity = Source.StartActivity($"ami {actionName}", ActivityKind.Client);
        if (activity is not null)
        {
            activity.SetTag("ami.action", actionName);
            activity.SetTag("ami.action_id", actionId);
        }

        return activity;
    }

    internal static void SetResponse(Activity? activity, string? response, string? message)
    {
        if (activity is null) return;

        activity.SetTag("ami.response", response);
        if (message is not null)
            activity.SetTag("ami.message", message);

        if (string.Equals(response, "Error", StringComparison.OrdinalIgnoreCase))
            activity.SetStatus(ActivityStatusCode.Error, message);
        else
            activity.SetStatus(ActivityStatusCode.Ok);
    }
}
