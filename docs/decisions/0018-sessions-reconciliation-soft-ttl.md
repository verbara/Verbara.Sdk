# ADR-0018: Sessions reconciliation loop (soft TTL in-app, not native backend TTL)

- **Status:** Accepted
- **Date:** 2026-04-13 (retrospective — decision made during the v1.7.0 Sessions completion sprint)
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0006 (pluggable session stores), ADR-0011 (Push bus in-memory non-durable)

## Context

`Asterisk.Sdk.Sessions` owns the domain state for a call, an agent, or a queue as it moves through the PBX. Sessions have a natural lifetime — a call ends, an agent logs out — and the SDK must reclaim storage when they expire. Two structurally different designs were evaluated during the v1.7.0 sprint.

The first is the "native TTL" design: configure Redis `EXPIRE` keys or a Postgres TTL column, and let the backend reclaim expired rows. It is the obvious move for someone familiar with either store. It also removes any sweep loop from the SDK, so the application process has less to do.

The second is the "soft TTL" design: keep `ISessionStore` storage contract neutral (no TTL surface), and run a per-application `SessionReconciliationService` that periodically sweeps sessions whose last-heartbeat timestamp is older than the configured threshold. Every session writes its last-heartbeat on activity; the reconciliation loop checks those stamps against a `PeriodicTimer` sweep and evicts stale sessions explicitly.

The first design is simpler to wire up for any single store. The second is simpler to reason about across stores. What tilted the decision is a property of multi-node contact centers: when a node crashes, its sessions become orphaned — not expired. The last-heartbeat timestamps are frozen at the crash point, which is typically far inside the TTL window. A native-TTL backend keeps those rows alive until the clock runs out, during which time the remaining nodes see "active" sessions that nobody owns. A reconciliation loop evicts them at the next sweep because the heartbeat is the source of truth, not the key's existence.

The second property that mattered is pluggability. ADR-0006 makes `ISessionStore` a stable seam with three implementations today (InMemory, Redis, Postgres) and a roadmap for more. Pushing TTL down into the contract forces every backend to implement it in a backend-specific way, which Postgres and Redis handle very differently. A sweep loop in application code works the same way against every backend.

## Decision

Session lifetime is managed by a `SessionReconciliationService` in the application process, not by native backend TTL features. The service uses `PeriodicTimer` with a configurable sweep interval (default 60 seconds) and a configurable stale threshold (default 5 minutes). Every session writes its last-heartbeat timestamp on activity; the sweep deletes sessions whose heartbeat is older than the threshold. `ISessionStore` exposes a basic CRUD contract and does not surface TTL as part of the interface — backends are expected to store what they are told and delete when asked.

## Consequences

- **Positive:**
  - Node crashes are detected at the next sweep interval (default ≤60 s), not after the full TTL expires (typically minutes).
  - `ISessionStore` stays backend-agnostic; adding a new store (e.g. MongoDB, DynamoDB) requires no TTL-specific logic.
  - The sweep is observable: `LiveMetrics` exposes reconciliation counters, and the sweep timing is tunable per deployment via `SessionOptions`.
  - Heartbeat-based freshness is the natural metric for "session is alive" in a contact center, where calls can last longer than any fixed TTL and an expired-but-active session is worse than a missed sweep.
- **Negative:**
  - Every application instance runs its own sweep loop. In a fleet of N nodes, the eviction load is distributed but the global scan frequency is N × (1 / sweep interval). At large scale (100+ nodes), this can be non-negligible read traffic to the backend if sweep interval is set aggressively.
  - Restart-storm scenarios (all nodes restarted together) can produce a brief window where sweeps have not yet run and stale sessions are visible. This is bounded by the sweep interval.
- **Trade-off:** We trade backend-specific efficiency (one TTL expression evaluated by Redis/Postgres) for backend-agnostic correctness (one sweep loop in the SDK). The backend-specific path is more efficient when everything is healthy; the sweep loop is more correct when a node dies. Since "a node dies" is the failure we are actually designing for, the sweep wins. The audit in [`docs/research/2026-04-19-product-alignment-audit.md`](../research/2026-04-19-product-alignment-audit.md) §4 item #8 flagged this decision because a well-meaning refactor to "use native Redis EXPIRE" would silently disable multi-node crash detection.

## Alternatives considered

- **Redis `EXPIRE` / Postgres TTL column alone** — rejected because it only evicts keys after the full TTL window, which keeps orphaned sessions from crashed nodes visible to survivors for the full window. A contact center cannot carry ghost sessions for 5–15 minutes while the TTL drains.
- **Native TTL plus sweep loop (belt and braces)** — rejected because it doubles the eviction paths, complicates `ISessionStore` with backend-specific TTL surface, and adds no correctness over sweep-alone. Each backend would also need TTL-to-heartbeat alignment logic to avoid the TTL firing mid-call.
- **Push-based invalidation (publish "session expired" events)** — rejected because it assumes the node that owned the session is still running to publish; when the node crashes, there is no publisher. A pull-based sweep does not have this dependency.
- **Client-side proactive eviction (hook into `HostApplicationLifetime.ApplicationStopping`)** — rejected as a graceful-shutdown-only path; it handles clean shutdown but does nothing for crashes, which is the case the sweep loop exists to handle. It can coexist with the sweep but is not a substitute.
