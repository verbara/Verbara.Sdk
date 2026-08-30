# Claim registry

The single answer to **"is this claim guarded?"** — one file, rather than a search across the test
projects, the AOT canary and the workflows. Required by `claim-guards` and
[ADR-0042](decisions/0042-public-claim-guard-classes.md).

**A pull request that adds or changes a quantitative figure in a living public document must add or
update its row here in the same PR.** A figure with no row does not ship (ADR-0042 D1).

## The four classes

| class | meaning | where the guard lives |
|---|---|---|
| **ENFORCING** | an executable gate fails when reality diverges | a unit test on the existing `Unit Tests` job, the AOT canary, or the scheduled perf gate |
| **COHERENCE** | a per-PR check that the published number equals a committed measurement record | a unit test on `Unit Tests` (D3 — never a new job, never a new required check) |
| **ATTRIBUTED** | a third party's published measurement, cited as theirs and pinned to the shipped artifact | the citation, the wording and the pin together (D8 — all three, or it is not ATTRIBUTED) |
| **EVIDENCE** | a dated record of a first-party measurement of something outside this repo's control | nothing — but the date and the measurement conditions are mandatory (D1a) |

**EVIDENCE is not an escape hatch.** A figure counting this repository's own contents is ENFORCING
however inconvenient, even inside a document whose other figures are EVIDENCE. `docs/guides/provider-wire-conformance.md`
is the worked example: its vendor wire captures are EVIDENCE, its counts of *our own tests* are not.

## Scope

**In:** `README.md`, `CONTRIBUTING.md`, `docs/README-technical.md`, `docs/README-commercial.md`,
`src/*/README.md`, `Examples/*/README.md`, all of `docs/guides/`, and the `<Description>` values in
`src/*/*.csproj` (not Markdown, but published verbatim on nuget.org).

**Out**, as period-correct records left verbatim: dated `CHANGELOG.md` entries,
`openspec/changes/archive/`, `docs/decisions/`, `docs/specs/`, `docs/research/`,
`docs/plans/{completed,archived}/`.

## Status legend

`OK` guard exists and passes · `GAP` no guard · `PARTIAL` guard covers less than the claim says ·
`WRONG` the published figure is false as of the inventory date · `TODO` guard scheduled by this change

---

## `README.md`

| line | claim | class | guard | status |
|---|---|---|---|---|
| 13 | Native AOT-ready badge | ENFORCING | `tools/AotCanary/` + `tools/verify-aot.sh` + `aot-validate.yml` | PARTIAL — canary references 22 of 29 packages |
| 23 | asterisk-java "790+ classes" | ATTRIBUTED | — | GAP — no citation, first-party voice |
| 45 | 148 actions, 278 events, 18 typed responses | ENFORCING | — | GAP — `src/Verbara.Sdk.Ami/README.md:7` publishes 111/261/17, and **all three quantities need a counting definition first** (see *Unresolved* 5) |
| 46 | 54 AGI commands | ENFORCING | — | GAP |
| 54 | four source generators, 0 trim warnings | ENFORCING | AotCanary (trim half) | PARTIAL — generator count unguarded |
| 61 | 29 NuGet packages | ENFORCING | — | GAP |
| 61 | 0 build warnings | ENFORCING | `Directory.Build.props` `TreatWarningsAsErrors` + `Pack Warnings Gate` | OK |
| 61 | 0 trim warnings | ENFORCING | AotCanary | PARTIAL — 22/29 |
| 61 | ~2,924 unit + 154 functional + 65 integration | COHERENCE | — | WRONG — the suite runs **3,295** (measured 2026-08-29); note nothing in-tree *records* that number until §4.1 commits the record |
| 61 | headline version **v2.2.1** | COHERENCE | — | WRONG — `Directory.Build.props` is 2.5.0 and v2.5.0 is tagged |
| 65 | ONNX model 8.3 MB | COHERENCE | — | GAP — actual 8,679,182 B |
| 67 | 94.26% English accuracy, in upstream's voice | ATTRIBUTED | citation to upstream's per-version benchmark + the content-hash pin in `OnnxSessionManagerTests` | **OK** — all three D8 legs present |
| 67 | ~12 ms CPU inference | ENFORCING | `TurnDetectionBenchmark` | **DELETED** — measured at 26.18–37.30 ms; deferred, see *Deferrals* |
| 72 | 148/152 AMI (97%), 94/98 ARI (96%), 46/46, 27/27, 278 events | ENFORCING | — | GAP |
| 74 | **37 ADRs** | ENFORCING | — | WRONG — 53 on disk |
| 98 | measurement provenance (Ryzen 9 9900X, .NET 10.0.5, BDN v0.14.0, 2026-04-18) | COHERENCE | — | GAP |
| 102 | AMI parse+dispatch 1.53M events/sec (653 ns) | ENFORCING + COHERENCE | `perf-regression.yml` `*AmiProtocolReader*` | GAP — observational only (`\|\| true`, no baseline) |
| 103 | ARI deserialize Channel 3.54M ops/sec (283 ns) | ENFORCING + COHERENCE | `*AriJson*` | GAP — observational only |
| 104 | ARI event parse 595K events/sec (1.68 µs), "2.7× faster than v1.0" | ENFORCING + COHERENCE | `*AriParseEvent*` | GAP — observational; the cross-version ratio has no guard at all |
| 105 | 163.9M lookups/sec (6.1 ns) | ENFORCING + COHERENCE | `*ChannelManager*` | GAP — observational |
| 106 | ~0.21 ns/observer, zero-alloc | ENFORCING + COHERENCE | `*ObserverDispatch*` | GAP — observational |
| 107 | Redis SaveAsync ~12.6K/sec (p50 79 µs), batch 65,738/sec | COHERENCE | — | GAP — **no workflow filter of any kind** |
| 108 | Postgres SaveAsync ~500/sec (p50 1.97 ms), batch 9,491/sec | COHERENCE | — | GAP — no filter; **and the record measures Dapper, removed in v2.2.0** |
| 133 | 9 ActivitySources | ENFORCING | `MarketingClaimsTests.cs:45-50` | OK |
| 134 | 15 Meters | ENFORCING | `MarketingClaimsTests.cs:52-57` | OK |
| 135 | 11 IHealthChecks — 6 core + 5 VoiceAi | ENFORCING | `MarketingClaimsTests.cs:76-97` | PARTIAL — total pinned, the 6/5 split is not |
| 136 | 60 const strings, 14 nested classes, "14+ unit tests" | ENFORCING | `MarketingClaimsTests.cs:59-74` | PARTIAL — the "14+ tests" sub-claim is unpinned |
| 164 | "First contact in 10 lines" | COHERENCE | — | WRONG — the snippet at :166-182 is 14 lines |
| 470 | Cartesia Sonic-3 40-90 ms TTFA | ATTRIBUTED | — | GAP |
| 472 | smart-turn-v3.2-cpu, 94.26% in upstream's voice | ATTRIBUTED | citation + hash pin | **OK** — this line previously carried the figure with no citation at all |

## `docs/README-technical.md`

| line | claim | class | guard | status |
|---|---|---|---|---|
| 10 | .NET 10.0.100+ pinned | ENFORCING | `global.json` | OK |
| 11 | tested with Asterisk 18, 20, 22, 23 | ENFORCING | `ci.yml:310` matrix | PARTIAL — matrix is `[23]` on PR, `[22,23]` on merge_group; **18 and 20 are tested by nothing** |
| 189 | Core (9 packages) | ENFORCING | — | GAP — correct as a subset |
| 203 | Voice AI (8 packages) | ENFORCING | — | GAP — correct |
| 213 | Pipecat smart-turn-v3.2 ONNX | ATTRIBUTED | content-hash pin | **OK** — `<Description>` now says v3.2-cpu too |
| 216 | **All** VoiceAi packages expose Meter + ActivitySource + IHealthCheck | ENFORCING | — | WRONG — 3 of 7; falsifiable against the very array it cites |
| 218 | Push (2 packages) | ENFORCING | — | WRONG — 4 on disk |
| 225 | Source Generators (1 analyzer) | ENFORCING | — | GAP — correct |
| 275-282 | four source generators | ENFORCING | — | GAP — correct |
| 304 | measurement tuple (BDN v0.14.0, .NET 10.0.5, Ryzen 9 9900X, ShortRun 3+3) | COHERENCE | — | GAP — **and carries no date**, which D7 requires |
| 310-390 | the 32-figure benchmark block | COHERENCE | — | **ORPHANED** — 29 of 32 means match no committed value; 26 of 29 allocations match exactly. Being replaced with the v1.11 recorded values by this change |
| 394-398 | derived ranges and "~3.9M messages/sec theoretical" | COHERENCE | — | GAP — inherits :370's class; the record's own figure is 26.7 µs → 3.75M |
| 432 | build produces zero warnings | ENFORCING | `TreatWarningsAsErrors` + `Pack Warnings Gate` | OK |
| 438-479 | project tree: 17 package dirs, 13 examples | ENFORCING | — | WRONG — 29 packages, 25 examples |
| 494 | `Ami.Port = 5038 // default` | ENFORCING | — | GAP — correct |
| 503 | `EventPumpCapacity = 10_000 // default` | ENFORCING | — | **WRONG — `AsyncEventPump.DefaultCapacity` is 20_000** |
| 543 | "all 111 AMI actions" | ENFORCING | — | WRONG — 148 |
| 544 | "all 215 AMI events" | ENFORCING | — | WRONG — 278 |

## `docs/README-commercial.md`

> **CI gap:** this file is **not** in the `docsnippets-carveout` block of
> `scripts/ci/classify-docs-only.sh:25`, so a PR touching only this file classifies `docs_only=true`
> and skips `Unit Tests` — where every COHERENCE guard lives. Any guard on these rows is decorative
> until the file is added to that carve-out (and to the superset assertion in
> `scripts/tests/test_classify_docs_only.sh`).

| line | claim | class | guard | status |
|---|---|---|---|---|
| 10 | AsterNET last updated 2018, .NET Framework 4.0; Asterisk.NET dormant since 2013 | EVIDENCE | — | GAP — needs a date-stamp |
| 17 | all three Asterisk interfaces | ENFORCING | — | GAP — trivially true |
| 19 | asterisk-java "over 2,470 commits", "**449 GitHub stars**" | — | — | **DELETE** — a star count fails all three D8 obligations and is stale the next day; the sentence's argument survives without it |
| 19 | four custom source generators | ENFORCING | — | GAP — correct |
| 30 | providers "Deepgram, ElevenLabs, Azure, Google, Whisper" (5) | ENFORCING | — | GAP — understates; 7 STT + 6 TTS ship |
| 32, 58 | ~2,924 unit + 154 functional + 65 integration | COHERENCE | — | WRONG — stale by ~370 |
| 32 | zero compiler warnings | ENFORCING | `TreatWarningsAsErrors` + `Pack Warnings Gate` | OK |
| 32 | passes AOT trim analysis cleanly | ENFORCING | AotCanary | PARTIAL — 22/29 |
| 32 | "**tested** for … exceeding 100,000 concurrent agents" | — | — | GAP — nothing executes a load test at any scale; "designed for" is supported, "tested" is not |
| 50 | "start in under 10 milliseconds" | — | — | GAP — **no startup measurement exists anywhere in the repo** |
| 54 | four Roslyn source generators | ENFORCING | — | GAP — correct |
| 56 | **28** composable NuGet packages | ENFORCING | — | WRONG — 29 |
| 56 | core alone under 200 KB | ENFORCING | — | GAP — true (dll 45,568 B) |
| 58 | ~3,000+ automated tests | COHERENCE | — | GAP — true |
| 58 | **26** example applications | ENFORCING | — | WRONG — 25 tracked |

## `src/*/README.md` and `src/*/*.csproj`

| location | claim | class | guard | status |
|---|---|---|---|---|
| `Verbara.Sdk/README.md:13` | 60 const strings, 14 nested classes | ENFORCING | `MarketingClaimsTests.cs:59-74` | OK |
| `Verbara.Sdk/README.md:14` | 9 ActivitySources, 15 Meters | ENFORCING | `MarketingClaimsTests.cs:45-57` | OK |
| `Verbara.Sdk/README.md:53` | 0 trim warnings **across the package family** | ENFORCING | AotCanary | PARTIAL — 22/29; the **seven** uncanaried are `OpenTelemetry`, `Push.AspNetCore`, `Push.Nats`, `Sessions.Redis`, `Sessions.Postgres`, `VoiceAi.TurnDetection` and `Ami.SourceGenerators` (packable, so it counts) |
| `Verbara.Sdk.Ami/README.md:7` | 111 actions, 261 events, 17 response types | ENFORCING | — | WRONG — 148/278/18, and contradicts `README.md:45` |
| `Verbara.Sdk.Ari/README.md:7-8` | 8 ARI resources, 46 event types | ENFORCING | — | GAP |
| `Verbara.Sdk.Agi/README.md:8` | 54 AGI commands | ENFORCING | — | GAP |
| `Verbara.Sdk.Live/README.md:9` | 100K+ agents | — | — | GAP — see *Unresolved* below |
| `Verbara.Sdk.Audio/README.md:35` | 12 telephony rate pairs | ENFORCING | — | GAP |
| `Verbara.Sdk.Audio/README.md:39` | zero-alloc Span API throughout | ENFORCING | — | GAP — `MemoryDiagnoser` runs but nothing asserts |
| `Verbara.Sdk.Push/README.md:112` | 0 trim warnings, **naming its own guard** | ENFORCING | AotCanary | OK — the only README that cites its guard |
| `Verbara.Sdk.Resilience/README.md:8` | maxAttempts capped at 10, ±20% jitter | ENFORCING | — | GAP — see *Unresolved* |
| `Verbara.Sdk.Hosting/README.md:106` | 0 trim warnings | ENFORCING | AotCanary | OK |
| `VoiceAi.Tts/README.md:3` | 6 providers | ENFORCING | — | GAP |
| `VoiceAi.Tts/README.md:9-14,73,81` | per-vendor TTFA (~150 ms, 40-90 ms, sub-100 ms, …) | ATTRIBUTED | — | GAP — no citation; the same vendor figure appears three times with three values |
| `VoiceAi.Stt/README.md:3` | 7 providers | ENFORCING | — | GAP |
| `VoiceAi.Stt/README.md:9` | "lowest latency in the catalog (~150ms)" | ATTRIBUTED | — | GAP |
| `VoiceAi.TurnDetection/README.md:3` | smart-turn-**v3** | ATTRIBUTED | — | WRONG — contradicts `:11` in the same file |
| `VoiceAi.TurnDetection/README.md:11` | bundles `smart-turn-v3.2-cpu.onnx` | ATTRIBUTED | — | GAP — no content-hash pin |
| `VoiceAi.TurnDetection.csproj:3` `<Description>` | smart-turn-**v3** | ATTRIBUTED | — | WRONG — ships to nuget.org; resource at `:26` is v3.2 |

The other 28 `<Description>` values carry no quantitative claim.

## `Examples/*/README.md`

| location | claim | class | status |
|---|---|---|---|
| `TelemetryExample:56` | 9 ActivitySources, 15 Meters | ENFORCING | OK — `MarketingClaimsTests` |
| `VoiceAiCustomProviderExample:15` | override 0.012 ns vs fallback 1.11 ns (~92×) | ENFORCING + COHERENCE | GAP — `VoiceAiBenchmarks.cs` measures exactly this and has no workflow filter |
| `VoiceAiCartesiaExample:3,35,48` | 40-90 ms TTFA, "lowest **measured** in the 2026 landscape", 200-400 ms end-to-end | ATTRIBUTED (must be reworded) | GAP — reads as a first-party benchmark and is not one |
| `VoiceAiSpeechmaticsExample:3,40-42` | ~27× cheaper, sub-150 ms, 55+ languages, ~$0.011/1K chars | ATTRIBUTED | GAP — all in first-party voice, no citation |
| `VoiceAiAssemblyAiExample:47` | zero reflection | ENFORCING | OK — AotCanary |

The other 16 example READMEs carry no quantitative claims (21 tracked, 5 listed above).

## `CONTRIBUTING.md`

Missed by the first sweep — tracked, public, and read as current by every contributor.

| line | claim | class | guard | status |
|---|---|---|---|---|
| 30 | "28 SDK packages: 9 core + 8 VoiceAi + 4 Push + 2 Sessions backends" | ENFORCING | — | **WRONG** — 29 |
| 33 | "26 example applications" | ENFORCING | — | **WRONG** — 25 tracked |
| 34 | "33 test projects" | ENFORCING | — | **WRONG** — 37 under `Tests/` |

## `docs/guides/`

`docs/guides/README.md` carries no quantitative claim beyond `:9` (below).

| location | claim | class | status |
|---|---|---|---|
| `high-load-tuning.md:70` | "All **five** VoiceAi packages publish a Meter + ActivitySource + IHealthCheck" | ENFORCING | **WRONG** — same falsity as `README-technical.md:216`: only 3 ActivitySources exist, so Stt and Tts publish a Meter and a HealthCheck but no source |
| `high-load-tuning.md:95` | 9 sources, 15 meters | ENFORCING | OK — `MarketingClaimsTests` |
| `high-load-tuning.md:13-18` | RAM per buffer (est.) | ENFORCING | GAP — derivable from capacity × entry size |
| `high-load-tuning.md:13-18,20,251` | events/sec per agent tier; 200K/sec queue storm; VarSet 50%+ of volume | — | GAP — workload estimates about the reader's PBX; see *Unresolved* |
| `high-load-tuning.md:138-140` | pauseWriter 1 MB / resumeWriter 512 KB / segment 4 KB "hardcoded" | ENFORCING | GAP |
| `high-load-tuning.md:197` | EventPumpCapacity 20,000 | ENFORCING | OK — matches source; `README-technical.md:503` is the wrong one |
| `session-store-backends.md:5,56,70,76` | three backends, three overloads, three indexes, pageSize 500 | ENFORCING | GAP |
| `session-store-backends.md:9-11,26,35` | read latency <0.1 ms / <1 ms / 5-10 ms | COHERENCE | WRONG — the record has Postgres `GetAsync` p50 = 51 µs, not 5-10 ms |
| `session-store-backends.md:179-182` | Redis SaveAsync ~250 µs | COHERENCE | WRONG — `README.md:107` and the record both say 79 µs |
| `troubleshooting.md:152` | designed for zero trim warnings | ENFORCING | PARTIAL — 22/29 |
| `troubleshooting.md:190,194-202` | 9 registered sources, enumerated by name | ENFORCING | PARTIAL — count pinned, the by-name list is not |
| `troubleshooting.md:230` | reconcile burst over 5-30 seconds | — | GAP |
| `log-analysis-reference.md:5,22` | SDK Tags (11), Dashboard Tags (8) | ENFORCING | GAP |
| `asterisk-version-compatibility.md:9,157` | "**no data is ever lost**", "zero data loss" | ENFORCING | GAP — absolute claim, testable against the `RawFields` fallback |
| `provider-test-substrate.md:6` | fourteen provider surfaces | ENFORCING | GAP |
| `provider-test-substrate.md:33,36,114` | ~30 assemblies, coverage 80.42% → 61.96%, six defects | **EVIDENCE** | dated record |
| `provider-recording-protocol.md:6-7,54` | 14 surfaces = 6 HTTP + 8 WebSocket; five of six automated | ENFORCING | GAP |
| `provider-recording-protocol.md:317,512-513` | five of eight not-cleared; 80,608 bytes over ten reads | **EVIDENCE** | dated record |
| `provider-recording-protocol.md:707,712-717` | 256 KiB cap, 819 frames, ~3 MiB | ENFORCING | GAP — repo size policy |
| `provider-wire-conformance.md:568-573` | 46 / 220 / 174 / 126 / 48 test counts | ENFORCING | GAP — **counts of our own tests: not EVIDENCE** |
| `provider-wire-conformance.md` (~120 further figures) | vendor byte counts, durations, accuracy scores, defect tallies | **EVIDENCE** | dated wire captures against live vendor APIs |

`asterisk-version-matrix.md`, `manual-asterisk-realtime-setup.md`, `log-analysis-prompt.md`: no quantitative claims.

---

## Deferrals — declared with their blocker (ADR-0042 D9)

**Turn-detection CPU inference latency.** The `~12 ms` figure was **removed from `README.md:67` and
`:472` and not replaced.** It was 2.2×–3.1× optimistic: `TurnDetectionBenchmark` measures the path a
caller actually pays — 8 kHz→16 kHz resample and accumulation, mel front-end, ONNX session — at
**26.18 ms** for a 1 s utterance rising to **37.30 ms** at the 8 s ring-buffer ceiling, on the
README's own Ryzen 9 9900X. Upstream's 12 ms is raw ONNX inference on v3.0 and is not the same
quantity.

*Why nothing is published in its place.* A latency figure is meaningless without the utterance length
it was measured at, because the mel cost scales with the accumulated audio. Publishing the measured
range today would put back an ENFORCING claim with no gate behind it — the benchmark has no
`baseline.json` entry, because every other band in that file was calibrated from 13 observed runs and
this one has none.

*Unblocking condition.* The benchmark ships in this change and starts producing weekly observations
immediately. Once it has enough runs to calibrate a band the same way — in the PR that flips
`PERF_GATE_ENFORCE` — the figure can be published as ENFORCING, stated per utterance length and with
its machine. Not before.

**First-party turn-detection accuracy.** `README.md` states accuracy as **upstream's** measurement
and will keep doing so until a first-party gate exists. The blocker is **labelling, not licensing**
(ADR-0042 D9, corrected in this change after the original wording was found false).

*What was checked.* AMI, ICSI and HCRC Map Task are CC BY 4.0 and **may** be redistributed from this
repo — so the "no licence permits it" claim the ADR used to make does not survive. What they lack is
turn-boundary labels: they carry word/segment timings and dialogue-act coding, and AMI and ICSI
release "signals and transcription, and *some* of the annotations" under that licence rather than the
corpus entire, so a derivation must draw from the covered layers. The one corpus with the native
label — Pipecat's `smart-turn-data-v3.x`, whose `endpoint_bool` is exactly the target and which
trained the model we ship — declares **no licence at all**, and carries a per-row `synthetic` flag
indicating a large commercial-TTS majority governed by the TTS vendors' terms. LDC corpora
(Switchboard, Fisher, CallHome, DIHARD) are excluded on firmer ground: their agreement forbids
redistribution outside the user's research group, and its excerpt allowance is scoped to
non-commercial research publications, which SDK test fixtures are not.

*The in-house recording option is deferred, not rejected, and its open questions are unanswered:*
speaker consent and how it would be evidenced; the licence under which recorded audio would be
committed to an MIT repo; and whether the Git-LFS budget absorbs it — the repo already carries an
8.6 MB model in LFS, so 20 short WAVs are unlikely to be the binding constraint, but nobody has
checked the quota.

*Unblocking condition — a decision, not a search.* Derive roughly twenty clips (10 turn-end positive,
10 turn-mid negative) from AMI or Map Task under CC BY 4.0 with in-repo attribution, and accept that
the gate's ground truth is our own hand-derived labelling rather than a third party's. Target once
unblocked: precision ≥ 0.85 and recall ≥ 0.85 over `Tests/fixtures/audio/turn-boundaries/`. A
cheaper parallel path: ask pipecat-ai to declare a licence on the dataset cards, which they already
describe as open source — that would make `endpoint_bool` usable directly and remove the labelling
work entirely.

## Unresolved — rulings still owed

These carry no class yet. Each needs a decision before it can ship under D1.

1. **Vendor latency and pricing under D8's pin.** Citation and third-party wording are achievable;
   the pin — "binding the citation to the artifact actually shipped" — has no meaning when what ships
   is our WebSocket client and the vendor can change Sonic-3's latency with no commit here. Either
   D8's pin leg is waived for vendor-service claims (with the citation carrying an access date), or
   these are deleted.
2. **Scale claims ("100K+ agents").** First-party, so not ATTRIBUTED; a 100K-agent load test does
   not exist and is not reachable under D2. The only backing is an extrapolation at
   `docs/research/benchmark-analysis.md:323`. COHERENCE against that stated derivation, or a D9
   deferral.
3. **Behavioural constants in package READMEs** (`Resilience/README.md:8`,
   `high-load-tuning.md:138-140`). Checkable against source, but they read as API documentation. If
   D1 covers them, the registry grows by every documented constant in the repo.
4. **The `README.md` Status release bullets (`:63-68`).** Dated release history restated inside the
   acquisition surface. D1 excludes dated `CHANGELOG` history; this block is the same content in a
   living document, and `:65`/`:67` carry real claims.
5. **AMI surface counts need a counting definition before any of them can be guarded.** Responses:
   18 files in `Responses/`, 17 `public sealed class` — one file is a helper type. Events: 278 files,
   but 270 concrete events plus 8 abstract bases under `Events/Base/`, so the published 278 is a
   *file* count. Actions: 148 concrete classes across 149 files. Every published figure here is
   defensible under some definition and indefensible under another, and a gate cannot be written
   until the definition is chosen. This blocks the §1.2 rows for `README.md:45`, `:72` and
   `src/Verbara.Sdk.Ami/README.md:7`.
6. **Workload estimates in `high-load-tuning.md`** (events/sec per tier, the 200K/sec storm, VarSet
   at 50%+). These describe the reader's PBX, not this SDK. Out of scope, or delete.

## Inventory provenance

First compiled 2026-08-29 against `main` at `2e931bf7`, by full sweep of every file in *Scope* above.
Figures marked WRONG were verified against the tree at that commit.
