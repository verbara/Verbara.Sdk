# ADR-0029: Resilience primitives move from Pro to SDK (MIT)

- **Status:** Proposed
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

**Pro.Resilience v2.0.0-pro retains:**
- Engine-specific policy builders (EventStore retry 3/100ms/timeout5s, Analytics 2/200ms/timeout2s, AgentAssist per-provider, CallAnalytics per-analyzer, Dialer circuit).
- Type-forwards para backward compat: `[assembly: TypeForwardedTo(typeof(Asterisk.Sdk.Resilience.ResiliencePolicy))]`.
- Tenant-aware budgets + SLA-backed hedging (commercial-only features).

**SDK adoption en Mes 2:**
- `AmiConnection.cs` backoff loop → wrapped en `ResiliencePolicy`.
- `AriLoggingHandler.cs` → retry primitive adoption.
- `WebhookDeliveryService.cs` → circuit breaker + retry primitive.

**Migration path para consumers:**
- v2.0.0 preview: ambos packages coexisten. Pro.Resilience redirects via type-forward.
- v2.1.x: Pro.Resilience marked `[Obsolete]`.
- v3.0.0: Pro.Resilience removed (only type-forwards remain en assembly de Pro para backcompat runtime).

## Consequences

**Positivas:**
- Elimina anti-pattern "primitives trapped in commercial".
- MIT users ganan primitive sin comprar Pro.
- Reduces Pro surface: Pro se concentra en SLA + tenant-aware features (su verdadero valor comercial).
- Stewardship pledge (ADR-0027) se honra: ejemplo concreto de Commercial→MIT movement.
- Alinea con Polly/Resilience4j convention (industry-standard).

**Negativas:**
- `Asterisk.Sdk.Pro.Resilience` v1.8.1-pro consumers tienen que migrate en v2.0 — mitigado por type-forwards.
- Meter name implícito cambia: `Asterisk.Sdk.Pro.Resilience` → `Asterisk.Sdk.Resilience`. Dashboards consumers necesitan ajuste. Mitigación: **re-emit ambos meter names durante v2.0-v2.1** (dual emit window).
- Pro.Resilience pierde identidad marketing (features advertised en 1.8.0-pro release notes como commercial).

## Alternatives considered

- **Status quo (mantener en Pro):** rechazado — anti-pattern documentado, MIT users quedan penalizados, stewardship pledge incompatible.
- **Asterisk.Sdk.Resilience via shim delgado (wrapper around Polly):** rechazado — complica dependency graph, Polly adds 450KB deps transitive, AOT compatibility requires validation.
- **Open-source Pro.Resilience as-is sin rename:** rechazado — package name `Asterisk.Sdk.Pro.Resilience` vendido como "commercial" confunde consumers.
- **Crear `Asterisk.Sdk.Resilience.Abstractions` MIT + keep impl Pro:** rechazado — split inútil; el primitive es simple, split duplica surface.

## References

- PSD §3.2 primitive table + §6.2 borderline cases
- Open-core tier boundaries research (2026-04-19) — conversación fuente
- Polly GitHub: github.com/App-vNext/Polly
- Pro 1.8.0-pro source: src/Asterisk.Sdk.Pro.Resilience/
