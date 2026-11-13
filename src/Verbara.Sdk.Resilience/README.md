# Verbara.Sdk.Resilience

Composable resilience primitives for .NET. AOT-safe, zero reflection, `TimeProvider`-based for testability. No Polly dependency. MIT licensed.

## What it does

- **Circuit breaker** — per-key `CircuitBreakerState` with classic closed → open → half-open → closed cycle: once the open window has elapsed one call is admitted as the probe and every other is rejected until its result is recorded; a failed probe re-opens the circuit for a full window, a successful one closes it. Thread-safe via `Interlocked`/`Volatile`; no locks.
- **Retry with exponential backoff** — configurable `maxAttempts` (capped at 10) and `baseDelay` with ±20% deterministic jitter.
- **Per-attempt timeout** — wraps action in linked `CancellationTokenSource` that honours both caller token and timeout.
- **Fluent builder** — compose any subset of the three primitives via `ResiliencePolicyBuilder` and call `Build()`.
- **First-class diagnostics** via `System.Diagnostics.Metrics` (meter name `Verbara.Sdk.Resilience`): `retry.attempts`, `circuit.opened`, `circuit.closed`, `timeout.fired`, `circuit.state` observable gauge.

No external runtime dependencies beyond `Microsoft.Extensions.*` abstractions. Trim-safe.

## Install

```
dotnet add package Verbara.Sdk.Resilience
```

## Quick start

```csharp
using Verbara.Sdk.Resilience;
using Verbara.Sdk.Resilience.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

// 1) Register a default policy (or a no-op if configure is null).
var services = new ServiceCollection();
services.AddVerbaraResilience(b => b
    .WithCircuitBreaker(threshold: 5, openDuration: TimeSpan.FromSeconds(30))
    .WithRetry(maxAttempts: 3, baseDelay: TimeSpan.FromMilliseconds(100))
    .WithTimeout(TimeSpan.FromSeconds(5)));

// 2) Resolve + execute.
var provider = services.BuildServiceProvider();
var policy = provider.GetRequiredService<ResiliencePolicy>();

var result = await policy.ExecuteAsync(
    key: "payment-gateway",
    action: async ct =>
    {
        // Work that may throw (network call, DB write, external API, etc.).
        return await CallPaymentGatewayAsync(ct);
    },
    ct: CancellationToken.None);
```

## Directly (without DI)

```csharp
var policy = new ResiliencePolicyBuilder()
    .WithRetry(3, TimeSpan.FromMilliseconds(100))
    .WithTimeout(TimeSpan.FromSeconds(5))
    .Build();

await policy.ExecuteAsync("key", async ct => { /* work */ return 42; }, ct: default);
```

## Observability

Meter name: `Verbara.Sdk.Resilience`. Enrol in OpenTelemetry:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("Verbara.Sdk.Resilience"));
```

Or transparently via `AddVerbaraOpenTelemetry().WithAllSources()`.

Instruments emitted:

| Instrument | Kind | Tags |
|---|---|---|
| `retry.attempts` | Counter<long> | `key` |
| `circuit.opened` | Counter<long>, one per opening: a trip from closed, or a failed probe | `key` |
| `circuit.closed` | Counter<long>, one per close: a successful probe, or a late success on an open circuit | `key` |
| `timeout.fired` | Counter<long> | `key` |
| `circuit.state` | ObservableGauge<int> (0=closed, 1=half-open, 2=open), the current state when observed | `key` |

`circuit.state` reports the state a key's circuit is in when the gauge is observed, from its policy's open window and clock, with no later call needed. `2` (open): the circuit is open within its window, and a call now is rejected with `CircuitBreakerOpenException`. `1` (half-open): the open window has elapsed and no probe result has been recorded since, either because no call has come yet, in which case the next call is admitted as the probe, or because the probe is in flight, in which case every other call is rejected. `0` (closed): anything else, including failures below the threshold. A failed probe reads `2` for a full window from that failure, whatever the threshold; a successful one reads `0`. A probe that reports nothing (its caller cancelled it) reads `1` and holds the circuit shut for at most one more window. Under a failing dependency with traffic the circuit cycles through `2` and `1` with one attempt per window, so an unbroken `2` does not measure an outage: `circuit.opened` counts openings. A key is reported from its first success, rejection or trip; where two policies share a key, the gauge follows the one that reported it last.

`ResiliencePolicy.GetCircuitState(key)` returns the same reading as a `ResilienceMetrics.CircuitStateValue` for that policy's own circuit, so a health check needs no `MeterListener`. A key with no circuit in the policy, and any key of a policy without a circuit breaker, reads `Closed`, because its next call is admitted. Neither the read nor the gauge changes the circuit, and the read does not make a key appear on the gauge.

## Testing

`TimeProvider.System` is the default clock. Inject `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`) via `WithTimeProvider(fakeTimeProvider)` for deterministic backoff delays and circuit open-duration testing.

## License

MIT. Part of the [Verbara.Sdk](https://github.com/verbara/Verbara.Sdk) project.
