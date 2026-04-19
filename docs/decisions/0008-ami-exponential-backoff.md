# ADR-0008: Deterministic exponential backoff (no jitter, no Polly) for AMI reconnection

- **Status:** Accepted
- **Date:** 2026-04-19 (retrospective)
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0001 (AOT-first)

## Context

AMI is a long-lived TCP connection to Asterisk's manager.so module. Production deployments must survive:

- Asterisk restarts (minutes of downtime).
- Network hiccups (seconds of TCP RST / half-close).
- Asterisk module reload (5–10 s when `core reload` runs).

Single-SDK-instance → single Asterisk sees no thundering herd. But when an Asterisk fleet restarts (rolling reload across 10 boxes), the SDK instances reconnect in parallel. The question is **what retry strategy to use?**

.NET ecosystem defaults are:

1. **Polly** — de-facto retry library. Supports exponential + full/decorrelated jitter + circuit breakers.
2. **Full jitter** (AWS SDK style) — delay is `random(0, cap)` on each attempt.
3. **Deterministic exponential** — delay doubles each attempt, up to a cap.

## Decision

AMI reconnection uses **deterministic exponential backoff with no jitter**, implemented inline in `AmiConnection.ReconnectLoopAsync()`. Defaults: `ReconnectInitialDelay = 1 s`, `ReconnectMultiplier = 2.0`, `ReconnectMaxDelay = 30 s`. All configurable via `AmiConnectionOptions`. No Polly dependency.

Sequence: 1 s → 2 s → 4 s → 8 s → 16 s → 30 s (clamped) → 30 s … forever (unless `MaxReconnectAttempts > 0`).

## Consequences

- **Positive:** Zero runtime dependencies in the reconnect path — AOT-clean trivially. Operators get a fully deterministic timing model for load tests and alerting ("after 6 failed attempts we're at 30 s"). Defaults match the production 30 s module-reload window.
- **Negative:** Thundering-herd risk in large deployments (100+ SDK instances) reconnecting to the same Asterisk cluster. Mitigation: consumers can set `ReconnectInitialDelay` to a slightly randomized value at app startup (rare in practice).
- **Trade-off:** We accept no jitter in exchange for 0 dependencies + predictability. If thundering herd becomes real for a consumer, the hook is there to wrap the reconnect call with jitter.

## Alternatives considered

- **Polly** — rejected because Polly 8.x is AOT-safe but adds 180 KB of assemblies and requires consumers to understand `ResiliencePipeline<T>` ceremony for a single-purpose concern. The SDK targets "drop-in, zero-config reconnect" as its happy path.
- **Full jitter (AWS style)** — rejected because it breaks the "after N attempts we're at M seconds" determinism that SREs rely on for dashboards and runbooks. A first-attempt sleep of 0 s is also wrong for Asterisk module-reload scenarios.
- **Decorrelated jitter** — rejected for the same reason as full jitter, plus its PRNG introduces observable randomness into integration tests.

## Notes

- Code: `src/Asterisk.Sdk.Ami/Connection/AmiConnection.cs` lines 545–583.
- Options: `src/Asterisk.Sdk.Ami/Connection/AmiConnectionOptions.cs` lines 42–59.
- The reconnect loop fires `AmiMetrics.ReconnectionAttempts` (an `ObservableUpDownCounter`), so consumers get the series without writing their own instrumentation.
