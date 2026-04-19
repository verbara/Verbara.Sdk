# ADR-0006: Session storage is a pluggable interface, not a framework requirement

- **Status:** Accepted
- **Date:** 2026-04-18 (retrospective — ships in v1.11.0)
- **Deciders:** Harol A. Reina H.
- **Related:** `docs/guides/session-store-backends.md`, `docs/research/benchmark-analysis.md` §1c

## Context

The Session Engine (`Asterisk.Sdk.Sessions`) tracks the lifetime of a call across every SDK layer: AMI events mutate it, ARI state reflects it, AGI scripts and VoiceAi pipelines attach to it, and dashboards render it. Two distinct deployment profiles need sessions:

- **Single-instance** SDK embedded in a monolith — one process, no clustering, InMemory session map is correct.
- **Multi-instance** SDK across a load-balanced fleet — two SDK nodes holding the two halves of a bridged call need to see each other's sessions.

Historically in v1.0 – v1.10 the SDK shipped only the InMemory store. Users asking "how do I run two SDK nodes behind a load balancer?" got a write-your-own-glue answer.

## Decision

**Session storage is `ISessionStore`**, a small interface (≈12 members) that any backend can implement. We ship three:

- `InMemorySessionStore` — default, zero-dependency, ~0.1 µs operations, correct for single-process.
- `Asterisk.Sdk.Sessions.Redis` — StackExchange.Redis, pipelined batches, TTL-driven retention.
- `Asterisk.Sdk.Sessions.Postgres` — Npgsql + Dapper + JSONB snapshot, UPSERT on conflict, partial index for actives.

Opt-in via `AddAsteriskSessionsBuilder().UseRedis(...)` or `.UsePostgres(...)`. The framework core never requires either — a consumer who never calls those extensions still works.

## Consequences

- **Positive:** The InMemory default stays unreasonably fast (no abstraction tax). Multi-instance consumers pick a backend explicitly; Redis is faster on writes (~79 µs p50) but Postgres is faster on reads with a warm connection pool (~51 µs p50). Durability/auditability is possible without baking Postgres into the core.
- **Negative:** Three codepaths to maintain. Each backend has its own test suite and Testcontainers fixture. Session serialization format (`CallSessionSnapshot`) is now a stable contract that can't be refactored freely.
- **Trade-off:** Extra package count (+2 backends) vs. monolithic dependency on Redis or Postgres.

## Alternatives considered

- **Bake Redis into the core.** Rejected: the InMemory user (Lambda, edge function, single-process PBX integration) should not be forced to install StackExchange.Redis.
- **Bake Postgres into the core.** Rejected: same reasoning; also, the Platform product (commercial) already owns the Postgres schema and we didn't want the SDK to fight it for ownership.
- **Provide only the interface + docs, no shipped backends.** Rejected: "opt-in multi-instance" is a core value-add of v1.11.0 per the Tier A sprint plan. Users need batteries, not a spec.
- **Replicate the InMemory store across instances via gossip.** Rejected as over-engineered for the user count we expect to need multi-instance; Redis/Postgres are industry-standard.

## Notes

- `SessionStoreBase` is a shared `abstract` base that handles `CancellationToken` honoring and metrics (hooked into `AsteriskTelemetry`); backends derive from it.
- AOT-safe via `SessionJsonContext` source-generated JSON serialization.
- Benchmark numbers: `docs/research/benchmark-analysis.md` §1c.
