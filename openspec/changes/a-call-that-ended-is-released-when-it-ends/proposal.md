---
tier: MEDIANO
owner: Harol
approver: Harol
stakeholder: Operators running this SDK as a 24/7 process — and the product at the end of the dependency chain, whose API container is capped at 512 MB in its production compose
decision_ref: Sdk/ADR-0063
---

# Proposal: a-call-that-ended-is-released-when-it-ends

## Why

What the session manager holds for a call is released only when *another* call completes, and the
release can stop working permanently. Neither is a design anyone chose.

**The eviction can wedge, and then nothing is ever released again.**
`CallSessionManager.EvictStaleCompleted` (`src/Verbara.Sdk.Sessions/Manager/CallSessionManager.cs:410-421`)
peeks the head of its completion queue and dequeues **inside the loop body**:

```csharp
while (_completedOrder.TryPeek(out var oldId) &&
       _sessions.TryGetValue(oldId, out var old) &&
       old.CompletedAt < cutoff)
{
    _completedOrder.TryDequeue(out _);
```

If the head is no longer in `_sessions`, or its `CompletedAt` is `null` (a nullable `<` is false for
`null`), the loop exits **without dequeuing**. That head stays at the front for the life of the
process, and every later call adds an entry that is never removed.

The trigger is reachable in code, not hypothetical: `OnSessionCompleted` enqueues unconditionally on
its first line (`:377`) and is called at `:233` **even when both state transitions failed** — which is
what happens to a session that is already terminal. The id is enqueued twice; once the first copy is
evicted, the second copy's lookup fails and the queue is wedged.

**Even unwedged, the bound is weak.** Eviction runs only from `OnSessionCompleted`, so a process that
stops completing calls stops releasing memory while it keeps accepting them. There is no timer and no
count cap: `SessionOptions.MaxCompletedSessions` (default 1000,
`src/Verbara.Sdk.Sessions/Manager/SessionOptions.cs:12`) is declared and **read by nothing**.

**And the manager's eviction frees almost nothing today.** `InMemorySessionStore` — the default, and
what the product resolves because it registers no store package — keeps the *same session object
reference*. When the manager evicts, the store still pins the whole session graph. Two other
structures also only grow: `_byChannelId`, whose stranded entries are scanned linearly on every queue
join, and `BridgeManager._bridges`, which marks a destroyed bridge with `DestroyedAt` and never
removes it, so `BridgeCount` counts every bridge ever created.

The numbers make it a date rather than a worry: the product's own production compose caps that
container at **512 MB** (`docker-compose.production.yml:50`).

**What is *not* wrong**, measured rather than assumed, because an earlier sweep of this area got two
of these backwards: the Redis store expires its own keys with a TTL of `CompletedRetention`
(`RedisSessionStore.cs:86`), so "no store shrinks" holds for the in-memory and Postgres stores only;
and `GetRecentCompleted` *is* exercised by a test — one that asserts only
`HaveCountGreaterOrEqualTo(3)`, so it can detect neither growth nor eviction.

## What Changes

- **Eviction stops depending on the head of the queue being well-formed.** A head that cannot be
  evaluated is discarded rather than left in place. This is the wedge, and it is the only item here
  that turns a bounded structure into an unbounded one.
- **A session is enqueued for release once.** A completion that finds the session already terminal
  does not enqueue it a second time.
- **The bound is time *and* count.** `MaxCompletedSessions` starts being honoured — it is already
  public, already documented and already defaulted, so this is the option's declared meaning finally
  taking effect. Time remains the floor: a completed session is never released before
  `CompletedRetention` has passed.
- **Release is evaluated on arrival as well as on completion**, so a process that stops completing
  calls still releases what it already holds. Without a timer: the evaluation rides the events the
  manager already handles.
- **The default in-memory store follows the manager.** Releasing in the manager while the store pins
  the same object frees nothing; a durable store keeps its own record and its own retention, which is
  what the Redis store already does.
- **A destroyed bridge is released.** `BridgeManager` stops retaining every bridge ever created.
- **What the process holds becomes visible** — resident counts as gauges, so an operator can see the
  bound working instead of inferring it from memory graphs.
- **Not in scope, deliberately:** any release of a session that is **not terminal**. Ageing a live
  call by a clock is exactly what `a-reconnect-reload-is-a-diff-not-a-wipe` rejected with a
  measurement, and the number that would make it arguable is owed by the product repo and unmeasured.
  Stranded sessions are that change's subject, not this one's. Also out of scope: registering
  `SessionReconciliationService` anywhere, the Postgres store's retention, and the consumer-side
  accumulation in Pro and Platform.

## Capabilities

### New Capabilities

- `session-residency` — what the SDK guarantees about how long it holds a call after that call has
  ended: that a terminal session is released under a bound of both time and count, that the release
  cannot be disabled by the state of its own bookkeeping, that it is evaluated without waiting for
  another call to end, and that a live call is never released by it.

### Modified Capabilities

None. `session-persistence-lifecycle` governs the token a save runs under and says nothing about how
long anything is held.

## Impact

- `src/Verbara.Sdk.Sessions/Manager/CallSessionManager.cs` — the eviction, the enqueue, and the
  release trigger.
- `src/Verbara.Sdk.Sessions/Manager/SessionOptions.cs` — `MaxCompletedSessions` gains a reader; its
  default and its documented meaning do not change.
- `src/Verbara.Sdk.Sessions/Internal/InMemorySessionStore.cs` and `SessionStoreBase` — the default
  store follows the manager's release.
- `src/Verbara.Sdk.Live/Bridges/BridgeManager.cs` — a destroyed bridge is released.
- `docs/decisions/0063-*.md`, plus the ADR-count coupling: `README.md`'s `**N ADRs**` figure, its
  `docs/claim-registry.md` row, and the `docs/decisions/README.md` catalog row, all in the same PR.
- **No public API is removed.** `MaxCompletedSessions` starts being honoured, which is a behaviour
  change for a consumer that set it high and relied on it being ignored — recorded as **BREAKING**
  in the CHANGELOG rather than assumed harmless.
- Downstream: the product pins an older SDK, so it receives this on its next bump. No consumer code
  change is required.
- **Sequencing:** this change edits the same method bodies as
  `a-reconnect-reload-is-a-diff-not-a-wipe` (`OnChannelRemoved`, `OnSessionCompleted`,
  `EvictStaleCompleted`). It lands **after** that change, not beside it. That change also *reduces*
  what this one has to bound: calls stranded in a connected state become terminal and therefore
  releasable, and phantom sessions stop being minted.
