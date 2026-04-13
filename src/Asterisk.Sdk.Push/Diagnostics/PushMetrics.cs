using System.Diagnostics.Metrics;

namespace Asterisk.Sdk.Push.Diagnostics;

/// <summary>
/// System.Diagnostics.Metrics instruments for the push pipeline. One instance per
/// host (registered as singleton). Exposed via Meter <c>Asterisk.Sdk.Push</c>.
/// </summary>
public sealed class PushMetrics : IDisposable
{
    /// <summary>Meter name for OpenTelemetry / dotnet-counters subscribers.</summary>
    public const string MeterName = "Asterisk.Sdk.Push";

    private readonly Meter _meter;
    private readonly Lock _gaugeLock = new();
    private bool _gaugeRegistered;
    private Func<int>? _activeSubscribersProvider;

    /// <summary>Total events accepted by the bus.</summary>
    public Counter<long> EventsPublished { get; }

    /// <summary>Events successfully delivered to at least one subscriber.</summary>
    public Counter<long> EventsDelivered { get; }

    /// <summary>Events dropped (tag <c>reason=buffer_full|filter_rejected</c>).</summary>
    public Counter<long> EventsDropped { get; }

    public PushMetrics()
    {
        _meter = new Meter(MeterName, "1.6.0");

        EventsPublished = _meter.CreateCounter<long>(
            "asterisk.push.events.published", "events",
            "Total push events accepted by the bus");

        EventsDelivered = _meter.CreateCounter<long>(
            "asterisk.push.events.delivered", "events",
            "Push events delivered to subscribers");

        EventsDropped = _meter.CreateCounter<long>(
            "asterisk.push.events.dropped", "events",
            "Push events dropped due to backpressure or filter rejection");
    }

    /// <summary>
    /// Registers an observable gauge backed by <paramref name="provider"/>. Idempotent —
    /// guarded so repeated calls (e.g. multi-registry scenarios) do not leak instruments.
    /// </summary>
    public void BindActiveSubscribersGauge(Func<int> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_gaugeLock)
        {
            _activeSubscribersProvider = provider;
            if (_gaugeRegistered) return;
            _meter.CreateObservableGauge(
                "asterisk.push.subscribers.active",
                () => _activeSubscribersProvider?.Invoke() ?? 0,
                "subscribers",
                "Currently active push subscriptions");
            _gaugeRegistered = true;
        }
    }

    public void Dispose() => _meter.Dispose();
}
