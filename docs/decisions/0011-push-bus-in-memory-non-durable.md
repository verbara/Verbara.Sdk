# ADR-0011: Push event bus is in-memory, non-durable, fire-and-forget

- **Status:** Accepted
- **Date:** 2026-04-19 (retrospective)
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0001 (AOT-first), ADR-0007 (topic hierarchy)

## Context

`Asterisk.Sdk.Push` is the SDK's real-time event fan-out primitive (ADR-0007 describes the topic tree). The question for the bus itself: **what delivery guarantees?**

Three positions on the spectrum:

1. **Durable outbox** — events written to Postgres/Redis/Kafka before dispatch; guaranteed delivery across restarts; at-least-once semantics.
2. **Reliable queue** — in-process queue with persistent spill-over + durable subscribers; at-most-once per instance.
3. **In-memory fan-out** — `Channel<T>` bounded buffer; fire-and-forget; no replay; events lost if published during a crash or a slow subscriber's drop window.

Asterisk itself emits events at O(10K/s) per PBX under load. A durable outbox has real cost: each event needs a DB write, and "guaranteed delivery" for a dropped DTMF event 3 minutes later is usually less useful than being fast right now.

## Decision

The MIT push bus is **in-memory, non-durable, fire-and-forget**. Implementation is `RxPushEventBus` backed by `System.Threading.Channels.Channel<T>` with bounded capacity (default 10,000) and a configurable overflow strategy (`DropOldest` / `DropNewest` / `Wait`).

Subscribers get `IObservable<PushEvent>`. If a subscriber is slow enough to cause buffer overflow, events are dropped per the overflow strategy; we emit a metric (`push.dropped`) so consumers can alert.

**Pro adds the durability layer separately**: `Asterisk.Sdk.Pro.Push` (private repo) provides a backplane for federated delivery across instances, durable outbox, and at-least-once semantics. The MIT bus publishes through the same `IPushEventBus` interface; Pro's backplane is a decorator.

## Consequences

- **Positive:** Zero dependencies (just `System.Threading.Channels`). Zero latency tax (no disk/network on the publish path). Bounded memory (configurable cap). AOT-clean. The 90% use case — live dashboards, agent presence updates, DTMF echo — is correctly served. Webhook delivery (`Asterisk.Sdk.Push.Webhooks`) adds its own retry + dead-letter semantics on top for the "must-deliver" subset.
- **Negative:** Events published during a crash are lost. Events dropped by overflow are lost. A subscriber that crashes between receiving and acting on an event has no replay. Multi-instance consumers need Pro for fan-out across processes.
- **Trade-off:** We accept "fast fan-out with visibility" over "guaranteed but expensive delivery." The MIT SDK is opinionated: durable queue semantics are a separate concern, not a framework default.

## Alternatives considered

- **Durable outbox by default** — rejected because it adds a hard dependency (Postgres or Redis) to every push consumer, even those running on Lambda/single-node. Violates ADR-0001's AOT-first, zero-surprise contract.
- **Reliable queue with disk spill** — rejected because .NET doesn't ship a good AOT-clean option; bringing one in (BrighterMessaging, MassTransit) would 10× the dependency graph for a marginal gain.
- **At-least-once semantics with ack/nack** — rejected because ack-based protocols require round-trips that don't fit the "observer subscribes once, receives forever" API. Push is not a command bus.
- **No backpressure (unbounded Channel)** — rejected because an unbounded queue with a slow subscriber eats all memory. The bounded + metric-emitting drop is loud but bounded.

## Notes

- Code: `src/Asterisk.Sdk.Push/Bus/RxPushEventBus.cs` lines 8–55 (Channel + dispatch loop).
- Metrics: `AsteriskTelemetry.PushMeter` emits `push.events.published`, `push.events.delivered`, `push.events.dropped`.
- Webhook transport (`Asterisk.Sdk.Push.Webhooks`) adds retry, exponential backoff, dead-letter queue on top — that's the "must-deliver" escape hatch for a specific subset of events.
- Pro backplane (private repo) is the answer for multi-instance at-least-once; it decorates the same `IPushEventBus`.
