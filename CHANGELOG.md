# Changelog

All notable changes to this project will be documented in this file.

## [Unreleased]

## [1.15.3] - 2026-05-03

**R1.5 "VoiceAi Refresh" — three new TTS providers + TTFA metric + housekeeping.** Strictly additive minor patch — zero breaking changes, all existing test suites pass without modification. Ships ElevenLabs Flash 2.5 polish, Deepgram Aura 2 TTS WebSocket as a new provider, LMNT TTS as a new provider, and the `tts.synthesis.ttfa_ms` histogram so the latency claims of the new providers are verifiable in production. Also rolls in tooling housekeeping (coverlet 10, CI dependency-review, xunit migration tracking).

### Added — VoiceAi providers

- **`Asterisk.Sdk.VoiceAi.Tts.Deepgram`** — new TTS provider using Deepgram's WebSocket streaming endpoint (`wss://api.deepgram.com/v1/speak`). NOT the older REST `/v1/speak` (which had ~70% higher LLM→TTS latency per Deepgram's published benchmarks). Mirrors the Cartesia WebSocket pattern (`Channel<ReadOnlyMemory<byte>>` + dedicated receive loop, half-close socket post-request). 12-voice catalog: 8 Aura 2 EN voices (Thalia default, Andromeda, Zeus, Orpheus, Helios, Apollo, Luna, Arcas) + 1 Aura 2 ES (Sirio) + 3 legacy Aura 1 voices (Asteria, Orion, Stella) for migration paths. New types under `Asterisk.Sdk.VoiceAi.Tts.Deepgram` namespace: `DeepgramTtsOptions`, `DeepgramSpeechSynthesizer`, `DeepgramVoices`. Register via `services.AddDeepgramSpeechSynthesizer(opts => { opts.ApiKey = "…"; opts.Model = DeepgramVoices.Thalia; })`. Auto-registers `TtsHealthCheck`. Multilingual Aura 2 voices (NL/FR/DE/IT/JA) intentionally not in the catalog yet — voice ids unconfirmed in public Deepgram docs at impl time; tracked as a TODO in `DeepgramVoices.cs`.

- **`Asterisk.Sdk.VoiceAi.Tts.Lmnt`** — new TTS provider for LMNT (sub-200 ms TTFA per third-party 2026 benchmarks). Supports both transports via `LmntTtsOptions.Transport` enum: `WebSocket` (default, low-latency, `wss://api.lmnt.com/v1/ai/speech/stream`) and `Http` (fallback for environments blocking outbound WS, `POST https://api.lmnt.com/v1/ai/speech/generate`). Auth via `X-API-Key` (header for HTTP; first-message JSON field for WS) + `lmnt-version: 1.0`. 4-voice catalog (`Leah` default, `Amy`, `Ansel`, `Elowen`). New types under `Asterisk.Sdk.VoiceAi.Tts.Lmnt` namespace: `LmntTtsOptions`, `LmntSpeechSynthesizer`, `LmntVoices`. Register via `services.AddLmntSpeechSynthesizer(opts => { opts.ApiKey = "…"; opts.Voice = LmntVoices.Leah; })`. Auto-registers `TtsHealthCheck`. A few contract details in the LMNT public docs were ambiguous; `TODO(R1.5)` comments in the source flag specific lines to verify against the live API at integration-test time.

### Added — ElevenLabs Flash 2.5

- **`ElevenLabsModels`** — public static class with const strings: `Flash25 = "eleven_flash_v2_5"`, `Turbo2 = "eleven_turbo_v2"`, `Multilingual2 = "eleven_multilingual_v2"`. Use these instead of magic strings in `ElevenLabsOptions.ModelId`.
- **`ElevenLabsLatencyOptimization`** — public enum (`Off`/`Low`/`Mid`/`High`/`Max`, mapped to ElevenLabs' `optimize_streaming_latency` URL param 0-4 scale).
- **`ElevenLabsOutputFormat`** — public enum (`Pcm16k` / `Pcm22050` / `Pcm24k`, mapped to provider's `output_format` URL param).
- **`ElevenLabsOptions.LatencyOptimization` and `.OutputFormat`** — additive properties. The synthesizer surfaces these as query parameters on the WebSocket endpoint URL.

### Added — Observability

- **`SpeechSynthesisMetrics.SynthesisTtfaMs`** — new public `Histogram<double>` exposed on the existing `Asterisk.Sdk.VoiceAi.Tts` `Meter`. Records **Time-To-First-Audio**: elapsed milliseconds from synthesis start until the first audio chunk is yielded to the caller. Tagged with `voiceai.provider`. Recommended histogram buckets: 5/10/25/50/100/250/500/1000/2500/5000 ms. The existing `tts.synthesis.latency_ms` (total synthesis duration) is preserved unchanged.
- **`VoiceAiPipeline`** records TTFA inline at the existing metric site — gated by a single boolean so only the first chunk emits the measurement; subsequent chunks pass through without extra cost. Behavior validated by 5 new pipeline tests covering: recording on first yield, no recording on empty enumerable, TTFA ≤ total latency, exactly-once on many chunks, no recording when synthesizer throws.

### Added — CI / tooling

- **`.github/workflows/dependency-review.yml`** — preventive scanning on every PR. Blocks merges that introduce a package with High/Critical CVE or a copyleft license incompatible with MIT (AGPL, GPL-2.0, GPL-3.0, SSPL). Complements the existing reactive Dependabot configuration.

### Changed

- **ElevenLabs default model** flips from `eleven_turbo_v2` → `eleven_flash_v2_5`. **Non-breaking default change**: callers who explicitly set `ElevenLabsOptions.ModelId` see no change; callers using the default see the new model. Flash 2.5 targets <150 ms TTFA per ElevenLabs' published latency guidance and is the correct choice for real-time telephony. Eleven v3 (GA 2026-03-14) is intentionally NOT a candidate for this SDK — v3 is the expressive flagship for non-realtime use; Flash 2.5 remains the streaming/telephony target.
- **`coverlet.collector` 6.0.4 → 10.0.0** — drop-in replacement for code coverage collection. Skips 8.x (no value sitting there). Real fixes that benefit this SDK: IAsyncEnumerable branch math (#1836) used in ARI/Live/Sessions stream code, `LibraryImport`/`DllImport` instrumentation crashes (#1762), `Mediator.SourceGenerator` empty reports (#1718). `nuspec` deps empty + `coverlet.collector.targets` and `VSTestIntegration.md` shipped surface idéntico across versions verified at audit time. Validated locally on `Tests/Asterisk.Sdk.Ami.Tests` with a `VersionOverride` spike — zero delta in coverage metrics (line/branch counts byte-identical between 6.0.4 and 10.0.0 baseline). VSTest collector hook works on .NET 10 SDK + xunit 2.9 without `TestingPlatformDotnetTestSupport=false` guard.
- **`.github/dependabot.yml`** — removed the obsolete `coverlet.collector` major-version ignore rule that mischaracterized 10.x as breaking. Only the MTP/VSTest split matters for the upgrade and the repo stays VSTest.

### Documentation

- **R1.5 spec + plan rewritten in place (v2)** — scope correction based on a deep state-of-the-art audit (May 2026): (a) **dropped** Whisper V3 local STT (quality unfit for telephony 8 kHz audio per third-party benchmarks — ~30-40% WER regression vs cloud STT options already in the SDK; Whisper.net AOT support unconfirmed in any release notes; deferred to a future on-prem privacy track); (b) **upgraded** Deepgram Aura 2 integration from REST to WebSocket; (c) **added** LMNT TTS as a new provider. Same total ~1 week of work, no Phase 0 AOT spike, lower risk, more product value. Original v1 spec retained in git history at commit `565a1bb`.
- **`docs/research/2026-05-03-xunit-v3-v4-migration-readiness.md`** — watch list documenting the four readiness gates that must flip before re-evaluating the migration from xunit 2.9.x: FluentAssertions #2935 detection bug fix shipped in FA 7.x, xunit #3167 NSubstitute false-positive resolved, xunit.v3 v4.0 stable released with full Native AOT, and a canary migration in dotnet/runtime or dotnet/aspnetcore. The `dependabot.yml` ignore rules for `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, and `FluentAssertions` remain tied to these gates.
- **`src/Asterisk.Sdk.VoiceAi.Tts/README.md`** — provider table updated to 6 providers (added Deepgram, LMNT). New "Metric catalog" section documents `tts.synthesis.ttfa_ms` and `tts.synthesis.latency_ms` with recommended histogram buckets.

### Notes

- 0 build warnings, 0 trim warnings, 0 IL3050/IL3053 across all 26 NuGet packages. Native AOT clean.
- Test totals: ~2,837 unit tests pass / 0 fail / 0 skip (was ~2,799 in v1.15.2). New tests: +9 ElevenLabs (Track 1.A), +12 Deepgram (Track 1.B), +12 LMNT (Track 1.C), +5 TTFA pipeline (Phase 2). 154/154 functional + 65/65 integration unchanged.
- **Deferred**: per-provider streaming-not-buffering quality gate (one test per TTS provider asserting the synthesizer yields its first frame before the upstream finishes sending) was scoped into Phase 3 but deferred to a follow-up patch — the TTFA metric works correctly today; this gate would catch *future* provider regressions where a provider buffers the full clip before yielding. Tracked as a follow-up issue.
- 26 packages pack clean with `TreatWarningsAsErrors=true`.
- Whisper V3 local STT is **deferred**, not cancelled. The original v1 R1.5 plan included it; the v2 scope-correction moved it to a future "on-prem privacy mode" track where it will deliver actual value (air-gapped privacy-sensitive deploys), not as a marginal STT option for telephony where Deepgram cloud already wins on quality and latency.

## [1.15.2] - 2026-04-27

**Documentation refresh + CI portability fix.** Zero public API surface delta (`PublicAPI.Shipped.txt` unchanged). Zero functional changes. Ships a doc-audit sprint that addresses the highest-impact P0+P1 findings on nuget.org / repo-landing pages, plus drops a machine-specific path from `nuget.config` so GitHub Actions runners can restore the project portably.

### Changed (documentation — root README + ops docs)

- **Root [`README.md`](README.md)** — "Status" paragraph rewritten end-to-end. The previous version still described **v1.12.0 / 24 packages** despite v1.13/v1.14/v1.15 having shipped since. Now describes v1.15.1 cumulative state (26 pkgs, 4-release rollup highlighting `Asterisk.Sdk.Resilience`, `Asterisk.Sdk.Cluster.Primitives`, per-URL circuit breaker on `Push.Webhooks`, `AsteriskSemanticConventions` catalog, multi-RID AOT matrix, dual Asterisk 22 LTS / 23 Standard support, 35 ADRs).
- **Root README Observability section** — Meter count corrected `14` → `15` (the `Asterisk.Sdk.Resilience` meter shipped in v1.14.0 but the doc still claimed the v1.13 count). Added explicit reference to the `AsteriskSemanticConventions` const-string catalog so consumers know it exists.
- **[`docs/operations/README.md`](docs/operations/README.md)** — Same meter-count correction (`12` → `15`) in two places. Added pointer to `AsteriskTelemetry.MeterNames` as the canonical source-of-truth list.

### Changed (per-package READMEs visible on nuget.org)

Five package READMEs were either 2-line stubs or inadequate-but-better. Each now follows the same template used by the well-documented packages (`Asterisk.Sdk.Resilience` v1.14, `Asterisk.Sdk.Cluster.Primitives` v1.15) — title + 1-line tagline, "What it does" with public surface, install instructions, working quickstart code, ADR cross-references where relevant, and a license note.

- **[`src/Asterisk.Sdk/README.md`](src/Asterisk.Sdk/README.md)** — was 10 lines. Now ~60 lines covering the actual public surface consumers reach for: `AsteriskSemanticConventions` catalog (60 const strings / 14 nested classes), `AsteriskTelemetry` runtime-discoverable lists (9 ActivitySources / 15 Meters), source-generator attribute markers, OTel one-liner registration snippet.
- **[`src/Asterisk.Sdk.Hosting/README.md`](src/Asterisk.Sdk.Hosting/README.md)** — was 20 lines. Now ~90 lines positioning the package as the recommended SDK entry point with `AddAsterisk` variants (`IConfiguration` and inline `Action<AsteriskOptions>`), `appsettings.json` binding example, multi-server pool pointer, health-endpoint wiring, hosted-lifecycle and AOT notes.
- **[`src/Asterisk.Sdk.VoiceAi.Stt/README.md`](src/Asterisk.Sdk.VoiceAi.Stt/README.md)** — was 2 lines and listed only 4 of 7 providers. Now ~65 lines with full provider table (Deepgram, Whisper local, Azure Whisper, Google Speech, Cartesia Ink-Whisper, AssemblyAI Universal-2, Speechmatics) including mode + latency notes, per-provider DI registration snippets, example pointers, and an ADR-0014 cross-reference for the no-vendor-SDK design rationale.
- **[`src/Asterisk.Sdk.VoiceAi.Tts/README.md`](src/Asterisk.Sdk.VoiceAi.Tts/README.md)** — was 2 lines and listed only 2 of 4 providers (missing Cartesia and Speechmatics). Now ~70 lines with full provider table (ElevenLabs, Cartesia Sonic-3, Speechmatics, Azure) including TTFA targets and a "choosing a provider" decision guide.
- **[`src/Asterisk.Sdk.VoiceAi.Testing/README.md`](src/Asterisk.Sdk.VoiceAi.Testing/README.md)** — was 2 lines. Now ~65 lines with the three fakes table (`FakeSpeechRecognizer`, `FakeSpeechSynthesizer`, `FakeConversationHandler`), quickstart code for stubbing recognizers + synthesizers in unit tests, and a "why use it" section (no API keys in CI, deterministic timing, failure injection).

### Fixed (build portability)

- **[`nuget.config`](nuget.config)** — drop the machine-specific local feed entry. The previous commit (`4393dfc`) added `<add key="local" value="/media/Data/Source/IPcom/local-nuget-feed/" />` to mirror the Pro/Platform pattern, but the MIT SDK is the **producer** of that cross-repo local feed (Pro and Platform are the consumers). The hard-coded absolute path broke `aot-check` on GitHub runners with `NU1301: The local source ... doesn't exist`. Comment expanded to document why no local source belongs in this repo's `nuget.config`.

### Documentation

- **R1.5 "VoiceAi Refresh" plan + spec** — re-targeted from v1.15.2 → **v1.15.3** to make room for this docs-only patch. R1.5 itself is unchanged; it remains the next non-trivial release with Phase 0 AOT spike for Whisper.net pending.

### Notes

- 0 build warnings, 0 trim warnings across all 26 NuGet packages. Native AOT clean.
- 35 ADRs in repo (0001–0035, no holes). Resolved 0031 collision in v1.15.1 stays resolved.
- Test totals unchanged: ~2,799 unit tests / 154 functional / 65 integration. Same numbers as v1.15.1.
- 4 commits on `main` since `v1.15.1` tag (`41ca790`): `4393dfc` nuget.config, `205125b` docs D1+D2, `42e4081` nuget.config fix, plus the `1.15.2` bump itself.
- README content is embedded into each `.nupkg` at pack time, so the new READMEs become visible on nuget.org as soon as `publish.yml` succeeds.

## [1.15.1] - 2026-04-26

**Housekeeping patch.** Zero public API surface delta (`PublicAPI.Shipped.txt` unchanged across all 26 packages). Zero functional changes in shipped binaries — the only production-code touch is an `internal` accessor used exclusively by the test assembly via `InternalsVisibleTo`. Ships accumulated dependency maintenance, a CI test-stability fix, and post-v1.15.0 ADR/spec documentation.

### Fixed

- **`Asterisk.Sdk.Cluster.Primitives.Tests.InMemoryClusterTransportTests`** — eliminated CI flakiness on 5 tests that used `Task.Delay(50)` to "wait for the subscriber's `await foreach` to register the channel". Replaced with deterministic polling on a new `internal int SubscriberCount` accessor (visible only via existing `InternalsVisibleTo` to the test assembly). 20/20 stability runs verified locally; CI verde on `f5a1bd9` and `e2f5e82`.

### Changed

- **`Microsoft.Extensions.*` 10.0.6 → 10.0.7** — patch bump on 11 packages (`Logging`, `Logging.Abstractions`, `Logging.Console`, `DependencyInjection`, `DependencyInjection.Abstractions`, `Hosting`, `Hosting.Abstractions`, `Configuration`, `Configuration.Abstractions`, `Diagnostics.HealthChecks`, `Http`, `Options`). Transitively visible to consumers of `Asterisk.Sdk.Hosting`, `Sessions`, `OpenTelemetry`, etc.
- **`OpenTelemetry` 1.15.2 → 1.15.3** — patch bump on 4 packages (`OpenTelemetry`, `Extensions.Hosting`, `Exporter.Console`, `Exporter.OpenTelemetryProtocol`). Visible to consumers of `Asterisk.Sdk.OpenTelemetry`.
- **`NATS.Client.Core` / `NATS.Client.Hosting` 2.5.10 → 2.7.3** — minor bump on the upstream client used by `Asterisk.Sdk.Push.Nats`. **Forward-compat verified** end-to-end: 6/6 NATS integration tests (Testcontainers + real `nats:latest`) pass; none of the 2.6.x/2.7.x breaking changes affect our usage (no JetStream APIs, ASCII-only subjects, internal timeouts wrapped in our own `CancellationTokenSource.CreateLinkedTokenSource` so the `OperationCanceledException` → `NatsTimeoutException` rename is irrelevant; OTel tag rename `network.protocol.version` → `network.transport` not referenced in our docs/dashboards).
- **`Microsoft.SourceLink.GitHub` 10.0.202 → 10.0.203** — patch bump (build-time, not user-facing).
- **`Meziantou.Analyzer` 3.0.50 → 3.0.52** — patch bump (build-time analyzer).
- **`dotnet-reportgenerator-globaltool` 5.5.5 → 5.5.6** — patch bump (CI tool, not shipped).

### Documentation

- **ADR-0035 "COS (Calling Permissions System) deferred — customer-driven trigger only"** (Accepted 2026-04-25) — locks the deferral of the `feat/calling-permissions` branch until a customer-driven trigger is met. Originally numbered ADR-0031; **renumbered to 0035 on 2026-04-26** to fix an accidental collision with the prior Proposed ADR-0031 "Domain vs Integration events" (part of the v1.15.0 Event Model v2 batch). Decision content unchanged.
- **R1.5 "VoiceAi Refresh" design spec + execution plan** — `docs/specs/2026-04-25-r1.5-voiceai-refresh-design.md` + `docs/plans/active/2026-04-25-r1.5-voiceai-refresh.md`. Pending Phase 0 AOT spike (Whisper.net AOT compatibility probe) before implementation. Targets v1.15.2 (re-targeted from v1.15.1 after this housekeeping cut).

### Notes

- 0 build warnings, 0 trim warnings across all 26 NuGet packages. Native AOT clean.
- 35 ADRs in repo (0001–0035 — no missing numbers; 0031 collision resolved by renumbering COS to 0035, original 0031 "Domain vs Integration events" remains Proposed).
- Test totals unchanged: ~2,799 unit tests / 154 functional / 65 integration. `Asterisk.Sdk.Cluster.Primitives.Tests` stays at 20 tests (the new helper is not a test).
- 13 commits on `main` since `v1.15.0` tag, all CI-verified before tag cut.

## [1.15.0] - 2026-04-20

**Pre-v2 Foundation.** No breaking changes. New MIT package `Asterisk.Sdk.Cluster.Primitives` (26th on nuget.org) ships domain-agnostic cluster abstractions that Pro.Cluster and future consumers can build on. `AsteriskSemanticConventions` catalog grows with `Tenant`/`Event`/`Node` nested classes (6 new const strings). `Asterisk.Sdk.Push.Webhooks` gains per-URL circuit breaker. ADR-0028 "Cadence commitment (v1 preview → v2 stable)" moves to `Accepted`. Operations starter kit (3 Grafana dashboards + Jaeger query catalog) lands in `docs/operations/`. Dual Asterisk support matrix (22 LTS + 23 Standard) added. AOT validation workflow expands to multi-RID matrix.

### Added

- **`Asterisk.Sdk.Cluster.Primitives`** — new MIT package with domain-agnostic cluster abstractions: `ClusterEvent` (abstract record canónico), `NodeInfo`, `NodeState`, `IClusterTransport` (pub/sub), `IDistributedLock`, `IMembershipProvider`. Ships 3 in-memory reference implementations for tests. 20 unit tests. Addresses PSD v2 §9 Mes 3 foundation item. Pro.Cluster consumes this in Pro v1.10.0-pro (R1-B bundled, not included in this release).
- **`AsteriskSemanticConventions.Tenant`** — new nested class with `Id` constant (`"tenant.id"`). Aligns tenant-context tag name across SDK + Pro telemetry.
- **`AsteriskSemanticConventions.Event`** — new nested class with `Type`, `Id`, `Count` constants. Standardizes event-attribution tag names for Push/EventStore/Analytics consumers.
- **`AsteriskSemanticConventions.Node`** — new nested class with `OriginId`, `ReceiverId` constants. Standardizes cluster node-identification tag names.
- **`Asterisk.Sdk.Push.Webhooks` per-URL circuit breaker** — `WebhookDeliveryService` now keys a `CircuitBreakerState` dictionary by `TargetUrl.AbsoluteUri`. Defaults: 5 failures → 30s open. New counters `CircuitOpened{url}` / `CircuitSkipped{url}` on meter `Asterisk.Sdk.Push.Webhooks`. `TimeProvider` injection for deterministic tests. 5 new unit tests.
- **`docs/operations/` starter kit** — 3 Grafana dashboards (JSON-validated): `grafana-overall.json`, `grafana-webhooks.json`, `grafana-resilience.json`. `jaeger-queries.md` with 9 query patterns for distributed tracing. `README.md` with import instructions.
- **`docs/guides/asterisk-version-matrix.md`** — dual Asterisk support guide (22 LTS + 23 Standard lifecycle, break-change risk areas, migration notes).
- **`docker/docker-compose.test-23.yml`** + parameterized `docker/Dockerfile.asterisk` (`ASTERISK_VERSION`, `CODEC_OPUS_VERSION` build args) — run Functional + Integration test matrix against Asterisk 22 and 23 in parallel.
- **`.github/workflows/aot-validate.yml`** (renamed from `aot-trim-check.yml`) — multi-RID AOT validation matrix (`linux-x64`, `win-x64`, `osx-arm64`). `verify-aot.sh` accepts RID arg + host-match smoke run. `AotCanary` app extended to cover `Webhooks` / `Resilience` / `Cluster.Primitives`.

### Changed

- **ADR-0028 "Cadence commitment (v1 preview → v2 stable)"** — status `Proposed` → `Accepted`. v2.0.0 target Q4 2026 formalizado. Cadencia minor releases cada 2-4 semanas durante v1.x; v2 preview → stable window documented.
- **`MeterNames_ShouldContainAllPackages` pin test** — expected count 14 → 15 (corrects v1.14 drift where Resilience meter shipped without test update).

### Documentation

- **Post-ADR-0029 roadmap** — `docs/plans/active/2026-04-20-post-adr-0029-roadmap.md` (sanitized SDK scope, full cross-repo mirror lives in private Pro repo). Covers R1/R1.5/R2/R3/R4 ~8-10 semanas plan.
- **ADR-0026..0034 batch** — PSD v2 foundation (Event Model v2 prerequisites, CloudEvents preview, IEventLog split, ISessionInterceptor, ClusterEvent contract, cadence commitment, AOT multi-RID policy). 10 ADRs total this release.
- **`docs/plans/archived/2026-04-21-v1.14-candidates-absorbed.md`** — historical record of v1.14 candidates absorbed into post-ADR-0029 plan.

### Notes

- 0 build warnings, 0 trim warnings across all 26 NuGet packages. Native AOT clean (multi-RID matrix).
- 25 ADRs (post-v1.14.0) → 34 ADRs (post-v1.15.0). ADR-0026..0034 batch covers PSD v2 foundation; ADR-0028 advances to `Accepted`; ADR-0029 remains `Accepted` (v1.14 shipped).
- 13 commits on `main` since `v1.14.0` tag, all CI-verified.
- Pro v1.10.0-pro coordinates adoption (consume `Cluster.Primitives` + adopt `SemanticConventions.Tenant/Event/Node` in 23 call-sites across 7 Pro packages).

## [1.14.0] - 2026-04-20

**Resilience primitives added to SDK (MIT).** No breaking changes. New `Asterisk.Sdk.Resilience` package (25th on nuget.org) ships composable `CircuitBreakerState`, `ResiliencePolicy`, `ResiliencePolicyBuilder`, `CircuitBreakerOpenException`, `ResilienceMetrics`, `BackoffSchedule`, and `AddAsteriskResilience` DI extension. Migrated from `Asterisk.Sdk.Pro.Resilience` v1.8.1-pro per [ADR-0029](docs/decisions/0029-resilience-primitives-mit.md) (stewardship pledge — generic primitives belong in MIT). Internal hot paths (AMI/ARI reconnect, Webhook delivery) now share a single backoff primitive instead of three duplicated open-coded loops.

### Added

- **`Asterisk.Sdk.Resilience`** — new MIT package with composable resilience primitives. AOT-safe, zero reflection, `TimeProvider`-based for testability. 38 migrated unit tests + 12 new `BackoffSchedule` tests (50 total). Meter `Asterisk.Sdk.Resilience` enrolled automatically by `AddAsteriskOpenTelemetry().WithAllSources()` via `AsteriskTelemetry.MeterNames` catalog.
- **`BackoffSchedule.Compute(attempt, baseDelay, multiplier, maxDelay)`** — stateless helper for reconnect loops and iterative retry schedules that don't fit the bounded `ResiliencePolicy.ExecuteAsync` model. Preserves configurable multiplier + max delay cap (critical for reconnect loops with specific timing requirements).
- **`BackoffSchedule.ComputeWithJitter`** — same with deterministic ±jitter via caller-provided `Random` source.

### Changed

- **`AmiConnection.ReconnectLoopAsync`** — internal refactor. Delegates backoff calculation to `BackoffSchedule.Compute` (preserves `ReconnectInitialDelay` + `ReconnectMultiplier` + `ReconnectMaxDelay` semantics exactly). Zero observable behavior change; 633/633 AMI tests green.
- **`AriClient.ReconnectLoopAsync`** — same refactor. 423/423 ARI tests green.
- **`WebhookDeliveryService.DeliverAsync`** — same refactor. 13/13 Webhook tests green.

### Migration

Consumers of `Asterisk.Sdk.Pro.Resilience` v1.8.x-pro migrate by renaming `using` + swapping `<PackageReference>`. See [ADR-0029 Migration guide](docs/decisions/0029-resilience-primitives-mit.md#migration-guide). Meter name changes from `Asterisk.Sdk.Pro.Resilience` to `Asterisk.Sdk.Resilience` (dashboards need one-time update; no dual-emit window).

## [1.13.0] - 2026-04-20

**Telemetry + multi-node Push.** No breaking changes. Public API grows with `AsteriskSemanticConventions` catalog (OpenTelemetry attribute names for SIP/Asterisk), `AsteriskSemanticConventions.Events` (span-event names), `RemotePushEvent` envelope, and new `Asterisk.Sdk.Push.Nats` subscribe-side options. Package count stable at 24 on nuget.org.

### Added

- **`Asterisk.Sdk.AsteriskSemanticConventions`** — new public static catalog (54 const strings across 11 nested classes) standardizing OpenTelemetry attribute names for SIP/Asterisk telephony. Consumers reference by name (`AsteriskSemanticConventions.Channel.Id`, `AsteriskSemanticConventions.VoiceAi.Provider`, etc.) so dashboard/query code remains stable across SDK versions. Pinned by 14 unit tests. Backed by the draft in `docs/research/2026-04-19-otel-sip-semantic-conventions.md`. ([c62f8ce](https://github.com/Harol-Reina/Asterisk.Sdk/commit/c62f8ce), [066cb3c](https://github.com/Harol-Reina/Asterisk.Sdk/commit/066cb3c))
- **`AsteriskSemanticConventions.Events`** nested class — span event names for transient, event-shaped telemetry (use with `Activity.AddEvent`, not `SetTag`). Five entries: `asterisk.channel.hangup`, `asterisk.dtmf.received`, `asterisk.media.started`, `asterisk.media.buffering`, `asterisk.media.mark_processed`. `WebSocketAudioSession` now emits these events on `Activity.Current` when the matching chan_websocket control message arrives. No-op when no span is active. XON/XOFF flow-control signals intentionally NOT instrumented (too noisy for span events). ([df0fe93](https://github.com/Harol-Reina/Asterisk.Sdk/commit/df0fe93), [2a7af1a](https://github.com/Harol-Reina/Asterisk.Sdk/commit/2a7af1a))
- **`Asterisk.Sdk.Push.Nats` subscribe side (bidirectional bridge)** — closes T2 of the v1.13 roadmap. New `NatsBridgeOptions.NodeId` (optional, enables loop prevention) and nested `Subscribe` options (`SubjectFilters`, `QueueGroup`, `SkipSelfOriginated`) turn the bridge bidirectional. Incoming NATS messages materialize as `Asterisk.Sdk.Push.Events.RemotePushEvent` (new public envelope) and are republished to the local `RxPushEventBus` so SSE / Webhook / dashboard subscribers on receiving nodes see the events without change to their filtering code. Loop prevention via optional `"source":"nodeId"` field in the JSON envelope + a .NET-type guard that never republishes a `RemotePushEvent`. New metrics: `EventsReceived`, `EventsSkipped`, `EventsDecodeFailed`. Extension point `INatsPayloadDeserializer` lets consumers round-trip to their concrete `PushEvent` subclasses if desired; default ships envelope-only. Queue-group semantics are opt-in; default pub/sub matches the local bus fan-out contract. JetStream / durable replay remain out of MIT (ADR-0011 boundary). Backed by [ADR-0025](docs/decisions/0025-push-nats-subscribe-and-loop-prevention.md). ([059e46d](https://github.com/Harol-Reina/Asterisk.Sdk/commit/059e46d) through [c98229f](https://github.com/Harol-Reina/Asterisk.Sdk/commit/c98229f))
- **Six new example apps** under `Examples/` (16 → 22): `VoiceAiCartesiaExample`, `VoiceAiAssemblyAiExample`, `VoiceAiSpeechmaticsExample`, `WebSocketMediaExample` (chan_websocket control protocol), `AriOutboundExample`, `NatsBridgeExample`. All v1.12 features now have runnable showcases. ([991078e](https://github.com/Harol-Reina/Asterisk.Sdk/commit/991078e), [60fcdbb](https://github.com/Harol-Reina/Asterisk.Sdk/commit/60fcdbb))
- **4 `Asterisk.Sdk.Push.Nats` Testcontainers integration tests** against real `nats:2.10-alpine` covering subject prefix, payload bytes, multi-event delivery, and custom prefix behavior. `[Trait("Category", "Integration")]`. ([7a6f6fa](https://github.com/Harol-Reina/Asterisk.Sdk/commit/7a6f6fa))
- **Shared `WebSocketTestServer`** in `Tests/Asterisk.Sdk.TestInfrastructure/WebSocket/` — TcpListener + manual HTTP/1.1 upgrade + `WebSocket.CreateFromStream(IsServer=true)`. Unblocks `ws.Abort()` test paths that previously hung on Linux under `HttpListener`. 2 new abort tests added (AssemblyAi STT, Speechmatics STT) closing the silent coverage gap. ([b02bf18](https://github.com/Harol-Reina/Asterisk.Sdk/commit/b02bf18))

### Changed

- **Activity.SetTag call-sites aligned to `AsteriskSemanticConventions`.** Five `Diagnostics/*ActivitySource.cs` files (VoiceAi, VoiceAi.AudioSocket, VoiceAi.OpenAiRealtime, Live, Sessions) now emit the conventions-matching attribute names: `voiceai.channel_id` → `asterisk.channel.id`, `originate.context/extension` → `dialplan.context/extension`, `session.direction/state/duration_ms` → `call.direction/state/duration_ms`. A T1.2 cross-package sweep added `agi.channel` → `asterisk.channel.name` to the list. Zero behavior change; consumer dashboards asserting on the old names will need to update. ([066cb3c](https://github.com/Harol-Reina/Asterisk.Sdk/commit/066cb3c), [4125c9e](https://github.com/Harol-Reina/Asterisk.Sdk/commit/4125c9e))
- **Cartesia STT/TTS hardening**: linked `CancellationTokenSource` between send/receive loops + 2-second `CloseOutputAsync` timeout. Production path is robust against half-dead WebSocket sockets. ([c0890ac](https://github.com/Harol-Reina/Asterisk.Sdk/commit/c0890ac))

### Tests

- **Zero deferred tests anywhere in repo.** The 2 `[Fact(Skip=…)]` Cartesia abort tests are un-skipped and passing against the new `WebSocketTestServer`. 2 new abort tests added for AssemblyAi STT + Speechmatics STT. ([b02bf18](https://github.com/Harol-Reina/Asterisk.Sdk/commit/b02bf18))
- **3 regression fixes** in `LiveActivitySourceTests` and `SessionActivitySourceTests` — assertions updated to match the new conventions-aligned tag names. ([ed7c2cd](https://github.com/Harol-Reina/Asterisk.Sdk/commit/ed7c2cd))
- **AudioSocketSession flake hardening** — replaced fixed `Task.Delay(100-200)` waits with `TaskCompletionSource` signals on `AudioStreamState` transitions. Avg test duration 210 ms → 28 ms. ([b384bde](https://github.com/Harol-Reina/Asterisk.Sdk/commit/b384bde))
- Unit tests **2,703 → 2,729** (+26: deferred cleanup +4, T1.1 pilot +4, T1.1 expansion +3, Tier 2 +2, pin-test extensions). Integration tests 59 → 65 (+6: 4 Push.Nats baseline + 2 bidirectional). Total across all categories: **2,948 pass / 0 fail / 0 Skip**.

### CI

- **New `pack-check` job** running `dotnet pack -p:TreatWarningsAsErrors=true` on every push/PR. Surfaces PackageValidation baseline drift, PublicAPI drift, missing release notes/icons, license-expression issues at PR time. 24/24 packages pack clean at HEAD. ([7174559](https://github.com/Harol-Reina/Asterisk.Sdk/commit/7174559))

### Documentation

- **ADR-0025** — `push.nats` subscribe + loop prevention rationale. Captures the `source`-header design, `RemotePushEvent`-as-envelope decision, queue-group default (pub/sub), and rejection of JetStream durable consumers (ADR-0011 boundary). ([64f0719](https://github.com/Harol-Reina/Asterisk.Sdk/commit/64f0719))
- **Benchmark re-baseline** — `docs/research/benchmark-analysis.md` §1a confirms hot-path parser/dispatcher numbers are stable vs v1.11.1 after the v1.13 changes. AMI `ParseSingleEvent` 619 ns vs 618 ns baseline; ARI `ParseStasisStart` within noise floor. Const folding validated by exclusion. ([fb078d5](https://github.com/Harol-Reina/Asterisk.Sdk/commit/fb078d5))
- **CONTRIBUTING** — new Release Process section + safe `NUGET_API_KEY` rotation flow (`pbpaste | gh secret set …` pattern) to prevent chat-exposure during future key rotations. Lesson learned from the v1.12.0 403 publish incident. ([25dc7e7](https://github.com/Harol-Reina/Asterisk.Sdk/commit/25dc7e7))
- `docs/plans/active/2026-04-20-v1.13.0-roadmap.md`, `2026-04-20-deferred-tests-cleanup.md`, and `2026-04-20-v1.13-tier2-push-nats-subscribe.md` — v1.13 planning + completed cleanup + Tier 2 execution retrospectives.
- `docs/research/2026-04-19-otel-sip-semantic-conventions.md` — §6 items 1-2 marked shipped. ([2ebacfe](https://github.com/Harol-Reina/Asterisk.Sdk/commit/2ebacfe))

### Notes

- 0 build warnings, 0 trim warnings across all 24 NuGet packages. Native AOT clean.
- 15 ADRs (post-v1.12.0) → 25 ADRs (post-v1.13.0). Only ADR-0025 added in v1.13.
- 30 commits on `main` since `v1.12.0` tag, all CI-verified on ubuntu-latest runners.

## [1.12.0] - 2026-04-19

**Asterisk 23 modernization + voice-agent readiness.** No breaking changes. Package count grows 23 → 24 (one new — `Asterisk.Sdk.Push.Nats`). Three new VoiceAI providers ship as subfolders inside the existing `VoiceAi.Stt` / `VoiceAi.Tts` packages (Deepgram/Azure convention, not new top-level packages).

### Added

- **`Asterisk.Sdk.Push.Nats`** (new MIT package): NATS bridge for `RxPushEventBus`. Subscribes to the local Push bus and republishes every event to a NATS subject derived from the topic hierarchy. Unlocks multi-node deployments (one NATS cluster, N SDK instances, fan-out via subject-tree filtering). `NATS.Client.Core 2.5.10` — AOT-clean, zero reflection. `NatsSubjectTranslator` handles `/` and `.` separators, sanitizes wildcards and control chars. Meter `Asterisk.Sdk.Push.Nats` (`events.published`, `events.failed`). Publish-only in v1.12; subscribe-side planned for v1.12.x.
- **ARI outbound WebSocket listener**: new `IAriOutboundListener` + `AriOutboundListener` under `src/Asterisk.Sdk.Ari/Outbound/`. The SDK acts as the WS server that Asterisk 22.5+ `application=outbound` dials into. Validates upgrade path, Basic-Auth credentials, and app allowlist. Exposes each accepted connection as an `AriOutboundConnection` with an `IObservable<AriEvent>`. Mirrors the RFC-6455 handshake pattern from `WebSocketAudioServer`. `AriOutboundListenerHostedService` in `Asterisk.Sdk.Hosting` for lifecycle management. DI: `services.AddAriOutboundListener(opts => ...)`.
- **`chan_websocket` JSON control protocol on `WebSocketAudioSession`**: Asterisk 22.8 / 23.2+ sends JSON control messages over TEXT frames (MEDIA_START, MEDIA_BUFFERING, MARK_MEDIA, SET_MEDIA_DIRECTION, XON/XOFF, DTMF, HANGUP). Session now exposes `IObservable<ChanWebSocketControlMessage>` via a new `IChanWebSocketSession : IAudioStream` sub-interface, plus send-side methods `SendMarkAsync`, `SendXonAsync`, `SendXoffAsync`, `SendSetMediaDirectionAsync`. Polymorphic JSON via source-gen `ChanWebSocketJsonContext`. Binary audio path unchanged. Writes serialized through a `SemaphoreSlim` so audio and control frames coexist safely on one WebSocket.
- **VoiceAI — Cartesia** (STT + TTS): `src/Asterisk.Sdk.VoiceAi.Stt/Cartesia/` (Ink-Whisper over WebSocket, streaming transcripts) and `src/Asterisk.Sdk.VoiceAi.Tts/Cartesia/` (Sonic-3 at 40-90ms TTFA — the lowest in market as of 2026). Raw WS per ADR-0014. `AddCartesiaStt` + `AddCartesiaTts` DI extensions.
- **VoiceAI — AssemblyAI** (STT): `src/Asterisk.Sdk.VoiceAi.Stt/AssemblyAi/`. Universal Streaming v3 protocol — fills the vacuum left by the discontinued official .NET SDK (April 2025). Parses `Turn` messages, ignores `Begin` / `Termination` lifecycle events. `AddAssemblyAi` DI extension.
- **VoiceAI — Speechmatics** (STT + TTS): `src/Asterisk.Sdk.VoiceAi.Stt/Speechmatics/` (Realtime v2 WebSocket — sub-150ms, 55+ languages) and `src/Asterisk.Sdk.VoiceAi.Tts/Speechmatics/` (REST synthesis — ~27× cheaper than ElevenLabs). Opens the enterprise price-sensitive segment. `AddSpeechmaticsStt` + `AddSpeechmaticsTts` DI extensions.
- **`.github/workflows/publish.yml`**: automated nuget.org release on `v*` tag push. Builds Release, packs all shipping projects, runs `dotnet nuget push ... --skip-duplicate` with `NUGET_API_KEY` secret. Concurrency-guarded per tag. Closes the manual-publish exposure risk documented in v1.11.1. `CLAUDE.md`'s claim about CI-driven releases is now accurate.
- **`Asterisk.Sdk.Push.Nats`** meter enrolled in `AsteriskTelemetry.MeterNames` (14 meters total; `MeterNames_ShouldContainAllPackages` assertion updated accordingly).

### Documentation

- **9 retrospective ADRs — 0016 through 0024** — backfills the load-bearing decisions identified in the v1.11.1 product alignment audit §4. ADR-0016 VoiceAi `ProviderName` virtual override (92× speedup); ADR-0017 AudioSocket codec negotiation; ADR-0018 Sessions soft-TTL reconciliation (not native Redis/Postgres TTL); ADR-0019 Push bus `TraceContext` ambient capture at publish time; ADR-0020 Webhook delivery retry-only without durable DLQ; ADR-0021 AMI heartbeat strategy (30 s / 10 s, on by default); ADR-0022 Activity `CancelAsync()` as first-class alongside `CancellationToken`; ADR-0023 PublicAPI tracker adoption across all 24 packages; ADR-0024 `BannedSymbols.txt` as build-time AOT policy. Catalog grows 15 → 24.
- **`docs/research/2026-04-19-v1.12.0-product-opportunities.md`** — three-angle investigation (internal codebase + deferred work + external market) that reframed v1.12 from housekeeping to strategic release. Convergence on `chan_websocket` across all three angles was the strongest signal.
- **`docs/plans/active/2026-04-19-v1.12.0-scope.md`** — four-tier execution plan with acceptance criteria.
- **`docs/research/2026-04-19-otel-sip-semantic-conventions.md`** — draft OpenTelemetry semantic conventions for SIP / Asterisk telephony. Proposes attribute names (`sip.call_id`, `sip.response_code`, `asterisk.channel.id`, `call.direction`, `call.state`, `voiceai.provider`, etc.) grounded in the 9 ActivitySources + 14 Meters the SDK already ships. Addresses the unresolved `open-telemetry/opentelemetry-specification#2517`. Code-side alignment (emit the proposed attribute names) is deferred to v1.13 after field validation.

### Scope clarifications (from v1.11.1 planning)

Two items originally scoped for v1.12.0 were found to be **already shipped pre-v1.12** during Week 1 kickoff and removed from scope:

- ARI exception context mapping (`AriNotFoundException` / `AriConflictException` with resource name + id) — shipped in v1.6.0 Sprint 1 (task B1). `AriHttpExtensions.EnsureAriSuccessAsync(resource, id)` + `AriResourceErrorContextTests` already present.
- New AMI events for Asterisk 22/23 (`ChannelTalkingStartEvent`, `ChannelTalkingStopEvent`, `BridgeVideoSourceUpdateEvent`, `ApplicationRegisteredEvent`, `ApplicationUnregisteredEvent`, `QueueMemberEvent.Logintime`) — all shipped in earlier cycles (`PublicAPI.Shipped.txt` lines 1843-2006).

### Notes

- 0 build warnings, 0 trim warnings across all 24 NuGet packages. Native AOT clean.
- Unit tests 2,637 → 2,703 (+66: +20 chan_websocket, +19 ARI outbound, +6 Cartesia, +4 AssemblyAi, +7 Speechmatics, +10 NATS). Two Cartesia-provider abort-path tests `[Fact(Skip=)]` due to HttpListener fake-server hang — tracked for v1.12.1; production path against real Cartesia endpoint not observed to hang.
- 15 ADRs → 24 ADRs.
- First release that will flow through `.github/workflows/publish.yml` rather than manual `dotnet nuget push`.

## [1.11.1] - 2026-04-19

### Performance

- **AMI event parser** — Fast-path length check on `Output` header accumulation in `AmiProtocolReader`. Restores ~35 ns of the v1.0 → v1.11 regression in `ParseSingleEvent`; `key.Length == 6` short-circuit lets 99%+ of non-`Output` keys skip the `Equals("Output", OrdinalIgnoreCase)` compare. Throughput 1.53M → 1.62M events/sec single-thread (AMD Ryzen 9 9900X, .NET 10.0.6). 633 AMI unit tests unchanged. ([41fff67](https://github.com/Harol-Reina/Asterisk.Sdk/commit/41fff67))

### Documentation

- **ADR-0013** — `ISessionHandler` as the VoiceAi dispatch seam. Captures why turn-based (`VoiceAiPipeline`) and streaming (`OpenAiRealtimeBridge`) both implement a single-method interface and why consumers swap by DI registration alone.
- **ADR-0014** — Raw HTTP / `ClientWebSocket` for VoiceAi providers. Captures why every STT + TTS provider is hand-rolled against the vendor's public API instead of depending on official vendor SDKs (AOT incompatibility).
- **ADR-0015** — AMI string interning pool (FNV-1a, 2048 buckets). Captures why the 344-LOC pool in `AmiStringPool` is load-bearing at 100K+ events/s workloads and why alternatives (`ConcurrentDictionary`, `FrozenDictionary`, `string.Intern`) are inadequate for UTF-8-span lookup.
- **Product alignment audit** — [docs/research/2026-04-19-product-alignment-audit.md](docs/research/2026-04-19-product-alignment-audit.md) reconciles the 12 accepted ADRs, 4 archived plans, and 6 archived specs against the v1.11.0 product state. Confirms `api-completeness-plan.md` is legitimately closed: 148/152 AMI (97%) + 94/98 ARI (96%) reflect an intentional scope decision, not abandoned work. Documents 12 further load-bearing decisions as ADR candidates for future releases.

### Notes

- No API changes. No breaking changes. 0-warning build preserved across all 23 NuGet packages.
- 12 ADRs → 15 ADRs in `docs/decisions/`.

## [1.11.0] - 2026-04-18

### Added

- **`Asterisk.Sdk.OpenTelemetry`** (new MIT package): batteries-included OpenTelemetry wiring. `services.AddAsteriskOpenTelemetry(b => b.WithAllSources().WithPrometheusExporter().WithOtlpExporter(...))` enrolls every `AsteriskTelemetry.ActivitySourceNames` + `MeterNames` and attaches Console / OTLP / Prometheus exporters. `ConfigureTracing` / `ConfigureMetrics` escape hatches give direct access to the underlying `TracerProviderBuilder` / `MeterProviderBuilder` for samplers, views, and custom processors. Uses OpenTelemetry 1.15.2 (avoids 1.10.x vulnerability).
- **`Asterisk.Sdk.Push.Webhooks`** (new MIT package): outbound HTTP webhook delivery consuming the Push bus. `services.AddAsteriskPush().AddAsteriskPushWebhooks(opts => ...)` registers `IWebhookSubscriptionStore` (in-memory default), `IWebhookSigner` (HMAC-SHA256 default), `IWebhookPayloadSerializer` (UTF-8 JSON envelope, AOT-safe), and a `WebhookDeliveryService` `BackgroundService`. Per-delivery HMAC-SHA256 signature in `X-Signature` header, exponential retry capped at `MaxDelay`, trace-context propagation via `traceparent`, per-subscription `MaxRetries`/`Headers` overrides, dead-letter metrics. Meter `Asterisk.Sdk.Push.Webhooks` (enrolled in `AsteriskTelemetry.MeterNames`): counters `deliveries.succeeded`, `deliveries.failed`, `deliveries.retried`, `deliveries.dead_letter`.
- **Contact-center activities** (in `Asterisk.Sdk.Activities`): four new supervisor/transfer primitives.
  - `AttendedTransferActivity` — wraps AMI `Atxfer` via a new `AmiActivityBase` (takes `IAmiConnection` instead of `IAgiChannel`); required when the supervisor operates outside a live AGI context.
  - `ChanSpyActivity` — AGI `ChanSpy` application with `ChanSpyMode` enum (`Both`, `SpyOnly`, `WhisperOnly`, `Coach`) plus free-form `Options` string for the full flag set.
  - `BargeActivity` — AGI `ChanSpy` with the `B` (barge) flag; supervisor joins as audible third party.
  - `SnoopActivity` — ARI snoop channel creation via `IAriClient.Channels.SnoopAsync`; exposes the resulting snoop channel via `SnoopChannel` property.
- **`Asterisk.Sdk.Sessions.Redis`** (new MIT package): `RedisSessionStore : SessionStoreBase` promoted from the prior spike. Fluent `UseRedis(...)` extension with three overloads — `Action<RedisSessionStoreOptions>`, pre-built `IConnectionMultiplexer`, and raw connection string. Data layout: one JSON snapshot per session, secondary linked-id index, active set (cursor-scanned), completed sorted-set with TTL-driven eviction. Pipelined I/O via `CreateBatch()` + `Task.WhenAll(...).WaitAsync(ct)`. Cancellation honored at entry and around all batch awaits. AOT-safe (source-gen `SessionJsonContext`). Integration tests use Testcontainers (`redis:7-alpine`, no env-var dependency).
- **`Asterisk.Sdk.Sessions.Postgres`** (new MIT package): `PostgresSessionStore : SessionStoreBase` using Npgsql 10 + Dapper + JSONB. Fluent `UsePostgres(...)` extension with the same three overloads as Redis. UPSERT via `INSERT ... ON CONFLICT (session_id) DO UPDATE`. `SaveBatchAsync` in a transaction with rollback. Partial index `ix_asterisk_sessions_active` backs `GetActiveAsync`. Identifier validation (`TableName`, `SchemaName`) at resolve time against `^[A-Za-z_][A-Za-z0-9_]*$` via `AddOptions<T>().Validate`. Migration SQL (`001_create_sessions_table.sql`) ships in the `.nupkg` at `contentFiles/any/any/Migrations/`.
- **`Asterisk.Sdk.Sessions.ISessionStore`** interface: additive companion to `SessionStoreBase` — enables NSubstitute mocking in tests and supports factory-based DI registration. `SessionStoreBase` now declares `: ISessionStore`; zero breaking changes for existing consumers.
- **`Asterisk.Sdk.Sessions.Extensions.ISessionsBuilder`** fluent-builder interface: entry point for backend-specific registration (`UseInMemory`, `UseRedis`, `UsePostgres`). Exposed by two new overloads in `Asterisk.Sdk.Hosting`: `AddAsteriskSessionsBuilder(...)` and `AddAsteriskSessionsMultiServerBuilder(...)`. The existing `AddAsteriskSessions` / `AddAsteriskSessionsMultiServer` methods still return `IServiceCollection` — consumers opt into the builder at their own pace.
- **`docs/guides/session-store-backends.md`**: decision guide, registration patterns, data layout, identifier-safety notes, benchmark reference.
- **README:** CI + AOT Trim workflow badges, NuGet download badge, Native AOT badge; `## Documentation` table of contents linking guides/benchmarks/technical+commercial READMEs/CHANGELOG/CONTRIBUTING/SECURITY; **Session Store Backends** subsection in the Packages table.
- **README Quick Start:** 10-line "First contact" preamble showing a minimal `AddAsterisk` snippet and a pointer to `Examples/BasicAmiExample/`.
- **`.github/dependabot.yml`:** daily NuGet updates (grouped: Microsoft.Extensions, test stack, analyzers) + weekly github-actions updates.
- **`.github/workflows/codeql.yml`:** CodeQL C# analysis on push + PR + weekly Sunday cron with `security-extended,security-and-quality` query suites.
- **`tools/install-hooks.sh`:** one-time installer for a local `pre-commit` hook that runs `claudelint` when `CLAUDE.md` or `.claude/` files are staged.

### Changed

- **`Asterisk.Sdk.Sessions`:** `CallSessionSnapshot` + `SessionJsonContext` hoisted from the Redis spike into `src/Asterisk.Sdk.Sessions/Serialization/` as `internal` — shared round-trip between Redis and Postgres backends. `InternalsVisibleTo` grants added for `Asterisk.Sdk.Sessions.Redis`, `Asterisk.Sdk.Sessions.Postgres`, and the matching test projects.

### Removed

- **`Tests/Asterisk.Sdk.Redis.Spike`**: retired after migration to production package `Asterisk.Sdk.Sessions.Redis`. Spike tests moved to `Tests/Asterisk.Sdk.Sessions.Redis.Tests/` (integration-tagged) and `Tests/Asterisk.Sdk.Sessions.Tests/SnapshotSerializationTests.cs` (unit). Latency smoke-test preserved with `[Trait("Category", "Benchmark")]` so CI integration filters can exclude it.
- **`Tests/Asterisk.Sdk.Redis.Spike.Aot`**: orphaned AOT smoke-check for the retired spike. Production `Asterisk.Sdk.Sessions.Redis` + `Asterisk.Sdk.Sessions.Postgres` are covered by the repo-wide AOT Trim workflow (`<IsAotCompatible>true</IsAotCompatible>` inherited from `Directory.Build.props`).

### Notes

- No breaking changes. All shipped API surfaces from v1.10.2 remain intact; new features are additive. `AddAsteriskSessions` continues to return `IServiceCollection`; consumers wanting fluent-builder access call `AddAsteriskSessionsBuilder` instead.
- Dapper's runtime IL emit is AOT-safe in .NET 10 under current toolchain; verified by the AOT Trim workflow.

---

## [1.10.2] - 2026-04-18

### Fixed

- **Push:** `RxPushEventBus.PublishAsync` now captures the ambient W3C traceparent from `Activity.Current` into `PushEventMetadata.TraceContext` when the publisher has not already set it. Previously the `ExecutionContext` flow was broken at the bus's internal `Channel` boundary (the dispatch loop runs under a `Task.Run` started at construction time), causing downstream transports — SSE endpoints and `Asterisk.Sdk.Pro.Push` backplanes — to see a null trace context and start receiver spans as new trace roots. The capture is guarded (`TraceContext: null` only) so publishers remain free to override the trace context explicitly.

### Notes

- Source- and binary-compatible with v1.10.1. Transparent behaviour change that only activates when an `Activity` is live at publish time.

---

## [1.10.1] - 2026-04-18

### Added

- **Push:** `PushEventMetadata.TraceContext` — optional `string?` parameter carrying a W3C traceparent (`00-{trace-id}-{span-id}-{flags}`) for cross-boundary distributed tracing. When present, transports crossing process/network boundaries (SSE endpoints in `Asterisk.Sdk.Push.AspNetCore`, backplane relays in `Asterisk.Sdk.Pro.Push`) inject it into the wire envelope so downstream subscribers can continue the publisher's trace. Null default; older consumers safely ignore the unknown field. Establishes the pattern for future cross-boundary propagation (AMI/ARI, tracked in a separate spec).

### Notes

- Fully source- and binary-compatible with v1.10.0. Additive optional parameter on a positional record — existing call sites with 5 args continue to compile and bind unchanged.
- 19 packages on nuget.org. 0 build warnings, 0 trim warnings.

---

## [1.10.0] - 2026-04-17

### Added

- **VoiceAi:** `SpeechRecognizer.ProviderName` and `SpeechSynthesizer.ProviderName` virtual properties — stable, allocation-free identifiers for the underlying STT/TTS provider. Default implementation returns `GetType().Name` (backwards-compatible for out-of-tree subclasses). Overridden with literals in built-in providers: `"Deepgram"`, `"Google"`, `"Whisper"`, `"AzureWhisper"`, `"Azure"`, `"ElevenLabs"`, `"Fake"` (STT + TTS).

### Changed

- **VoiceAi:** `VoiceAiPipeline` hot path now reads `_stt.ProviderName` / `_tts.ProviderName` instead of calling `GetType().Name` on every utterance — removes per-utterance reflection from STT recognition and TTS synthesis activity tags.
- **PublicAPI:** Promoted `PublicAPI.Unshipped.txt` → `PublicAPI.Shipped.txt` for the six VoiceAi packages (`VoiceAi`, `VoiceAi.Stt`, `VoiceAi.Tts`, `VoiceAi.Testing`, `VoiceAi.OpenAiRealtime`, `VoiceAi.AudioSocket`). Consolidates the v1.9.0 telemetry stack (Metrics + HealthCheck + ActivitySource) along with the new `ProviderName` virtual property.

### Fixed

- **Tests:** `AsteriskTelemetryTests.ActivitySourceNames_ShouldContainAllPackages` / `MeterNames_ShouldContainAllPackages` — updated stale counts (6→9 and 7→12) to reflect the VoiceAi telemetry registrations added in v1.9.0.

### Notes

- Fully source- and binary-compatible with v1.9.0. Additive public API only.
- 19 packages on nuget.org. 0 build warnings, 0 trim warnings.

---

## [1.9.0] - 2026-04-17

### Added

- **VoiceAi telemetry — full stack in 5 packages:**
  - `VoiceAiMetrics`, `SpeechRecognitionMetrics`, `SpeechSynthesisMetrics`, `AudioSocketMetrics`, `OpenAiRealtimeMetrics` — counters, histograms, gauges per package (sessions started/completed/failed, transcription/synthesis latency, synthesis characters, session duration, bytes/frames).
  - `VoiceAiActivitySource`, `AudioSocketActivitySource`, `OpenAiRealtimeActivitySource` — distributed tracing for pipeline/session/recognition/synthesis spans.
  - Health checks: `VoiceAiHealthCheck`, `SttHealthCheck`, `TtsHealthCheck`, `AudioSocketHealthCheck`, `OpenAiRealtimeHealthCheck`.
- **Hosting:** `AsteriskTelemetry.ActivitySourceNames` count 6→9 and `MeterNames` count 7→12 to include VoiceAi/AudioSocket/OpenAiRealtime.

### Fixed

- **VoiceAi OpenAiRealtime:** Guard `SessionsCompleted` counter on failure path so the metric is not double-counted when a session throws.
- **VoiceAi AudioSocket:** Wire frame/byte counters inside `AudioSocketSession` for per-session I/O telemetry.
- **Ari:** `AriChannel.Creationtime` changed to `string?` (tolerant reader — some Asterisk versions omit the field).
- **Live:** `LiveMetrics` now uses a per-instance `Meter` with an explicit `<long>` gauge type so multiple hosts in the same process don't collide.
- **Packaging:** `CompatibilitySuppressions.xml` added in `Sdk` and `Ari` to accept accepted ABI shifts against the 1.5.3 baseline.

### Notes

- 19 packages on nuget.org. 0 build warnings, 0 trim warnings.
- Three Asterisk PBX integration tests explicitly skipped pending docker infra: Session `Local/s`, Session `Local/101`, LiveMetrics per-instance meter.

---

## [1.8.0] - 2026-04-13

### Added

- **NEW PACKAGE — `Asterisk.Sdk.Push.AspNetCore` (MIT):** SSE endpoint extraction from downstream consumers. `AddAsteriskPushAspNetCore()` DI registration and `IEndpointRouteBuilder.MapPushEndpoints(prefix = "/api/v1/push")` extension wire up Server-Sent Events delivery on top of `IPushEventBus`. Closes the v1.7+ deferred extraction.
- **Push:** Hierarchical topic routing primitives in the `Asterisk.Sdk.Push.Topics` namespace.
  - `TopicName` value object (segmented topic identifiers).
  - `TopicPattern` with single-segment (`*`) and multi-segment (`**`) wildcards plus `{self}` placeholder resolution against the current subscriber.
  - `ITopicRegistry` / `TopicRegistry` for mapping event types to topic templates.
- **Push:** Subscription authorization in the new `Asterisk.Sdk.Push.Authz` namespace — `ISubscriptionAuthorizer`, `AuthorizationResult` (`Allow()` / `Deny(reason)`), `ITopicPermissionMap`, and `AllowAllSubscriptionAuthorizer` default.
- **Push:** New `PushEventMetadata.TopicPath` and `SubscriberContext.RequestedTopicPattern` fields enable topic-aware routing without breaking the existing constructor signature (additional parameters default to `null`).
- **Hosting:** `AddAsteriskPush()` now also registers `ITopicRegistry` (singleton) and `ISubscriptionAuthorizer` (singleton, defaults to `AllowAllSubscriptionAuthorizer`).

### Changed

- **Push:** `DefaultDeliveryFilter.IsDeliverableToSubscriber` now applies optional topic pattern matching when the subscriber declares `RequestedTopicPattern` and the event carries `TopicPath`. Backwards-compatible: subscribers/events without these fields behave as before.

### Notes

- 19 packages on nuget.org (was 18 in v1.7.0; the new package is `Asterisk.Sdk.Push.AspNetCore`).
- 0 build warnings, 0 trim warnings, all unit tests pass.
- `PublicAPI.Shipped.txt` finalized for `Asterisk.Sdk.Push`, `Asterisk.Sdk.Push.AspNetCore`, `Asterisk.Sdk.Hosting`, `Asterisk.Sdk.Sessions`, and `Asterisk.Sdk.Live` (the latter three promote leftover entries from v1.5.x and v1.7.0 that were never moved out of Unshipped at release time).

---

## [1.7.0] - 2026-04-13

### Added

- **Sessions:** `AgentSession` + `AgentSessionTracker` — per-agent state with rolling statistics (calls handled, talk/hold/wrap-up time, idle), driven by `ICallSessionManager.Events`. New `AgentSessionStateChanged` domain event.
- **Sessions:** `QueueSession` + `QueueSessionTracker` — aggregate queue SLA using the previously-defined-but-unused `SessionOptions.SlaThreshold` (20s) and `.QueueMetricsWindow` (30m).
- **Sessions:** `SessionReconciliationService` (`IHostedService` with `PeriodicTimer`) — drives the previously-orphaned `SessionReconciler.TryMarkOrphaned` / `.TryMarkTimedOut` on a `SessionOptions.ReconciliationInterval` (30s) cadence.
- **Sessions:** `SessionOptions.WrapUpDuration` (default 30s).
- **Observability:** `ActivitySource`s for `Asterisk.Sdk.Live`, `Asterisk.Sdk.Sessions`, and `Asterisk.Sdk.Push` (now 6/6 core packages).
- **Observability:** `IHealthCheck` for Live, Sessions, and Push (now 6/6 core packages, auto-registered in `AddAsterisk()` / `AddSessionsCore()` / `AddAsteriskPush()`).
- **Hosting:** `AsteriskTelemetry` static helper exposes `ActivitySourceNames[]` (6) and `MeterNames[]` (7) — discoverability without coupling to OpenTelemetry.

### Fixed

- **Sessions:** `CallSessionManager.PersistAsync` now uses the stored shutdown token instead of `CancellationToken.None`, enabling graceful shutdown.

---

## [1.6.0] - 2026-04-13

### Added

- **NEW PACKAGE — `Asterisk.Sdk.Push` (MIT):** Domain-layer push event bus with `IPushEventBus` (Rx-based default), `PushEvent` base record + `PushEventMetadata`, `IEventDeliveryFilter` / `DefaultDeliveryFilter`, `ISubscriptionRegistry` / `InMemorySubscriptionRegistry`, `PushMetrics`, and `BackpressureStrategy` (`DropOldest`/`DropNewest`/`Block`).

### Fixed

- **ARI:** Tightened exception scopes during event enrichment so a single bad event no longer kills the stream.
- **Config:** `#include` directives now resolve relative to the current file's directory.
- **AMI:** Restored `EventsDropped` counter regression coverage.

---

## [1.5.3] - 2026-03-30

### Fixed

- **Hosting:** Added `AriAudioHostedService` to start/stop ARI audio servers (`AudioSocketServer`, `WebSocketAudioServer`) automatically with the application host — without this, `ExternalMedia` channels could not connect because TCP listeners were never opened

---

## [1.5.2] - 2026-03-30

### Fixed

- **Hosting:** Registered `AgiHostedService` in DI so the FastAGI server starts automatically with the application host
- **Hosting:** Added `AriConnectionHostedService` to connect/disconnect the ARI WebSocket client automatically with the application host

---

## [1.5.1] - 2026-03-26

### Fixed

- **VoiceAi:** Fixed `CancellationTokenSource` leak in `VoiceAiPipeline.DisposeAsync` — `_ttsCts` was not disposed
- **VoiceAi:** Fixed `ContinueWith` in `VoiceAiSessionBroker` to use `TaskScheduler.Default`, preventing synchronization context capture

### Improved

- **Build:** Added SourceLink, deterministic builds, and PackageValidation baseline (v1.5.0)
- **Build:** Added code quality analyzers — Meziantou, IDisposableAnalyzers, Threading Analyzers (Layers 1-3)
- **Build:** Populated `PublicAPI.Shipped.txt` for all 17 packages (API surface tracking)
- **Tests:** 1,430 unit tests (+364 since v1.5.0) — all assemblies at 82%+ coverage
  - Ari: 306 → 357 (AudioSocketServer, WebSocketAudioSession, event parse, metrics)
  - Ami: 82%, Agi: 86%, Live: 81.6%, Ari: ~83%

### Changed

- **Repo:** PbxAdmin moved to standalone repository (`Asterisk.Sdk.PbxAdmin`)

---

## [1.5.0] - 2026-03-24

### Added

- **AMI:** `Context` and `Priority` fields on `ListDialplanEvent`; `Context` filter on `ShowDialplanAction`
- **AMI:** Accumulate multi-line `Output:` headers for Command responses
- **CI:** GitHub Actions pipeline with unit tests, AOT verification, and functional tests (Testcontainers)

### Fixed

- **AMI:** Fix `QueueManager.RemoveQueue` to properly clean up secondary indices

### Changed

- **Repo:** PbxAdmin moved to standalone repository ([Asterisk.Sdk.PbxAdmin](https://github.com/Harol-Reina/Asterisk.Sdk.PbxAdmin))

---

## [1.4.0] - 2026-03-22

### Added

- **AMI:** 11 new actions — `VoicemailRefresh`, `VoicemailUserStatus`, `PresenceState`, `PresenceStateList`, `QueueReload`, `QueueRule`, `DBGetTree`, `CoreShowChannelMap`, `Flash`, `DialplanExtensionAdd`, `DialplanExtensionRemove`
- **AMI:** 3 new response events — `QueueRuleEvent`, `QueueRuleListCompleteEvent`, `DbGetTreeResponseEvent`
- **AudioSocket:** 8 new high sample rate frame types for Asterisk 23 — `AudioSlin12` (12 kHz) through `AudioSlin192` (192 kHz)
- **AudioSocket:** `GetSampleRate()` and `IsAudio()` extension methods on `AudioSocketFrameType`
- **AudioSocket:** `WriteAudioAsync` overload accepting explicit `AudioSocketFrameType` for high-rate audio

### Compatibility

- AMI Action coverage: 150/152 (99%) of Asterisk 22-23 actions (remaining 2: DAHDI-specific)
- ARI endpoint coverage: 92/98 (94%)
- AudioSocket: full Asterisk 18-23 protocol support including high sample rate types

---

## [1.3.1] - 2026-03-22

### Added

- **ARI:** `SetEventFilterAsync` on Applications resource — filter WebSocket events per app (reduces traffic at scale)
- **ARI:** `GetStoredFileAsync` on Recordings resource — binary download of stored recordings (enables CallAnalytics transcription)
- **ARI:** `GenerateUserEventAsync` on AriClient — emit custom user events between Stasis apps

---

## [1.3.0] - 2026-03-22

### Added

- **ARI:** New `AriAsteriskResource` — 16 endpoints for system info, modules, logging, config, and global variables
- **ARI:** New `AriMailboxesResource` — 4 endpoints for mailbox state management (list, get, update, delete)
- **ARI:** 8 new `AriChannelsResource` endpoints — `Move`, `Dial`, `GetRtpStatistics`, `Silence/StopSilence`, `StartMoh/StopMoh`, `StopRing`
- **ARI:** 5 new `AriBridgesResource` endpoints — `CreateWithId`, `SetVideoSource`, `ClearVideoSource`, `StartMoh`, `StopMoh`
- **ARI:** 8 new `AriRecordingsResource` endpoints — `ListStored`, `GetStored`, `CopyStored`, `Cancel`, `Pause/Unpause`, `Mute/Unmute`
- **ARI:** 2 new `AriApplicationsResource` endpoints — `Subscribe`, `Unsubscribe` event sources
- **ARI:** 3 new `AriEndpointsResource` endpoints — `ListByTech`, `SendMessage`, `SendMessageToEndpoint`
- **ARI:** 11 new models — `AriAsteriskInfo`, `AriBuildInfo`, `AriSystemInfo`, `AriConfigInfo`, `AriStatusInfo`, `AriAsteriskPing`, `AriLogChannel`, `AriModule`, `AriMailbox`, `AriConfigTuple`, `AriRtpStats`
- **ARI:** `IAriClient` extended with `Asterisk` and `Mailboxes` resource properties

### Compatibility

- ARI endpoint coverage: ~94/98 (96%) of Asterisk 22-23 endpoints
- AMI Action coverage: 139/152 (91%)

---

## [1.2.0] - 2026-03-22

### Added

- **AMI:** 11 PJSIP management actions — `PJSIPShowAors`, `PJSIPShowAuths`, `PJSIPShowRegistrationsInbound`, `PJSIPShowRegistrationsOutbound`, `PJSIPShowResourceLists`, `PJSIPShowSubscriptionsInbound`, `PJSIPShowSubscriptionsOutbound`, `PJSIPRegister`, `PJSIPUnregister`, `PJSIPQualify`, `PJSIPHangup`
- **AMI:** 7 bridge management actions — `BridgeDestroy`, `BridgeInfo`, `BridgeKick`, `BridgeList`, `BridgeTechnologyList`, `BridgeTechnologySuspend`, `BridgeTechnologyUnsuspend`
- **AMI:** 2 transfer actions — `BlindTransfer`, `CancelAtxfer`
- **AMI:** 6 new response events for event-generating actions (`BridgeListItem`, `BridgeListComplete`, `BridgeTechnologyListItem`, `BridgeTechnologyListComplete`, `ResourceListDetailComplete`, `SubscriptionsComplete`)

### Compatibility

- AMI Actions coverage: 139/152 (91%) of Asterisk 22-23 actions

---

## [1.1.0] - 2026-03-22

### Added

- **AMI:** 3 new actions for Asterisk 20+ compatibility (`PJSIPShowContacts`, `PJSIPShowEndpoint`, `PJSIPShowRegistrationInboundContactStatuses`)
- **ARI:** `AriBridgesResource` — bridge management operations (create, addChannel, removeChannel, startMoh, stopMoh, record)
- **ARI:** Extended `IAriClient` with `Bridges` property for ARI bridge operations

### Fixed

- **AMI:** Complete queue event fields (`QueueEntryEvent`, `QueueMemberStatusEvent`, `QueueMemberPauseEvent`, `PeerEntryEvent`) for Asterisk 18-23 compatibility
- **Live:** Use `Location` field for queue member interface on Asterisk 22+ (falls back to `StateInterface`)

### Compatibility

- Tested with Asterisk 18, 20, 22, and 23

---

## [1.0.0] - 2026-03-21

First stable release of Asterisk.Sdk — a .NET 10 Native AOT SDK for Asterisk PBX.

**API Stability:** API is frozen as of v1.0.0. Semantic versioning applies — no breaking changes in 1.x releases.

### Core SDK (9 packages)

- **Asterisk.Sdk** — Core interfaces, base types, enums, and attributes shared across all layers
- **Asterisk.Sdk.Ami** — AMI client with 115 actions, 249 events, and 17 typed responses. Zero-copy TCP parsing via `System.IO.Pipelines`. MD5 challenge-response authentication. Auto-reconnection with exponential backoff. Configurable heartbeat monitoring. Source-generated action serialization and event deserialization (zero reflection).
- **Asterisk.Sdk.Agi** — FastAGI server with 54 commands and pluggable script mapping strategies (`SimpleMappingStrategy`). Per-connection timeout, status 511 hangup detection, and `AgiMetrics` instrumentation.
- **Asterisk.Sdk.Ari** — ARI REST + WebSocket client with 8 resource APIs (channels, bridges, playbacks, recordings, endpoints, applications, sounds, device states). Domain exceptions for HTTP error mapping. WebSocket reconnect with exponential backoff. Source-generated JSON serialization via `AriJsonContext`.
- **Asterisk.Sdk.Live** — Real-time in-memory tracking of channels, queues, agents, and conference rooms from AMI events. Secondary indices for O(1) lookups by name. Observable gauges and event counters via `System.Diagnostics.Metrics`.
- **Asterisk.Sdk.Activities** — High-level telephony operations (Dial, Hold, Transfer, Park, Bridge, Conference) modeled as async state machines with `IObservable<ActivityStatus>` tracking. Real cancellation support, re-entrance guards, and channel variable capture.
- **Asterisk.Sdk.Sessions** — Session Engine: AMI event correlation into unified call sessions using LinkedId grouping. State-machine lifecycle (Ringing, Answered, OnHold, Transferred, Completed), domain events (`SessionStarted`, `SessionEnded`, `SessionStateChanged`), automatic orphan detection via `SessionReconciler`, and pluggable extension points (`ISessionEnricher`, `ISessionPolicy`, `ISessionEventHandler`).
- **Asterisk.Sdk.Config** — Asterisk `.conf` file parser including `extensions.conf` dialplan support. Quote-aware comment stripping.
- **Asterisk.Sdk.Hosting** — DI registration via `AddAsterisk()` with AOT-safe options validation. `IHostedService` lifecycle for AMI and Live API. `IHealthCheck` for AMI connection state. Meta-package referencing all core sub-packages.

### Voice AI (7 packages)

- **Asterisk.Sdk.Audio** — Pure C# polyphase FIR resampler with 12 pre-computed rate pairs (8 kHz ↔ 16 kHz ↔ 24 kHz ↔ 48 kHz). Zero-alloc output buffers, PCM16 processing, RMS energy measurement, and voice activity detection. Zero external dependencies.
- **Asterisk.Sdk.VoiceAi** — Voice AI orchestration pipeline (`VoiceAiPipeline`). Dual-loop design: audio monitor + pipeline. VAD → STT → `IConversationHandler` → TTS with barge-in detection. `ISessionHandler` interchange point makes `VoiceAiPipeline` and `OpenAiRealtimeBridge` drop-in replacements for each other.
- **Asterisk.Sdk.VoiceAi.AudioSocket** — AudioSocket server and client using `System.IO.Pipelines` for zero-copy bidirectional PCM streaming. `AudioSocketSession` handles bidirectional audio with backpressure. `AudioSocketClient` enables local testing without a live Asterisk instance.
- **Asterisk.Sdk.VoiceAi.Stt** — Speech-to-text providers: Deepgram (WebSocket streaming, real-time), OpenAI Whisper (batch REST), Azure Whisper, and Google Speech (REST). DI registration via `AddDeepgramSpeechRecognizer()`, `AddWhisperSpeechRecognizer()`, `AddAzureWhisperSpeechRecognizer()`, `AddGoogleSpeechRecognizer()`.
- **Asterisk.Sdk.VoiceAi.Tts** — Text-to-speech providers: ElevenLabs (WebSocket streaming, ultra-low-latency) and Azure TTS (REST). DI registration via `AddElevenLabsSpeechSynthesizer()`, `AddAzureTtsSpeechSynthesizer()`.
- **Asterisk.Sdk.VoiceAi.OpenAiRealtime** — Bridges Asterisk AudioSocket directly to the OpenAI Realtime API, bypassing the STT+LLM+TTS chain entirely. Single persistent WebSocket with bidirectional PCM (resampled 8 kHz ↔ 24 kHz). Server-side VAD, function calling (`IRealtimeFunctionHandler`), and typed observable events (`RealtimeSpeechStartedEvent`, `RealtimeTranscriptEvent`, `RealtimeFunctionCalledEvent`).
- **Asterisk.Sdk.VoiceAi.Testing** — Fake implementations (`FakeSpeechRecognizer`, `FakeSpeechSynthesizer`, `FakeConversationHandler`) for unit testing Voice AI pipelines without real API calls.

### Key Properties

- **.NET 10 Native AOT** — Zero runtime reflection, 0 trim warnings
- **Source generators** — 4 compile-time generators for AOT-safe AMI serialization (`ActionSerializerGenerator`, `EventDeserializerGenerator`, `EventRegistryGenerator`, `ResponseDeserializerGenerator`)
- **System.IO.Pipelines** — Zero-copy TCP parsing with backpressure for AMI, AGI, and AudioSocket transports
- **System.Threading.Channels** — Async event pump with configurable capacity and drop metrics
- **System.Reactive** — Observable state machines in Live, Activities, and Session layers
- **Multi-server support** — `IAmiConnectionFactory` + `AsteriskServerPool` for federated N-server deployments with agent routing
- **Observability** — `System.Diagnostics.Metrics` counters, histograms, and observable gauges in `AmiMetrics` and `LiveMetrics`; `IHealthCheck` integration
- **Reconnection** — Exponential backoff with configurable max attempts for AMI and ARI WebSocket connections
- **Thread safety** — `ConcurrentDictionary` for all entity collections, per-entity `Lock` for atomic property updates, copy-on-write volatile arrays for zero-alloc observer dispatch
- **878 unit tests, 25 integration tests, 15 benchmarks**
- **14 standalone examples** covering every SDK layer, including a full Blazor Server PBX administration panel

### Requirements

- .NET 10.0.100 or later
- Asterisk 13+ (tested through Asterisk 21.x LTS)

## v1.5.0 (2026-03-24)

### AMI
- Add `Context` and `Priority` properties to `ListDialplanEvent`
- Add optional `Context` filter to `ShowDialplanAction`
- Fix: accumulate `Output:` headers for AMI Command responses
- Add `AddDelete(section, key, value)` overload to `UpdateConfigAction`

### Live
- Add `QueueManager.RemoveQueue()` for runtime queue removal
- Fix: show logged-off agents in queue member listing
- Fix: allow file-mode config writes for queue sync

### Notes
- PbxAdmin example has been moved to its own repository: [Asterisk.Sdk.PbxAdmin](https://github.com/Harol-Reina/Asterisk.Sdk.PbxAdmin)
