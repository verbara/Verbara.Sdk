---
tier: MEDIANO
owner: Harol
approver: Harol
stakeholder: Prospective and existing SDK users, who read README.md numbers as promises — and the maintainer, who carries the credibility cost when one silently stops being true
decision_ref: Sdk/ADR-0042
---

# Proposal: enforce-unguarded-public-claims

## Why

`README.md` sells this SDK on numbers. Most of them are now executable: the observability counts
are pinned by `Tests/Verbara.Sdk.OpenTelemetry.Tests/MarketingClaimsTests.cs`, the code snippets are
Roslyn-compiled by `Tests/Verbara.Sdk.DocSnippets.Tests/`, and the AOT-ready badge is backed by
`tools/AotCanary/` (22 project references, AOT publish + smoke-run) via `tools/verify-aot.sh` and
`.github/workflows/aot-validate.yml`. **Two claims were left out, and nothing executes them.**

1. **AMI throughput — `1.53M events/sec` (653 ns).** `.github/workflows/perf-regression.yml` exists
   but is observational by design: weekly `cron: '0 4 * * 0'` plus `workflow_dispatch`, every
   benchmark step suffixed `|| true`, output a downloadable JSON artifact for manual comparison. Its
   own header names the missing piece — a gate diffing against
   `Tests/Verbara.Sdk.Benchmarks/baseline.json` — and calls it "a follow-up issue". **That file does
   not exist.** So the workflow can go green while `AmiProtocolReaderBenchmark` has halved in
   throughput, or while it fails to run at all (`|| true` swallows it). The README number has no
   enforcement whatsoever.

2. **Turn-detection — `94.3% English accuracy, ~12 ms CPU inference`** (README lines 67 and 472).
   `Tests/fixtures/audio/turn-boundaries/` does not exist; the labelled-corpus harness was deferred
   for want of licensable labelled audio and never returned.
   `Tests/Verbara.Sdk.VoiceAi.TurnDetection.Tests/` exercises the detector's mechanics against the
   real ONNX but asserts nothing about accuracy, and `Tests/Verbara.Sdk.Benchmarks/` does not
   reference the TurnDetection package at all — so the `~12 ms` half is unmeasured too.

   Worse, `94.3%` is **not our measurement**. It is the upstream Pipecat smart-turn model card's
   figure, restated in first-party voice, and its attribution has already drifted: the README labels
   the model *smart-turn-v3.2* while linking to the *smart-turn-v3* card, the package
   `<Description>` says *v3*, and the embedded resource is `smart-turn-v3.2-cpu.onnx`. Nothing binds
   the cited number to the artifact shipped — the `.onnx` is Git-LFS tracked and could be replaced
   without touching a single assertion.

The reason both were deferred is cost, and that cost is real. ADR-0038 rebuilt this repo's CI
because Sdk was the ecosystem's slowest gate (median 23 min, ~25% failed runs); ADR-0039 then cut
what a bot PR runs because Dependabot opens ~31 PRs/month. A 20–25 min BenchmarkDotNet job on
`pull_request` would undo both, and on `merge_group` it would be worse — the queue is serial, so
every landing pays it in sequence. **So the fix is not "gate everything the same way".** It is to
separate the expensive question (*did this get slower?* — weekly, enforcing) from the cheap one
(*does the document still match the recorded measurement?* — per-PR, riding a job that already
runs), and to be honest where neither is reachable. That policy is recorded as `Sdk/ADR-0042`.

## What Changes

1. **A claim guard registry (`claim-guards` capability).** Every quantitative claim in a living
   public document declares exactly one guard class — **ENFORCING**, **COHERENCE**, or
   **ATTRIBUTED** — and a claim with no class must not ship. Dated `CHANGELOG` history and archived
   docs are excluded as period-correct records, the same exclusion `docs-brand-consistency` already
   applies.

2. **The weekly perf workflow becomes enforcing (still weekly).** Add the missing
   `Tests/Verbara.Sdk.Benchmarks/baseline.json` — hosted-runner means with per-benchmark tolerance
   bands, **not** the README's workstation figures — plus a comparison step that fails the scheduled
   job on a breach and **fails closed** when a result is missing or unparseable (today `|| true`
   would render a broken benchmark green). A breach also files/updates an issue, so the signal
   outlives the run. Cadence, triggers and runner are unchanged: no `pull_request` trigger, no
   `merge_group` trigger, no new required check, no branch-protection reconciliation
   (Sdk/ADR-0042 D2–D5; the ADR-0038 / verbara-meta/ADR-0003 pending-context failure mode is
   deliberately not touched).

3. **A per-PR coherence test for the published Performance table.** The README's absolute numbers
   are reproduction claims — "measured on this machine, on this date, reproducible". Bind them to a
   committed record of that measurement (machine, runtime, BenchmarkDotNet version, date) and assert
   the two agree in an ordinary unit test riding the existing `Unit Tests` job, the
   `MarketingClaimsTests` precedent. Milliseconds, zero new jobs (Sdk/ADR-0042 D3, D7).

4. **The turn-detection claim is split into its two honest halves.**
   - `94.3% accuracy` → **ATTRIBUTED**: reworded as the upstream model card's figure, cited to the
     *matching* model version, and pinned by a content-hash assertion on the embedded ONNX so a
     model swap breaks the build instead of silently orphaning the number. This also resolves the
     existing v3 / v3.2 inconsistency across README, `<Description>` and resource filename.
   - `~12 ms CPU inference` → **ENFORCING** on the weekly train: a TurnDetection benchmark added to
     `Tests/Verbara.Sdk.Benchmarks/` (which does not reference the package today) and a
     `baseline.json` band, exactly like the AMI row.

5. **The first-party accuracy gate stays deferred — with its blocker written down.** No corpus of
   labelled turn-boundary speech has been identified whose licence permits redistribution from a
   public MIT repository, and recording one in-house raises consent, licensing and Git-LFS budget
   questions that must be answered *before* any recording. **This proposal does not solve that.** It
   records it as an open question and forbids the claim from being presented as first-party until a
   gate exists (Sdk/ADR-0042 D9). The originally-planned shape — ≥ 20 labelled WAVs (10 turn-end
   positive, 10 turn-mid negative), precision ≥ 0.85 / recall ≥ 0.85 — is preserved as the target
   for whenever the corpus question is answered, not scheduled here.

## Capabilities

### New Capabilities

- `claim-guards`: the reverse coupling between shipped public documentation and executable
  verification — which claims must carry a guard, which class of guard each may use, what a
  scheduled gate must do on breach, and how a claim that cannot be economically measured in-repo is
  handled (attributed and pinned, or deleted). Chosen over folding into `ci-gating` because
  `ci-gating` is event-scoping policy owned by ADR-0038/ADR-0039 (which validation runs on which
  event, coverage collected once); this is documentation honesty that happens to be enforced partly
  in CI, and is nearer in kind to `docs-brand-consistency` (Sdk/ADR-0042, Option F).

### Modified Capabilities

- None. `ci-gating` is deliberately untouched: this change adds no `pull_request` or `merge_group`
  validation, no new job, and no required check, so the event-scoping policy ADR-0038 and ADR-0039
  recorded is unchanged by construction.

## Impact

- `.github/workflows/perf-regression.yml` — baseline comparison step, fail-closed on missing
  results, breach notification. Schedule and triggers unchanged.
- `Tests/Verbara.Sdk.Benchmarks/baseline.json` — new committed artifact; human-authored updates
  only, no CI write-back (Sdk/ADR-0042 D6, mirroring the coverage floor's manual ratchet).
- `Tests/Verbara.Sdk.Benchmarks/` — new TurnDetection benchmark + the project reference it needs.
- A unit-test project already inside the default unit filter (the `MarketingClaimsTests` home) —
  coherence tests for the Performance table and the ONNX content-hash pin. The `Unit Tests` job
  already checks out with `lfs: true`, so the Git-LFS-tracked model is present.
- `README.md` — turn-detection wording reworded to attributed voice with a version-consistent
  citation; Performance table gains its measurement-record binding. **No number is changed** by this
  proposal.
- `src/Verbara.Sdk.VoiceAi.TurnDetection/` — package `<Description>` and README model version
  aligned to the shipped `smart-turn-v3.2-cpu.onnx`.
- `docs/decisions/0042-public-claim-guard-classes.md` — new ADR (Proposed).
- Downstream (Pro / Platform): **none.** No public API surface, no behaviour, no package contract
  changes. Docs, tests and one workflow only.
- CI cost: **+0 min** on `pull_request` and `merge_group` beyond the sub-second coherence tests
  inside an existing job. The weekly job's wall-clock is unchanged (a JSON comparison).

## Architectural Risk

**Level:** LOW. **Affected:** `.github/workflows/perf-regression.yml` (scheduled only — not in any
required-check set, so a defect here cannot block a landing or hang the merge queue); the `Unit
Tests` required job, which gains a handful of sub-second assertions; `README.md` wording. No `src/`
behaviour changes, no public API changes, so no downstream cascade to Pro or Platform.
**Mitigation:** the expensive gate stays on the existing weekly trigger and adds no check-run name,
so the verbara-meta/ADR-0003 never-reporting-context failure mode (which ADR-0038 had to reconcile
by hand) is structurally out of reach. Tolerance bands are calibrated from several observed
scheduled runs before the gate is armed, so its first act is not a false red; the gate is landed
observing-only and flipped to failing in a second step. Baselines are human-authored with no CI
write-back, so the threshold cannot ratchet away from what it protects. The only irreversible-feeling
piece is README wording, and it is a reversible text edit. The genuine residual risk is honesty, not
breakage: the deferred accuracy corpus must be recorded as an open question with its blocker rather
than quietly closed — this proposal treats that as a deliverable, not a footnote.
