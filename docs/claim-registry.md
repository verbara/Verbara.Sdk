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
| 61 | headline version **v2.5.3** | COHERENCE | `StatusBlockCoherenceTests` — against `Directory.Build.props` `<PackageVersion>` | **OK** |
| ~~65~~ | ONNX model 8.3 MB | — | — | **DELETED** — lived in the release bullets, cut by the 2026-09-20 ruling on `README.md` release history |
| ~~67~~ | 94.26% English accuracy, in upstream's voice | — | — | **DELETED** — lived in the release bullets, cut by the 2026-09-20 ruling on `README.md` release history. The figure survives at `:465` (row below) with its citation and hash pin; this was the duplicate. |
| ~~67~~ | ~12 ms CPU inference | — | `TurnDetectionBenchmark` | **DELETED** — measured at 26.18–37.30 ms; deferred, see *Deferrals*. Its line was also removed with the release bullets on 2026-09-20. |
| 65 | 148/152 AMI (97%), 94/98 ARI (96%), 46/46, 27/27, 278 events | ENFORCING | — | GAP |
| 67 | **58 ADRs** | ENFORCING | `StatusBlockCoherenceTests` — counts `docs/decisions/*.md`, excluding the catalog `README.md` | **OK** |
| 91 | measurement provenance (Ryzen 9 9900X, .NET 10.0.5, BDN v0.14.0, 2026-04-18) | COHERENCE | `PerformanceTableCoherenceTests` — the header provenance test | PARTIAL — matched against the whole file rather than this line, and blind to the AMI row's .NET 10.0.6 MediumRunJob exception (`performance-record.json:12`); the two session-store rows measured apart from it state their own at :110 |
| 95 | AMI parse+dispatch 1.62M events/sec (617.6 ns) | ENFORCING + COHERENCE | `perf-regression.yml` `*AmiProtocolReader*` | GAP — observational only (`\|\| true`, no baseline) |
| 96 | ARI deserialize Channel 3.54M ops/sec (283 ns) | ENFORCING + COHERENCE | `*AriJson*` | GAP — observational only |
| 97 | ARI event parse 595K events/sec (1.68 µs), "2.7× faster than v1.0" | ENFORCING + COHERENCE | `*AriParseEvent*` | GAP — observational; the cross-version ratio has no guard at all |
| 98 | 163.9M lookups/sec (6.1 ns) | ENFORCING + COHERENCE | `*ChannelManager*` | GAP — observational |
| 99 | ~0.21 ns/observer, zero-alloc | ENFORCING + COHERENCE | `*ObserverDispatch*` | GAP — observational |
| 100 | Redis SaveAsync ~33.3K/sec (p50 30 µs), batch 91,021/sec | COHERENCE | `PerformanceTableCoherenceTests` — the value test | OK — re-measured 2026-09-12; the April figures it replaced (~12.6K/sec, p50 79 µs, batch 65,738/sec) understate today's measurement of the same store code ~2.6× and ~1.4×. Its provenance is row 110 |
| 101 | Postgres SaveAsync ~500/sec (p50 1.97 ms), batch 13,489/sec | COHERENCE | `PerformanceTableCoherenceTests` — the value test | OK — re-measured 2026-09-12 on the `NpgsqlExecutor` store, so no longer a measurement of Dapper; batch was 9,491/sec and its rise is not attributed to the rewrite. Its provenance is row 110 |
| 103 | session-store provenance: re-measured 2026-09-12, .NET 10.0.12, xunit Fact + Stopwatch against local Docker, PostgreSQL 18.4 / Redis 7.4.8, median of five runs; Postgres single-save latency is the WAL flush | COHERENCE | `PerformanceTableCoherenceTests` — the per-row provenance test | PARTIAL — date and runtime asserted against the Performance section; server versions, run count and the WAL-flush attribution stated, not asserted; and the check is opt-in, so deleting `provenance` from the record unbinds this line unnoticed |
| 126 | 9 ActivitySources | ENFORCING | `MarketingClaimsTests.cs:45-50` | OK |
| 127 | 15 Meters | ENFORCING | `MarketingClaimsTests.cs:52-57` | OK |
| 128 | 11 IHealthChecks — 6 core + 5 VoiceAi | ENFORCING | `MarketingClaimsTests.cs:76-97` | PARTIAL — total pinned, the 6/5 split is not |
| 129 | 60 const strings, 14 nested classes, "14+ unit tests" | ENFORCING | `MarketingClaimsTests.cs:59-74` | PARTIAL — the "14+ tests" sub-claim is unpinned |
| 157 | "First contact in 10 lines" | COHERENCE | — | WRONG — the snippet at :166-182 is 14 lines |
| 463 | Cartesia Sonic-3, no figure | — | — | **DELETED** — the figure moved to `src/Verbara.Sdk.VoiceAi.Tts/README.md`, cited; `40-90 ms` was never Cartesia's number (they publish sub-90 ms) |
| 465 | smart-turn-v3.2-cpu, 94.26% in upstream's voice | ATTRIBUTED | citation + hash pin | **OK** — this line previously carried the figure with no citation at all |

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
| 32 | "**designed for** … exceeding 100,000 concurrent agents" | EVIDENCE | — | **OK** — reworded 2026-09-20: "tested" removed, deferral declared with its blocker (D9). Was: GAP — nothing executes a load test at any scale; "designed for" is supported, "tested" is not |
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
| `session-store-backends.md:9-11,22,27,35` at `e250182e` | read latency <0.1 ms / <1 ms / 5-10 ms; the "sub-millisecond" InMemory and Redis bullets; "5-10 ms read latency is acceptable" | COHERENCE | **DELETED** — nothing measures InMemory, the record binds no read figure, and the only read the committed measurements time, `GetAsync` over loopback, put Postgres at p50 48 µs, not 5-10 ms; Postgres `GetAsync` measures under a millisecond too, so "sub-millisecond reads" did not tell Redis apart. This row cited `:26` until then; the bullets are `:22` and `:27`. The words that replaced these figures are the next two rows |
| `session-store-backends.md:9-11,22,35` | what a store call costs, in words: InMemory in-process, with no I/O and no serialization; Redis one `GET` for `GetAsync` and two for `GetByLinkedIdAsync`; Postgres one `SELECT` per read, one WAL flush per single save under the default `synchronous_commit=on`, and one shared by a `SaveBatchAsync` | ENFORCING | GAP — this repository's own code: true against `InMemorySessionStore.cs`, `RedisSessionStore.cs` and `PostgresSessionStore.cs` as of 2026-09-12 (`GetByLinkedIdAsync` sends one `GET` when the linked index misses), and nothing counts the commands |
| `session-store-backends.md:27` | of the two networked backends, Redis measures the faster single save | COHERENCE | PARTIAL — follows from the two `SaveAsync` p50s the guide test binds, but that test checks the `## Benchmarks` section as a whole, so which backend holds which figure is not asserted |
| `session-store-backends.md:175,177,179-180` | Redis `SaveAsync` p50 30 µs, batch 91,021 sess/sec; Postgres p50 1.97 ms, batch 13,489 sess/sec; batches of 500 sessions; measured 2026-09-12 on .NET 10.0.12 | COHERENCE | PARTIAL — `PerformanceTableCoherenceTests` — the guide test binds each session-store row's latency and batch, as whole figures, and its date and runtime to the `## Benchmarks` section, and fails if the record has no session-store row. Replaces ~250 µs / ~1.5 ms. Machine, server versions, instrument, loopback-without-TLS, run count and the batch size are stated, not asserted — the record holds no batch size. The check is section-wide, so the two backends' figures swapped between table rows still pass, and so does an unbound figure added to the section; and it binds only rows whose `operation` starts with `Session store`, so renaming one of the two off that prefix drops its backend from the check without a failure |
| `session-store-backends.md:177-182` at `e250182e` | the old Benchmarks table's InMemory column, its `GetAsync` ~200 µs / ~1.2 ms, `GetActiveAsync (1,000 active)` ~8 ms / ~12 ms and `SaveBatchAsync (100 sessions)` ~3 ms / ~15 ms | COHERENCE | **DELETED** — nothing records a measurement of InMemory, `GetActiveAsync` or a 100-session batch: the `Fact`s the table credited time only `SaveAsync`, `GetAsync` and 500-session batches, and `SessionsBackendsBenchmark`, which defines `GetActive_1000` and `SaveBatch_100`, has no committed result, no baseline entry and no workflow. `GetAsync` is measured, in the addendum, but the record does not bind it, so the guide points there instead of publishing it |
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

**The 100,000-concurrent-agent figure** (`src/Verbara.Sdk.Live/README.md:9`,
`docs/README-commercial.md:32`, and the scope statements in `docs/guides/high-load-tuning.md`).
Reworded 2026-09-20 from "designed **and tested** for" to "designed for"; the word *tested* was
false and is gone.

- **Blocker:** no load harness exists at any scale, and a 100K-agent Asterisk estate is not
  reachable in CI under D2 — this is the blocker that survives being argued with, not the absence
  of a number. The sizing arithmetic in `high-load-tuning.md` is a derivation, not a measurement,
  and one of its own inputs (the per-agent event rate) is itself an unguarded planning assumption,
  so a COHERENCE guard against it would assert arithmetic and look like evidence.
- **Unblocking condition:** a harness that can generate agent load and report a sustained rate.
  The open change `openspec/changes/longevity-soak-and-chaos/` is the natural vehicle if it adopts
  a scale target; until it does, nothing in flight discharges this.


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

1. **Behavioural constants in package READMEs** (`src/Verbara.Sdk.Resilience/README.md:8`,
   `docs/guides/high-load-tuning.md:138-140`). Checkable against source, but they read as API
   documentation. If D1 covers them, the registry grows by every documented constant in the repo.
   *(Note, 2026-09-20: whichever way this goes, a guard on `src/*/README.md` does not currently run —
   `scripts/ci/classify-docs-only.sh:25` treats `*/README.md` as docs-only, so the PR that breaks
   such a figure skips `Unit Tests`. The carve-out has to move in the same change.)*
2. **AMI surface counts need a counting definition before any of them can be guarded.** Responses:
   18 files in `Responses/`, 17 `public sealed class` — one file is a helper type. Events: 278
   files, of which **269** are `public sealed class` and **9** are shared base types (the 8 under
   `Events/Base/` plus `Events/ResponseEvent.cs`, base of 60 events). Actions: 148 concrete classes
   across 149 files. Every published figure here is defensible under some definition and
   indefensible under another, and a gate cannot be written until the definition is chosen. This
   blocks the §1.2 rows for `README.md:45`, `:72` and `src/Verbara.Sdk.Ami/README.md:7`.
   *(Corrected 2026-09-20: this entry previously read "270 concrete plus 8 abstract bases under
   `Events/Base/`". Neither half held — `grep -rn 'abstract class' Events` returns nothing, and the
   ninth base sits outside `Events/Base/`. `EventRegistryGenerator` already implements one
   executable definition: `[VerbaraMapping]` and not `IsAbstract`, which yields 269.)*

## Rulings settled 2026-09-20

Four of the six were decided and discharged in one change; each is recorded where it applies, and
the rows above and in the tables carry the result.

- **Vendor latency and pricing under D8's pin** → split. Latency stays as the vendor's own figure,
  cited with an access date, licensed by the new **D8a** ([ADR-0042 amendment](decisions/0042-public-claim-guard-classes.md)).
  Prices, cross-vendor ratios, market rankings in our own voice, and our own undated measurements of
  a third-party service were deleted. The sweep that settled this found **five of six** TTS latency
  figures did not match the vendor's published number, and that LMNT has shut down.
- **Scale claims ("100K+ agents")** → design target, not a measurement. "tested" removed; deferral
  declared below.
- **`README.md` Status release bullets** → cut. Release history lives in `CHANGELOG.md` only. The
  block had drifted three releases behind; the 94.26% figure it carried survives at `README.md:472`
  with its citation and hash pin intact.
- **Workload estimates in `high-load-tuning.md`** → out of scope as planning assumptions, with the
  label written into the guide above the table rather than only recorded here.

## Inventory provenance

First compiled 2026-08-29 against `main` at `2e931bf7`, by full sweep of every file in *Scope* above.
Figures marked WRONG were verified against the tree at that commit.

Updated 2026-09-12 for the session-store re-measurement only — `README.md` rows 98, 107 and 108, the new row 110 for the provenance statement that re-measurement added, and the two `session-store-backends.md` rows citing the record's figures; no other row was re-verified.

Updated 2026-09-12 again, for `docs/guides/session-store-backends.md` only — its read-latency row, re-cited from `:9-11,26,35` to `:9-11,22,27,35` because `:26` never held a latency claim, marked DELETED and pinned to `e250182e`; two new rows for the words that replaced those figures, the store-call costs at `:9-11,22,35` and the comparison at `:27`; its Benchmarks row, now bound; and a new DELETED row for the old Benchmarks figures nothing records, also pinned to `e250182e`. No line above `## Benchmarks` moved, so the row for `:5,56,70,76` still points at its claims and was left as it was; no other row was re-verified.

Updated 2026-09-20 for the `README.md` Status block only — rows 61 (headline version) and 74 (ADR count), both WRONG since before the 2026-08-29 sweep and both now bound by `StatusBlockCoherenceTests`. Re-verified against the tree at that date: the headline read v2.2.1 while `Directory.Build.props` `<PackageVersion>` and the latest tag were both 2.5.3, and "37 ADRs" against 53 files in `docs/decisions/` excluding the catalog. The `29 NuGet packages` figure on the same line was re-counted and is correct (29 projects under `src/`, none `IsPackable=false`), so its row is unchanged and still a GAP. The test-count figure on that same line is still WRONG and is NOT fixed here: it is COHERENCE with nothing to cohere against, and committing that record is its own change. No other row was re-verified.

Updated 2026-09-20 again, for the four rulings settled that day — the vendor-latency and pricing
surface (`README.md:470`, `src/Verbara.Sdk.VoiceAi.Tts/README.md` table and `Choosing a provider`,
`src/Verbara.Sdk.VoiceAi.Stt/README.md:9`, both `Examples/VoiceAi*Example/README.md`), the
100K-agent scale claim (`src/Verbara.Sdk.Live/README.md:9`, `docs/README-commercial.md:32`), the
`README.md` release bullets (`:63-68`, deleted, taking row 65 with them), and the
`high-load-tuning.md` load column. Every vendor figure that survived was re-verified against the
vendor's own page on that date and the access date is carried in the citation; five of the six TTS
latency figures did **not** match and were corrected or removed, and LMNT was found to have shut
down. The AMI counting-definition entry under *Unresolved* was corrected against the tree (269 + 9,
not 270 + 8). No other row was re-verified.

**Line pointers re-based 2026-09-20.** Cutting the `README.md` release bullets removed seven lines
(six bullets and a blank), so every row in the `README.md` section pointing below the cut moved by
−7. All eighteen were re-pointed in that same change and each was checked against the line it now
names; two rows that lived *inside* the cut (`~~65~~` the ONNX size, `~~67~~` the duplicated 94.26%)
are struck through, and the 94.26% figure survives at `:465` with its citation and hash pin. A
registry keyed by line number does not survive an edit above the line it points at, and nothing
enforces that — the check is manual and belongs in any PR that adds or removes README lines.

Corrected in the same pass, and **pre-existing** rather than caused by that edit: the AMI
parse+dispatch row transcribed the claim as `1.53M events/sec (653 ns)` while `README.md` has said
`1.62M events/sec (617.6 ns)` on `main` for some time. The registry's own copy of a figure had
drifted from the figure — the exact failure it exists to detect, one level up.
