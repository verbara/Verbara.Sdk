using System.Diagnostics;

namespace Asterisk.Sdk.Agi.Diagnostics;

/// <summary>
/// OpenTelemetry-compatible ActivitySource for distributed tracing of AGI operations.
/// Produces spans for AGI script execution lifecycle.
/// <para>
/// To enable tracing, register the source name with your OpenTelemetry tracer:
/// <c>builder.AddSource("Asterisk.Sdk.Agi")</c>
/// </para>
/// </summary>
public static class AgiActivitySource
{
    public static readonly ActivitySource Source = new("Asterisk.Sdk.Agi", "1.0.0");

    internal static Activity? StartScript(string? scriptName, string? channel)
    {
        var activity = Source.StartActivity($"agi {scriptName ?? "unknown"}", ActivityKind.Server);
        if (activity is not null)
        {
            activity.SetTag("agi.script", scriptName);
            activity.SetTag("agi.channel", channel);
        }

        return activity;
    }

    internal static void SetResult(Activity? activity, AgiScriptResult result, string? error = null)
    {
        if (activity is null) return;

        activity.SetTag("agi.result", result.ToString());

        if (result == AgiScriptResult.Failed)
            activity.SetStatus(ActivityStatusCode.Error, error);
        else
            activity.SetStatus(ActivityStatusCode.Ok);
    }
}

internal enum AgiScriptResult
{
    Completed,
    Hangup,
    NotFound,
    Failed,
    Timeout
}
