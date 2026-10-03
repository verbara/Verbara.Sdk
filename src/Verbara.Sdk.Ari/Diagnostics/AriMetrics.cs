using System.Diagnostics.Metrics;

namespace Verbara.Sdk.Ari.Diagnostics;

/// <summary>
/// ARI connection metrics exposed via System.Diagnostics.Metrics.
/// Compatible with OpenTelemetry, Prometheus, dotnet-counters, and any .NET metrics consumer.
/// <para>
/// Usage: <c>dotnet-counters monitor --process-id &lt;pid&gt; Verbara.Sdk.Ari</c>
/// </para>
/// </summary>
public static class AriMetrics
{
    public static readonly Meter Meter = new("Verbara.Sdk.Ari", "1.0.0");

    // --- Counters ---

    /// <summary>Total ARI events received from Asterisk WebSocket.</summary>
    public static readonly Counter<long> EventsReceived =
        Meter.CreateCounter<long>("ari.events.received", "events",
            "Total ARI events received from Asterisk WebSocket");

    /// <summary>
    /// ARI events dropped by a full event buffer or discarded by a caller's ending, tagged <c>reason</c>:
    /// <c>buffer_full</c> for the oldest buffered event a full buffer discarded to make room for a new one (one
    /// measurement per event), <c>caller_ending</c> for the events still buffered when the caller's
    /// <c>DisconnectAsync</c> or <c>DisposeAsync</c> ended the connection (one measurement per ending, with the count).
    /// An alert on capacity filters on <c>reason=buffer_full</c>.
    /// </summary>
    public static readonly Counter<long> EventsDropped =
        Meter.CreateCounter<long>("ari.events.dropped", "events",
            "ARI events dropped by a full event buffer (reason=buffer_full) or discarded by a caller's ending (reason=caller_ending)");

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
