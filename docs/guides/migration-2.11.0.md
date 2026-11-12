# Upgrading from 2.10.0 to 2.11.0

This is the one migration guide for 2.11.0. It has one section per change a reader may have to act on, and says who
each one reaches and what to do. The [CHANGELOG](../../CHANGELOG.md) has the full entry for each.

| Package | Change | Kind |
|---|---|---|
| `Verbara.Sdk.Resilience`, `Verbara.Sdk.Push.Webhooks` | [A half-open circuit admits one probe](#a-half-open-circuit-admits-one-probe) | behaviour, metric meaning, new member, BREAKING |

## Resilience

### A half-open circuit admits one probe

**What changed.** A tripped circuit used to reset itself once its open window had elapsed: every call was admitted, and
a failed call then needed the full threshold of failures to open the circuit again. It now follows the closed → open →
half-open → closed cycle the package always described:

- Once the open window has elapsed, the next call is the circuit's **probe**. Every other call to that key is rejected
  until the probe's result is recorded: `CircuitBreakerOpenException` from `ResiliencePolicy.ExecuteAsync`, a skipped
  delivery (`asterisk.push.webhooks.circuit.skipped`) in webhook delivery, and `false` from
  `CircuitBreakerState.ShouldAllow`.
- A **failed probe** re-opens the circuit at once, whatever the threshold, for a full open window from that failure, and
  counts on `circuit.opened`.
- A **successful probe** closes the circuit, clears its failure count, and counts on `circuit.closed`. After that the
  full threshold of failures is needed to open it again.
- A probe that **reports nothing** (its caller cancelled it, or a direct caller never recorded a result) holds the circuit
  shut for at most one more open window; then the next call is admitted as a new probe.
- `circuit.state` and `ResiliencePolicy.GetCircuitState` read `1` (half-open) from the end of the open window until the
  probe's result is recorded, so for the whole time a probe is in flight. A failed probe reads `2` for a full window, also
  under a threshold above one, where 2.10.0 read `0`. Reading the state never takes the probe.

No public member was removed or changed its signature. One was added: `CircuitBreakerState.RecordFailureReportingTrip`.

**Who is affected.**

- **Anyone whose dependency fails for longer than one open window.** During the outage a key now sees one attempt per open
  window and rejections for the rest, instead of a burst of real calls after every window. Expect a higher rate of
  `CircuitBreakerOpenException` and fewer real failures; review alerts set on either.
- **A policy with retries.** The policy checks the circuit before every attempt, so when a probe fails its own next attempt
  is rejected: the caller gets `CircuitBreakerOpenException`, not the probe's exception. A trip in the middle of the retries
  already did this; it is now also what a failed probe does under a threshold above one. A retry whose backoff outlasts
  the open window reaches the check after the re-opened window has elapsed, and is admitted as the next probe.
- **Recovery waits for the probe.** After the dependency comes back, other calls to the key are rejected until the probe
  returns, so a slow probe delays recovery by its own duration, at most one open window.
- **A reader of `circuit.closed`.** A successful probe did not count before, so the counter was nearly flat. It now counts
  once per recovery: expect its rate to rise to the real one.
- **A reader of `circuit.state`.** `1` no longer implies that the next call is admitted: while a probe is in flight, calls
  are rejected and the gauge still reads `1`. `2` still means a call now is rejected.
- **A webhook target.** Webhook delivery checks the circuit once per delivery. A probe delivery keeps its own retries, and
  a later attempt's success closes the circuit. A failed attempt re-opens it at once, so other deliveries are skipped
  while that delivery retries. `asterisk.push.webhooks.circuit.opened` now counts each opening once when several
  deliveries to one URL fail together, and also counts a failed probe; its Warning is logged once per opening.
- **A direct caller of `CircuitBreakerState`.** See below.

**What to do.** A caller of `ResiliencePolicy` or of webhook delivery changes no code. Review the alerts named above.

A direct caller of `CircuitBreakerState.ShouldAllow` must treat a `true` past the window as holding the probe and record
that call's result with `RecordSuccess` or `RecordFailure`. A caller that uses `ShouldAllow` as a question, without making
the call, takes the probe and leaves the circuit rejecting for up to one more window. A caller that compares `IsOpen`
before and after `RecordFailure` to count an opening keeps working (`IsOpen` reads `false` while half-open, so a failed
probe shows as an opening), but under concurrent failures it can count one opening several times or none.
`RecordFailureReportingTrip` records the failure and returns `true` to the one call that opened the circuit, from closed
or from half-open:

```csharp
using Verbara.Sdk.Resilience;

// A direct caller: a true from ShouldAllow past the window is the probe, so its result must be recorded.
static async Task<bool> CallThroughAsync(CircuitBreakerState circuit, TimeSpan openDuration, int threshold, Func<Task> call)
{
    if (!circuit.ShouldAllow(openDuration, TimeProvider.System))
        return false; // open, or its one probe is in flight

    try
    {
        await call();
        circuit.RecordSuccess();
        return true;
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        if (circuit.RecordFailureReportingTrip(threshold, TimeProvider.System))
        {
            // This failure opened the circuit: count and log the opening here, once.
        }

        throw;
    }
}
```
