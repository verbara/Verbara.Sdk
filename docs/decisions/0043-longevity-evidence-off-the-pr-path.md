# ADR-0043: Longevity evidence is produced on a scheduled train, never on the PR path

- **Status:** Proposed
- **Date:** 2026-08-02
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0005 (Testcontainers for integration), ADR-0009 (three-tier test strategy),
  ADR-0021 (AMI heartbeat strategy — the half-open-connection contract a soak is meant to exercise),
  ADR-0038 (CI pipeline slimming — the cost regime this ADR must not undo),
  ADR-0039 (Dependabot CI load — the self-hosted-runner deferral this ADR inherits),
  verbara-meta/ADR-0003 (CI-gating & branch-protection standard — required-check reconciliation),
  verbara-meta/ADR-0005 (public-repo content rule).
  Change: `longevity-soak-and-chaos` (openspec).

## Context

This repo publishes a telephony SDK. Its consumers run it as a **long-lived 24/7 process**: an AMI
connection held open for weeks, a channel/session index mutated millions of times, an event pump
that never drains to idle. The failure modes that matter in that regime — a file-descriptor leak on
reconnect, a few hundred KB of heap creep per hour, an unobserved-task exception that only surfaces
after the tenth reconnect — are **invisible to every gate this repo currently runs**. Unit tests
finish in milliseconds; the functional suite tears its stack down after minutes. Nothing observes
the SDK over hours.

That gap has a name in this repo's own record. The 2026-05 test-audit plan listed it as finding 5,
"Longevidad ZERO", and scoped a *Fase 3* that was never started. Meanwhile the README makes
production-shaped claims ("production-grade", steady-state throughput numbers) that no long-running
evidence backs.

The obvious fix — "add a soak and a chaos matrix to CI" — collides head-on with two accepted
decisions:

1. **ADR-0038** measured Sdk CI as the ecosystem's slowest and most-failing gate (median 23 min per
   validation, ~25% failed runs; ≈46 min per landing) and *deliberately removed* work from the PR
   path: coverage collected once, a representative single-version functional matrix on
   `pull_request` with the full matrix reserved for `merge_group`. It also recorded, in its 2026-07-15
   addendum, that a mis-declared required check **hangs the merge queue** — the failure mode
   verbara-meta/ADR-0003 codifies.
2. **ADR-0039** then cut *bot-authored* PR load for the same reason, and explicitly **deferred**
   self-hosted runners with a named trigger, on two grounds that apply verbatim here: GitHub
   discourages self-hosted runners on public repos, and a lab-offline runner would hold the merge
   queue hostage for the full 60-minute `check_response_timeout` **per PR**.

A 24-hour soak is roughly **1,440 runner-minutes per execution** — about sixteen times the ≈90
compute-min ADR-0039 measured for an entire merged Dependabot PR. A chaos matrix that brings up
Asterisk plus Postgres plus NATS plus Toxiproxy is the most expensive fixture shape this repo has.
Neither can go anywhere near a per-PR gate without reversing both ADRs.

The question this ADR settles is therefore not *whether* to produce longevity evidence, but **what
evidence, on what train, read by whom, and with what consequence on failure** — such that the
answer is compatible with ADR-0038 and ADR-0039 rather than a quiet rollback of them.

Two facts about the existing substrate shape the answer, and both were verified against the tree
rather than assumed:

- The chaos substrate **already exists and is running**: Toxiproxy is in the functional stack, the
  fixture publishes an AMI proxy, `ToxiproxyControl` can add *any* toxic type the Toxiproxy API
  accepts, and `DockerControl` can kill/start/restart/pause containers. Network-partition,
  latency, throughput and reconnection suites already exercise it. Extending chaos is therefore
  **incremental work on a live fixture**, not new infrastructure — and it fits inside the existing
  functional job's cost envelope.
- The **soak substrate does not exist**. There is no long-running host process to observe, and the
  SIPp scenario directory checked into `docker/functional/sipp-scenarios/` contains only a
  `.gitkeep` — zero scenarios. Both SIPp wrappers hard-code a bounded call count (`-m 1` /
  `-m {calls} -r 1`), so "steady-state 100 calls/min for 24 h" is not a configuration change; it is
  a driver that has to be built. The soak is the expensive, uncertain half; the chaos and benchmark
  work is the cheap, certain half.

## Decision

Longevity evidence is a **first-class, scheduled, non-gating product of this repo**, produced on its
own train and read by a human. Concretely:

- **D1 — Nothing in this program is ever a required check.** No soak job, chaos-matrix job, or
  benchmark job SHALL be added to `main`'s required status checks, and none SHALL run on
  `pull_request` or `merge_group`. ADR-0038's cost regime and ADR-0039's bot-load relief stay intact
  by construction: the PR path's runner-minute budget is **unchanged by this program**. This is not
  a soft preference — a required check that can take 24 hours to report would exceed the merge
  queue's 60-minute `check_response_timeout` and hang every landing, which is precisely the
  never-reporting-context failure ADR-0038's addendum and ADR-0039's addendum both had to repair.
- **D2 — The evidence this repo commits to producing is three named artifacts, not "a soak".**
  (a) a **resource-stability trail** — periodic heap and open-file-descriptor samples across a
  multi-hour run, retained as a downloadable artifact; (b) a **fault-recovery matrix** — for each
  injected fault, evidence that the SDK recovered to a serving state and left no orphaned resource;
  (c) a **hot-path timing series** for the operations the README quantifies. Anything not reducible
  to one of those three is out of scope for this decision.
- **D3 — Cadence is weekly, and weekly is the ceiling.** The soak runs on a weekly `schedule:` trigger
  plus `workflow_dispatch`. Weekly is chosen because it is the coarsest cadence at which a leak
  introduced in week *N* is still attributable to a bounded set of commits, and because the existing
  `perf-regression.yml` already established a weekly scheduled slot for exactly this class of
  evidence. Nightly is rejected in D-alternatives below. The chaos additions ride the **existing**
  functional job (they are ordinary functional tests on an already-running fixture) and add no new
  schedule.
- **D4 — The run is bounded by wall-clock and by budget, and the bound is declared in the workflow.**
  Every scheduled longevity job SHALL carry an explicit `timeout-minutes` and SHALL be
  `workflow_dispatch`-able so a human can reproduce it without waiting a week. A soak that would
  exceed its declared bound fails loudly rather than occupying a runner indefinitely.
- **D5 — Self-hosted runners stay deferred, on ADR-0039's terms.** This program does **not** trip
  ADR-0039's named trigger. It runs on GitHub-hosted standard runners, which are free for this
  public repo, or it does not run. If hosted-runner limits make a 24-hour job unworkable, the
  correct response is to **shorten the observation window** (D7) — never to introduce a self-hosted
  runner into a public repo's CI.
- **D6 — A failed soak opens an Issue; it does not block anything.** On failure the workflow SHALL
  create a GitHub Issue carrying the snapshot trail and the run link. That Issue is the *only*
  enforcement mechanism. The **operator** (this repo's maintainer) is the reader; there is no
  automated bisect, no auto-revert, and no branch-protection consequence. A red soak is a triage
  item with evidence attached, and it is triaged as regular repo work.
- **D7 — The observation window is a tunable, the acceptance thresholds are the contract.** The
  durable commitment is the *shape* of the assertion — heap delta bounded after a warm-up period,
  descriptor count bounded, zero unhandled exceptions — not the literal number 24. The window MAY be
  shortened (to fit a runner limit) or lengthened without superseding this ADR; changing what is
  asserted requires a new ADR.
- **D8 — Thresholds are committed data with a stated warm-up, never inline magic numbers.** The
  first N runs of a new soak establish the numbers; until they do, the job runs in
  **observe-only** mode (collect and publish the trail, do not fail). Promoting it to
  fail-on-threshold is a deliberate, separate step. A threshold invented before any trail exists is
  noise, and noise that opens Issues trains the operator to ignore Issues.
- **D9 — Phase closure is four consecutive green weekly soaks.** The program is not "done" when the
  workflow merges; it is done when the *signal* has proven stable, which is what the original plan's
  four-consecutive-green criterion encodes. Four weeks is the minimum that distinguishes a stable
  signal from a lucky one at weekly cadence, and it is retained unchanged.
- **D10 — This ADR owns hot-path *coverage*, not the perf *gate*.** Extending the benchmark set is in
  scope here. The regression gate that compares results against a committed baseline, and the
  baseline file itself, belong to the separate `enforce-unguarded-public-claims` change and are
  explicitly **out of scope** for this decision. Two changes touching one workflow file is a merge
  conflict; two changes owning one *decision* is a governance conflict, and only the second kind is
  worth an ADR clause.

## Consequences

- **Positive:** the "production-grade 24/7" posture stops being an unbacked adjective. There is a
  named, dated, downloadable artifact behind it, produced on a schedule, with a stated acceptance
  shape (D2, D7).
- **Positive:** ADR-0038 and ADR-0039 are preserved *by construction* rather than by good intentions.
  D1 makes the PR-path cost of this entire program exactly zero, so no future contributor has to
  re-derive why the soak is not a required check.
- **Positive:** the failure mode that actually threatens this repo — a required check that cannot
  report inside the queue's timeout — is designed out rather than mitigated. The merge queue never
  learns that longevity jobs exist.
- **Positive:** D8's observe-only warm-up prevents the single most likely way this program dies:
  auto-created Issues from thresholds nobody calibrated, ignored within a month.
- **Negative / accepted:** detection latency is up to **one week**. A leak introduced on Monday is
  found the following Sunday, against a week of commits rather than one. Accepted: a leak that takes
  hours to manifest was never going to be caught at PR time at any affordable cost, and a week of
  Sdk commits is a bisectable set.
- **Negative / accepted:** a failing soak blocks nothing. A regression can ship in a release cut
  between two soak runs. Accepted deliberately — the alternative (gating releases on a 24-hour job)
  makes every release wait a day, and D6's Issue is a strictly better trade than a release train
  that stalls on infrastructure flake.
- **Negative / accepted:** scheduled jobs on a public repo are best-effort. GitHub may delay or drop
  a `schedule:` trigger under load, so "four consecutive weekly soaks" (D9) may take more than four
  calendar weeks. `workflow_dispatch` (D4) is the manual recovery path.
- **Neutral:** the chaos additions change no cadence at all — they are functional tests on a fixture
  that already runs, so they inherit ADR-0038's representative-PR / full-queue matrix policy
  unchanged and are governed by the `ci-gating` capability, not by this ADR.
- **Neutral:** this ADR governs *how longevity evidence is produced and read*. Which specific faults
  are injected, and which hot paths are measured, are implementation scope owned by the
  `longevity-soak-and-chaos` change and its living spec.

## Alternatives considered

- **Option B: make the soak a required check (or a release gate).** Highest assurance —
  a leak could never land or ship. **Rejected**, and not narrowly. A required check that takes hours
  cannot report inside the merge queue's 60-minute `check_response_timeout`; it would hang every
  landing in the repo, which is exactly the never-reporting-context failure ADR-0038's addendum
  documented empirically (`enqueuePullRequest` refused until the offending context was dropped) and
  ADR-0039's addendum repaired a second time. It would also reverse ADR-0038's entire premise by
  putting the most expensive possible job on the hottest possible path.
- **Option C: nightly instead of weekly.** Seven times the detection resolution; a leak is
  attributable to a single day of commits. **Rejected** at this repo's commit volume: nightly means
  ~7 × 1,440 runner-minutes/week for a signal that, by construction, changes slowly. Weekly matches
  the cadence `perf-regression.yml` already established for scheduled evidence, keeps one triage
  item per week instead of seven, and D3 sets it as a **ceiling** so this cannot drift upward
  without a new decision. Revisit only if two consecutive soaks fail for *different* causes — i.e.
  when detection resolution is demonstrably the binding constraint.
- **Option D: run the soak on a self-hosted runner in the operator's lab.** Removes every hosted
  runner limit and makes a true 24-hour (or 72-hour) window trivial. **Rejected here and deferred
  under ADR-0039's existing terms**, on that ADR's own two grounds: GitHub discourages self-hosted
  runners on public repos because a fork PR can execute arbitrary code on them, and an offline
  lab runner holds the merge queue hostage for the full 60-minute timeout per PR. D5 makes the
  refusal explicit so this is not re-litigated as an obvious optimisation. Note that D1 removes
  most of the second objection's force — but the first objection is fatal on its own for a public
  repo.
- **Option E: no soak; rely on consumer field reports.** Zero CI cost; downstream repos run real
  workloads. **Rejected.** It inverts the open-core contract: this repo is the *root* of the
  dependency chain (`Sdk` → `Sdk.Pro` → `Platform`), so a leak here is discovered by consumers, at
  their cost, in their production. It also leaves the README's production claims permanently
  unbacked, which is the same class of gap the sibling `enforce-unguarded-public-claims` change
  exists to close.
- **Option F: fold the perf-gate and `baseline.json` into this decision.** One ADR covering all
  scheduled evidence would read tidier. **Rejected** — `enforce-unguarded-public-claims` already
  owns the claim-guard mechanism (baseline file, comparison, failure semantics). Two decisions
  owning one gate is how a gate ends up with two incompatible thresholds. D10 draws the line at
  *coverage* (this ADR) versus *enforcement* (that change).
- **Option G: shorten the window to ~2 hours and gate it per-PR.** Cheap enough to be a required
  check. **Rejected:** two hours is far below the horizon where FD and heap creep separate from
  warm-up noise, so it would buy the full cost of a required long job while delivering a signal too
  weak to act on — and it would still add ~120 min to every landing, doubling down against ADR-0038.
