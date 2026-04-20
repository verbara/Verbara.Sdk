# ADR-0029: Resilience primitives move from Pro to SDK (MIT)

- **Status:** Accepted (executed 2026-04-20 in SDK v1.14.0 + Pro v1.9.0-pro)
- **Date:** 2026-04-20
- **Deciders:** Harold Reina
- **Related:**
  - PSD v2: `Asterisk.Platform/docs/specs/2026-04-19-product-strategy-v2.md` §3.2, §6.2
  - Pro ADR-0002 (production hardening baseline — where Pro.Resilience was born)
  - ADR-0027 (stewardship pledge)

## Context

En v1.8.0-pro (abril 2026) se creó el paquete `Asterisk.Sdk.Pro.Resilience` con primitives genéricos: `CircuitBreakerState`, `ResiliencePolicy` (compose: retry + circuit breaker + timeout), `ResiliencePolicyBuilder`, `CircuitBreakerOpenException`, `ResilienceMetrics`. Fue ownership decision contingente (parte del hardening release de Pro), no deliberada.

**Problema identificado por analysis:**

Resilience primitives (circuit breaker + retry + timeout) son **infraestructura genérica sin domain logic commercial**. No cruzan ninguno de los 5 gates de open-core (multi-tenancy, scale, integration, compliance, cross-DR).

**Evidencia de industry:** TODOS los resilience libraries comparables son OSS/free:
- **Polly** (.NET reference): BSD-3-Clause, .NET Foundation.
- **Resilience4j** (JVM): Apache 2.0.
- **Hystrix** (Netflix, retired): Apache 2.0.
- **failsafe-go** / **go-resiliency**: MIT / Apache 2.0.
- **Istio circuit-breaker / Envoy outlier-detection**: Apache 2.0.

Ningún open-core player monetiza primitive resilience. Donde aparece commercial, es en **managed SLA layer** encima del primitive — no en el primitive mismo.

**Impacto del mis-categorization actual:**

1. **SDK retry open-coded en 3 lugares** (AmiConnection, AriLoggingHandler, WebhookDeliveryService) — MIT users no pueden usar primitive sin comprar Pro.
2. **Anti-pattern open-core** documentado (OCV "primitives trapped in commercial").
3. **Fork risk preventivo:** si un cloud provider necesita circuit breaker, re-implementa en lugar de depender de Pro. Reduce valor de Pro.

Movimiento es aditivo (no breaking) via type-forward.

## Decision

**Crear paquete `Asterisk.Sdk.Resilience` MIT en SDK v2.0.0 con contents equivalentes de Pro.Resilience v1.8.1-pro:**

- `Asterisk.Sdk.Resilience.CircuitBreakerState`
- `Asterisk.Sdk.Resilience.ResiliencePolicy`
- `Asterisk.Sdk.Resilience.ResiliencePolicyBuilder`
- `Asterisk.Sdk.Resilience.CircuitBreakerOpenException`
- `Asterisk.Sdk.Resilience.Diagnostics.ResilienceMetrics` (meter `Asterisk.Sdk.Resilience`)

**Pro v1.9.0-pro changes (executed):**
- Package `Asterisk.Sdk.Pro.Resilience` **deleted entirely** — no type-forward viable (namespace change breaks FQN resolution).
- Engine-specific policy builders (EventStore retry 3/100ms/timeout5s, Analytics 2/200ms/timeout2s, AgentAssist per-provider, CallAnalytics per-analyzer, Dialer circuit) preserved in-place — 5 consumers migrated `using` statements only.
- Tenant-aware budgets + SLA-backed hedging remain deferred as potential Pro-only features for future commercial differentiation.

**SDK v1.14.0 adoption (executed — hybrid approach):**
- `AmiConnection.ReconnectLoopAsync` → uses `BackoffSchedule.Compute` helper (zero behavior change; preserves configurable multiplier + max cap).
- `AriClient.ReconnectLoopAsync` → same helper adoption.
- `WebhookDeliveryService.DeliverAsync` → same helper adoption.
- Full `ResiliencePolicy.ExecuteAsync` wrap + per-URL circuit breaker deferred — requires explicit feature design (what counts as "failure" for webhooks). AMI/ARI reconnect is abstraction mismatch with bounded retry policy; helper is the correct shared primitive.
- New public type added to Resilience package: `BackoffSchedule` (static helper for stateless backoff delay calculation in continuous state loops).

**Migration mechanism (revised from original ADR):**
- ~~TypeForwardedTo not viable~~: FQN must match for forwarding; namespace change breaks this.
- Clean break with migration guide. External consumers rename `using` + swap `<PackageReference>` (documented below).
- NuGet `Asterisk.Sdk.Pro.Resilience` v1.8.1-pro marked deprecated on nuget.org after v1.9.0-pro publishes.

## Consequences

**Positivas:**
- Elimina anti-pattern "primitives trapped in commercial".
- MIT users ganan primitives (CircuitBreakerState, ResiliencePolicy, BackoffSchedule) sin comprar Pro.
- SDK elimina retry-backoff duplicado en 3 hot paths (AMI, ARI, Webhook) — maintenance win.
- Pro surface reduced 25→24 packages; se concentra en SLA + tenant-aware + engine-specific policies.
- Stewardship pledge (ADR-0027) se honra: primer ejemplo concreto de Commercial→MIT movement.
- Alinea con Polly/Resilience4j convention (industry-standard).

**Negativas:**
- `Asterisk.Sdk.Pro.Resilience` v1.8.1-pro consumers deben migrar (rename `using` + swap package ref). Mitigado por documentación + scope (4 meses de vida, sin adopción externa conocida).
- Meter name cambia: `Asterisk.Sdk.Pro.Resilience` → `Asterisk.Sdk.Resilience`. Dashboards deben actualizar (documentado en migration guide).
- Pro.Resilience pierde identidad marketing (features advertised en 1.8.0-pro release notes).
- Dual-emit window NO implementado por scope mínimo — dashboards migran en una acción.

## Alternatives considered

- **Status quo (mantener en Pro):** rechazado — anti-pattern documentado, MIT users quedan penalizados, stewardship pledge incompatible.
- **Asterisk.Sdk.Resilience via shim delgado (wrapper around Polly):** rechazado — complica dependency graph, Polly adds 450KB deps transitive, AOT compatibility requires validation.
- **Open-source Pro.Resilience as-is sin rename:** rechazado — package name `Asterisk.Sdk.Pro.Resilience` vendido como "commercial" confunde consumers.
- **Crear `Asterisk.Sdk.Resilience.Abstractions` MIT + keep impl Pro:** rechazado — split inútil; el primitive es simple, split duplica surface.

## Migration guide (for external consumers of Asterisk.Sdk.Pro.Resilience v1.8.x-pro)

```diff
  // csproj
- <PackageReference Include="Asterisk.Sdk.Pro.Resilience" Version="1.8.*" />
+ <PackageReference Include="Asterisk.Sdk.Resilience" Version="1.14.0" />
```

```diff
  // source files
- using Asterisk.Sdk.Pro.Resilience;
- using Asterisk.Sdk.Pro.Resilience.Diagnostics;
- using Asterisk.Sdk.Pro.Resilience.DependencyInjection;
+ using Asterisk.Sdk.Resilience;
+ using Asterisk.Sdk.Resilience.Diagnostics;
+ using Asterisk.Sdk.Resilience.DependencyInjection;
```

```diff
  // DI registration
- services.AddProResilience(b => b.WithRetry(3, TimeSpan.FromMilliseconds(100)));
+ services.AddAsteriskResilience(b => b.WithRetry(3, TimeSpan.FromMilliseconds(100)));
```

```diff
  // Observability dashboards
- meter:Asterisk.Sdk.Pro.Resilience
+ meter:Asterisk.Sdk.Resilience
```

All type shapes, method signatures, AOT compatibility, test contracts preserved verbatim. Only the namespace + DI method name + meter name changed.

## References

- PSD §3.2 primitive table + §6.2 borderline cases: `Asterisk.Platform/docs/specs/2026-04-19-product-strategy-v2.md`
- Pro ADR-0006 (sunset rationale): `Asterisk.Sdk.Pro/docs/decisions/0006-pro-resilience-sunset.md`
- Execution plan with per-decision log: `docs/plans/completed/2026-04-20-adr-0029-resilience-migration.md`
- Polly GitHub: github.com/App-vNext/Polly
- Pro 1.8.0-pro source (reference, deleted in v1.9.0-pro): was `src/Asterisk.Sdk.Pro.Resilience/`
