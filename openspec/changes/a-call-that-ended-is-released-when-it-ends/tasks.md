# Tasks

Execution follows `rules.tasks`: **a fresh subagent per task, never inline in the main session.**
Phase A is batched, Phase B is one focused subagent per component, Phase C is batched.

**Sequencing, not optional.** This change edits the same method bodies as
`a-reconnect-reload-is-a-diff-not-a-wipe` (`OnChannelRemoved`, `OnSessionCompleted`,
`EvictStaleCompleted`). It starts **after** that change has merged. Verify before task 1.1 that
`openspec list` no longer shows it open.

## 1. Phase A — foundation (batched)

- [ ] 1.1 Write the failing regression test for the wedge, against the unfixed code: complete a
      session, drive the path that enqueues its id a second time while it is already terminal, let the
      first copy be evicted, then complete further sessions past `CompletedRetention`. Verify it fails
      today, and paste its failure verbatim into this file under the task — the repo's rule for a bug
      fix is the failing test first, with its output recorded.

- [ ] 1.2 Write the failing regression test for the count bound: retain more ended sessions than
      `MaxCompletedSessions`, all older than `CompletedRetention`, and assert the oldest are released.
      Verify it fails today (the option is read by nothing) and record the failure.

- [ ] 1.3 Write the failing regression test for the store: assert that after the manager releases an
      ended session the default in-memory store no longer retains it. Verify it fails today — the
      store holds the same object reference.

- [ ] 1.4 Write the measurement that has never existed: 50 completed calls, then assert the resident
      count. Record the number it produces on today's code in this file. This is the "does it grow"
      figure the investigation was missing; it is a measurement first and a regression test second.

- [ ] 1.5 Write `docs/decisions/0063-what-the-sdk-holds-for-a-call-is-released-when-the-call-ends.md`
      (Status: Proposed → Accepted at merge) carrying D1–D6 from `design.md`, including the rejected
      alternatives — a timer, a count that overrides retention, and the manager reaching into the
      store. Verify `openspec validate --all --strict` passes.

- [ ] 1.6 Land the ADR-count coupling in the same commit as 1.5: `README.md`'s `**N ADRs**` figure,
      its `docs/claim-registry.md` row, and the `docs/decisions/README.md` catalog row. Verify
      `dotnet test Tests/Verbara.Sdk.OpenTelemetry.Tests/` passes — two guards there fail if any of
      the three is missed.

## 2. Phase B — critical components (one focused subagent each)

- [ ] 2.1 Make release discard an entry it cannot evaluate instead of stopping (design D1): dequeue
      first, decide after. Verify 1.1 goes green, and add unit cases for both bad heads — an entry
      naming a session no longer retained, and an entry with no completion time — each asserting that
      every other eligible entry is still released.

- [ ] 2.2 Stop a terminal session being queued for release twice (design D2). Verify with a test that
      drives a second ending for an already-terminal session and asserts the queue gained no entry.

- [ ] 2.3 Honour `MaxCompletedSessions` with retention as the floor (design D3). Verify 1.2 goes
      green **and** that a test asserting the floor fails if the floor is removed: more retained than
      the maximum, none past retention, nothing released.

- [ ] 2.4 Evaluate release on arrival as well as on completion, with no timer and no new hosted
      service (design D4). Verify with a test where ended sessions age past retention and only
      arrivals follow — they are released — and a second test that an idle manager releases nothing
      and grows nothing.

- [ ] 2.5 Make the default in-memory store follow the manager's release through one additive member
      on the store base type, defaulting to doing nothing so every existing store still compiles
      (design D5). Verify 1.3 goes green, and that a durable store's own retention is unaffected —
      the Redis store expires on its own TTL and must keep doing so.

- [ ] 2.6 Release a destroyed bridge in `BridgeManager` (design D6). Verify with a test that
      `BridgeCount` drops when a bridge is destroyed, and confirm by reading the callers that nothing
      in this repo depended on destroyed bridges remaining addressable.

- [ ] 2.7 Publish the resident counts as gauges — live calls and retained ended calls, separately.
      Verify by reading them through the existing meter surface in a test, and check whether the
      published instrument count in `README.md` moves; if it does, its claim-registry row moves in
      the same PR.

## 3. Phase C — integration (batched)

- [ ] 3.1 Turn every scenario in `specs/session-residency/spec.md` into a test, including the two that
      bind the failure direction (an old connected call is untouched; live calls do not consume the
      maximum). Verify each scenario has a test and that removing the guard it describes turns that
      test red.

- [ ] 3.2 Strengthen `IndexAndQueryTests.GetRecentCompleted_ShouldReturnCompletedSessions`, which
      asserts only `HaveCountGreaterOrEqualTo(3)` and can therefore detect neither growth nor
      eviction. Verify the strengthened assertion fails if release is disabled.

- [ ] 3.3 Write the `CHANGELOG.md` entry under `[Unreleased]`, labelled `### Fixed — BREAKING`
      (ADR-0061: a `Fixed — BREAKING` does not by itself force a minor). State three observable
      changes: release no longer stops permanently, `MaxCompletedSessions` now takes effect, and
      `BridgeCount` no longer counts destroyed bridges. **Pick an insertion anchor distinct from any
      other in-flight PR's and state it in the PR body.**

- [ ] 3.4 Confirm the public API surface: verify `PublicAPI.Unshipped.txt` changes only by the
      additive store member from 2.5, and that no `CompatibilitySuppressions.xml` is needed.

- [ ] 3.5 **Verification.** On the integrated branch: `dotnet build Verbara.Sdk.slnx -c Release` with
      **0 warnings**; the full unit lane under the CI filter; `Tests/Verbara.Sdk.Governance.Tests` and
      `Tests/Verbara.Sdk.OpenTelemetry.Tests` (tree-scanning guards — green on touched projects is not
      green in CI); `openspec validate --all --strict`. Then read `.github/workflows/ci.yml` and run
      the remaining fast, deterministic, non-service steps it lists rather than recalling job names.

- [ ] 3.6 Do **not** bump `Directory.Build.props`. The version is cut at release time (ADR-0055).
      Verify it is absent from this change's diff.

## 4. Recorded, not fixed here

Found while investigating this, deliberately left out, with where each belongs — a deferred finding
without a home is how the last set was lost:

- **`_byChannelId` and `_bridgeToSession` entries stranded by an unobserved hangup.** Their cause is
  the reload defect that `a-reconnect-reload-is-a-diff-not-a-wipe` fixes; bounding them here would
  hide it. Note that `_byChannelId` is scanned linearly on every queue join, so stranded entries cost
  time as well as memory.
- **Sessions reconstructed by the closed-source cluster layer carry no participants**, so they can
  never reach the completion path and are permanent residents that are re-snapshotted periodically.
  That is the cluster layer's own repository, not this one.
- **The Postgres session store never deletes**, unlike the Redis store which expires on its own TTL.
  A durable store's retention is its own decision and needs its own change.
- **The product's retention service ships disabled** with no configuration path to enable it. That is
  the product repo's finding, recorded here only because this investigation surfaced it.
