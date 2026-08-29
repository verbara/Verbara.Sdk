# Tasks — enforce-unguarded-public-claims

Backlog proposal — nothing here is started. Phases run Subagent-Driven with FCM batching:
Phase A foundation (§1, batch) → Phase B the two claims (§2–§3, focused) → Phase C integration
(§4–§6, batch).

## 1. Foundation

- [x] 1.1 Land `docs/decisions/0042-public-claim-guard-classes.md` (currently `Proposed`); move to
      `Accepted` when this change lands

      Landed and **corrected while still `Proposed`**, which is the only window the repo's own rule
      allows — an `Accepted` ADR is never edited, only superseded. Two substantive amendments:

      - **D1 gains a fourth class, `EVIDENCE`, plus a new D1a defining it.** The §1.2 inventory found
        that the three provider guides carry ~150 measured figures — alone about 80% of the registry
        — and that none of the three original classes can apply: ENFORCING needs vendor credentials
        and paid egress, COHERENCE has nothing to bind to because the document *is* the record, and
        ATTRIBUTED fails all three legs since the measurement is ours and the "artifact" is a live
        vendor service. D1a carries two guards so the class cannot become an escape hatch: an
        EVIDENCE claim MUST carry its date and measurement conditions, and a figure counting this
        repository's own contents stays ENFORCING even inside a document whose other figures are
        EVIDENCE. `provider-wire-conformance.md` is written in as both the motivating case and its
        own counter-case.
      - **D9's stated blocker was false and is replaced.** It claimed no labelled turn-boundary
        corpus permits redistribution from a public MIT repo. §5.1 verified the opposite: AMI, ICSI
        and HCRC Map Task are all CC BY 4.0 (AMI's licence sentence re-read at the primary source).
        The blocker that survives is **labelling, not licensing** — the CC BY corpora carry word
        timings and dialogue acts but no turn-end labels, and the one corpus with the native
        `endpoint_bool` label, Pipecat's own, declares no licence at all and is ~81% commercial-TTS
        output. The unblocking condition changed shape with it: a decision to derive and hand-verify
        ~20 clips, not a search. LDC corpora are excluded separately and on firmer ground.

      Status stays `Proposed` until §7.1.
- [x] 1.2 Inventory every quantitative claim in living public docs (`README.md`, package and example
      READMEs, `docs/guides/`) and assign each one a class — ENFORCING / COHERENCE / ATTRIBUTED —
      including the two backend-dependent Performance rows (Redis / Postgres session store) that
      the current workflow filters do not cover

      Swept. **The task's own scope list was short by three files**, all of them living, public and
      linked from `README.md`: `docs/guides/troubleshooting.md`, `docs/README-technical.md` and
      `docs/README-commercial.md` — the last two being the largest unguarded concentration in the
      repo. Ruled in scope by the operator; the spec delta's living-document definition was widened
      to name them, since it listed only `docs/guides/` under `docs/`.

      **The inventory found false claims, not merely unguarded ones.** Verified against the tree at
      `2e931bf7`:

      | claim | published | actual |
      |---|---|---|
      | `README.md:74` | 37 ADRs | **53** |
      | `README.md:61` | headline v2.2.1 | **2.5.0**, tagged |
      | `README-technical.md:503` | `EventPumpCapacity = 10_000 // default` | **20_000** (`AsyncEventPump.cs:21`) |
      | `README-technical.md:216` | **all** VoiceAi packages expose an ActivitySource | **3 of 7** — falsifiable against the array it cites as proof |
      | `README-technical.md:218` | Push (2 packages) | **4** |
      | `README-technical.md:543/:544` | 111 actions / 215 events | **148** / **278** |
      | `README-commercial.md:56` | 28 packages | **29** |
      | `README.md:45` vs `Ami/README.md:7` | 148/278/18 vs 111/261/17 | two public numbers of the same quantity, in one repo |

      **The Redis/Postgres rows are worse than unfiltered.** Neither has any CI touch:
      `SessionsBackendsBenchmark` exists but `Tests/Verbara.Sdk.Benchmarks/README.md:21` calls it
      "known flaky" and steers readers to xunit `Fact`s that contain **zero assertions**, and no
      workflow runs `Category=Benchmark`. Worse, the record they would bind to
      (`docs/research/benchmark-analysis.md:149`) describes the Postgres store as **"Npgsql + Dapper
      + JSONB"** — Dapper was removed repo-wide in v2.2.0 under ADR-0022 Phase D. **The Postgres row
      measures code that no longer ships**, so §4.1 cannot bind it without a re-measurement. Not in
      the plan; flagged rather than silently bound.
- [x] 1.3 Commit the claim registry as the single answer to "is this claim guarded?", cross-linking
      each entry to its guard (`MarketingClaimsTests`, `DocSnippetCompilationTests`,
      `tools/AotCanary/`, `baseline.json`, the ONNX hash pin)

      `docs/claim-registry.md`, ~120 rows across `README.md`, both `docs/README-*.md`, the package
      and example READMEs, all of `docs/guides/` and the `.csproj` `<Description>` values. Each row
      carries class, guard and a status of `OK` / `GAP` / `PARTIAL` / `WRONG` / `TODO`.

      The registry itself is deliberately **not** a living public document under the spec's
      definition — it sits in `docs/` outside `docs/guides/` — which avoids the recursion of a
      registry that must register its own contents.

      Three findings the cross-linking exposed that a per-claim sweep would have missed:

      - **"0 trim warnings across the package family" is PARTIAL everywhere it appears.**
        `tools/AotCanary/` references **22 of 29** packages; `OpenTelemetry`, `Push.AspNetCore`,
        `Push.Nats`, `Sessions.Redis`, `Sessions.Postgres` and `VoiceAi.TurnDetection` are outside
        it. Only `src/Verbara.Sdk.Push/README.md:112` names its own guard, and it is the one package
        for which the claim is fully true.
      - **`README-technical.md:304-390` is orphaned.** 29 of its 32 published means match no
        committed value anywhere in the repo, while **26 of 29 allocations match the record exactly**.
        Allocations are deterministic across runs of the same code and means are not, so the
        signature is a real BenchmarkDotNet execution whose output was never committed — not a
        transcription error and not a fabrication. `:406` points readers at an artifacts directory
        that is not in the repository. Operator ruling: replace the 32 figures with the v1.11
        recorded values, so the block becomes bindable.
      - **`README-commercial.md:19`'s "449 GitHub stars" is being deleted.** A third party's metric
        about a third party's repository, stale the day after it is written, failing all three D8
        obligations at once. The sentence's argument — asterisk-java is the mature incumbent —
        survives without it.

      Six claims carry **no class yet** and are listed under *Unresolved* rather than assigned a
      convenient one: the vendor TTFA/pricing figures under D8's pin leg, the "100K+ agents" scale
      claims, behavioural constants in package READMEs, the `README.md` Status release bullets, the
      17-vs-18 AMI response-type definition, and the workload estimates in `high-load-tuning.md`.
- [x] 1.4 Add the registry to the review checklist so a new number without a class is caught at
      review time (Sdk/ADR-0042 D1)

      Added to `.github/PULL_REQUEST_TEMPLATE.md`, linking both the registry and ADR-0042 D1.

- [x] 1.5 Adversarial review of §1 (added after the fact — the review found enough that its outcome
      belongs in the record, not only in the commit that answered it).

      Reviewed on a second model against `d3971d15`. **Verdict YELLOW**, with the structure intact:
      every headline `WRONG` row re-verified true, the licence correction verified at the primary
      sources, `validate` 9/9, suite 3,295/3,295, and the commit confirmed to touch no C#. Six
      findings were real and are fixed here:

      - **D1a was exploitable.** Two of its three conditions were author-controlled: "the document
        is the record" is true of any figure whose author declines to commit one, and "re-measuring
        needs something outside our control" stretches to "CI cannot reproduce my workstation". A
        first-party benchmark of our own code could have been classed EVIDENCE and dodged COHERENCE
        entirely — the exact escape hatch D1a was written to prevent. Rewritten around the
        **subject** of the measurement rather than its circumstances, and it now refuses that case by
        name.
      - **D1a contradicted the spec delta in the same commit** — "declared per document" against the
        spec scenario's "per claim". The registry had been applying it per claim. D1a now says per
        claim.
      - **The registry's scope exceeded the spec's.** The registry covered `.csproj`
        `<Description>` values; the spec defined living documents as tracked `*.md` only, leaving the
        one nuget.org-shipping WRONG outside its own rule. The spec now names them, and
        `CONTRIBUTING.md` with them.
      - **`CONTRIBUTING.md` was missed entirely** — tracked, public, read by every contributor, and
        carrying three stale figures: 28 packages (29), 26 examples (25), 33 test projects (37).
      - **A WRONG filed as GAP.** `high-load-tuning.md:70` asserts all five VoiceAi packages publish
        an `ActivitySource`; only three exist. Same falsity the registry already marked at
        `README-technical.md:216`, missed one row down.
      - **The canary enumeration was off by one** — 29 − 22 = 7, and `Ami.SourceGenerators` is
        packable, so it counts.

      **D9 was also overstated** and is qualified: AMI and ICSI release "signals and transcription,
      and *some* of the annotations" under CC BY 4.0, not the corpus entire, so a derivation must
      draw from the covered layers; and the "~81% TTS" proportion is not published on the dataset
      card, so it is now stated as a per-row flag to be computed rather than as a figure.

      The review's remaining point stands unfixed on purpose: §1.2's rows for the AMI surface counts
      cannot be gated until a counting definition is chosen, which is now *Unresolved* 5 rather than
      an implied 278.

## 2. AMI throughput — arm the weekly gate

- [x] 2.1 Run `.github/workflows/perf-regression.yml` via `workflow_dispatch` several times and
      record the hosted-runner spread per benchmark — the bands are calibrated from observation,
      never guessed
      **Calibrated from 13 real runs, not from dispatches.** The workflow has run weekly for months
      and twelve runs still had unexpired artifacts, plus one dispatched here — better evidence than
      ad-hoc dispatches, because they are the exact runner and the exact Sunday-04:00 slot the gate
      will run in.

      **The dominant finding is that the spread is not jitter.** `ubuntu-latest` alternates between
      AMD EPYC 7763 (9 runs) and EPYC 9V74 (4), and the 9V74 is uniformly faster — up to **−20.7%**
      on `AriJson.SerializeChannel`. Within-run CV is 0.11–0.67%; across-run CV is 0.5–11.2%. So a
      band derived from `Statistics.StandardDeviation` in the report would be roughly **20× too
      tight** and would red the gate on the first run that landed on the other VM. Normalising by a
      per-run machine factor was tested and rejected: it helps the machine-sensitive benchmarks,
      hurts the insensitive ones, and would normalise away a systemic regression hitting everything.

- [x] 2.2 Author `Tests/Verbara.Sdk.Benchmarks/baseline.json`: hosted-runner mean + tolerance band
      per benchmark, explicitly NOT the README's workstation figures (Sdk/ADR-0042 D4)
      `Tests/Verbara.Sdk.Benchmarks/baseline.json` — 19 entries keyed by `FullName`, each carrying
      `mean_ns` and `tolerance_pct`, plus a `calibration` block recording the 13 runs, the window
      2026-06-07 → 2026-08-29 and both CPU models.

      **Hosted-runner means, explicitly not the README's** (D4): the workstation figures are
      1.97×–2.35× faster, so an absolute comparison would be permanently red.

      Band formula `max(3σ_pooled, 1.5 × worst-observed-deviation, 1.25 × full-range)`, floored at
      ±10% and ±15% under 100 ns. Verified: **0 breaches across all 13 runs**, tightest headroom
      5.8pp. `AriJson.SerializeChannel` needs ±45%, which is a weak detector and is honest about it —
      it exists only to swallow the two-CPU gap. Keying the baseline on `ProcessorName` would collapse
      it to ±10%, and is deferred to the enforce PR so the observe-only window can say whether a
      third CPU model shows up.

- [x] 2.3 Add the comparison step to the workflow: parse the collected BenchmarkDotNet JSON, compare
      each mean against its band, and **fail closed** on a missing/empty/unparseable result — today
      `|| true` would render a benchmark that never ran as green
      `scripts/ci/check-perf-baseline.py`, wired into the workflow — a script rather than inline
      YAML so it is testable, following the `classify-docs-only.sh` precedent. **37 unit tests**,
      riding the existing `unittest discover` step: no new job, no new check-run name (D3).

      **Fail-closed proven against real artifacts**, not synthetic ones:

      | mutation | `--enforce` | observing |
      |---|---|---|
      | a `results/` directory deleted | **exit 1**, 4 structural errors naming the benchmarks | exit 0, same report |
      | report file replaced with non-JSON | **exit 1** | — |
      | artifacts root absent entirely | **exit 1** | — |

      The first row is the `\|\| true` failure mode this task exists to close: a benchmark that never
      ran currently reads as green.

- [x] 2.4 Add breach notification: file or update an issue naming the benchmark, baseline, observed
      value and band, so the signal outlives the run (Sdk/ADR-0042 D5)
      `scripts/ci/report-perf-breach.sh` files or updates an issue naming the benchmark, the
      baseline, the observed value and the band. 27 unit tests, registered in `ci.yml` beside the two
      guards before it. A weekly red nobody is told about is observational under another name (D5).

- [x] 2.5 Land the comparison observing-only first, confirm two consecutive scheduled runs would
      have passed, then flip it to failing — the gate's first act must not be a false red
      Landed **observing-only**: `PERF_GATE_ENFORCE: 'false'`. The comparison runs, prints the full
      table and reports, and does not fail the job — except on exit 2 (gate misconfigured), which
      fails in both modes, because a guard that is broken must not read green.

      Replayed over **all 13 historical runs: 19/19 inside band on every one**, so the flip has its
      evidence for the past. The flip itself waits for two consecutive *scheduled* runs under
      observation and ships as its own PR — the cron is weekly, so that is ~2 weeks of calendar no
      amount of work shortens, and it is why this change lands in two PRs.

- [x] 2.6 Confirm the workflow still has no `pull_request` and no `merge_group` trigger and
      contributes no required check — no branch-protection reconciliation is performed or needed
      (Sdk/ADR-0042 D2–D3; ADR-0038, verbara-meta/ADR-0003)
      Verified in the file: `on:` is `schedule` (`cron: '0 4 * * 0'`) + `workflow_dispatch` and
      nothing else. No `pull_request`, no `merge_group`, so the workflow contributes no check-run to
      any PR and no branch-protection reconciliation is performed or needed.

- [x] 2.7 Document the human-authored baseline-update protocol next to `baseline.json`: no CI
      write-back, and a re-baseline PR states which benchmark moved, how much, and why
      (Sdk/ADR-0042 D6)
      `Tests/Verbara.Sdk.Benchmarks/baseline.README.md`: CI never writes back, and a re-baseline PR
      states which benchmark moved, in which direction, by how much, and why (D6). Same
      manual-ratchet semantics as the coverage floor.

## 3. Turn detection — split the claim into its two honest halves

- [x] 3.1 Add a content-hash assertion over the embedded `smart-turn-v3.2-cpu.onnx` in
      `Tests/Verbara.Sdk.VoiceAi.TurnDetection.Tests/` (read via the assembly manifest stream; the
      `Unit Tests` job already checks out with `lfs: true`)
      `OnnxSessionManagerTests.EmbeddedModel_ShouldBeTheExactArtifactTheAccuracyClaimCites`, read
      through the manifest stream under the resource's explicit `LogicalName`.

      **The hash is asserted, not the length, and that is measurable rather than cautious**: upstream's
      `v3.1-cpu` is 8 679 180 bytes against v3.2-cpu's 8 679 182 — **two bytes apart**. A length pin
      would pass a silent downgrade to a model whose published English accuracy is 90.66% rather than
      94.26%.

      Negative-tested: one character changed in the expected SHA-256 turns it red at index 63.
      Without Git LFS the stream is a ~130-byte pointer and the length assertion fails loudly, which
      is intended.

- [x] 3.2 Resolve the model-version drift: align the `README.md` citation link, the package
      `<Description>` in `src/Verbara.Sdk.VoiceAi.TurnDetection/`, the package README and the
      resource filename on one model version (currently v3 link vs v3.2 resource)
      Five sites said bare `smart-turn-v3` while the shipped resource is `smart-turn-v3.2-cpu.onnx`.
      All now say v3.2-cpu: the `<Description>` (which ships to nuget.org), the package README,
      the `SmartTurnDetector` XML doc, and both README lines. Zero stray `v3` mentions remain in
      living docs.

      **Convergence justified by the artifact, not by convenience**: the shipped bytes hash to
      upstream's v3.2-cpu blob exactly, and it is the only version for which the published 94.3% is
      true. Left verbatim as dated records: the `CHANGELOG.md` entries and the pre-implementation
      spec, which correctly describe the v3 family as a then-future gap.

- [x] 3.3 Reword the `94.3% English accuracy` claim in `README.md` (lines 67 and 472) into
      attributed voice — upstream's published figure, cited to the matching model card. Leave the
      dated `CHANGELOG.md` entry verbatim as a period-correct record
      Both README lines now read as upstream's measurement and cite the per-version benchmark
      (`.../benchmarks/smart-turn-v3.2-cpu.md`), which is what makes the citation checkable against
      the §3.1 pin. `:472` previously carried the figure with **no citation at all**.

      **Two corrections the rewording forced.** First, the number could not be attributed to "the
      model card": that page states neither figure — only `Params: 8M` and the checkpoint size. The
      accuracy lives in upstream's per-version `benchmarks/` folder, at **94.26%** for v3.2-cpu.
      Second, the figure's likely origin — the vendor launch post publishing 94.31% — is **v3.0 on a
      different test set**, and matches only by rounding coincidence; citing it would have cited the
      wrong model.

      The `~12 ms` latency figure was **removed from both lines rather than reworded**: §3.5 measured
      the real path at 26–37 ms, so restating it in attributed voice would have attributed a false
      number to a third party. Its replacement wording is the one open question in this section.

- [x] 3.4 Add the `Verbara.Sdk.VoiceAi.TurnDetection` project reference to
      `Tests/Verbara.Sdk.Benchmarks/` (absent today)
      Added; the benchmark project referenced nine projects and TurnDetection was not among them,
      which is why the latency claim had no benchmark.

- [x] 3.5 Add the inference-latency benchmark covering the mel-spectrogram front-end plus the ONNX
      session — the path the `~12 ms CPU` figure actually claims
      `TurnDetectionBenchmark`, driven entirely through the **public DI surface** — the detector's
      constructor is `internal` and the benchmark project has no `InternalsVisibleTo`, which is the
      right constraint: a benchmark needing privileged access is not measuring what ships.

      Inference fires once per utterance, on the silence frame that crosses `SilenceTriggerDuration`,
      so the utterance and all but the last silence frame are fed in `[IterationSetup]` and the
      measured region is the single `Analyze` call that runs mel + ONNX. Feeding inside the measured
      region would average one inference over a hundred cheap frames.

      **Parameterised by utterance length, and it had to be.** The mel front-end's cost scales with
      the accumulated audio (`numFrames = 1 + (len − 400)/160`) even though its output pads to a fixed
      80×800 — so a single latency figure is meaningless without the length it was measured at. The
      first draft fixed 2 s and produced a publishable number resting on an arbitrary choice.

      Measured on `AMD Ryzen 9 9900X · .NET 10.0.11 · BDN 0.15.8` — the README's own machine:

      | utterance | mean | allocated |
      |---|--:|--:|
      | 1 s | **26.18 ms** | 353 KB |
      | 2 s | **28.20 ms** | 449 KB |
      | 4 s | **31.22 ms** | 641 KB |
      | 8 s (ring-buffer ceiling) | **37.30 ms** | 1 025 KB |

      **The published `~12 ms` is between 2.2× and 3.1× optimistic**, on hardware faster than where
      upstream measured, with 0.6–0.7% intra-run spread. Upstream's 12 ms is raw ONNX inference on
      v3.0; this is the path a caller of this SDK actually pays. Not a refutation of their number —
      a refutation of ours.

- [x] 3.6 Add its workflow filter and its `baseline.json` entry, calibrated per §2.1
      Workflow filter added. **Deliberately no `baseline.json` entry yet**: every other band was
      calibrated from 13 real runs and this benchmark has none. Its band gets calibrated the same
      way, from its own observations, in the PR that flips `PERF_GATE_ENFORCE`. A band guessed today
      would break §2.1's own rule — calibrated from observation, never guessed — in the change that
      wrote it.

- [x] 3.7 Verify no `src/` behaviour changed anywhere in this section — docs, packaging metadata,
      tests and benchmarks only, so no downstream cascade to Pro or Platform
      Confirmed from the diff. `src/` carries exactly three changed lines: one package-README
      sentence, one XML doc comment, and the `<Description>`. No executable code, so no downstream
      cascade to Pro or Platform. Build 0 warnings in Debug and Release.

## 4. Performance-table coherence (per-PR, no new job)

- [ ] 4.1 Commit the measurement record backing the `README.md` Performance table: machine, runtime
      version, BenchmarkDotNet version, date, one entry per published figure
- [ ] 4.2 Add coherence tests asserting the published figures equal the record, in a project already
      inside the default unit filter — the `MarketingClaimsTests` precedent, riding the existing
      `Unit Tests` job (Sdk/ADR-0042 D3, D7)
- [ ] 4.3 Confirm no new check-run name appears on `pull_request` and the required-check set is
      byte-identical before and after

## 5. Deferred: first-party accuracy gate (investigation only — no code)

- [ ] 5.1 Survey candidate labelled turn-boundary speech corpora and record, per candidate, whether
      its licence permits redistribution from a public MIT repository — the specific blocker, not a
      general one
- [ ] 5.2 Record the in-house recording option's open questions (speaker consent, licence under
      which audio would be committed, Git-LFS budget) as an explicit unanswered set — Sdk/ADR-0042
      Option G is deferred, not rejected
- [ ] 5.3 Write the deferral and its unblocking condition into the claim registry entry so the gap
      is visible rather than forgotten (Sdk/ADR-0042 D9). Target shape when unblocked, unchanged
      from the original plan: ≥ 20 labelled WAVs under `Tests/fixtures/audio/turn-boundaries/`
      (10 turn-end positive, 10 turn-mid negative), precision ≥ 0.85 and recall ≥ 0.85
- [ ] 5.4 Confirm no first-party accuracy wording ships while this gate is absent (cross-check §3.3)

## 6. Verification

- [ ] 6.1 `dotnet test Verbara.Sdk.slnx --filter "Category!=Functional&Category!=Integration"` green
      locally with **zero warnings** (`TreatWarningsAsErrors`)
- [ ] 6.2 `openspec validate --all --strict` green
- [ ] 6.3 CI green on `pull_request` and `merge_group`, zero warnings, with `pull_request`
      wall-clock unchanged versus the pre-change baseline
- [ ] 6.4 One `workflow_dispatch` run of the armed perf workflow passes end to end; one run with a
      deliberately corrupted/removed result artifact fails closed (proves §2.3)
- [ ] 6.5 Negative check: temporarily alter a README Performance figure and confirm the coherence
      test fails; revert
- [ ] 6.6 Negative check: temporarily alter the ONNX hash constant and confirm the pin test fails;
      revert
- [ ] 6.7 Confirm the branch-protection required-check set is unchanged (no context added, none
      removed) — the ADR-0038 / verbara-meta/ADR-0003 reconciliation is deliberately not triggered

## 7. Close-out

- [ ] 7.1 Flip `docs/decisions/0042-public-claim-guard-classes.md` to `Accepted` and add its entry to
      the `docs/decisions/README.md` catalog (deliberately not added while `Proposed`, and to avoid
      colliding with the parallel work owning ADR-0041 / ADR-0043)
- [ ] 7.2 `CHANGELOG.md` entry. **No `Directory.Build.props` `PackageVersion` bump and no `v*` tag:**
      this change touches docs, tests, benchmarks and one scheduled workflow — no shipped `src/`
      behaviour — so there is nothing to publish to nuget.org
- [ ] 7.3 Archive the change (`openspec archive`) and confirm the `claim-guards` living spec
      materializes under `openspec/specs/`
