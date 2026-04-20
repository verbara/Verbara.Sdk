# ADR-0025: `Asterisk.Sdk.Push.Nats` subscribe side — `source` header loop prevention + `RemotePushEvent` envelope

- **Status:** Accepted
- **Date:** 2026-04-20
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0007 (topic hierarchy on Push bus), ADR-0011 (Push bus in-memory non-durable), ADR-0019 (Push bus `TraceContext` ambient capture), ADR-0020 (Webhook delivery retry-only, no durable DLQ)

## Context

`Asterisk.Sdk.Push.Nats` shipped in v1.12.0 as a publish-only bridge: the local `RxPushEventBus` was observed, every event was re-serialized to NATS on a subject derived from the topic hierarchy, and that was the end of the story on each node. Multi-node deployments got one half of the fan-out story — a dashboard running on Node A could not see events generated on Node B, because Node B's in-memory bus never received anything from NATS.

The subscribe side was deliberately deferred. It introduces four design questions that are cheap to get wrong and expensive to change later:

1. **Loop prevention.** If Node A publishes event *E* and Node B is subscribed to the same subject tree, Node B's bridge receives *E*, re-publishes to its local bus, and then — if its publish-to-NATS path is also active — re-emits *E* to NATS. Node A's bridge would then receive *E* back. Two bridged nodes with no loop guard produce an infinite republish storm. This is not optional.
2. **Event-type round-trip.** `PushEvent` is abstract. Concrete subclasses live in consumer code, vary per product, and cannot be enumerated by the SDK without reflection. A naive "deserialize back to the originating concrete type" design requires either consumer-registered type maps (non-trivial API surface) or reflection (breaks AOT). Either direction expands the MIT surface considerably.
3. **Queue-group semantics.** NATS supports two subscription modes: plain pub/sub (every subscriber on a subject gets every message) and queue groups (exactly one subscriber per group gets each message — the NATS equivalent of a work queue). Which default matches the Push bus contract?
4. **Durability.** JetStream would let the SDK offer at-least-once delivery across restarts with consumer-managed acks. But ADR-0011 drew a clear line: durability is Pro territory. Adding JetStream to MIT reopens that boundary.

The v1.13 roadmap scoped this as T2 ("Tier 2 — Push.Nats subscribe side"). The design that ships needs to preserve all four constraints without quietly adding a storage dependency or a reflection-based deserialization path.

## Decision

### 1. Loop prevention via a `source` header in the envelope

`DefaultNatsPayloadSerializer` now emits an optional `"source":"nodeId"` field in the JSON envelope when `NatsBridgeOptions.NodeId` is configured. The subscribe side compares each incoming message's `source` against its own configured `NodeId`; if they match *and* `NatsSubscribeOptions.SkipSelfOriginated` is `true` (default), the message is dropped and counted as `EventsSkipped`.

The `source` field is optional — deployments that run publish-only (v1.12 shape) do not set `NodeId`, the serializer omits the field, and on-the-wire messages remain byte-identical to v1.12. Upgrading one node at a time is safe.

A second belt-and-suspenders guard runs inside `NatsBridge.DispatchAsync`: a `RemotePushEvent` never triggers a publish-to-NATS. This protects against scenarios where `NodeId` was forgotten on one side or where two nodes share the same `NodeId` by misconfiguration — the remote event still short-circuits the outbound path based on its .NET type, not just its wire-level header.

### 2. `RemotePushEvent` envelope — no concrete-type round-trip in MIT

The subscribe path materializes incoming messages as a new public record:

```csharp
public sealed record RemotePushEvent(
    string OriginalEventType,
    string? SourceNodeId,
    byte[] RawPayload) : PushEvent
{
    public override string EventType => OriginalEventType;
    public override PushEventMetadata Metadata { get; init; } = null!;
}
```

The local bus receives `RemotePushEvent` instances. Existing subscribers that filter by `EventType` string (the standard routing pattern — used by `Asterisk.Sdk.Push.Webhooks`, `Asterisk.Sdk.Push.AspNetCore` SSE, and typical consumer dashboards) continue to work without change: they see `remoteEvent.EventType == "calls.inbound.started"` regardless of whether the event originated locally or came over NATS.

Consumers who need access to event-specific fields on the receive side have two options: implement a custom `INatsPayloadDeserializer` that knows their concrete types (the extension point is public and consumer-owned), or deserialize `RawPayload` JSON themselves downstream of the bus. Neither path is forced on MIT consumers, and neither expands the SDK surface.

### 3. Queue-group defaults — pub/sub, work-queue is opt-in

`NatsSubscribeOptions.QueueGroup` defaults to `null` (plain pub/sub — every subscribing node receives every message). This matches the local `RxPushEventBus` fan-out contract (ADR-0011: every observer sees every event). Consumers who want work-queue semantics — typical use case: a pool of webhook delivery workers where exactly one should POST to the external endpoint per event — set `QueueGroup` to a stable string (all workers in the pool use the same value) and NATS handles the single-delivery partitioning.

### 4. No JetStream, no durable replay

JetStream is not wired into `NatsBridge` and not exposed through `NatsBridgeOptions`. ADR-0011 stands: durability, at-least-once, cross-restart replay — all of these live in `Asterisk.Sdk.Pro` (private). Consumers who need those semantics with NATS as the transport use Pro's backplane. The MIT bridge stays core-NATS fire-and-forget.

## Consequences

- **Positive:**
  - Multi-node fan-out works with zero persistence dependency and zero changes to how consumers filter events (`EventType` string lookups keep working). Dashboards that previously saw only local events now see the cluster.
  - Upgrade path is safe: v1.12 publish-only nodes interoperate with v1.13 bidirectional nodes on the same NATS bus. Downgrade is also safe — the v1.13 envelope's additional `source` field is ignored by v1.12 readers (they never read the envelope; they only produce it).
  - Loop prevention is layered: wire-level `source` + .NET-type guard in dispatch. Either alone catches misconfiguration.
  - The `INatsPayloadDeserializer` extension point lets consumers with concrete-type requirements build exactly what they need without the SDK having to guess at shape.
  - Three new counters (`EventsReceived`, `EventsSkipped`, `EventsDecodeFailed`) give operators a legible view of the receive path mirroring the existing `EventsPublished` / `EventsFailed` on the send side.
- **Negative:**
  - `RemotePushEvent` is a weaker type than the original. A consumer that expected to `switch (evt) { case MyChannelHangup hangup: ... }` on remote events will find the cast path doesn't exist; they'll need to inspect `OriginalEventType` string or `RawPayload` bytes. The SDK trades this ergonomic hit for a smaller API surface.
  - Shared `NodeId` across nodes (misconfiguration — two hosts accidentally configured identically) silently drops events that arrive from the misidentified peer. The .NET-type guard still prevents the loop, but the event does not reach the bus. The `EventsSkipped` counter surfaces the symptom; operators diagnose via logs.
  - Plain pub/sub without queue groups means N subscribers = N copies of each event delivered. For 10-node deployments that's fine; for 1000-node it's a load multiplier. Consumers at scale should either (a) configure `QueueGroup` on delivery-side workloads (webhooks) or (b) move to Pro's backplane. The ADR does not auto-detect scale.
- **Trade-off:** We trade the rich "round-trip to original concrete type" experience for an AOT-clean, zero-storage, boundary-respecting bridge. The `RemotePushEvent` envelope is the contract consumers read; anything beyond that is consumer code.

## Alternatives considered

- **Source-generated type registry for concrete-event deserialization.** Rejected. Would require consumers to maintain a `JsonSerializerContext` listing every `PushEvent` subclass they want round-tripped; any forgetten type silently becomes a deserialization failure. The SDK would also have to surface a registration API (`RegisterPushEventType<T>()` or similar), which is exactly the kind of mid-level API the MIT package tries to avoid when a weaker envelope suffices.
- **Content-hash dedupe as the loop-prevention mechanism.** Rejected. Content hashing requires O(N)-sized working memory per node to remember recent message hashes; without storage, the window is fixed and flaps. `source`-based filtering is O(1) and deterministic.
- **NATS message headers for the source marker instead of the JSON envelope.** Considered. NATS does support typed headers on published messages. Rejected for v1.13 because it would fork the wire format between v1.12 payloads (plain JSON) and v1.13 (JSON + headers), breaking interop on the same bus. Putting `source` in the envelope JSON keeps one wire format and makes the field inspectable through any tool that can read the payload.
- **JetStream with durable consumer subscriptions.** Rejected. ADR-0011 is the relevant boundary; durability lives in `Asterisk.Sdk.Pro`.
- **Mandatory `NodeId`.** Considered — making `NodeId` required would make `SkipSelfOriginated` impossible to misconfigure. Rejected because a deployment that does not need loop prevention (single-node with one subscriber, or publish-only asymmetric topology) should not be forced to invent an identifier. `NodeId` stays optional; `SkipSelfOriginated` without `NodeId` is documented as a no-op and surfaced as an options-validation warning.
- **Auto-generated `NodeId` from `Environment.MachineName + Process.Id`.** Considered. Rejected because machine/process identity is unstable across container restarts (rolling deployments produce a constant churn of new IDs), which defeats the point of a stable dedupe marker. Explicit `NodeId` configuration is the only reliable choice for a deployment that needs dedup semantics.

## Notes

- Code: `src/Asterisk.Sdk.Push.Nats/NatsBridge.cs` subscribe loop + dispatch guard; `src/Asterisk.Sdk.Push.Nats/DefaultNatsPayloadSerializer.cs` optional `source` field; `src/Asterisk.Sdk.Push.Nats/DefaultNatsPayloadDeserializer.cs` `Utf8JsonReader`-based envelope parser.
- Metrics: `NatsMetrics.EventsReceived`, `EventsSkipped`, `EventsDecodeFailed` under the `Asterisk.Sdk.Push.Nats` meter.
- Public API: `Asterisk.Sdk.Push.Events.RemotePushEvent`, `Asterisk.Sdk.Push.Nats.NatsSubscribeOptions`, `Asterisk.Sdk.Push.Nats.INatsSubscriber`, `Asterisk.Sdk.Push.Nats.INatsPayloadDeserializer`, `Asterisk.Sdk.Push.Nats.NatsBridgeOptions.NodeId`, `Asterisk.Sdk.Push.Nats.NatsBridgeOptions.Subscribe`.
- Tests: `Tests/Asterisk.Sdk.Push.Nats.Tests/NatsBridgeSubscribeTests.cs` (fake `INatsSubscriber`); `Tests/Asterisk.Sdk.Push.Nats.IntegrationTests/NatsBridgeBidirectionalTests.cs` (two bridges on one Testcontainers NATS server, asserts cross-node delivery + zero self-loops).
