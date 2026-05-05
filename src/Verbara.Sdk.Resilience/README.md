# Asterisk.Sdk.Resilience

Composable resilience primitives for .NET. AOT-safe, zero reflection, `TimeProvider`-based for testability. No Polly dependency. MIT licensed.

## What it does

- **Circuit breaker** — per-key `CircuitBreakerState` with classic closed → open → half-open → closed cycle. Thread-safe via `Interlocked`/`Volatile`; no locks.
- **Retry with exponential backoff** — configurable `maxAttempts` (capped at 10) and `baseDelay` with ±20% deterministic jitter.
- **Per-attempt timeout** — wraps action in linked `CancellationTokenSource` that honours both caller token and timeout.
- **Fluent builder** — compose any subset of the three primitives via `ResiliencePolicyBuilder` and call `Build()`.
- **First-class diagnostics** via `System.Diagnostics.Metrics` (meter name `Asterisk.Sdk.Resilience`): `retry.attempts`, `circuit.opened`, `circuit.closed`, `timeout.fired`, `circuit.state` observable gauge.

No external runtime dependencies beyond `Microsoft.Extensions.*` abstractions. Trim-safe.

## Install

```
dotnet add package Asterisk.Sdk.Resilience
```

## Quick start

```csharp
using Asterisk.Sdk.Resilience;
using Asterisk.Sdk.Resilience.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

// 1) Register a default policy (or a no-op if configure is null).
var services = new ServiceCollection();
services.AddAsteriskResilience(b => b
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

Meter name: `Asterisk.Sdk.Resilience`. Enrol in OpenTelemetry:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("Asterisk.Sdk.Resilience"));
```

Or transparently via `AddAsteriskOpenTelemetry().WithAllSources()`.

Instruments emitted:

| Instrument | Kind | Tags |
|---|---|---|
| `retry.attempts` | Counter<long> | `key` |
| `circuit.opened` | Counter<long> | `key` |
| `circuit.closed` | Counter<long> | `key` |
| `timeout.fired` | Counter<long> | `key` |
| `circuit.state` | ObservableGauge<int> (0=closed, 1=half-open, 2=open) | `key` |

## Testing

`TimeProvider.System` is the default clock. Inject `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`) via `WithTimeProvider(fakeTimeProvider)` for deterministic backoff delays and circuit open-duration testing.

## License

MIT. Part of the [Asterisk.Sdk](https://github.com/Harol-Reina/Asterisk.Sdk) project.
