using System.Diagnostics.Metrics;

namespace Asterisk.Sdk.Push.Webhooks;

/// <summary>
/// Metrics for webhook delivery. Uses <see cref="Meter"/> name <c>Asterisk.Sdk.Push.Webhooks</c>
/// so it enrolls in <c>Asterisk.Sdk.Hosting.AsteriskTelemetry.MeterNames</c>.
/// </summary>
internal sealed class WebhookMetrics : IDisposable
{
    public const string MeterName = "Asterisk.Sdk.Push.Webhooks";

    private readonly Meter _meter;

    public Counter<long> DeliveriesSucceeded { get; }
    public Counter<long> DeliveriesFailed { get; }
    public Counter<long> DeliveriesRetried { get; }
    public Counter<long> DeadLetter { get; }
    public Counter<long> CircuitOpened { get; }
    public Counter<long> CircuitSkipped { get; }

    private const string DeliveriesUnit = "deliveries";

    public WebhookMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");
        DeliveriesSucceeded = _meter.CreateCounter<long>(
            "asterisk.push.webhooks.deliveries.succeeded", DeliveriesUnit,
            "Webhook deliveries that returned 2xx on any attempt.");
        DeliveriesFailed = _meter.CreateCounter<long>(
            "asterisk.push.webhooks.deliveries.failed", DeliveriesUnit,
            "Webhook delivery attempts that threw or returned non-2xx.");
        DeliveriesRetried = _meter.CreateCounter<long>(
            "asterisk.push.webhooks.deliveries.retried", DeliveriesUnit,
            "Retry attempts (excludes the first attempt).");
        DeadLetter = _meter.CreateCounter<long>(
            "asterisk.push.webhooks.deliveries.dead_letter", DeliveriesUnit,
            "Deliveries that exhausted MaxRetries without a 2xx response.");
        CircuitOpened = _meter.CreateCounter<long>(
            "asterisk.push.webhooks.circuit.opened", "transitions",
            "Transitions from closed to open for a target URL's circuit breaker.");
        CircuitSkipped = _meter.CreateCounter<long>(
            "asterisk.push.webhooks.circuit.skipped", DeliveriesUnit,
            "Deliveries short-circuited because the target URL's circuit is open.");
    }

    public void Dispose() => _meter.Dispose();
}
