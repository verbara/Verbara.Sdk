using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Verbara.Sdk.Resilience.Diagnostics;

/// <summary>
/// <see cref="System.Diagnostics.Metrics"/> instruments for the resilience primitives.
/// Meter name <c>Verbara.Sdk.Resilience</c> — enrol via
/// <c>builder.AddMeter(ResilienceMetrics.MeterName)</c> or
/// <c>services.AddVerbaraResilience()</c> (which registers the meter for OpenTelemetry
/// auto-enrolment alongside the other SDK meters).
/// </summary>
public static class ResilienceMetrics
{
    /// <summary>Canonical meter name. Keep in sync with <see cref="Meter"/>.</summary>
    public const string MeterName = "Verbara.Sdk.Resilience";

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

    // Per-key source of the observable gauge: the live breaker of the policy that reported the key
    // last, or a value SetCircuitState wrote. The gauge asks it at each observation, so an idle key
    // reports the state it is in now. Numeric encoding: 0 = closed, 1 = half-open (the open window
    // has elapsed and no call has been admitted since), 2 = open.
    private static readonly ConcurrentDictionary<string, CircuitSource> _circuitStates = new();

    /// <summary>
    /// Observable gauge tagged by <c>key</c> emitting <c>0 = closed</c>, <c>1 = half-open</c>,
    /// <c>2 = open</c>: the state the key's circuit is in when observed, with no later call needed.
    /// A key is reported from its first success, rejection or trip.
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

        /// <summary>The open window has elapsed and no call has been admitted since.</summary>
        HalfOpen = 1,

        /// <summary>Circuit is open; traffic is rejected.</summary>
        Open = 2,
    }

    /// <summary>
    /// Stores a fixed value as the reported circuit state for <paramref name="key"/>, which the gauge
    /// reports until a <see cref="ResiliencePolicy"/> reports that key; safe to invoke from any thread.
    /// </summary>
    public static void SetCircuitState(string key, CircuitStateValue value)
    {
        ArgumentNullException.ThrowIfNull(key);
        _circuitStates[key] = new FixedCircuitValue(value);
    }

    // One dictionary read and no allocation once the key follows this breaker: the success path of
    // every call comes through here.
    internal static void TrackCircuit(CircuitBreakerState state, TimeSpan openDuration, TimeProvider time)
    {
        if (_circuitStates.TryGetValue(state.Key, out var source)
            && source is TrackedCircuit tracked
            && ReferenceEquals(tracked.State, state))
        {
            return;
        }

        _circuitStates[state.Key] = new TrackedCircuit(state, openDuration, time);
    }

    private static IEnumerable<Measurement<int>> ObserveCircuitStates()
    {
        foreach (var kvp in _circuitStates)
        {
            yield return new Measurement<int>(
                (int)kvp.Value.Read(),
                new KeyValuePair<string, object?>("key", kvp.Key));
        }
    }

    private abstract class CircuitSource
    {
        public abstract CircuitStateValue Read();
    }

    private sealed class TrackedCircuit(CircuitBreakerState state, TimeSpan openDuration, TimeProvider time)
        : CircuitSource
    {
        public CircuitBreakerState State { get; } = state;

        public override CircuitStateValue Read() => State.ReadState(openDuration, time);
    }

    private sealed class FixedCircuitValue(CircuitStateValue value) : CircuitSource
    {
        public override CircuitStateValue Read() => value;
    }
}
