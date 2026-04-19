# ADR-0020: Webhook delivery retry-only, no durable dead-letter queue

- **Status:** Accepted
- **Date:** 2026-04-18 (retrospective — decision made during the v1.11.0 Push.Webhooks introduction)
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0007 (topic hierarchy on Push bus), ADR-0011 (Push bus in-memory non-durable), ADR-0019 (Push bus `TraceContext` capture)

## Context

`Asterisk.Sdk.Push.Webhooks` is the outbound-webhook sink for the Push bus: it subscribes to topics, signs payloads with HMAC, and POSTs them to consumer-configured URLs. Webhooks fail in the real world — transient network errors, 5xx responses, target endpoints temporarily offline. A delivery service must decide what to do with failures.

Two designs were on the table. The first is "retry-only": bounded retry with exponential backoff, and when retries exhaust, increment a `deliveries.dead_letter` counter and drop the event. The second is "retry plus durable DLQ": persist failed events to a durable store (Redis, Postgres, filesystem) and surface an admin API to replay them.

The second looks more professional and checks a box labelled "at-least-once delivery". But it has concrete costs that do not pay for themselves in the open-core SDK:

- **Durability is a storage dependency.** The DLQ needs a persistent store, which means a backend choice (Redis? Postgres?), operational ownership (who provisions it? who monitors it?), and a data-retention policy (how long do we keep dead letters?). Each of those decisions has consequences for every consumer that installs the package.
- **DLQ replay is an API surface.** If dead letters are durable, consumers will want to list them, replay them, delete them, reason about them. That is a non-trivial admin feature set that belongs in a product, not in a protocol sink.
- **Durability is what Pro SDK owns.** ADR-0011 already established that durability, federation, and backplanes live in the private `Asterisk.Sdk.Pro` repo. Adding a durable DLQ to the MIT package would cross that boundary.
- **"Dead-letter counter" is still useful.** The `WebhookMetrics.deliveries_dead_letter` counter, combined with `succeeded`, `failed`, and `retried`, gives operators a legible signal about delivery health without a persistent store. A consumer can page on the counter, alert on the ratio, and take action — all without the SDK having to own the dead-letter data.

The right shape for the MIT package is to do retry correctly and surface metrics; anything beyond that is either a consumer integration (connect the counter to an incident-response workflow) or a Pro SDK feature (durable replayable DLQ).

## Decision

`WebhookDeliveryService` implements bounded retry with exponential backoff configured through `WebhookDeliveryOptions` (max attempts, initial delay, multiplier, max delay). When retries exhaust without success, the service increments the `deliveries.dead_letter` counter and drops the event. No persistent dead-letter store, no replay API. Operators page on the dead-letter counter and fix the delivery path at the consumer side.

## Consequences

- **Positive:**
  - `Asterisk.Sdk.Push.Webhooks` has zero storage dependencies. Installing the package does not require provisioning Redis, Postgres, or a filesystem mount.
  - The metrics surface (`deliveries.succeeded`, `deliveries.failed`, `deliveries.retried`, `deliveries.dead_letter`, all part of the `Asterisk.Sdk.Push.Webhooks` Meter registered in `AsteriskTelemetry.MeterNames`) gives operators the signal they need without a durability commitment.
  - The package stays consistent with ADR-0011 (Push bus non-durable) and ADR-0007 (topic hierarchy stays protocol-concern). Durability is a coherent Pro SDK boundary.
  - Retry policy is fully configurable per consumer; a consumer that needs harder guarantees can tune attempts higher or wrap the delivery service with a consumer-side persistence layer.
- **Negative:**
  - A consumer who assumes "dead_letter" means durable and builds a compliance workflow on top of the counter will be surprised on host restart: in-flight retries are lost, and the counter resets. The documentation must be clear that the counter is an operational signal, not a durability promise.
  - Retry storms against a fully offline target can fill the Push bus channel and trigger backpressure. The retry policy's max-delay knob bounds this, but a misconfigured deployment can still produce noisy retry traffic.
- **Trade-off:** We trade the compliance-ticking benefit of a durable DLQ for an SDK that is deployable without infrastructure prerequisites. Durability is not free — it has an ops cost and an admin-API cost — and the Pro SDK is the right place to pay those costs for consumers who need them. The audit in [`docs/research/2026-04-19-product-alignment-audit.md`](../research/2026-04-19-product-alignment-audit.md) §4 item #10 flagged this as a boundary decision that must be documented so consumers do not build compliance workflows on the retry-only counter.

## Alternatives considered

- **Persistent dead-letter queue with at-least-once guarantees (Redis/Postgres-backed)** — rejected because durability is ADR-0011's Pro SDK boundary; adding it to the MIT package crosses a product line deliberately drawn elsewhere. It also adds a storage dependency and a replay API surface that the MIT package should not own.
- **Filesystem-backed DLQ (JSONL appends)** — rejected because it creates an operational artefact (the DLQ file) that nobody is accountable for — rotation, cleanup, permissions, replay are all unsolved and would become consumer issues. "Just write a file" is the design that sounds easiest but is hardest to maintain correctly.
- **Infinite retry until success** — rejected because it converts every permanently-unreachable endpoint into a channel-filling retry loop that blocks other deliveries. Bounded retry with a dead-letter counter is the correct failure mode.
- **Delegate DLQ to a user-supplied `IWebhookDeadLetterSink`** — considered but rejected for v1.11.0 to keep the MIT surface minimal; a future version can add the extension point if a real consumer need emerges. The `deliveries.dead_letter` counter already lets a consumer implement an out-of-band sink (e.g. a background hosted service that subscribes to the Push bus and writes failed events to its own store) without the SDK owning the extension.
