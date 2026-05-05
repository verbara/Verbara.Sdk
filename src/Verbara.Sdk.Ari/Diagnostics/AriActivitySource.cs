using System.Diagnostics;

namespace Verbara.Sdk.Ari.Diagnostics;

/// <summary>
/// OpenTelemetry-compatible ActivitySource for distributed tracing of ARI operations.
/// Produces spans for ARI REST API requests.
/// <para>
/// To enable tracing, register the source name with your OpenTelemetry tracer:
/// <c>builder.AddSource("Verbara.Sdk.Ari")</c>
/// </para>
/// </summary>
public static class AriActivitySource
{
    public static readonly ActivitySource Source = new("Verbara.Sdk.Ari", "1.0.0");

    internal static Activity? StartRequest(string method, string? url)
    {
        var activity = Source.StartActivity($"ari {method} {url}", ActivityKind.Client);
        if (activity is not null)
        {
            activity.SetTag("http.request.method", method);
            activity.SetTag("url.path", url);
        }

        return activity;
    }

    internal static void SetResponse(Activity? activity, int statusCode)
    {
        if (activity is null) return;

        activity.SetTag("http.response.status_code", statusCode);

        if (statusCode >= 400)
            activity.SetStatus(ActivityStatusCode.Error, $"HTTP {statusCode}");
        else
            activity.SetStatus(ActivityStatusCode.Ok);
    }
}
