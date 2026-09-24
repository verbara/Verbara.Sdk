# Design: a-call-that-ended-is-released-when-it-ends

## Context

See proposal.md — *Why* for the measured defects. The design-relevant shape:

- `CallSessionManager` holds five structures keyed per call or per channel. Only `_sessions` and
  `_byLinkedId` are ever released, and only by `EvictStaleCompleted`.
- `EvictStaleCompleted` is called from exactly one place, `OnSessionCompleted`, which is itself
  reached only from `OnChannelRemoved`. Release therefore rides the completion of *another* call.
- `OnSessionCompleted` runs inside the lock taken in `OnChannelRemoved`, and emits `CallEndedEvent`
  synchronously through a `Subject<T>` **before** eviction runs. Every subscriber in this ecosystem
  subscribes directly, with no scheduler hop — so a consumer's handler runs while that lock is held,
  and a `GetById` from inside it must still find the session. Ordering is not a free choice here.
- `InMemorySessionStore` stores the same `CallSession` reference the manager holds.
- `SessionOptions.MaxCompletedSessions` is public, documented, defaulted to 1000 and read by nothing.

## Goals / Non-Goals

**Goals**

- Make release unable to stop permanently.
- Bound by time and by count, with time as the floor.
- Release without waiting for another call to end, and without adding a timer.
- Make the default store follow the manager, and make the resident counts visible.

**Non-Goals**

- Releasing, ageing or ending a call that is not terminal. Excluded by requirement, not by habit.
- Registering `SessionReconciliationService` anywhere, or changing what it does.
- The Postgres store's retention, and the consumer-side accumulation in the closed-source cluster
  layer and the product. Those are the consumers' own retention decisions.
- `_byChannelId` and `_bridgeToSession` entries stranded by an unobserved hangup. Their cause is the
  reload defect, which another change fixes; bounding them here would paper over it.

## Decisions

### D1 — Release discards an entry it cannot evaluate, instead of stopping

The release loop dequeues first and decides afterwards. An entry naming a call no longer retained,
or carrying no completion time, is dropped and the loop continues.

*Why:* this is the wedge. The current loop makes dequeuing conditional on the entry being usable, so
one unusable entry at the head disables release forever. Inverting that — take it off the queue, then
decide what to do with it — makes progress unconditional. The failure mode it removes is silent and
permanent, which is why the spec states it rather than leaving it to review.

*Alternative rejected:* keeping the peek-first shape and adding guards for the two known bad cases.
It fixes the two we found and leaves the structure that produced them.

### D2 — A terminal session is not queued twice

`OnSessionCompleted` enqueues on its first line regardless of what the session already was. The
enqueue becomes conditional on the session not already being queued.

*Why:* the duplicate is what creates D1's unusable head in the first place. Fixing only D1 leaves
the queue growing a redundant entry per re-completion; fixing only D2 leaves the wedge reachable by
any other route. Both, or neither.

### D3 — Time is a floor the count cannot undercut

The count bound releases the oldest entries beyond the maximum, but only among those already past
the retention period. A call that ended a second ago is never released because the count is high.

*Why:* the two bounds answer different questions — retention answers "how long is this useful", the
maximum answers "how much will we hold". Letting the count override retention would make the SDK's
answer to the first question depend on traffic, and a consumer reading a just-ended call by id would
get `null` under load and a session when idle. That is the silent-null failure this design exists to
avoid.

### D4 — Release is evaluated on arrival too, and no timer is added

The same evaluation runs when a call is admitted as well as when one completes. Nothing schedules it.

*Why:* release that only rides completions stops when completions stop, which is precisely the
degenerate case — a process still accepting calls but no longer completing them. Arrivals are the
other event the manager already handles, so the trigger costs no new machinery. A timer would be the
obvious alternative and is rejected: it means a hosted service or a background loop, which is a
lifetime the manager does not own today, and the multi-server registration deliberately has no hosted
service (`ADR-0059` records that gap as known and deferred). Adding one here would reopen a decision
that belongs elsewhere.

*Cost, stated:* an idle process releases nothing. That is acceptable — an idle process is not
growing either — and the spec says so explicitly rather than leaving it as a gap.

### D5 — Release runs after the ending has been delivered, and the store follows

Eviction stays after `CallEndedEvent` is emitted, and the default store is told to release the same
call the manager released, through one additive member on the store base type.

*Why the ordering:* a subscriber's handler runs synchronously inside the manager's lock, and those
handlers call `GetById`. Releasing before the event would hand every consumer a null for the call
they were just told about.

*Why the store:* the in-memory store holds the same object, so the manager's release frees a
dictionary node and nothing else. A store that provides durability keeps its own retention — the
Redis store already expires its keys on the same retention value, which is the shape to follow rather
than to override.

*Alternative rejected:* having the manager reach into the store's dictionary. The store owns its
storage; an additive virtual that defaults to doing nothing keeps every existing store compiling and
lets a durable one ignore it.

### D6 — A destroyed bridge is released

`BridgeManager` marks `DestroyedAt` and keeps the entry; `BridgeCount` counts destroyed bridges as
though they were live. Destruction releases the entry.

*Why it is here and not in its own change:* it is the same class of defect — something that ends and
is not released — in the layer directly beneath, and its fix is smaller than the paperwork of a
separate change. `BridgeCount`'s reported value changes, which is why it is called out rather than
folded in silently.

## Risks / Trade-offs

- **Releasing a call a consumer still needs** → the worst outcome, and the reason D3 makes time a
  floor and the spec forbids touching non-terminal calls. D5's ordering covers the narrower version
  of the same risk inside a subscriber's own handler.
- **`MaxCompletedSessions` starts taking effect** → a consumer that set it high while relying on it
  being ignored sees releases it did not before. It is a published option with a documented meaning,
  so honouring it is the contract; marked **BREAKING** in the CHANGELOG.
- **`BridgeCount` changes value** → it counted destroyed bridges. Any consumer dashboard reading it
  will show a lower, correct number. Called out in the CHANGELOG entry.
- **An idle process releases nothing** → accepted, stated in D4 and in the spec.
- **Conflict with the reload change** → both edit `OnChannelRemoved`, `OnSessionCompleted` and
  `EvictStaleCompleted`. This change lands after it; the merge queue would otherwise resolve the text
  and leave the semantics to chance.

## Migration Plan

No migration. No public API is removed, no data shape changes, no configuration is required. A
consumer that wants the old unbounded behaviour sets `MaxCompletedSessions` higher — the option means
what it says.

Rollback is a revert. Nothing persists that would survive it.

`Sdk/ADR-0063` records the durable rule: what the SDK holds for a call is released when that call
ends, bounded by time and by count, and that release never depends on its own bookkeeping being
well-formed.

## Open Questions

None that can be deferred. The one question that could have changed the spec — whether the count may
release a call that is still within its retention period — is answered in D3, because a consumer
receiving `null` for a just-ended call under load, and a session for the same call when idle, is a
behaviour the spec has to settle rather than discover.
