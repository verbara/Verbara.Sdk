using System.Diagnostics.Metrics;

namespace Asterisk.NetAot.Ami.Diagnostics;

/// <summary>
/// AMI connection metrics exposed via System.Diagnostics.Metrics.
/// Compatible with OpenTelemetry, Prometheus, dotnet-counters, and any .NET metrics consumer.
/// <para>
/// Usage: <c>dotnet-counters monitor --process-id &lt;pid&gt; Asterisk.NetAot.Ami</c>
/// </para>
/// </summary>
public static class AmiMetrics
{
    public static readonly Meter Meter = new("Asterisk.NetAot.Ami", "1.0.0");

    // --- Counters ---

    /// <summary>Total AMI events received from Asterisk.</summary>
    public static readonly Counter<long> EventsReceived =
        Meter.CreateCounter<long>("ami.events.received", "events",
            "Total AMI events received from Asterisk");

    /// <summary>AMI events dropped due to full event pump buffer.</summary>
    public static readonly Counter<long> EventsDropped =
        Meter.CreateCounter<long>("ami.events.dropped", "events",
            "AMI events dropped due to full event pump");

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
