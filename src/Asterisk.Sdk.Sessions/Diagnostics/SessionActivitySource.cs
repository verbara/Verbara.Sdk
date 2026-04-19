using System.Diagnostics;

namespace Asterisk.Sdk.Sessions.Diagnostics;

/// <summary>
/// OpenTelemetry-compatible ActivitySource for distributed tracing of session operations.
/// Produces spans for session completion and reconciliation sweeps.
/// <para>
/// To enable tracing, register the source name with your OpenTelemetry tracer:
/// <c>builder.AddSource("Asterisk.Sdk.Sessions")</c>
/// </para>
/// </summary>
public static class SessionActivitySource
{
    public static readonly ActivitySource Source = new("Asterisk.Sdk.Sessions", "1.0.0");

    internal static Activity? StartSessionCompleted(
        string sessionId, CallDirection direction, CallSessionState finalState, TimeSpan duration)
    {
        var activity = Source.StartActivity($"session completed {sessionId}", ActivityKind.Internal);
        if (activity is not null)
        {
            activity.SetTag("session.id", sessionId);
            activity.SetTag("call.direction", direction.ToString().ToLowerInvariant());
            activity.SetTag("call.state", finalState.ToString().ToLowerInvariant());
            activity.SetTag("call.duration_ms", duration.TotalMilliseconds);

            if (finalState is CallSessionState.Failed or CallSessionState.TimedOut)
                activity.SetStatus(ActivityStatusCode.Error, finalState.ToString());
            else
                activity.SetStatus(ActivityStatusCode.Ok);
        }

        return activity;
    }

    internal static Activity? StartReconciliation()
    {
        var activity = Source.StartActivity("session reconciliation", ActivityKind.Internal);
        activity?.SetStatus(ActivityStatusCode.Ok);
        return activity;
    }
}
