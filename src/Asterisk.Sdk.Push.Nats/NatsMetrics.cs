using System.Diagnostics.Metrics;

namespace Asterisk.Sdk.Push.Nats;

/// <summary>
/// Metrics for the NATS bridge. Uses meter name <c>Asterisk.Sdk.Push.Nats</c> so it
/// enrolls in <c>Asterisk.Sdk.Hosting.AsteriskTelemetry.MeterNames</c> once registered.
/// </summary>
internal sealed class NatsMetrics : IDisposable
{
    public const string MeterName = "Asterisk.Sdk.Push.Nats";
    private const string EventsUnit = "events";

    private readonly Meter _meter;

    public Counter<long> EventsPublished { get; }
    public Counter<long> EventsFailed { get; }
    public Counter<long> EventsReceived { get; }
    public Counter<long> EventsSkipped { get; }
    public Counter<long> EventsDecodeFailed { get; }

    public NatsMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");
        EventsPublished = _meter.CreateCounter<long>(
            "asterisk.push.nats.events.published", EventsUnit,
            "Push events successfully republished to NATS.");
        EventsFailed = _meter.CreateCounter<long>(
            "asterisk.push.nats.events.failed", EventsUnit,
            "Push events that failed to publish to NATS (serialize or network error).");
        EventsReceived = _meter.CreateCounter<long>(
            "asterisk.push.nats.events.received", EventsUnit,
            "NATS messages successfully decoded and delivered to the local Push bus.");
        EventsSkipped = _meter.CreateCounter<long>(
            "asterisk.push.nats.events.skipped", EventsUnit,
            "NATS messages dropped by the subscriber (e.g. self-originated with SkipSelfOriginated=true).");
        EventsDecodeFailed = _meter.CreateCounter<long>(
            "asterisk.push.nats.events.decode_failed", EventsUnit,
            "NATS messages that failed to decode (malformed JSON or missing required fields).");
    }

    public void Dispose() => _meter.Dispose();
}
