# ADR-0019: Push bus `TraceContext` ambient capture at publish time

- **Status:** Accepted
- **Date:** 2026-04-18 (retrospective — decision made during the v1.10.2 fix)
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0007 (topic hierarchy on Push bus), ADR-0011 (Push bus in-memory non-durable)

## Context

`Asterisk.Sdk.Push` is built on `System.Threading.Channels`. A publisher drops a `PushEvent<T>` into a bounded channel and returns; a background consumer loop reads from the channel and dispatches to subscribers. That shape is what makes the bus fast and non-blocking for publishers, but it also means that every publish switches execution context between the publisher and the consumer. The publisher's `Activity.Current` — the W3C traceparent that distributed-tracing consumers rely on to correlate spans across boundaries — does not follow the event into the channel.

Concretely: if an API handler starts an `Activity`, publishes an event, and the event is dispatched by the background consumer loop a few milliseconds later, the consumer's `Activity.Current` is whatever span was active when the consumer thread last ran — usually nothing. Downstream subscribers and webhook sinks see their spans terminate at the publish boundary. A consumer wiring OpenTelemetry through `Asterisk.Sdk.Push.Webhooks` expects trace continuity from the originating API call through the webhook POST; without propagation, that expectation breaks silently.

This surfaced in production during v1.10.1's telemetry work. The fix landed in v1.10.2 as commit `bd21271`: capture the ambient `Activity.Current` W3C traceparent at the moment `PublishAsync` is called — inside the publisher's execution context — and attach it to the `PushEventMetadata.TraceContext`. The consumer loop reads the captured traceparent from metadata and re-establishes the context before dispatching to subscribers. Trace continuity is preserved across the Channel hop without coupling the bus to OpenTelemetry or to any specific telemetry provider.

A subtlety worth recording: the capture must happen synchronously inside `PublishAsync` before the awaitable returns, not inside the consumer loop. Moving it into the consumer gives back `Activity.Current = null` because the consumer's SynchronizationContext has already switched by the time it runs. This is easy to get wrong on a simplification pass.

## Decision

`RxPushEventBus.PublishAsync` captures the current W3C traceparent at publish time (synchronously, in the publisher's execution context) and stores it on `PushEventMetadata.TraceContext`. The consumer dispatch loop honours the captured context by setting the ambient parent activity before invoking subscribers. Consumers that inject OpenTelemetry or any `ActivitySource` listener receive continuous traces across the publish-consume boundary without extra wiring.

## Consequences

- **Positive:**
  - W3C distributed tracing works end-to-end across the Push bus with zero consumer configuration.
  - `PushEventMetadata.TraceContext` is optional — callers without an active `Activity.Current` at publish time produce events without trace context, and the consumer dispatches them as root spans. No null-reference hazard, no required dependency on OpenTelemetry.
  - The fix is localized to `RxPushEventBus`; subscribers and delivery services (webhooks, NATS, future backplanes) consume the already-propagated context without changes.
  - Observability: `AsteriskTelemetry.MeterNames` (13 meters as of v1.11.1) includes `Asterisk.Sdk.Push.Webhooks` delivery counters that are traceable back to their origin span through this mechanism.
- **Negative:**
  - The capture is an implicit contract: call sites do not signal that propagation is happening, and a cleanup refactor that moves capture into the consumer loop silently breaks it. The unit test `RxPushEventBus.PublishAsync_ShouldPreserveTraceContext_WhenActivityIsAmbient` is the sole guardrail.
  - Storing a traceparent string on every event costs one small string allocation per publish. At the bus's target throughput this is well under the backpressure threshold and has not shown up in benchmarks.
- **Trade-off:** We trade a small allocation per event for end-to-end trace continuity. The alternative — let tracing break at the bus boundary and ask consumers to re-establish context manually — fails closed in the sense that traces are simply incomplete rather than wrong, but it forces every consumer deployment to solve the same problem individually. Solving it once in the bus is the better product shape. The audit in [`docs/research/2026-04-19-product-alignment-audit.md`](../research/2026-04-19-product-alignment-audit.md) §4 item #9 flagged this as a silent-regression candidate.

## Alternatives considered

- **Read `Activity.Current` from inside the Channel consumer** — rejected because the consumer runs on a different execution context by the time the event is dequeued; `Activity.Current` is typically `null` there, and the traceparent is lost.
- **Require callers to pass `TraceContext` explicitly** — rejected because it forces every call site to know about distributed tracing, which defeats the purpose of an ambient propagation mechanism. Consumers without tracing would pay an ergonomic cost for a feature they do not use.
- **Wrap the Channel in `System.Diagnostics.ActivitySource.CreateActivity` at the boundary** — rejected because creating a new Activity changes the span shape (extra synthetic span per event); the W3C traceparent propagation model is meant to carry an existing context, not synthesize a new one per hop.
- **OpenTelemetry context propagation via `Baggage` or `Propagators.DefaultTextMapPropagator`** — rejected because it binds the bus to the OpenTelemetry SDK as a hard dependency. W3C traceparent on `Activity` is built into the BCL and works whether or not OpenTelemetry is present.
