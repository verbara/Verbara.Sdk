# Design: a-reconnect-reload-is-a-diff-not-a-wipe

## Context

See proposal.md — *Why* for the measured behaviour and the mechanism. The design-relevant shape of
the current code:

- `VerbaraServer.OnReconnected` clears five live managers and then streams a fresh snapshot into
  them. The clearing and the loading are separate steps with nothing between them that could notice
  a difference.
- `ChannelManager` owns the channel table **and** the `ChannelAdded` / `ChannelRemoved` events that
  `CallSessionManager` subscribes to in `AttachToServer`. It is the only component that can raise a
  removal, and its `Clear()` bypasses that.
- `CallSessionManager.OnChannelRemoved` is the single route to `CallEndedEvent`, and it decides
  `Completed` versus `Failed` by reading `channel.HangupCause`, which defaults to `NotDefined`.
- `RequestInitialStateAsync` is used for both the initial load and the post-reconnect reload. On the
  initial load there is nothing held, so a diff degenerates to "everything is added" — the same
  behaviour it has today.

## Goals / Non-Goals

**Goals**

- Make the reload a reconciliation, with the removal side reaching `CallSessionManager` through the
  event it already listens to.
- Preserve correlation across the reload so one call stays one call.
- Fail towards leaving calls alone, never towards ending them.

**Non-Goals**

- The four other managers (`Queues`, `Agents`, `MeetMe`, `Bridges`) keep their current
  clear-and-reload. They hold no session identity, nothing subscribes to their removals for
  lifecycle purposes, and widening the change to them would multiply the surface without evidence
  that anything depends on it. Recorded so a later reader sees a boundary, not an oversight.
- `ChannelManager.Clear()` is public and stays. This change removes one of its callers, not the
  method.
- `SessionReconciliationService`, the multi-server registration set, and the unbounded growth of
  `InMemorySessionStore` are out of scope (see proposal.md — *What Changes*).

## Decisions

### D1 — The reload is buffered, then reconciled; it is not streamed into a cleared table

The snapshot is read into a local set first, and only a snapshot that was read to completion is
applied. Reconciliation then compares it against what is held.

*Why:* buffering is what makes the difference knowable — you cannot compute "absent from the
snapshot" while streaming into the structure you are comparing against. It also delivers the
"a reload that cannot be trusted ends nothing" requirement structurally rather than by care: if the
enumeration throws, the buffer is discarded and nothing was mutated. `OnReconnected` already wraps
everything in a `try`/`catch` that swallows into a log line, so a failure path that mutates nothing
is the only one that stays safe under it.

*Alternative rejected:* mark-and-sweep in place (tag every held channel, clear tags as the snapshot
arrives, remove the still-tagged ones at the end). It avoids the buffer but leaves the table in a
half-updated state if the enumeration fails midway, which is exactly the failure direction the spec
forbids.

### D2 — Reconciliation lives in `ChannelManager`, not in `VerbaraServer`

`ChannelManager` gains a reconcile entry point that takes the snapshot and raises `ChannelAdded` /
`ChannelRemoved` as the difference requires. `VerbaraServer.OnReconnected` stops calling
`Channels.Clear()` and hands the snapshot over instead.

*Why:* the table and its events are one thing. A diff computed in `VerbaraServer` would have to reach
into the manager's state to learn what is held and then ask it to raise events for entries it did not
decide — two components sharing one invariant. `ChannelManager` already owns both halves.

*Alternative rejected:* leaving the diff in `VerbaraServer` and adding a public removal-raising
method to `ChannelManager`. That is a wider public surface for a narrower benefit, and this repo
treats the public API as a contract with two downstream repos.

### D3 — A reload-produced ending carries a marker and no hangup cause

Ruled by the owner on 2026-09-24. A call ended because the reload proved it gone is marked as such,
and its departing participants are left without a hangup cause rather than being given `NotDefined`.

*Why:* `NotDefined` is not neutral downstream. A consumer classifier that treats anything other than
`NormalClearing` as an abnormal ending would read every reconnect-lost call as an abnormal hangup and
act on it — in the product at the end of this dependency chain, that path leads to calling a customer
back. Asserting `NormalClearing` instead would be the opposite lie: it records as clean an ending
nobody observed. The marker is the only option that does not claim knowledge the SDK does not have.

*Consequence for the implementation:* `CallSessionManager.OnChannelRemoved` currently derives
everything from `channel.HangupCause`. The reload-driven removal must reach it carrying "no cause",
distinct from "cause zero", and the resulting session state must follow from what the session already
was rather than from a cause that does not exist.

*Alternatives rejected:* both are recorded in the proposal's ruling — end as `Failed` with
`NotDefined` (cheapest, but produces the spurious-callback path), or end as `Completed` with
`NormalClearing` (silent, but records an unobserved ending as normal).

### D4 — The reload passes the correlation identifier it already receives

`RequestInitialStateAsync` passes `StatusEvent.LinkedId` to `OnNewChannel`, and a channel already
held is reconciled rather than re-added.

*Why:* the value is already on the wire and already parsed; not passing it is the whole reason one
call becomes three. This is the smallest half of the change and the one with the clearest evidence.

*Residual:* whether Asterisk always populates `Linkedid` on `Status` across the supported versions
(18, 20, 22 LTS, 23) is not knowable from this repo. The requirement "a reload without correlation
does not invent calls" covers the case where it is absent, so the fix degrades rather than breaks —
but the functional lane must measure it against a real Asterisk rather than assume it.

## Risks / Trade-offs

- **Ending a live call by mistake** → the worst outcome available, and worse than the defect. D1
  makes a failed or partial reload mutate nothing, and the spec states it as a requirement so a test
  binds it rather than a comment.
- **A consumer that relied on the ghost** → a session that stayed `Connected` forever is not a
  contract anyone chose, but a consumer may have grown a workaround (its own timeout, say) that now
  fires alongside the real ending. Called out as **BREAKING** in the proposal and in the CHANGELOG
  entry.
- **`Linkedid` absent on some Asterisk version** → D4's residual. Degrades to today's behaviour for
  that channel instead of failing; measured in the functional lane, not assumed.
- **The initial load shares the code path** → a diff against an empty table must behave exactly as
  today's load. Cheap to bind with a test, and worth binding: a regression here breaks every startup,
  not just reconnects.
- **Double work on a large estate** → buffering a snapshot of every channel costs memory
  proportional to the estate for the duration of the reload. Bounded by what the reload already
  materialises event by event, so the delta is the retained set, not the stream.

## Migration Plan

No migration for consumers: no public API is added or removed, and the ending arrives on the event
they already handle. A consumer that wants to distinguish a reload-produced ending reads the marker
from D3; one that does not care needs no change.

Rollback is a revert of the change. There is no data shape, no persisted format and no configuration
switch, so nothing survives a rollback that would need undoing.

The ADR (`Sdk/ADR-0062`) records the durable half: that a reload is a reconciliation, and that an
ending nobody observed is recorded as unknown rather than guessed in either direction.

## Open Questions

None that can be deferred. The one genuine fork — how a reload-produced ending is attributed — was
ruled before this document was written (D3), because it changes what the spec requires and what a
downstream product does with the result.
