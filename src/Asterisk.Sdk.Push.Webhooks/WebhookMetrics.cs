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

    public WebhookMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");
        DeliveriesSucceeded = _meter.CreateCounter<long>(
            "asterisk.push.webhooks.deliveries.succeeded", "deliveries",
            "Webhook deliveries that returned 2xx on any attempt.");
        DeliveriesFailed = _meter.CreateCounter<long>(
            "asterisk.push.webhooks.deliveries.failed", "deliveries",
            "Webhook delivery attempts that threw or returned non-2xx.");
        DeliveriesRetried = _meter.CreateCounter<long>(
            "asterisk.push.webhooks.deliveries.retried", "deliveries",
            "Retry attempts (excludes the first attempt).");
        DeadLetter = _meter.CreateCounter<long>(
            "asterisk.push.webhooks.deliveries.dead_letter", "deliveries",
            "Deliveries that exhausted MaxRetries without a 2xx response.");
    }

    public void Dispose() => _meter.Dispose();
}
