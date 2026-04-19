using System.Diagnostics.Metrics;

namespace Asterisk.Sdk.Push.Nats;

/// <summary>
/// Metrics for the NATS bridge. Uses meter name <c>Asterisk.Sdk.Push.Nats</c> so it
/// enrolls in <c>Asterisk.Sdk.Hosting.AsteriskTelemetry.MeterNames</c> once registered.
/// </summary>
internal sealed class NatsMetrics : IDisposable
{
    public const string MeterName = "Asterisk.Sdk.Push.Nats";

    private readonly Meter _meter;

    public Counter<long> EventsPublished { get; }
    public Counter<long> EventsFailed { get; }

    public NatsMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");
        EventsPublished = _meter.CreateCounter<long>(
            "asterisk.push.nats.events.published", "events",
            "Push events successfully republished to NATS.");
        EventsFailed = _meter.CreateCounter<long>(
            "asterisk.push.nats.events.failed", "events",
            "Push events that failed to publish to NATS (serialize or network error).");
    }

    public void Dispose() => _meter.Dispose();
}
