---
tier: MEDIANO
owner: Harol
approver: Harol
stakeholder: Every consumer that holds call state across an AMI reconnect — and the downstream repos (Sdk.Pro, Platform) that inherit this behaviour from the root of the dependency chain
decision_ref: Sdk/ADR-0062
---

# Proposal: a-reconnect-reload-is-a-diff-not-a-wipe

## Why

An AMI reconnect silently discards every channel the SDK is tracking and then re-adds the survivors
as if they had just appeared. `CallSessionManager` is never told the channels went away, so a call
that ended during the outage stays "in progress" for the life of the process, and a call that
survived the outage is split into several sessions. Both were **measured**, not inferred, on
2026-09-24 by two tests written for this change:

| Scenario | Measured today |
|---|---|
| The call hung up during the outage (`Status` returns nothing) | `1 active session [linked=linked-001 state=Connected participants=2]`, **0** `CallEndedEvent` |
| Both legs survived (`Status` returns both, carrying `Linkedid`) | **3** active sessions: the stale original, plus one `Created` session per leg |

The mechanism, read line by line:

1. `VerbaraServer.OnReconnected` (`src/Verbara.Sdk.Live/Server/VerbaraServer.cs:122`) calls
   `Channels.Clear()` (`:130`).
2. `ChannelManager.Clear()` empties its two dictionaries and raises **no** `ChannelRemoved`. The
   session manager's subscription hears nothing, so its sessions are untouched.
3. `RequestInitialStateAsync` (`:158-168`) re-adds the survivors through `Channels.OnNewChannel`
   **without passing the status event's `LinkedId`**, although `StatusEvent.LinkedId` exists
   (`src/Verbara.Sdk.Ami/Events/StatusEvent.cs:19`). Each leg therefore defaults to
   `linkedId = uniqueId` and becomes a session of its own.

This affects **both** registration paths. It is not a clustered-deployment problem.

One consequence decides the design. The reloaded legs land in `Created`, and `Created` past
`DialingTimeout` (60 s) is exactly what `SessionReconciliationService`'s orphan branch marks
`Failed`. Registering that sweep where it does not run today would therefore mark **healthy calls
dead after every reconnect**. That option is rejected here rather than left open.

The fix must also be honest about who can see it: `SessionReconciler` marks state and nothing else —
it persists nothing, evicts nothing and emits no domain event. The only ending a consumer ever
observes is `CallEndedEvent`, emitted solely by `OnSessionCompleted` via `OnChannelRemoved`. A repair
that does not travel that path is invisible to every consumer already written against this SDK.

## What Changes

- **The reload becomes a diff instead of a wipe.** `OnReconnected` stops clearing the channel table
  blind. The reloaded snapshot is compared against what is held, and the difference is what drives
  events — not the clearing.
- **A channel Asterisk no longer has ends its session through the normal completion path.**
  `OnChannelRemoved` → `OnSessionCompleted` → `CallEndedEvent`, the one route every consumer is
  already written against, rather than a new signal nobody subscribes to. **BREAKING**: a consumer
  that today sees a session stay `Connected` forever will now see it end.
- **The reload carries the correlation it is given.** `RequestInitialStateAsync` passes
  `StatusEvent.LinkedId` to `OnNewChannel`, and a channel already held is not re-added as new.
  **BREAKING**: one surviving call stops producing extra `Created` sessions, so any consumer counting
  sessions across a reconnect sees a different (correct) number.
- **A reload that fails or returns nothing verifiable ends nothing.** The diff acts only on a
  snapshot it actually received; a failed or partial reload leaves every session alone. Ending a live
  call by mistake is worse than the defect being fixed, so the failure direction is stated as a
  requirement rather than left to the implementation.
- **Two regression tests land first, red, against the unfixed code** — the measurements in the table
  above, already written at
  `Tests/Verbara.Sdk.Sessions.FunctionalTests/ReconnectReloadTests.cs`.
- **Not in scope, deliberately:** registering `SessionReconciliationService` on the multi-server
  registrations, and any change to what the sweep does. The measurement above is the argument
  against the first; the second is a separate decision with its own risks. Also out of scope: the
  multi-server shutdown token (`ADR-0059` records it as a known, deferred gap) and the unbounded
  growth of `InMemorySessionStore`, both of which this change neither worsens nor repairs.

## Capabilities

### New Capabilities

- `live-state-reload` — what the SDK guarantees when it reloads live state after a reconnect: that a
  reload is a reconciliation rather than a replacement, that a call which is gone ends through the
  path consumers already observe, that correlation survives the reload, and that an unverifiable
  reload ends nothing.

### Modified Capabilities

None. `session-persistence-lifecycle` governs the token a save runs under and
`client-connection-state` governs a client's published state; neither states anything about what a
reload does to a session.

## Impact

- `src/Verbara.Sdk.Live/Server/VerbaraServer.cs` — `OnReconnected` and `RequestInitialStateAsync`.
- `src/Verbara.Sdk.Live/Channels/ChannelManager.cs` — `Clear()` is the silent step; whether it gains
  a removal-raising sibling or loses its caller is the design question.
- `Tests/Verbara.Sdk.Sessions.FunctionalTests/` — the two regression tests, plus the scenarios the
  spec adds.
- `docs/decisions/0062-*.md` — the durable decision. Adding an ADR also moves `README.md`'s
  `**N ADRs**` figure, its `docs/claim-registry.md` row and the `docs/decisions/README.md` catalog
  row, all in the same pull request.
- **No public API is added or removed.** `ChannelManager.Clear()` is public and stays; the change is
  to who calls it and what the reload does around it.
- Downstream: Sdk.Pro's cluster layer and Platform both hold sessions across reconnects and inherit
  the current behaviour. Neither needs a code change for the fix to reach them, which is why the
  repair belongs on this side of the boundary.
