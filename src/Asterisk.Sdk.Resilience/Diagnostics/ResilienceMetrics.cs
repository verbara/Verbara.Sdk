using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Asterisk.Sdk.Resilience.Diagnostics;

/// <summary>
/// <see cref="System.Diagnostics.Metrics"/> instruments for the resilience primitives.
/// Meter name <c>Asterisk.Sdk.Resilience</c> — enrol via
/// <c>builder.AddMeter(ResilienceMetrics.MeterName)</c> or
/// <c>services.AddAsteriskResilience()</c> (which registers the meter for OpenTelemetry
/// auto-enrolment alongside the other SDK meters).
/// </summary>
public static class ResilienceMetrics
{
    /// <summary>Canonical meter name. Keep in sync with <see cref="Meter"/>.</summary>
    public const string MeterName = "Asterisk.Sdk.Resilience";

    /// <summary>The shared meter instance for resilience instruments.</summary>
    public static readonly Meter Meter = new(MeterName);

    /// <summary>Counter tagged by <c>key</c> incremented on every retry attempt.</summary>
    public static readonly Counter<long> RetryAttempts = Meter.CreateCounter<long>(
        "retry.attempts",
        description: "Total retry attempts performed by ResiliencePolicy, tagged by key.");

    /// <summary>Counter tagged by <c>key</c> incremented when a circuit transitions from closed to open.</summary>
    public static readonly Counter<long> CircuitOpened = Meter.CreateCounter<long>(
        "circuit.opened",
        description: "Circuit breaker transitions from closed to open, tagged by key.");

    /// <summary>Counter tagged by <c>key</c> incremented when a circuit transitions from open back to closed.</summary>
    public static readonly Counter<long> CircuitClosed = Meter.CreateCounter<long>(
        "circuit.closed",
        description: "Circuit breaker transitions from open to closed (successful probe), tagged by key.");

    /// <summary>Counter tagged by <c>key</c> incremented when a per-attempt timeout elapses.</summary>
    public static readonly Counter<long> TimeoutFired = Meter.CreateCounter<long>(
        "timeout.fired",
        description: "Per-attempt timeouts fired by ResiliencePolicy, tagged by key.");

    // Per-key circuit state snapshot for the observable gauge. We store a numeric encoding:
    // 0 = closed, 1 = half-open (transition in progress — set briefly on successful probe),
    // 2 = open.
    private static readonly ConcurrentDictionary<string, int> _circuitStates = new();

    /// <summary>
    /// Observable gauge tagged by <c>key</c> emitting <c>0 = closed</c>, <c>1 = half-open</c>,
    /// <c>2 = open</c>. Values are reported for every key that has been observed at least once.
    /// </summary>
    public static readonly ObservableGauge<int> CircuitStateGauge = Meter.CreateObservableGauge(
        "circuit.state",
        ObserveCircuitStates,
        description: "Current circuit state per key: 0 = closed, 1 = half-open, 2 = open.");

    /// <summary>Encoded circuit state values reported by <see cref="CircuitStateGauge"/>.</summary>
    public enum CircuitStateValue
    {
        /// <summary>Circuit is closed; traffic flows normally.</summary>
        Closed = 0,

        /// <summary>Circuit is in a half-open probe window.</summary>
        HalfOpen = 1,

        /// <summary>Circuit is open; traffic is rejected.</summary>
        Open = 2,
    }

    /// <summary>
    /// Updates the reported circuit state for <paramref name="key"/>. Called by
    /// <see cref="ResiliencePolicy"/> whenever state transitions; safe to invoke from any thread.
    /// </summary>
    public static void SetCircuitState(string key, CircuitStateValue value)
    {
        ArgumentNullException.ThrowIfNull(key);
        _circuitStates[key] = (int)value;
    }

    private static IEnumerable<Measurement<int>> ObserveCircuitStates()
    {
        foreach (var kvp in _circuitStates)
        {
            yield return new Measurement<int>(
                kvp.Value,
                new KeyValuePair<string, object?>("key", kvp.Key));
        }
    }
}
