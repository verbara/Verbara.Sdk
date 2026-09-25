# Tasks

Execution follows `rules.tasks`: **a fresh subagent per task, never inline in the main session.**
Phase A is batched, Phase B is one focused subagent per component, Phase C is batched.

**Coordination.** `externalmedia-returns-a-channel-id-that-finds-its-stream` landed on 2026-09-24
and no longer holds anything. What is still another session's territory is the open change
`a-published-surface-is-one-something-measures`, which claims `Verbara.Sdk.Ari`'s
`WebSocketAudioServer` keying, the audio metrics surface, and **the functional suite's coverage
claims**. Task 3.3 below adds a functional test and therefore needs that coordination stated in its
pull request, not a blanket ban: the two changes touch the same project for different reasons and
must not both edit `Tests/Verbara.Sdk.FunctionalTests/Verbara.Sdk.FunctionalTests.csproj` at once.

## 1. Phase A — foundation (batched)

- [x] 1.1 Commit the two failing regression tests exactly as measured, at
      `Tests/Verbara.Sdk.Sessions.FunctionalTests/ReconnectReloadTests.cs` (already written and run
      on 2026-09-24, against the unfixed code). Verify by running
      `dotnet test Tests/Verbara.Sdk.Sessions.FunctionalTests/ --filter "FullyQualifiedName~ReconnectReloadTests"`
      and confirming **2 failed, 0 passed** with these two messages, verbatim:

      ```
      Expected _sessions.ActiveSessions to be empty because a call Asterisk no longer has cannot
      still be in progress; the reload is the only notification the consumer will ever get that it
      ended. Measured: 1 active session(s) [linked=linked-001 state=Connected participants=2], but
      found at least one item

      Expected _sessions.ActiveSessions to contain a single item because one call is one session
      across a reconnect; the reload carries the LinkedId that says so. Measured: 3 active
      session(s) [linked=agent-001 state=Created participants=1 | linked=linked-001 state=Connected
      participants=2 | linked=caller-001 state=Created participants=1], but found
      ```

- [x] 1.2 Record the fact the tests exposed about the existing suite: the shared `SessionTestFixture`
      never calls `VerbaraServer.StartAsync`, which is where `Reconnected` is subscribed, so
      `ReconciliationTests.Reconnection_ShouldCleanSessions_WhenServerReconnects` exercises no
      reconnect at all. Verify by asserting in that test's file, or in this change's notes, that the
      subscription happens in `StartAsync` and the fixture does not call it — do **not** rewrite that
      test here; it belongs to whatever change fixes it.

- [x] 1.3 Write `docs/decisions/0062-a-reload-is-a-reconciliation-and-an-unobserved-ending-is-unknown.md`
      (Status: Proposed → Accepted at merge), carrying D1–D4 from `design.md` and the owner's ruling
      of 2026-09-24 on how a reload-produced ending is attributed. Verify `openspec validate --all --strict`
      passes and the file exists.

- [x] 1.4 Land the ADR-count coupling in the same commit as 1.3: bump `README.md`'s `**N ADRs**`
      figure, update its row in `docs/claim-registry.md`, and add the catalog row in
      `docs/decisions/README.md`. Verify `dotnet test Tests/Verbara.Sdk.OpenTelemetry.Tests/` passes
      — `ThePublishedAdrCount_ShouldMatchTheDecisionsOnDisk` and
      `TheDecisionCatalog_ShouldListEveryAdrOnDisk` both fail if any of the three is missed.

- [x] 1.5 Add a test that the **initial** load is unchanged by anything this change will do: a first
      `StartAsync` against an empty table produces exactly the channels the snapshot contains, and
      the same `ChannelAdded` events as today. Verify it passes **before** Phase B, so a Phase B
      regression in startup is attributable.

## 2. Phase B — critical components (one focused subagent each)

- [x] 2.1 Give `ChannelManager` a reconcile entry point that takes a complete snapshot and raises
      `ChannelAdded` for what is new and `ChannelRemoved` for what the snapshot does not contain
      (design D2). `Clear()` stays public and untouched. Verify with unit tests over the manager
      alone: added-only, removed-only, mixed, and identical-snapshot (which must raise nothing).

- [x] 2.2 Make `VerbaraServer` buffer the channel snapshot to completion before reconciling, and make
      `OnReconnected` hand it to 2.1's entry point instead of calling `Channels.Clear()` (design D1).
      A snapshot whose enumeration throws or never completes MUST leave every held channel alone.
      Verify with a test whose status enumeration throws midway: zero removals, zero endings, the
      held call still active with its participants intact.

- [ ] 2.3 Pass `StatusEvent.LinkedId` through `RequestInitialStateAsync` to `OnNewChannel`, and make a
      channel already held reconcile rather than re-enter as new (design D4). Verify against the
      second regression test from 1.1: it must go green, reporting **1** active session under the
      identity it had before the reconnect, not 3.

- [ ] 2.4 Carry "no cause observed" from a reload-driven removal into `CallSessionManager`, so the
      ending is marked as coming from a reload and the departing participants are left without a
      hangup cause — distinct from `HangupCause.NotDefined` (design D3, owner ruling). The resulting
      session state must follow from what the session already was, not from a cause that does not
      exist. Verify with tests that a reload-ended call carries the marker and no cause, and that a
      call ended by an observed hangup still carries Asterisk's cause and no marker.

- [ ] 2.5 Make the first regression test from 1.1 go green through the normal completion path:
      exactly one `CallEndedEvent`, the call gone from the active set. Verify that the event is the
      same one a hangup produces — a consumer subscribed only to call endings must observe it.

- [ ] 2.6 Make the reload read the headers Asterisk actually sends. `RequestInitialStateAsync`
      currently reads `se.State` and `se.CallerId`, and **no supported Asterisk version sends a
      `State:` or `CallerID:` header on a `Status` frame** — measured on 18.26.4, 20.20.1, 22.9.0 and
      23.4.1 by task 3.3, whose kept test re-measures it. Every reloaded channel therefore lands as
      `ChannelState.Unknown` with a null caller id today. Read `ChannelStateDesc` (or the numeric
      `ChannelState`; the `ChannelState` enum maps 1:1 to Asterisk's numeric values, `Up = 6`) and
      `CallerIDNum` through `se.RawFields`, the route `Context` already uses at
      `src/Verbara.Sdk.Live/Server/VerbaraServer.cs:168` — design D5.
      **Do NOT add properties to `StatusEvent`** to do this: that is a public API addition, it would
      falsify task 3.4, and the reason `StatusEvent` lacks them is a separate defect with its own
      entry in section 4. Verify with a test that a channel the snapshot reports as answered is
      admitted in that state and not `Unknown`, that its calling number survives, and that a channel
      reported with no state header at all still defaults to `Unknown` and is still admitted.

## 3. Phase C — integration (batched)

- [ ] 3.1 Turn every scenario in `specs/live-state-reload/spec.md` into a test, including the two that
      bind the failure direction (reload fails partway; reload never answered). Verify each scenario
      has a test and that deleting the guard it describes turns that test red — a scenario whose
      mutation survives is not bound.

- [ ] 3.2 Write the `CHANGELOG.md` entry under `[Unreleased]`, labelled **`### Fixed — BREAKING`**.
      The owner ruled the label on 2026-09-24, under ADR-0061 D3: the SDK never promised to hold a
      stranded session for the life of the process, nor to turn one call into several — `LinkedId`
      correlation is its declared design — so this restores documented behaviour rather than
      withdrawing a kept promise. Under ADR-0061 D1 a `Fixed — BREAKING` does **not** force a minor,
      so this change may ship in a patch, and ADR-0028's migration-guide obligation — which attaches
      to a minor carrying a breaking change — does not attach to it. That is why `design.md`'s
      Migration Plan says there is nothing for a consumer to migrate.
      State both observable changes — a lost call now ends, and a surviving call no longer multiplies
      — and the marker a consumer reads to tell a reload-produced ending apart.
      **Pick an insertion anchor distinct from any other in-flight PR's and state it in the PR body**;
      the bottom of `[Unreleased]`, immediately above the newest released heading, is the anchor least
      likely to be contested.

- [ ] 3.3 Measure both Asterisk-side premises against a real Asterisk, do not assume either. Follow
      the pattern in `Tests/Verbara.Sdk.FunctionalTests/Layer5_Integration/NetworkPartition/ConnectionCutTests.cs`
      (Toxiproxy `ami-proxy`, `AutoReconnect=true`), on the supported versions:
      **(a)** whether `Status` populates `Linkedid` (design D4's residual — if a version does not, verify
      the "a reload without correlation does not invent calls" scenario covers it); and
      **(b)** whether Asterisk replays the `Hangup` events missed during the outage. If it replays
      them, the reload is not the last notification such a call produces and requirement 2's premise
      narrows — so this measurement can change the spec, and it is the one task here that must run
      before Phase B is called done. Record both results in the ADR.

- [ ] 3.4 Confirm the change adds and removes no public API: verify `PublicAPI.Unshipped.txt` is
      unchanged in every package this change touches, and that no `CompatibilitySuppressions.xml` is
      needed.

- [ ] 3.5 **Verification.** On the integrated branch: `dotnet build Verbara.Sdk.slnx -c Release` with
      **0 warnings**; the full unit lane under the CI filter; `Tests/Verbara.Sdk.Governance.Tests`
      and `Tests/Verbara.Sdk.OpenTelemetry.Tests` (tree-scanning guards — green on touched projects
      is not green in CI); `openspec validate --all --strict`. Then read
      `.github/workflows/ci.yml` and run the remaining fast, deterministic, non-service steps it
      lists rather than recalling job names.

- [ ] 3.6 Do **not** bump `Directory.Build.props`. The version is cut at release time (ADR-0055), so
      this change ships its CHANGELOG entry and the release that carries it decides the tier — which,
      per 3.2's ruling, may be a patch. Verify `Directory.Build.props` is absent from this change's
      diff.

## 4. Measured elsewhere, or not yet owned

The investigation that produced this change proposed five measurements. Two are here (1.1 and 3.3).
The other three are recorded so they are not lost, because a finding deferred without a home is how
the last one was lost:

- **How long a healthy inbound call legitimately sits in `Created`.** Dropped from this change on
  purpose: it exists to decide whether a clock-based sweep can be safe, and this change rejects that
  sweep with the measurement in `proposal.md` instead. It becomes owed again the day anyone reopens
  the sweep, and it is a Platform measurement, not an SDK one.
- **Whether the session table and the in-memory store grow without bound** (50 completed calls,
  count what stays resident; `MaxCompletedSessions` is declared and read by nothing, and eviction
  fires only when another session completes). Out of scope here — this change neither worsens nor
  repairs it — and **it has no open change of its own**. It needs one.
- **`StatusEvent` declares two properties no Asterisk version populates, and lacks the four that
  carry the values.** `StatusEvent.State` and `StatusEvent.CallerId` are in
  `src/Verbara.Sdk.Ami/PublicAPI.Shipped.txt` and are always null on 18/20/22/23 — measured by task
  3.3. What the wire carries is `ChannelState`/`ChannelStateDesc` and `CallerIDNum`/`CallerIDName`,
  which is exactly the set `ChannelEventBase` declares for every other channel-bearing event;
  `StatusEvent` extends `ResponseEvent` instead and so never got them. This affects **every consumer
  that reads `StatusEvent` directly**, not only the reload, so it is an `Verbara.Sdk.Ami` parsing
  defect rather than a reload defect, and task 2.6 deliberately routes around it through `RawFields`
  instead of fixing it here. Fixing it properly means adding the four properties (a public API
  addition, not a break) and deciding what to do about the two dead ones — removing them **is** a
  break (CP0002), so they can only be documented or obsoleted. **It has no open change of its own.**
  It needs one.

- **The product-level blast radius of a stranded call** (conversation left active, voice capacity
  held, agent left busy). Belongs to the Platform repo, not this one.
