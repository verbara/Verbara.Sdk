# Architecture Decision Records (ADRs)

Append-only log of load-bearing architectural decisions — the **why**, not the **how**.

## When to add an ADR

Write an ADR when a decision:

- Constrains or shapes future work.
- Was debated (multiple options evaluated, one chosen).
- Would be surprising to a new engineer reading the code 6 months from now.
- Rules out a path that might look attractive later ("why don't we just…?").

Do **not** write an ADR for obvious or trivial choices; that's what code and commit messages are for.

## File convention

`{NNNN}-{kebab-case-title}.md` — sequential 4-digit prefix, starting at `0001`.

Status values: `Proposed` · `Accepted` · `Superseded by ADR-XXXX` · `Deprecated`.
Once `Accepted`, never edit the body — supersede with a new ADR that references this one.

## Template

```markdown
# ADR-NNNN: {Title}

- **Status:** Proposed | Accepted | Superseded by ADR-XXXX
- **Date:** YYYY-MM-DD
- **Deciders:** {names or role}
- **Related:** ADR-XXXX, spec file, plan file

## Context
What problem are we solving? What forces / constraints are in play?

## Decision
The decision, stated in one or two sentences.

## Consequences
- Positive: …
- Negative: …
- Neutral / trade-off: …

## Alternatives considered
- **Option B:** … — rejected because …
- **Option C:** … — rejected because …
```

## Catalog

- [ADR-0001](0001-native-aot-first.md) — Target Native AOT from day one for zero runtime reflection.
- [ADR-0002](0002-open-core-mit-plus-pro.md) — MIT SDK as public core; commercial features ship in a separate private `Verbara.Sdk.Pro` repo.
- [ADR-0003](0003-source-generators-over-reflection.md) — Use Roslyn source generators for AMI/ARI/JSON (de)serialization instead of runtime reflection.
- [ADR-0004](0004-central-package-management.md) — All NuGet versions pinned in `Directory.Packages.props` with `TreatWarningsAsErrors=true`.
- [ADR-0005](0005-testcontainers-for-integration.md) — Docker-backed Testcontainers is the integration-test substrate; no in-process PBX mocks for functional tests.
- [ADR-0006](0006-pluggable-session-stores.md) — Session storage is an `ISessionStore` interface with InMemory/Redis/Postgres implementations; multi-instance is opt-in, not a framework requirement.
- [ADR-0007](0007-topic-hierarchy-push-bus.md) — Real-time push uses a hierarchical topic tree (`TopicName` + `TopicPattern`, `**` + `{self}` wildcards) with HMAC-signed webhook delivery.
- [ADR-0008](0008-ami-exponential-backoff.md) — AMI reconnection uses deterministic exponential backoff (no jitter, no Polly) for determinism + zero dependencies.
- [ADR-0009](0009-three-tier-test-strategy.md) — Three-tier test pyramid: Unit (no Docker) + Integration (Testcontainers) + Functional (live Asterisk, Layer2/Layer5).
- [ADR-0010](0010-ari-asymmetric-transport.md) — `AriClient` mirrors Asterisk's native split: one `ClientWebSocket` for events, one `HttpClient` for REST commands.
- [ADR-0011](0011-push-bus-in-memory-non-durable.md) — Push bus is in-memory fire-and-forget with bounded `Channel<T>`; durability/federation lives in Pro.
- [ADR-0012](0012-live-aggregate-root-orthogonal.md) — `Verbara.Sdk.Live` is a separate package owning domain state; AMI + ARI are data sources, not owners.
- [ADR-0013](0013-isessionhandler-abstraction.md) — `ISessionHandler` is the single VoiceAi dispatch seam; turn-based pipeline and OpenAI Realtime bridge are swappable at DI time.
- [ADR-0014](0014-raw-http-websocket-voiceai-providers.md) — VoiceAi providers ship as hand-rolled `HttpClient` / `ClientWebSocket` code; no vendor SDKs (AOT-incompatible).
- [ADR-0015](0015-ami-string-interning-pool.md) — AMI protocol reader uses a 2048-bucket FNV-1a string pool pre-computed with 941 keys + 35 values; zero-alloc on the hot path.
- [ADR-0016](0016-voiceai-provider-name-override.md) — VoiceAi providers override `ProviderName` with a `const string` instead of relying on `GetType().Name`; ~92× speedup on the telemetry hot path.
- [ADR-0017](0017-audiosocket-codec-negotiation.md) — AudioSocket sessions negotiate codec (slin16 / ulaw / alaw / gsm) per-connection from the first inbound frame; no hard-coded or configuration-driven codec.
- [ADR-0018](0018-sessions-reconciliation-soft-ttl.md) — Session lifetime is managed by an in-app `SessionReconciliationService` sweep loop (heartbeat-based), not by native backend TTL features.
- [ADR-0019](0019-push-bus-trace-context-capture.md) — `RxPushEventBus.PublishAsync` captures the ambient W3C traceparent at publish time so distributed tracing survives the Channel hop.
- [ADR-0020](0020-webhook-delivery-no-durable-dlq.md) — Webhook delivery is bounded-retry with a `deliveries.dead_letter` counter; no durable dead-letter queue in the MIT package (durability is Pro SDK territory).
- [ADR-0021](0021-ami-heartbeat-strategy.md) — AMI heartbeat is enabled by default at 30 s interval / 10 s timeout; application-level ping separates "idle" from "half-open connection".
- [ADR-0022](0022-activity-cancellation-semantics.md) — `IActivity.CancelAsync()` is a first-class method alongside `CancellationToken`; consumers observe terminal outcomes through `Status`, not exceptions.
- [ADR-0023](0023-publicapi-tracker-adoption.md) — Every shipping package carries `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt`; breaking API changes cannot merge silently.
- [ADR-0024](0024-bannedsymbols-as-aot-policy.md) — `BannedSymbols.txt` + `Microsoft.CodeAnalysis.BannedApiAnalyzers` enforce the AOT policy at build time (no reflection, no `DateTime.Now`).
- [ADR-0025](0025-push-nats-subscribe-and-loop-prevention.md) — `Verbara.Sdk.Push.Nats` subscribe side: `source` header loop prevention + `RemotePushEvent` envelope.
- [ADR-0026](0026-product-identity-runtime-not-sdk.md) — Product identity is "Verbara Runtime for .NET" (not "SDK") in user-facing copy.
- [ADR-0027](0027-stewardship-pledge-mit-commercial.md) — Stewardship pledge: "Primitives stay MIT. Forever."
- [ADR-0028](0028-cadence-v1-preview-v2-stable.md) — Cadence commitment: v1.x preview series, v2.0 stable Q4 2026.
- [ADR-0029](0029-resilience-primitives-mit.md) — Resilience primitives (`BackoffSchedule`, `RetryBudget`) move from Pro to SDK (MIT).
- [ADR-0030](0030-cloudevents-v1-adoption.md) — CloudEvents v1.0 adoption as canonical envelope + domain extensions.
- [ADR-0031](0031-domain-vs-integration-events.md) — Domain events vs Integration events: namespace convention + stability guarantees.
- [ADR-0032](0032-events-not-commands.md) — Event bus transports facts only; commands use a separate `ICommandDispatcher`.
- [ADR-0033](0033-eventlog-sdk-eventstore-pro-split.md) — `IEventLog` (SDK MIT) vs `IEventStore` (Pro): tier split.
- [ADR-0034](0034-isessioninterceptor-public-contract.md) — `ISessionInterceptor` public contract replaces `InternalsVisibleTo Pro.Cluster` leak.
- [ADR-0035](0035-cos-deferred-customer-driven.md) — COS (Calling Permissions System) deferred: customer-driven trigger only.
- [ADR-0036](0036-rebrand-to-verbara.md) — Rebrand product family from `Asterisk.Sdk` to **Verbara Sdk** for trademark safety (Sangoma owns "Asterisk" trademark). License unchanged (MIT). (Accepted, 2026-05-03)
- [ADR-0037](0037-cross-repo-adr-reference-convention.md) — Cross-repo ADR reference convention: bare `ADR-NNNN` = this-repo, `Platform/ADR-NNNN` / `Pro/ADR-NNNN` / `Web/ADR-NNNN` for sister-repo references. Disambiguates the `Platform/ADR-0022` references in this repo's v2.2.x commit history. (Accepted, 2026-05-23)
