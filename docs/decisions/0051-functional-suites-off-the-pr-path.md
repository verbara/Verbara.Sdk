# ADR-0051: The functional matrix runs in the queue, not on every PR push

- **Status:** Accepted
- **Date:** 2026-08-18
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0038 (CI pipeline slimming — D3 established the representative PR matrix this
  ADR retires), ADR-0039 (Dependabot CI load — its addendum established the step-level guard this
  ADR widens), ADR-0043 (longevity evidence off the PR path — the same principle applied to soak
  evidence), ADR-0009 (three-tier test strategy), verbara-meta/ADR-0003 (CI-gating &
  branch-protection standard), verbara-meta/ADR-0016 (docs-only fast-path gate). Change:
  `functional-off-the-pr-path` (openspec).

## Context

ADR-0038 measured a 23-minute median and moved the Asterisk 22 leg of the functional matrix to the
merge queue. Fourteen months of habit later the number is worse, not better: **the median
`pull_request` validation is 29.0 min** (11 code-PR runs, 2026-08-16 → 2026-08-18), with tail
outliers of **51.4 min** and **170.4 min**. Landing a change costs ≈ **60 min** in series — the PR
run plus the `merge_group` run — and the maintainer opens or updates PRs more than three times a
day.

Measuring the critical path rather than the sum of jobs shows the shape (medians):

| Job | Duration | Starts | Ends |
|---|---|---|---|
| **Functional Tests (Testcontainers) (23)** | **19.7 min** | 9.2 | **29.0** |
| Unit Tests | 9.0 min | 0.2 | 9.2 |
| Analyze (C#) — codeql.yml | 6.2–10.0 min | 0 | ≤10 |
| Pack Warnings Gate | 2.5 min | 0.2 | 2.7 |
| AOT Trim Check | 1.3 min | 0.2 | 1.5 |
| Coverage Ratchet | 0.5 min | 9.2 | 9.7 |
| gate / OpenSpec Validate / Audit Test Asserts / Coverage Script Tests | ≤0.3 min | 0 | ≤0.3 |

Everything except the two heavy suites has finished by minute 10. **The 29 minutes are 9.0 + 19.7,
in series** — and they are in series only because `functional-tests` carries `needs: unit-tests`, an
edge introduced in the very first CI commit (543a2bf0, 2026-03-22) with no recorded rationale.
ADR-0038 slimmed everything around that edge without revisiting it.

Three further facts decide this:

1. **The PR-time functional run has never been the signal that stopped a bad change.** Across
   **457 `ci.yml` runs (2026-05-06 → 2026-08-18)**, 57 failed. The failing job was Unit Tests on 47,
   Coverage Ratchet on 9, Pack Warnings Gate on 4, AOT Trim Check on 3, Coverage Script Tests on 2,
   and **Functional Tests on zero**. Stated honestly: on those 47 Unit-Tests failures the functional
   job was skipped by the `needs` edge and had no chance — but on the **~410 runs where it did
   execute it passed every single time**, for three and a half months.
2. **The cost is the tests, not the containers.** Inside the job, setup and image pre-pull take
   0.4 min; `dotnet test` spends ~3 min building the whole `.slnx` in Debug and probing 30
   non-matching assemblies, and **~16 min is `Verbara.Sdk.FunctionalTests.dll` alone** — 154 tests
   against the Asterisk container, serialized by `MaxCpuCount=1` with container restarts between
   classes. No caching change touches this.
3. **The outliers are runner starvation, not execution.** No workflow declared `concurrency:`, so a
   superseded PR run kept its ~42 runner-minutes to completion — **zero `cancelled` conclusions in
   457 runs.** On a burst day (15 runs on 2026-08-17) the 20-job public pool saturates: run
   31975392598 spent **2 h 22 min** with Unit Tests merely *queued*, and run 32021139519 lost
   20.7 min waiting for a functional runner.

ADR-0039 already carved bot-authored PRs out of these same steps. That precedent read a bot PR as
"a diff that does not need 19.7 min of Asterisk to be judged" — which describes almost every PR,
not only the bot's.

## Decision

### D1 — The functional/Testcontainers steps run on `merge_group` only

The two heavy steps of `functional-tests` (`Pre-pull Docker images…`, `Run functional + integration
tests`) run when `github.event_name == 'merge_group'`, or when the PR carries the **`ci:functional`**
label as an explicit opt-in. This widens ADR-0039's bot-only skip to every `pull_request` event and
retires ADR-0038 D3's representative-PR-matrix arm; D3's queue-side arm is untouched.

The guard stays at **step** level and the `pull_request` matrix stays `[23]`. This is not stylistic:
a false *job*-level `if:` collapses the matrix, GitHub reports a single unsuffixed `SKIPPED` check
run, the matrix-suffixed required context `Functional Tests (Testcontainers) (23)` never reports,
and the PR is stranded `BLOCKED` forever (observed on #104/#105; ADR-0039 addendum). The job and its
matrix therefore always run and report success in seconds, doing no work. The `merge_group` term
MUST lead the expression, because on a queue run every `github.event.pull_request.*` field is empty.

### D2 — `functional-tests` no longer depends on `unit-tests`

`needs: [unit-tests, gate]` becomes `needs: gate`, and the job-level condition simplifies to
`!cancelled()` — still never false, per D1's stranding hazard. The two heavy suites now start
together instead of in series.

### D3 — In-flight `pull_request` runs are superseded

`ci.yml` and `codeql.yml` declare `concurrency` keyed on the PR number, with
`cancel-in-progress` true **for `pull_request` only**. `merge_group` is never cancelled: each queue
entry is the authoritative landing gate. `codeql.yml`'s `push:[main]` and weekly schedule are never
cancelled either — they maintain the default-branch security baseline.

## Consequences

- **PR green drops from ~29 min to ~10 min**; the new critical path is Unit Tests (9.0–11.4) racing
  Analyze (C#) (6–10). **The queue leg drops from 29.7 to ~20.5 min** (D2), so a landing costs
  ≈ 31 min instead of ≈ 60.
- **Nothing lands on `main` unvalidated.** Every landing still runs the full `[22, 23]` matrix in
  the queue, and `(23)` remains a required context reporting on both events. What moves is *when* a
  functional regression is reported: queue time instead of PR time. Measured expected cost of that
  latency: **0 occurrences in 457 runs**. When it does happen the PR loses one queue cycle and pops
  out of the queue red — noisier than a red PR check, but not a landing.
- **A doomed run now burns ~2 extra runner-jobs** (D2 lets functional start before Unit Tests
  fails, on ~10% of runs). D3 more than refunds that: it retires the whole class of superseded runs.
- **The `ci:functional` label is the escape hatch** for a branch that genuinely touches the AMI/ARI
  surface and wants the answer before the queue. It is opt-in on purpose — a default-on heuristic
  over changed paths would have to model which C# edit can move a dialplan test, and getting that
  wrong is silent.
- ADR-0038's addendum still governs: under classic branch protection the `merge_group` full-matrix
  run is **detection**, not automatic hard enforcement. This ADR does not change that, and the
  detection surface is unchanged — the same matrix, on the same event.

## Alternatives considered

- **Shard `Unit Tests` (~9 min) across two runners.** Real (~6–7 min PRs) but it *suffixes a
  plain-named required context*, forcing the same-moment branch-protection reconciliation that
  ADR-0038 had to perform once already (verbara-meta/ADR-0003). Deferred until ~10-min PRs chafe.
- **Point the functional `dotnet test` at the 8 container-backed projects instead of the `.slnx`.**
  Saves ~2–3 min of Debug build and dead assembly probing, but a new `[Trait("Category",
  "Integration")]` in an unlisted project would then silently stop running. Wants a guard script
  first — a spec-drift class this repo already knows how to close.
- **Drop the Asterisk 22 leg from `merge_group`.** Rejected in ADR-0038 and rejected again: that
  deletes coverage rather than moving it.
- **Move the functional suites to a nightly schedule with no queue gate.** Rejected — it converts
  `main`'s gate into after-the-fact detection, exactly what ADR-0043 designed its scheduled train to
  avoid. A nightly full-matrix run on `main` is welcome as an *addition* (image-drift canary), never
  as the replacement.
- **NuGet / build caching.** Measured ≤1 min, almost all of it off the critical path. Not worth the
  cache-invalidation surface.
