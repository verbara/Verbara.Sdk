# ADR-0030: CloudEvents v1.0 adoption as canonical envelope + domain extensions

- **Status:** Proposed
- **Date:** 2026-04-20
- **Deciders:** Harold Reina
- **Related:**
  - PSD v2: `Asterisk.Platform/docs/specs/2026-04-19-product-strategy-v2.md` §3, §12 (Event Model Technical Reference)
  - ADR-0025 (Push NATS subscribe + loop prevention — envelope-based constraint)
  - ADR-0011 (Push bus in-memory non-durable — tier boundary con Pro.EventStore)
  - ADR-0033 (IEventLog vs IEventStore split)

## Context

El modelo de eventos actual (`PushEvent` + `PushEventMetadata` + `RemotePushEvent`) tiene gaps estructurales identificados en análisis profundo (ver conversación fuente 2026-04-20):

- **No tiene `EventId`** — cualquier escalamiento multi-node/idempotency/dedupe requiere esto primero.
- **No tiene `SchemaVersion`** — consumers no tienen guardrails contra breaking payload changes.
- **No tiene `datacontenttype`** — asume JSON implícito, bloqueando binary formats.
- **No tiene `dataschema`** referencia a schema registry.
- **No tiene `subject`** para fine-grained routing.

Diseñar un envelope custom desde cero para cubrir estos gaps implicaría reinventar un spec que CNCF ya estandarizó: **CloudEvents v1.0** (GA enero 2020).

**CloudEvents es industry-standard:**
- Consumido nativamente por Azure Event Grid, AWS EventBridge, Google Eventarc, Kubernetes Events, Knative, Argo Workflows, Dapr, Keda.
- `.NET` library oficial (`CloudNative.CloudEvents` 2.8+, Apache 2.0, AOT-compatible verificado).
- Transport bindings pre-documented (HTTP, NATS, Kafka, MQTT, WebSockets, AMQP) — no reinventamos.
- Extensions formales (CloudEvents Extension Attributes) permiten domain-specific fields sin hacks.

**Constraint preservado (ADR-0025):** `RemotePushEvent` es envelope opaco con `OriginalEventType` + `RawPayload`. CloudEvents es perfectamente compatible — el CE `type` actúa como `OriginalEventType` y `data` como `RawPayload`.

**UUIDv7 (RFC 9562, mayo 2024):** nativo en .NET 9+ via `Guid.CreateVersion7()`. Supera a ULID en .NET context:
- 16 bytes binario vs ULID 16 bytes: mismo.
- DB-native (Postgres `uuid`, SQL Server `uniqueidentifier`) — ULID requiere string.
- Cero dependencias externas — ULID requiere NuGet lib.
- Ordenable por tiempo (igual que ULID).

## Decision

**Adoptar CloudEvents v1.0 + domain extensions como envelope canónico en SDK v2.0.**

**Wire format:** CloudEvents v1.0 structured mode (JSON serialization). Bindings documented:
- **In-memory (`IPushEventBus`):** `CloudEvent` .NET record directamente.
- **NATS:** CloudEvents NATS Protocol Binding (structured mode — `application/cloudevents+json`).
- **Webhooks:** CloudEvents HTTP Binding (structured mode default).
- **SSE:** CloudEvents HTTP Binding.

**Core CE attributes:** `id` (UUIDv7 string), `source`, `type`, `specversion="1.0"`, `time`, `datacontenttype`, `dataschema?`, `subject?`, `data`.

**Domain extensions (formalmente permitidas):**
- `schemaversion` (int) — version del payload contract.
- `causationid` (UUIDv7 string) — cadena causal.
- `correlationid` (string) — agrupación de eventos de una operación.
- `aggregatetype` (string) — entidad de negocio (CallSession, Conversation, etc.).
- `aggregateid` (string) — instancia.
- `sequencenumber` (long) — secuencia incremental por aggregate.
- `tenantid` (string, null en single-tenant).
- `originnodeid` (string) — loop prevention.
- `hopcount` (int, default 0) — loop prevention.
- `dedupekey` (string, nullable — YAGNI inicial, documented solo si use-case real emerge).
- `payloadencoding` (string: `inline` | `reference-http` | `reference-s3`) — para events grandes.
- `signature` (string, HMAC-SHA256) — multi-tenant trust (Pro only).
- `keyid` (string) — identificador de key HMAC (Pro only).

**UUIDv7 via `Guid.CreateVersion7()`** .NET 9 native — no ULID.

**Dependencies:**
- NuGet: `CloudNative.CloudEvents` 2.8+ (Apache 2.0, AOT-compat).
- Bindings: `CloudNative.CloudEvents.Protobuf`, `CloudNative.CloudEvents.SystemTextJson` según need.

**Migration path (v1.x → v2.0):**
- `PushEventMetadata` se refactoriza como adapter bidireccional a `CloudEvent`.
- `PushEvent` existentes siguen funcionando — publisher-side convierte a CloudEvent antes de ir a transport.
- `RemotePushEvent` ahora transporta `CloudEvent` con `originaltype` + `data`.
- Period coexistencia v2.0 → v2.2. v2.3+ PushEvent legacy `[Obsolete]`. v3.0 removed.

**Observability correlation:** tags obligatorios en spans — `event.id`, `event.type`, `event.source`, `event.schema_version`, `event.aggregate_type`, `event.aggregate_id`, `event.sequence_number`, `event.correlation_id`, `event.causation_id`, `event.origin_node_id`, `tenant.id`, `call.id`/`conversation.id`. Agregar a `AsteriskSemanticConventions.Event.*` (extender catalog shipped en v1.13).

## Consequences

**Positivas:**
- Industry-standard wire format. Integration gratuita con Azure Event Grid, AWS EventBridge, Kubernetes Events.
- Transport bindings pre-documented. No reinventamos NATS/HTTP/Kafka mappings.
- Extensions formales cubren todo lo domain-specific sin hacks.
- `.NET` library oficial + mantenido por CNCF.
- Credibilidad positioning ante enterprise buyers ("usamos CloudEvents").
- UUIDv7 nativo .NET sin deps.
- Forward-compat con CNCF ecosystem.

**Negativas:**
- Dependencia externa (`CloudNative.CloudEvents`) en SDK core — evaluar impacto AOT cuidadosamente.
- ~60 bytes overhead por evento vs minimal custom envelope — trivial (<0.01ms serialization).
- Learning curve para contributors que no conocen CloudEvents spec.
- Migration cost: adapter layer ~200 LOC + tests.

**Mitigación:**
- AOT validation en `AotCanary` tool antes de ship.
- Docs + examples específicos en `docs/guides/event-model.md`.
- Migration guide detallado en `docs/migrations/v1.x-to-v2.0-event-model.md`.

## Alternatives considered

- **Custom `EventEnvelope` record con 20 campos** (propuesta original del diseño): rechazado — reinventa CloudEvents sin ganancia; pierde industry-interop + bindings free.
- **CloudEvents sin extensions (puro spec):** rechazado — no cubre `causationid`/`aggregatetype`/`aggregateid`/`sequencenumber`/`tenantid`. Esencial para event sourcing + multi-tenant.
- **`System.Text.Json` polymorphic types con discriminators:** rechazado por ADR-0025 (anti-reflection, AOT concerns).
- **ULID en lugar de UUIDv7:** rechazado — requiere NuGet lib, no DB-native, menos interop con .NET tooling.
- **Full rewrite sin backward compat:** rechazado — break consumers existentes sin necesidad.

## References

- CloudEvents v1.0 spec: https://github.com/cloudevents/spec
- CloudNative.CloudEvents .NET library: github.com/cloudevents/sdk-csharp
- RFC 9562 UUIDv7: https://datatracker.ietf.org/doc/rfc9562/
- PSD §12 Event Model Technical Reference
- Análisis profundo del modelo de eventos (2026-04-20) — conversación fuente
