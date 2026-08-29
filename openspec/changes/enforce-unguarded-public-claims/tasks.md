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

## 2. AMI throughput — arm the weekly gate

- [ ] 2.1 Run `.github/workflows/perf-regression.yml` via `workflow_dispatch` several times and
      record the hosted-runner spread per benchmark — the bands are calibrated from observation,
      never guessed
- [ ] 2.2 Author `Tests/Verbara.Sdk.Benchmarks/baseline.json`: hosted-runner mean + tolerance band
      per benchmark, explicitly NOT the README's workstation figures (Sdk/ADR-0042 D4)
- [ ] 2.3 Add the comparison step to the workflow: parse the collected BenchmarkDotNet JSON, compare
      each mean against its band, and **fail closed** on a missing/empty/unparseable result — today
      `|| true` would render a benchmark that never ran as green
- [ ] 2.4 Add breach notification: file or update an issue naming the benchmark, baseline, observed
      value and band, so the signal outlives the run (Sdk/ADR-0042 D5)
- [ ] 2.5 Land the comparison observing-only first, confirm two consecutive scheduled runs would
      have passed, then flip it to failing — the gate's first act must not be a false red
- [ ] 2.6 Confirm the workflow still has no `pull_request` and no `merge_group` trigger and
      contributes no required check — no branch-protection reconciliation is performed or needed
      (Sdk/ADR-0042 D2–D3; ADR-0038, verbara-meta/ADR-0003)
- [ ] 2.7 Document the human-authored baseline-update protocol next to `baseline.json`: no CI
      write-back, and a re-baseline PR states which benchmark moved, how much, and why
      (Sdk/ADR-0042 D6)

## 3. Turn detection — split the claim into its two honest halves

- [ ] 3.1 Add a content-hash assertion over the embedded `smart-turn-v3.2-cpu.onnx` in
      `Tests/Verbara.Sdk.VoiceAi.TurnDetection.Tests/` (read via the assembly manifest stream; the
      `Unit Tests` job already checks out with `lfs: true`)
- [ ] 3.2 Resolve the model-version drift: align the `README.md` citation link, the package
      `<Description>` in `src/Verbara.Sdk.VoiceAi.TurnDetection/`, the package README and the
      resource filename on one model version (currently v3 link vs v3.2 resource)
- [ ] 3.3 Reword the `94.3% English accuracy` claim in `README.md` (lines 67 and 472) into
      attributed voice — upstream's published figure, cited to the matching model card. Leave the
      dated `CHANGELOG.md` entry verbatim as a period-correct record
- [ ] 3.4 Add the `Verbara.Sdk.VoiceAi.TurnDetection` project reference to
      `Tests/Verbara.Sdk.Benchmarks/` (absent today)
- [ ] 3.5 Add the inference-latency benchmark covering the mel-spectrogram front-end plus the ONNX
      session — the path the `~12 ms CPU` figure actually claims
- [ ] 3.6 Add its workflow filter and its `baseline.json` entry, calibrated per §2.1
- [ ] 3.7 Verify no `src/` behaviour changed anywhere in this section — docs, packaging metadata,
      tests and benchmarks only, so no downstream cascade to Pro or Platform

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
