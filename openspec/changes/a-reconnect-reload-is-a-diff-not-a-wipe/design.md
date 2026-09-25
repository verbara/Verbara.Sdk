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
  behaviour it has today **for the channel table**. It is NOT the same for the session table, and the
  original wording of this bullet claimed it was. Because the initial load drops `StatusEvent.LinkedId`
  through the same three lines the reload does, two bridged legs of one live call that Asterisk
  reports with a shared `Linkedid` become **two** sessions on a first load today, and become one after
  D4. So a process restart during live traffic multiplies sessions exactly as a reconnect does, and
  D4 fixes both. Task 1.5's `InitialLoadTests` deliberately binds neither the loaded channels'
  `LinkedId` nor any session identity derived from correlated legs, precisely so it does not bind the
  defect; its single-leg session assertion survives D4 on purpose. The CHANGELOG entry and the ADR
  owe this the same sentence: a restart, not only a reconnect.

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

*Residual — CLOSED, favourably, by task 3.3 on 2026-09-24.* `Linkedid` is present, non-empty and
identical across every leg of one call on **all four** supported versions (18.26.4, 20.20.1, 22.9.0,
23.4.1), measured on a real bridge with `BridgeID` populated as well as on a simple two-leg pair, and
the `Status` header set is byte-for-byte identical across the four. The wire header reaches
`StatusEvent.LinkedId` through the SDK's own parser, verified by probe. So D4 is implementable exactly
as written, with no mapping work first, and no version needs the fallback.

Keep the requirement "a reload without correlation does not invent calls" anyway, but understand what
it now is: a defensive path with **no measured triggering version** among 18/20/22/23, not a
workaround for a known version difference. It costs nothing — an empty `Linkedid` degrades to today's
`linkedId = uniqueId` — and it covers what the measurement did not reach: `Local` channels were the
only technology exercised, because the functional dialplan has no PJSIP endpoint that answers without
SIPp. One line of the ADR should say this, so a later reader does not reopen the question hunting for
the version that drops the header.

### D5 — The reload reads the headers Asterisk sends, through `RawFields`, without widening the public API

`RequestInitialStateAsync` reads `se.State` and `se.CallerId`. Task 3.3 measured that **no supported
Asterisk version sends a `State:` or a `CallerID:` header on a `Status` frame** — not 18.26.4, not
20.20.1, not 22.9.0, not 23.4.1, and the header set is byte-for-byte identical across the four. Every
reloaded channel therefore lands as `ChannelState.Unknown` with a null caller id, today, on every
version. The reload reads `ChannelStateDesc` and `CallerIDNum` from `se.RawFields` instead — the same
route `Context` already travels at `VerbaraServer.cs:168`.

*Why through `RawFields` and not by giving `StatusEvent` the properties it is missing:* the missing
properties are a real defect, but a different one. `ChannelEventBase` declares
`ChannelState`/`ChannelStateDesc`/`CallerIdNum`/`CallerIdName` for every other channel-bearing event;
`StatusEvent` extends `ResponseEvent` and never got them, which is why it has `State` and `CallerId`
that nothing populates. That shape affects **every consumer reading `StatusEvent` directly**, not just
the reload, so it belongs to `Verbara.Sdk.Ami` and to its own change — recorded in `tasks.md`
section 4. Fixing it here would add four public members (an addition, not a break) and would falsify
this change's own claim that no public API moves, for a benefit the reload does not need: `RawFields`
already carries the values, verified on the wire.

*Which header, and why the numeric one:* `ChannelState` is **defined as** Asterisk's numeric values
(`Down = 0` … `Unknown = 10`, contiguous), so reading the numeric `ChannelState` header round-trips by
construction for every state. The text `ChannelStateDesc` carries no such guarantee — its values are
Asterisk's prose, not this enum's member names, and `Rsrvd` does not parse as `Reserved`. The reload
therefore reads the numeric header, range-checks it, and falls back to `Unknown` for an absent,
non-numeric or out-of-range value. Task 3.3 observed only `Up` (state 6, `ChannelStateDesc: Up`) on
the wire; that Asterisk's text spellings diverge for some states is read from its `ast_state2str`
and is **not measured in this repo**, which is why the decision rests on the numeric header's
definitional round trip rather than on a list of divergent spellings. The bug was never the parse; it
was the field handed to it.

*Failure direction:* a channel for which Asterisk sends no state header at all still defaults to
`Unknown` and is still admitted. Defaulting is correct when the value is genuinely absent; what was
wrong was defaulting a value that could never arrive.

### D6 — A reload only judges channels its snapshot could have seen, by admission mark

Ruled by the owner on 2026-09-25, after tasks 2.1 and 2.2 made the reload able to end calls.

`OnReconnected` re-subscribes the event observer before it awaits the reload, so live events resume
while the snapshot is still being read. A call that starts in that window is admitted to the table,
is absent from the older snapshot, and the reconcile would remove it — ending a call that is up. On a
large estate the window is a full `Status` round trip.

*This is a regression the change introduces, not a pre-existing one.* Before it, `Channels.Clear()`
removed every channel silently and raised nothing, so no call ever ended from a reload and none could
end wrongly. D1 and D2 gave the reload the power to end calls; D6 bounds what it is allowed to judge.

`ChannelManager` carries a counter incremented on every admission and stamps each channel with it.
The snapshot reader captures the counter's value before its first read, and the reconcile skips any
held channel whose stamp is above that mark: the snapshot is older than the channel and says nothing
about it. Absence is evidence only about channels the snapshot could have contained.

*Alternative rejected — filter on `AsteriskChannel.CreatedAt`.* The field already exists and costs
nothing to read, but it is `DateTimeOffset.UtcNow` evaluated at construction and is not injectable, so
a test binding this requirement would rest on real-clock ordering between two operations microseconds
apart. Two equal stamps make it intermittent, and this repo already carries five hand-rolled
`FakeTimeProvider` copies from seams built this way. A counter is deterministic and the test that
binds it needs no clock at all.

*Alternative rejected — re-subscribe after the reload.* It removes the window with no new state, but
every event Asterisk sends during the reload is then lost outright rather than delayed: a call that
starts in the window appears in neither the snapshot nor the event stream, so the SDK never learns of
it. That trades a wrongly-ended call for a permanently invisible one, which is not an improvement.

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
