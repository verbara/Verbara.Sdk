# ADR-0031: Domain events vs Integration events — namespace convention + stability guarantees

- **Status:** Proposed
- **Date:** 2026-04-20
- **Deciders:** Harold Reina
- **Related:**
  - PSD v2: `Asterisk.Platform/docs/specs/2026-04-19-product-strategy-v2.md` §12.3
  - ADR-0030 (CloudEvents adoption — provides envelope shape)
  - ADR-0028 (cadence commitment — defines stability windows)

## Context

Actualmente el repo trata todos los eventos igual. No distingue entre:

- **Domain events:** internos a un bounded context. Ej: `SessionStateChangedEvent` consumido por CallAnalytics dentro del mismo proceso Pro. Cambios frecuentes son OK — solo internal consumers.
- **Integration events:** cross-bounded-context. Ej: `ConversationAssignedEvent` consumido por un external webhook de cliente. Cambios rompen compromisos externos.

**Sin esta distinción:**

- Developers tratan todo como integration → over-versioning de eventos internos, lentitud evolutiva, cognitive overhead innecesario.
- O tratan todo como domain → breakage silencioso de consumers externos, trust loss.

Es un pattern arquitectónico establecido (Eric Evans DDD, Vaughn Vernon, Microservices patterns) pero que requiere **convención explícita + tooling** para ser respetado.

Con CloudEvents `type` field (ADR-0030) como string semántico, la convención se codifica en el naming.

## Decision

**Establecer convención de namespace en `type` attribute del CloudEvent:**

| Namespace pattern | Origen | Audience | Estabilidad |
|---|---|---|---|
| `asterisk.domain.*` | SDK (MIT) internal | Consumers within Asterisk.Sdk runtime | **Breakable en minors** con migration notes en CHANGELOG |
| `asterisk.integration.*` | SDK (MIT) cross-boundary | External (Pro, Platform, client apps) | **Semver strict. 6-month deprecation window.** |
| `pro.domain.*` | Pro internal | Consumers within Asterisk.Sdk.Pro | Breakable en Pro minors |
| `pro.integration.*` | Pro cross-boundary | External (Platform, client apps) | **Semver strict. 6-month deprecation.** |
| `platform.integration.*` | Platform → external consumers | Tenant apps, webhook receivers | **Semver strict. 6-month deprecation.** |

**Reglas:**

1. **Event naming:** `<scope>.<domain-or-integration>.<aggregate>.<verb-past>`. Ej:
   - `asterisk.domain.call.started`
   - `asterisk.integration.call.completed`
   - `pro.domain.dialer.campaign.started`
   - `pro.integration.cluster.node-joined`
   - `platform.integration.conversation.assigned`

2. **Integration events son subset curado.** No todo domain event debe ser integration event. Publishers cross-boundary opt-in mediante `IIntegrationEventPublisher` (vs default `IEventLog.AppendAsync` para domain).

3. **Translation layer:** integration events a menudo se derivan de domain events (ADR-0032 no-reply pattern). Un domain event → 0-1 integration event.

4. **Breaking changes:**
   - Domain event: bump `schemaversion` extension (CE field), document en CHANGELOG, no migration window required.
   - Integration event: bump `schemaversion`, deprecate old version por 6 meses, document migration guide.
   - Removal de integration event: semver major only.

5. **Cross-tier events:** `pro.*` consumes `asterisk.*` eventos; `platform.*` consume `pro.*` + `asterisk.integration.*` eventos.

**Enforcement:**
- PR review checklist: new event type debe tener namespace correcto.
- Runtime check: `IEventLog.AppendAsync` valida que `type` coincide con `source` origin (ej: source=`sdk.sessions` solo puede publicar `asterisk.domain.*` o `asterisk.integration.*`).

## Consequences

**Positivas:**
- Stability guarantees diferenciadas permiten evolution velocity alta para internal events.
- Integration events explícitos: sellable como contracts a enterprise clients (webhooks semver-stable).
- Disciplina previene accidental breakage cross-boundary.
- Simplifica routing: regex `type ~= "platform.integration.*"` captura todos los eventos externos.

**Negativas:**
- Overhead cognitivo: developers deben decidir domain vs integration upfront.
- Renaming posterior (si un domain event merece integration promotion) requiere new event type + deprecation del old.

**Mitigación:**
- Guía clara en `docs/guides/event-model.md` con ejemplos.
- Template examples en `Examples/` repo.
- Default seguro: si hay duda, **publish como domain**. Promotion a integration requiere criterio explícito ("debe ser consumido por external app").

## Alternatives considered

- **No distinguir:** rechazado — conduce a los 2 anti-patterns (over-version internos o silently-break externos).
- **Visibility modifier en el envelope** (ej: `visibility: internal|public`): rechazado — tercera capa de metadata innecesaria si namespace lo captura.
- **Separate event buses** (`IInternalEventBus` + `IPublicEventBus`): rechazado — over-engineering; single bus con namespace convention es suficiente.

## References

- PSD §12.3 namespace convention
- Domain-Driven Design (Eric Evans) — ubiquitous language per bounded context
- Microservices Patterns (Chris Richardson) — integration event pattern
