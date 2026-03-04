using System.Diagnostics.Metrics;

namespace Asterisk.Sdk.Ari.Diagnostics;

/// <summary>
/// ARI connection metrics exposed via System.Diagnostics.Metrics.
/// Compatible with OpenTelemetry, Prometheus, dotnet-counters, and any .NET metrics consumer.
/// <para>
/// Usage: <c>dotnet-counters monitor --process-id &lt;pid&gt; Asterisk.Sdk.Ari</c>
/// </para>
/// </summary>
public static class AriMetrics
{
    public static readonly Meter Meter = new("Asterisk.Sdk.Ari", "1.0.0");

    // --- Counters ---

    /// <summary>Total ARI events received from Asterisk WebSocket.</summary>
    public static readonly Counter<long> EventsReceived =
        Meter.CreateCounter<long>("ari.events.received", "events",
            "Total ARI events received from Asterisk WebSocket");

    /// <summary>ARI events dropped due to full event pump buffer.</summary>
    public static readonly Counter<long> EventsDropped =
        Meter.CreateCounter<long>("ari.events.dropped", "events",
            "ARI events dropped due to full event pump");

    /// <summary>ARI events dispatched to observers.</summary>
    public static readonly Counter<long> EventsDispatched =
        Meter.CreateCounter<long>("ari.events.dispatched", "events",
            "ARI events dispatched to observers");

    /// <summary>Total ARI REST requests sent to Asterisk.</summary>
    public static readonly Counter<long> RestRequestsSent =
        Meter.CreateCounter<long>("ari.rest.requests.sent", "requests",
            "Total ARI REST requests sent to Asterisk");

    /// <summary>ARI WebSocket reconnection attempts.</summary>
    public static readonly Counter<long> Reconnections =
        Meter.CreateCounter<long>("ari.reconnections", "attempts",
            "ARI WebSocket reconnection attempts");

    // --- Histograms ---

    /// <summary>Roundtrip time for ARI REST requests.</summary>
    public static readonly Histogram<double> RestRoundtripMs =
        Meter.CreateHistogram<double>("ari.rest.roundtrip", "ms",
            "Roundtrip time for ARI REST requests");

    /// <summary>Time to dispatch an event to all observers.</summary>
    public static readonly Histogram<double> EventDispatchMs =
        Meter.CreateHistogram<double>("ari.event.dispatch", "ms",
            "Time to dispatch an event to all observers");
}
