# ADR-0042: Public quantitative claims carry a declared guard class

- **Status:** Proposed
- **Date:** 2026-08-02
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0038 (CI pipeline slimming — the PR wall-clock budget this decision spends
  against), ADR-0039 (Dependabot CI load — the per-PR job multiplier), ADR-0009 (three-tier test
  strategy), ADR-0037 (cross-repo ADR reference convention),
  verbara-meta/ADR-0003 (CI-gating & branch-protection standard — required-check reconciliation,
  manual-ratchet floors with no CI write-back), verbara-meta/ADR-0005 (public-repo content rule).
  Change: `enforce-unguarded-public-claims` (openspec). Capability: `claim-guards`.

## Context

`README.md` is this repo's primary acquisition surface, and its strongest differentiators against
`asterisk-java` are **numbers**: a seven-row Performance table, a nine-`ActivitySource` /
fifteen-`Meter` observability inventory, an AOT-ready badge, and a turn-detection accuracy figure.
A number in a public README is a promise. A promise nothing executes is a liability that decays
silently — the code changes, the number does not, and nobody finds out until a prospective user
does.

A prior phase set out to give every such claim a CI guard, and most of them landed:

- **Observability counts** — `Tests/Verbara.Sdk.OpenTelemetry.Tests/MarketingClaimsTests.cs` pins
  the `ActivitySource` / `Meter` / `HealthCheck` / semantic-convention counts against the SDK's own
  source of truth. It rides the existing `Unit Tests` job.
- **Documentation snippets** — `Tests/Verbara.Sdk.DocSnippets.Tests/` Roslyn-compiles the code
  blocks so a README snippet cannot reference an API that no longer exists.
- **AOT readiness** — `tools/AotCanary/` (22 project references) publishes Native AOT and
  smoke-runs, driven by `tools/verify-aot.sh` from `.github/workflows/aot-validate.yml`.

Two claims were left without enforcement, and each was left for a different reason:

1. **AMI throughput.** `README.md` claims *"AMI event parse + dispatch — **1.53M events/sec**
   (653 ns)"*. `.github/workflows/perf-regression.yml` exists but is **observational by design**:
   weekly `cron: '0 4 * * 0'` plus `workflow_dispatch`, every benchmark step suffixed `|| true`, and
   the output is a downloadable JSON artifact that "operators compare manually for now". The
   workflow's own header names the missing piece — a gate diffing against
   `Tests/Verbara.Sdk.Benchmarks/baseline.json` — and calls it "a follow-up issue". That file does
   not exist. The workflow also explains *why* a per-PR gate was rejected: BenchmarkDotNet needs
   ~3–5 min per benchmark for stable numbers, so five hot paths cost ~20–25 min, and hosted-runner
   neighbour noise makes the measurement unreliable at PR granularity. That reasoning is sound and
   this ADR does not overturn it — it completes it.

2. **Turn-detection accuracy.** `README.md` claims *"94.3% English accuracy, ~12 ms CPU
   inference"* in two places. The original plan was a labelled-audio harness — ≥ 20 WAV samples
   (10 turn-end positive, 10 turn-mid negative) under `Tests/fixtures/audio/turn-boundaries/`
   driving the bundled ONNX to precision ≥ 0.85 / recall ≥ 0.85 — deferred for lack of labelled
   audio. That directory does not exist and the blocker is unchanged: no corpus of labelled
   turn-boundary speech has been identified whose licence permits redistribution from a public MIT
   repository. `Tests/Verbara.Sdk.VoiceAi.TurnDetection.Tests/` covers the detector's mechanics
   (unit-level, against the real ONNX) but asserts nothing about accuracy, and
   `Tests/Verbara.Sdk.Benchmarks/` does not reference the TurnDetection package at all, so the
   `~12 ms` half is unmeasured too.

The 94.3% figure additionally is **not this repo's measurement**. It is the upstream Pipecat
smart-turn model card's number, restated in first-party voice. The attribution has already drifted:
the README labels the model *smart-turn-v3.2* while linking to the *smart-turn-v3* model card, the
package `<Description>` says *v3* , and the embedded resource is `smart-turn-v3.2-cpu.onnx`. Nothing
binds the cited measurement to the artifact actually shipped — swapping the `.onnx` (Git-LFS
tracked) would leave the accuracy claim untouched and unverified.

The constraint that shapes the answer is cost. ADR-0038 rebuilt this repo's CI around the finding
that Sdk was the ecosystem's slowest gate (median 23 min, ~25% failed runs) and traded full-matrix
PR coverage for representative feedback; ADR-0039 then cut what a bot PR runs because Dependabot
opens ~31 PRs/month, so any per-PR job is multiplied by ~31 before a human ever sees it. Adding a
20–25 min benchmark job to `pull_request` would undo both. Adding it to `merge_group` is worse:
the queue is serial, so the cost lands on every landing in sequence. Any new *required* check also
triggers the branch-protection reconciliation that verbara-meta/ADR-0003 codifies and ADR-0038 had
to perform, whose failure mode is a permanently pending context and a hung merge queue.

So "guard every claim" cannot mean "gate every claim the same way". This ADR records what guarding
a claim actually obliges, and what happens when the honest answer is that it cannot be guarded.

## Decision

Public quantitative claims are guarded by **class**, not uniformly. Concretely:

- **D1 — Every quantitative public claim carries exactly one declared class.** The classes are:
  **ENFORCING** (an executable gate fails when reality diverges), **COHERENCE** (a cheap, per-PR
  consistency check that the published number equals a committed recorded measurement),
  **ATTRIBUTED** (the number is a third party's measurement, cited as theirs and pinned to the
  artifact it describes), and **EVIDENCE** (a dated record of a measurement we took ourselves
  against something we do not control). A quantitative claim in a living public document with no
  declared class MUST NOT ship. "Living" excludes dated `CHANGELOG` history and archived docs, which
  stay verbatim as period-correct records — the same exclusion the `docs-brand-consistency`
  capability already applies.

- **D1a — EVIDENCE is a disposition, not a guard, and it is declared per document rather than
  inferred from a folder.** It applies where none of the other three *can* apply: the measurement is
  first-party, so ATTRIBUTED's citation and third-party voice are wrong; the document itself is the
  record, so COHERENCE has nothing to bind to; and re-measuring requires something outside this
  repo's control — vendor credentials, paid egress, a live third-party service that can change with
  no commit here — so ENFORCING is not reachable under D2. An EVIDENCE claim carries no guard
  obligation and stays verbatim, but it MUST carry the date and the conditions of the measurement,
  because that is the whole of what makes it honest. It MUST NOT be used to excuse a number that
  could be gated: a claim about this repo's own contents (test counts, package counts, surface
  counts) is ENFORCING however inconvenient, even when it sits in a document whose other figures are
  EVIDENCE. The provider guides under `docs/guides/` are the motivating case — dated wire captures
  against live vendor APIs — and `provider-wire-conformance.md`'s counts of *this repo's own tests*
  are the motivating counter-case: same document, different class.

- **D2 — Expensive measurement never runs per-PR.** Anything requiring statistically meaningful
  timing (BenchmarkDotNet, model inference over a corpus) runs on the existing weekly schedule plus
  `workflow_dispatch`. It runs on neither `pull_request` nor `merge_group`. This preserves ADR-0038's
  PR budget and ADR-0039's bot-PR economics, and it is also the *statistically* correct call:
  hosted-runner neighbour noise at PR granularity produces false reds, and a gate that cries wolf
  is routed around until it means nothing.

- **D3 — Per-PR guards add no CI job and no required check.** COHERENCE guards are implemented as
  ordinary unit tests riding the existing `Unit Tests` job — the `MarketingClaimsTests` precedent.
  This is deliberate: a new job means a new check-run name, which means a branch-protection edit,
  which is the verbara-meta/ADR-0003 never-reporting-context failure mode ADR-0038 had to reconcile
  by hand. A guard that costs a protection edit is not cheap, whatever its runtime.

- **D4 — The scheduled performance gate is a relative-regression gate, not an absolute one.** It
  compares each benchmark's mean against a committed hosted-runner baseline
  (`Tests/Verbara.Sdk.Benchmarks/baseline.json`) within a per-benchmark tolerance band, and MUST
  fail closed: a missing or unparseable result counts as a breach, because every benchmark step is
  suffixed `|| true` and a broken filter would otherwise read as green. It does **not** compare
  against the README's workstation figure. The README table states its machine (AMD Ryzen 9 9900X,
  12C/24T); a shared hosted runner is materially slower and noisier, so an absolute comparison
  would either never pass or would force the published numbers down to hosted-runner values —
  understating the product, which is a different kind of dishonesty.

- **D5 — A scheduled breach produces a durable artifact, not just a red run.** A weekly failure
  nobody is notified of is observational again under a different name. A breach MUST leave
  something assignable that outlives the run (a filed/updated issue naming the benchmark, the
  baseline, the observed value and the band), in addition to failing the scheduled job.

- **D6 — Baselines move only by human-authored commit.** CI never writes back to `baseline.json`.
  A baseline change is a reviewed PR stating which benchmark moved, in which direction, by how much,
  and why. This mirrors the coverage floor's manual-ratchet semantics (ADR-0038 D2,
  verbara-meta/ADR-0003) for the same reason: a self-updating threshold ratchets away from the thing
  it was supposed to protect.

- **D7 — Published workstation figures are reproduction claims, guarded by COHERENCE.** The
  README's absolute numbers assert "this is what we measured, on this machine, on this date, and you
  can reproduce it". They are bound to a committed record of that measurement (machine, runtime,
  BenchmarkDotNet version, date) and a per-PR test asserts the two agree. Regression protection is
  D4's job; correspondence between document and record is this one's. The two are separate failures
  and are detected separately.

- **D8 — A claim that cannot be measured in-repo is ATTRIBUTED or deleted — never left bare.**
  ATTRIBUTED is not a formatting convention; it obliges three things together: (a) a citation to the
  third party's published measurement, (b) wording that reads as theirs rather than as a first-party
  benchmark, and (c) a pin binding the citation to the artifact actually shipped — for the bundled
  ONNX, an assertion on the model's content hash, so replacing the model breaks the build rather
  than silently orphaning the number. Citation, wording and pin MUST refer to the same model
  version; the current v3 / v3.2 split across README link, package `<Description>` and resource
  filename is exactly the drift this guards.

- **D9 — Deferral is declared with its blocker, and the blocker is the one that survives being
  checked.** When a first-party gate is not economically reachable, the deferral is written down
  with the specific blocker and the condition that would unblock it, and the claim MUST NOT be
  presented as first-party until the gate exists.

  For turn-detection accuracy, the blocker is **labelling, not licensing**. An earlier draft of this
  ADR said no corpus had been identified whose licence permits redistribution from a public MIT
  repository; the survey run for `enforce-unguarded-public-claims` §5.1 found that to be false.
  **AMI, ICSI and HCRC Map Task are all CC BY 4.0**, which grants redistribution and commercial use
  against attribution alone. The blocker that does survive:

  > No corpus has been identified that combines a redistribution-permitting licence with ready-made
  > turn-boundary labels. The three CC BY 4.0 conversational corpora may be redistributed from this
  > repo but carry only word/segment timings and dialogue-act coding, so turn-end versus turn-mid
  > labels would have to be derived and hand-verified by us. The only corpus with the native label —
  > Pipecat's own `smart-turn-data-v3.x`, whose `endpoint_bool` field is exactly the target and
  > which was used to train the model actually shipped — declares no licence at all (the
  > BSD-2-Clause covers the model repository, not the datasets), and roughly four fifths of it is
  > commercial-TTS output whose redistribution is governed by the TTS vendors' terms rather than
  > Pipecat's.

  The unblocking condition is therefore a **decision**, not a search: derive roughly twenty clips
  from AMI or Map Task under CC BY 4.0 with in-repo attribution, and accept that the gate's ground
  truth is our own hand-derived labelling rather than a third party's. A second, cheaper path exists
  and is worth pursuing in parallel: ask pipecat-ai to declare a licence on the dataset cards, which
  they already describe as open source. Absent either, the figure stays ATTRIBUTED to upstream —
  and, per D8, attributed to the **version actually shipped**, whose published accuracy differs from
  its predecessor's by nearly nine percentage points.

  LDC-distributed corpora (Switchboard, Fisher, CallHome, DIHARD) are excluded on a separate and
  firmer ground: the LDC non-members agreement forbids redistribution outside the user's research
  group, and its limited-excerpt allowance is scoped to non-commercial research publications, which
  test fixtures in a shipped SDK are not.

## Consequences

- Positive: the two remaining unguarded claims stop being unguarded without buying a slower
  pipeline. The expensive half stays weekly (D2), the cheap half rides a job that already runs
  (D3), and no branch-protection context changes.
- Positive: D1 turns "is this claim guarded?" into a question with a filed answer instead of an
  archaeology exercise across three test projects, a canary and a workflow. A future claim that
  ships without a class is a review defect, visible at review time.
- Positive: D4 makes the perf gate honest about what a hosted runner can measure. It detects the
  failure that actually matters — *this got slower* — and declines to assert the one it cannot —
  *this is exactly 1.53M events/sec*.
- Positive: D8 fixes a real, already-present drift (v3 vs v3.2 across README, `<Description>` and
  the embedded resource) and makes a model swap a build failure rather than a silent lie.
- Negative: a performance regression is now caught up to seven days after it lands, and the
  bisection window is a week of commits. Accepted: the alternative costs ~25 min on every PR and
  ~31 bot PRs a month, and would still be noisy.
- Negative: `baseline.json` becomes a maintained artifact. A legitimate speed-up leaves the baseline
  stale-conservative until someone lands a human-authored update (D6), and a runner-fleet change can
  require a re-baseline that carries no code meaning.
- Negative: restating 94.3% as upstream's number rather than ours is a slightly weaker marketing
  line. It is the true one, and D9 keeps the stronger claim available the moment a corpus exists.
- Neutral: this ADR governs *how* a claim is guarded, not *which* claims the README should make.
  Whether to publish a given number stays an editorial call.
- Neutral: the two backend-dependent Performance rows (Redis / Postgres session store) are not
  covered by the current benchmark workflow filters. D1 forces them to declare a class; it does not
  by itself make them ENFORCING.

## Alternatives considered

- **Option B: make the BenchmarkDotNet suite a per-PR required gate.** The obvious reading of
  "guard every claim". **Rejected** on two independent grounds. Cost: ~20–25 min added to every
  `pull_request`, multiplied by ADR-0039's ~31 bot PRs/month, against a pipeline ADR-0038 had just
  finished slimming for exactly this reason. Validity: hosted runners share hardware, so run-to-run
  variance at PR granularity would produce false reds, and a required check that is routinely red
  for reasons unrelated to the change gets ignored, then bypassed, then removed.
- **Option C: run the benchmarks on `merge_group` instead.** Superficially attractive — the queue is
  already the authoritative gate (ADR-0038 D3), so the number would be validated on the exact merge
  result. **Rejected:** the queue is serial. A 25 min benchmark job is paid per landing with no
  parallelism to hide behind, which is strictly worse than the PR case it was meant to avoid.
- **Option D: gate absolute numbers against the README's workstation figures.** **Rejected:** a
  shared hosted runner cannot reproduce a dedicated 12C/24T workstation. The gate would fail
  permanently, or the numbers would be rewritten downward to whatever CI happens to achieve —
  publishing a worse product than the one that ships.
- **Option E: delete the unguardable numbers from the README.** **Rejected** as the default,
  retained as the fallback in D8. The Performance table is the clearest evidence of the redesign
  this SDK is built on; the defect is *unattributed* claims, not claims. Deletion remains the answer
  when attribution is impossible.
- **Option F: record these requirements in the existing `ci-gating` capability.** **Rejected:**
  `ci-gating` is about which validation runs on which *event* and how coverage is collected once —
  an event-scoping policy owned by ADR-0038/ADR-0039. Claim guards are a documentation-honesty
  capability (nearer `docs-brand-consistency`) that happens to be enforced partly in CI. Folding
  them in would blur what ADR-0038 decided with what this decides, and a later change to one would
  read as a change to the other.
- **Option G: record labelled turn-boundary audio in-house to unblock the accuracy gate.**
  **Deferred, not rejected** (D9). It is the only path to a genuine first-party accuracy claim, but
  human speech committed to a public MIT repository raises consent, licensing and Git-LFS budget
  questions that must be answered before any recording happens — not after.
