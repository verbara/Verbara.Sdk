using System.Diagnostics.Metrics;

namespace Verbara.Sdk.Ami.Diagnostics;

/// <summary>
/// AMI connection metrics exposed via System.Diagnostics.Metrics.
/// Compatible with OpenTelemetry, Prometheus, dotnet-counters, and any .NET metrics consumer.
/// <para>
/// Usage: <c>dotnet-counters monitor --process-id &lt;pid&gt; Verbara.Sdk.Ami</c>
/// </para>
/// </summary>
public static class AmiMetrics
{
    public static readonly Meter Meter = new("Verbara.Sdk.Ami", "1.0.0");

    // --- Counters ---

    /// <summary>Total AMI events received from Asterisk.</summary>
    public static readonly Counter<long> EventsReceived =
        Meter.CreateCounter<long>("ami.events.received", "events",
            "Total AMI events received from Asterisk");

    /// <summary>
    /// AMI events dropped by a full event pump or discarded by a caller's ending, tagged <c>reason</c>:
    /// <c>buffer_full</c> for an event the full buffer refused (one per event), <c>caller_ending</c> for the events
    /// still buffered when the caller's <c>DisconnectAsync</c> or <c>DisposeAsync</c> ended the connection (one
    /// measurement per ending, with the count). An alert on capacity filters on <c>reason=buffer_full</c>.
    /// </summary>
    public static readonly Counter<long> EventsDropped =
        Meter.CreateCounter<long>("ami.events.dropped", "events",
            "AMI events dropped by a full event pump or discarded by a caller's ending");

    /// <summary>AMI events dispatched to observers.</summary>
    public static readonly Counter<long> EventsDispatched =
        Meter.CreateCounter<long>("ami.events.dispatched", "events",
            "AMI events dispatched to observers");

    /// <summary>Total AMI actions sent to Asterisk.</summary>
    public static readonly Counter<long> ActionsSent =
        Meter.CreateCounter<long>("ami.actions.sent", "actions",
            "Total AMI actions sent to Asterisk");

    /// <summary>Total AMI responses received from Asterisk.</summary>
    public static readonly Counter<long> ResponsesReceived =
        Meter.CreateCounter<long>("ami.responses.received", "responses",
            "Total AMI responses received from Asterisk");

    /// <summary>AMI reconnection attempts.</summary>
    public static readonly Counter<long> ReconnectionAttempts =
        Meter.CreateCounter<long>("ami.reconnections", "attempts",
            "AMI reconnection attempts");

    // --- Histograms ---

    /// <summary>Roundtrip time for AMI action send to response receive.</summary>
    public static readonly Histogram<double> ActionRoundtripMs =
        Meter.CreateHistogram<double>("ami.action.roundtrip", "ms",
            "Roundtrip time for AMI action send->response");

    /// <summary>Time to dispatch an event to all observers.</summary>
    public static readonly Histogram<double> EventDispatchMs =
        Meter.CreateHistogram<double>("ami.event.dispatch", "ms",
            "Time to dispatch an event to all observers");
}
