# ADR-0038: CI pipeline slimming — single-collection coverage + representative PR matrix

- **Status:** Accepted
- **Date:** 2026-07-14
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0009 (three-tier test strategy), ADR-0005 (Testcontainers for integration), verbara-meta/ADR-0003 (CI-gating & branch-protection standard — coverage ratchet, `merge_group` triggers, required-check contexts), verbara-meta/ADR-0004 (deterministic-test-fences program). Change: `ci-pipeline-slimming` (openspec).

## Context

Sdk CI is the ecosystem's slowest and most-failing gate: **median 23 min per validation, ~25%
failed runs** over the recent window, so landing a PR through the merge queue costs ≈ 46 min (PR
run + `merge_group` run), and a flake adds another full cycle (measured 2026-07-06; verbara-meta
`docs/research/2026-07-06-ci-pipeline-durations.md`). Every other repo lands in 3–5 min. The cost
concentrates in two structural places (a third — a raced TTS cancellation test — is a
test-determinism fix recorded by the `test-determinism` spec delta, not this ADR):

1. **The functional matrix runs on both events.** Asterisk 22 + 23 (~20 min each, parallel) run
   on `pull_request` *and* `merge_group`, so the same ~20 min wall-clock is paid twice per
   landing, and PR iteration carries the full-matrix cost on every push.
2. **The Coverage Ratchet job duplicates the unit suite.** A second full `dotnet build` +
   `dotnet test` run (~11 min) whose only purpose is to collect coverage — the `unit-tests` job
   already built and ran the exact same subset.

These are durable CI-policy questions (which events run which validation; whether coverage is
collected once or twice) that outlive any single session, so they are recorded here rather than
only in a change that will be archived.

## Decision

### D2 — Coverage is collected once per validation run

The `unit-tests` job runs the unit subset with `--collect:"XPlat Code Coverage" --settings
coverlet.runsettings` and uploads the raw results as an artifact. The `Coverage Ratchet` job
(same job name — it is a required check) becomes `needs: unit-tests` and a fast *consumer*:
download the artifact → `reportgenerator` merge → `check-coverage-floor.py`. It no longer builds
or runs the suite. The committed floor (`coverage-floor.json`), the runsettings exclusions, and
the manual-ratchet-only semantics (no CI write-back) are unchanged (verbara-meta/ADR-0003).

### D3 — Representative PR matrix, full queue matrix

`functional-tests` gets a conditional matrix keyed on the triggering event:
`pull_request` → `[23]` (newest supported version, fast representative feedback); `merge_group` →
`[22, 23]` (the full support matrix). The merge queue remains the authoritative full-matrix gate:
no change lands on `main` without the `merge_group` run passing every supported Asterisk version.
A 22-only regression is caught at queue time rather than PR time — accepted, because 22-only
regressions are rare and the PR run keeps ~20 min of full-matrix wall-clock out of every
iteration.

Because `Functional Tests (Testcontainers) (22)` no longer reports on `pull_request`, it MUST be
removed from the PR-required (branch-protection) check set while remaining queue-validated via
`merge_group` — the verbara-meta/ADR-0003 required-check reconciliation rule. The branch-protection
edit lands together with the workflow change, never after it, or the merge queue hangs waiting for
a context that will never report (the ADR-0003 documented failure mode).

## Consequences

- **Positive:** PR iteration drops from ~23 min to ~8 min (one functional variant + no duplicate
  coverage run). A landing pays the full matrix once (in the queue) instead of twice. Coverage
  compute is halved with no loss of the floor gate.
- **Positive:** Required-check *names* are preserved — `Unit Tests`, `Coverage Ratchet`,
  `Functional Tests (Testcontainers) (23)` still report on PRs — so only one context
  (`... (22)`) leaves the PR-required set.
- **Negative / accepted:** A regression that manifests *only* on Asterisk 22 is detected at
  queue time, not PR time. This shifts a rare failure later in the pipeline; the queue still
  blocks the landing, so `main` is never exposed.
- **Neutral / trade-off:** The coverage artifact becomes a cross-job dependency (`Coverage
  Ratchet` needs `unit-tests`). If the unit job fails, the ratchet job is skipped rather than
  redundantly failing on its own build — acceptable, since a failing unit job already blocks the
  merge.

## Alternatives considered

- **Keep the full matrix on `pull_request`.** Simplest, no reconciliation. **Rejected:** it is
  the single largest wall-clock cost and it is paid twice per landing; the queue already
  re-validates the exact merge result, so PR-time full-matrix is largely redundant for the common
  case.
- **Collect coverage inside `unit-tests` and gate in the same job.** Fewer jobs, no artifact
  hand-off. **Rejected:** `Coverage Ratchet` is a named required check mirrored across repos
  (verbara-meta/ADR-0003); folding it into `unit-tests` would rename/remove a required context and
  break the cross-repo parity the standard depends on. Keeping it as an artifact consumer
  preserves the name while removing the duplicate work.
- **Drop the 22 variant entirely.** Cheapest. **Rejected:** 22 is still a supported target;
  dropping it removes real coverage rather than deferring it to the queue. The conditional matrix
  keeps full validation on the path that actually gates `main`.
