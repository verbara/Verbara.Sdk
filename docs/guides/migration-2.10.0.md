# Upgrading from 2.9.0 to 2.10.0

This is the one migration guide for 2.10.0. It has one section per change a reader may have to act on, and says who
each one reaches and what to do. The [CHANGELOG](../../CHANGELOG.md) has the full entry for each.

| Package | Change | Kind |
|---|---|---|
| `Verbara.Sdk.Resilience` | [`circuit.state` reports the breaker's current state](#circuitstate-reports-the-breakers-current-state) | metric meaning |

## Resilience

### `circuit.state` reports the breaker's current state

**What changed.** The `circuit.state` gauge reports the state a key's circuit is in when it is observed, not the last
value a call wrote. A tripped circuit that sees no more calls used to read `2` (open) forever; it now reads `2` until its
open window has elapsed and `1` (half-open) after that, with no call needed. The instrument keeps its name, meter, `key`
tag and encoding (`0` closed, `1` half-open, `2` open), and a key still appears from its first success, rejection or trip.

The values now mean:

- `2`: a call now is rejected with `CircuitBreakerOpenException`.
- `1`: the open window has elapsed and no call has been admitted since. It does not mean that one probe is in flight.
  The breaker has no such state: every call is admitted once the window has elapsed, and the first one resets the
  circuit, so `1` lasts until that call.
- `0`: anything else. That includes a circuit whose call after the window failed below the threshold: the old reading
  stayed at `2`, and the next call is admitted. If that failure reaches the threshold, the gauge reads `2` for a full
  open window from the failure.

**Who is affected.** Anyone with an alert, a dashboard or a health check that needs `circuit.state` to stay at `2`.
That reading was unbroken only because it was stale. With traffic and a failing dependency the circuit really cycles:
open for the window, then closed until `threshold` failures trip it again, so a sample in that gap reads `0` or `1`. A
rule such as "`circuit.state` is `2` for five minutes" restarts its timer on such a sample and may not fire during the
outage it was written for, and a timer started when the state last changed is reset the same way.

**What to do.** Move an alert that needs an unbroken `2` to `circuit.opened`, which counts every re-trip: its increase
over a window says that a key keeps tripping, whether or not a sample lands in the gap. Keep `circuit.state` for what it
now answers, which is whether a call to that key would be rejected right now.

A health check that observes the gauge through a `MeterListener` can call
`ResiliencePolicy.GetCircuitState(key)` instead. It returns the same reading as a
`ResilienceMetrics.CircuitStateValue` for that policy's own circuit, reads `Closed` for a key with no circuit, and
changes nothing. Where two policies share a key, the gauge follows the one that reported the key last, and each policy's
`GetCircuitState` keeps reading its own circuit. No code needs to change: the member is additive, and
`ResilienceMetrics.SetCircuitState` keeps its signature.
