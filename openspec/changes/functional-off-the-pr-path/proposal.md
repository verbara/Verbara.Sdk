---
tier: MEDIANO
owner: Harol
approver: Harol
stakeholder: The maintainer, who pays ~60 min of wall-clock per landing and opens or updates PRs more than three times a day
decision_ref: Sdk/ADR-0051
---

# Proposal: functional-off-the-pr-path

## Why

ADR-0038 measured a 23-minute median in July 2026 and moved the Asterisk 22 leg of the functional
matrix into the merge queue. The number since got worse, not better. Measured over the last 11
code-PR runs (2026-08-16 → 2026-08-18): **median `pull_request` validation 29.0 min**, with tail
outliers of **51.4 min** and **170.4 min**. Because the queue re-validates, **landing a change costs
≈ 60 min in series.**

The critical path is two jobs and one YAML edge:

```
gate (0.1) → Unit Tests (9.0) → Functional Tests Testcontainers (19.7) ≈ 29 min
```

Every other job — Analyze (C#) 6–10, Pack Warnings Gate 2.5, AOT Trim Check 1.3, Coverage Ratchet
0.5, and four sub-minute gates — has finished by minute 10. The 29 minutes are 9.0 and 19.7 **in
series**, and they are in series only because `functional-tests` carries `needs: unit-tests`, an
edge added in the first CI commit (543a2bf0, 2026-03-22) with no recorded rationale. ADR-0038
slimmed everything around it without revisiting it.

Three measurements decide what to do about the 19.7:

1. **The PR-time functional run has never caught anything.** Of **457 `ci.yml` runs (2026-05-06 →
   2026-08-18)**, 57 failed: Unit Tests 47, Coverage Ratchet 9, Pack Warnings Gate 4, AOT Trim Check
   3, Coverage Script Tests 2, **Functional Tests 0**. On 47 of those the `needs` edge skipped it —
   but on the **~410 runs where it executed it passed every time**, for three and a half months.
2. **The cost is the tests, not the containers.** Image pre-pull is 0.4 min. `dotnet test` spends
   ~3 min building the whole `.slnx` in Debug and probing 30 non-matching assemblies, and **~16 min
   is `FunctionalTests.dll` alone** (154 tests, `MaxCpuCount=1`, container restarts between
   classes). No caching option touches that.
3. **The outliers are runner starvation.** No workflow declared `concurrency:`, so a superseded run
   ran to completion — **zero `cancelled` conclusions in 457 runs**. On a 15-run day the 20-job
   public pool saturates: one run sat **2 h 22 min** with Unit Tests merely queued.

ADR-0039 already excused bot PRs from these same steps. That precedent treated a bot PR as a diff
that does not need 19.7 min of Asterisk to be judged — which describes nearly every PR, not just
the bot's.

## What Changes

Three edits across two workflow files, recorded as Sdk/ADR-0051:

1. **The heavy functional steps run on `merge_group` only**, plus an opt-in `ci:functional` label
   for a branch that does touch the AMI/ARI surface. This widens ADR-0039's bot-only skip to every
   `pull_request` and retires ADR-0038 D3's representative-PR-matrix arm; the queue arm stands.
2. **`functional-tests` drops `needs: unit-tests`**, so the two heavy suites start together. This is
   what shortens the queue leg, from 29.7 to ~20.5 min.
3. **`ci.yml` and `codeql.yml` declare `concurrency` with `cancel-in-progress` for `pull_request`
   only.** `merge_group`, `push:[main]` and the weekly CodeQL schedule are never cancelled.

The guard stays at **step** level and the PR matrix stays `[23]`: a job-level skip collapses the
matrix, the required context `Functional Tests (Testcontainers) (23)` never reports, and the PR
strands `BLOCKED` — the #104/#105 incident, which this change must not re-open.

Expected: **PR green ~29 → ~10 min; a landing ~60 → ~31 min; the 51/170-min tail eliminated.**

## Impact

- **Affected specs:** `ci-gating` — one requirement modified, three added, one removed (the
  bot-only carve-out, subsumed).
- **Affected code:** `.github/workflows/ci.yml`, `.github/workflows/codeql.yml`. No `src/**`, no
  package, no public API — nothing cascades to Sdk.Pro or Platform.
- **Required contexts:** unchanged. All nine keep reporting on both events, so no
  branch-protection reconciliation is needed (contrast ADR-0038, which had to rename one).
- **What is traded:** detection latency, not coverage. A functional regression surfaces at queue
  time instead of PR time, costing one queue cycle when it happens. Measured base rate: 0 in 457
  runs.
