# Claim registry

The single answer to **"is this claim guarded?"** — one file, rather than a search across the test
projects, the AOT canary and the workflows. Required by `claim-guards` and
ADR-0042.

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

**Out**, as period-correct records left verbatim: dated `CHANGELOG.md` entries. Architecture
decision records, specs, plans and research notes are kept locally and are not tracked here.

## Status legend

`OK` guard exists and passes · `GAP` no guard · `PARTIAL` guard covers less than the claim says ·
`WRONG` the published figure is false as of the inventory date · `TODO` guard scheduled by this change

---

## `README.md`

| line | claim | class | guard | status |
|---|---|---|---|---|
| 13 | Native AOT-ready badge | ENFORCING | `tools/AotCanary/` + `tools/verify-aot.sh` + `aot-validate.yml` | PARTIAL — canary references 22 of 29 packages |
| 23 | asterisk-java "790+ classes" | ATTRIBUTED | — | GAP — no citation, first-party voice |
| 45 | 148 actions, 269 events, 17 typed responses | ENFORCING | — | GAP — corrected 2026-09-24 under the counting definition settled that day; all four published copies now agree. A guard still needs the `classify-docs-only.sh` carve-out moved first (see *Unresolved* 1) |
| 46 | 54 AGI commands | ENFORCING | — | GAP |
| 54 | four source generators, 0 trim warnings | ENFORCING | AotCanary (trim half) | PARTIAL — generator count unguarded |
| 61 | 29 NuGet packages | ENFORCING | — | GAP |
| 61 | 0 build warnings | ENFORCING | `Directory.Build.props` `TreatWarningsAsErrors` + `Pack Warnings Gate` | OK |
| 61 | 0 trim warnings | ENFORCING | AotCanary | PARTIAL — 22/29 |
| 61 | ~2,924 unit + 154 functional + 65 integration | COHERENCE | — | WRONG — the suite runs **3,295** (measured 2026-08-29); note nothing in-tree *records* that number until §4.1 commits the record |
| 61 | headline version **v2.6.1** | COHERENCE | `StatusBlockCoherenceTests` — against `Directory.Build.props` `<PackageVersion>` | **OK** |
| ~~65~~ | ONNX model 8.3 MB | — | — | **DELETED** — lived in the release bullets, cut by the 2026-09-20 ruling on `README.md` release history |
| ~~67~~ | 94.26% English accuracy, in upstream's voice | — | — | **DELETED** — lived in the release bullets, cut by the 2026-09-20 ruling on `README.md` release history. The figure survives at `:465` (row below) with its citation and hash pin; this was the duplicate. |
| ~~67~~ | ~12 ms CPU inference | — | `TurnDetectionBenchmark` | **DELETED** — measured at 26.18–37.30 ms; deferred, see *Deferrals*. Its line was also removed with the release bullets on 2026-09-20. |
| 65 | 148/152 AMI (97%), 94/98 ARI (96%), 46/46, 27/27, 269 events | ENFORCING | — | GAP — the event figure was corrected 2026-09-24 (was 278) |
| ~~67~~ | ~~**60 ADRs**~~ | — | — | **DELETED** — the figure left `README.md` with the public decision catalog it counted (decision records are kept locally, no longer tracked); the two `StatusBlockCoherenceTests` cases that pinned it were removed with it. |
| 89 | measurement provenance (Ryzen 9 9900X, .NET 10.0.5, BDN v0.14.0, 2026-04-18) | COHERENCE | `PerformanceTableCoherenceTests` — the header provenance test | PARTIAL — matched against the whole file rather than this line, and blind to the AMI row's .NET 10.0.6 MediumRunJob exception (`performance-record.json:12`); the two session-store rows measured apart from it state their own at :110 |
| 93 | AMI parse+dispatch 1.62M events/sec (617.6 ns) | ENFORCING + COHERENCE | `PerformanceTableCoherenceTests` against `performance-record.json`; `perf-regression.yml` `*AmiProtocolReader*` against `baseline.json` | PARTIAL — COHERENCE bound; the regression gate has a baseline since #232 but observes only (`PERF_GATE_ENFORCE: 'false'`) |
| 94 | ARI deserialize Channel 3.54M ops/sec (283 ns) | ENFORCING + COHERENCE | `PerformanceTableCoherenceTests`; `*AriJson*` | PARTIAL — as row 93 |
| 95 | ARI event parse 595K events/sec (1.68 µs), "2.7× faster than v1.0" | ENFORCING + COHERENCE | `PerformanceTableCoherenceTests` (binds the ratio too, `versus_v1_0`); `*AriParseEvent*` | PARTIAL — as row 93 |
| 96 | 163.9M lookups/sec (6.1 ns) | ENFORCING + COHERENCE | `PerformanceTableCoherenceTests`; `*ChannelManager*` | PARTIAL — as row 93 |
| 97 | ~0.21 ns/observer, zero-alloc | ENFORCING + COHERENCE | `PerformanceTableCoherenceTests`; `*ObserverDispatch*` | PARTIAL — as row 93; zero-alloc is not asserted |
| 98 | Redis SaveAsync ~33.3K/sec (p50 30 µs), batch 91,021/sec | COHERENCE | `PerformanceTableCoherenceTests` — the value test | OK — re-measured 2026-09-12; the April figures it replaced (~12.6K/sec, p50 79 µs, batch 65,738/sec) understate today's measurement of the same store code ~2.6× and ~1.4×. Its provenance is row 110 |
| 99 | Postgres SaveAsync ~500/sec (p50 1.97 ms), batch 13,489/sec | COHERENCE | `PerformanceTableCoherenceTests` — the value test | OK — re-measured 2026-09-12 on the `NpgsqlExecutor` store, so no longer a measurement of Dapper; batch was 9,491/sec and its rise is not attributed to the rewrite. Its provenance is row 110 |
| 101 | session-store provenance: re-measured 2026-09-12, .NET 10.0.12, xunit Fact + Stopwatch against local Docker, PostgreSQL 18.4 / Redis 7.4.8, median of five runs; Postgres single-save latency is the WAL flush | COHERENCE | `PerformanceTableCoherenceTests` — the per-row provenance test | PARTIAL — date and runtime asserted against the Performance section; server versions, run count and the WAL-flush attribution stated, not asserted; and the check is opt-in, so deleting `provenance` from the record unbinds this line unnoticed |
| 124 | 9 ActivitySources | ENFORCING | `MarketingClaimsTests.cs:45-50` | OK |
| 125 | 15 Meters | ENFORCING | `MarketingClaimsTests.cs:52-57` | OK |
| 126 | 12 IHealthChecks — 7 core + 5 VoiceAi | ENFORCING | `MarketingClaimsTests.cs:76-97` | PARTIAL — total pinned, the 7/5 split is not |
| 127 | 60 const strings, 14 nested classes, "14+ unit tests" | ENFORCING | `MarketingClaimsTests.cs:59-74` | PARTIAL — the "14+ tests" sub-claim is unpinned |
| 155 | "First contact in 10 lines" | COHERENCE | — | WRONG — the snippet at :158-172 is 15 lines (13 non-blank); the old anchor :166-182 was already off before #322 |
| 461 | Cartesia Sonic-3, no figure | — | — | **DELETED** — the figure moved to `src/Verbara.Sdk.VoiceAi.Tts/README.md`, cited; `40-90 ms` was never Cartesia's number (they publish sub-90 ms) |
| 463 | smart-turn-v3.2-cpu, 94.26% in upstream's voice | ATTRIBUTED | citation + hash pin | **OK** — this line previously carried the figure with no citation at all |

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
| 543 | "all 148 AMI actions" | ENFORCING | — | GAP — corrected 2026-09-24 (was 111). The same row also named a type that does not exist, `AmiAction` in `Verbara.Sdk.Ami.Actions`; the base is `ManagerAction` in `Verbara.Sdk` |
| 544 | "all 269 AMI events" | ENFORCING | — | GAP — corrected 2026-09-24 (was 215). Same type defect: `AmiEvent` in `Verbara.Sdk.Ami.Events` does not exist; the base is `ManagerEvent` in `Verbara.Sdk` |

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
| `Verbara.Sdk.Ami/README.md:7` | 148 actions, 269 events, 17 response types | ENFORCING | — | GAP — corrected 2026-09-24 (was 111/261/17); now agrees with `README.md:45`. This file is the package's `PackageReadmeFile`, published verbatim on nuget.org |
| `Verbara.Sdk.Ari/README.md:7-8` | 8 ARI resources, 46 event types | ENFORCING | — | GAP |
| `Verbara.Sdk.Agi/README.md:8` | 54 AGI commands | ENFORCING | — | GAP |
| `Verbara.Sdk.Live/README.md:9` | 100K+ agents | — | — | GAP — see *Unresolved* below |
| `Verbara.Sdk.Audio/README.md:35` | 12 telephony rate pairs | ENFORCING | — | GAP |
| `Verbara.Sdk.Audio/README.md:39` | zero-alloc Span API throughout | ENFORCING | — | GAP — `MemoryDiagnoser` runs but nothing asserts |
| `Verbara.Sdk.Push/README.md:143` | 0 trim warnings, **naming its own guard** | ENFORCING | AotCanary | OK — the only README that cites its guard. Was `:112` until the push stream change added the SSE drop counter row and the *Tracing* section above it |
| `Verbara.Sdk.Push/README.md:106` | `asterisk.push.sse.events.dropped` counts one per event frame an SSE connection dropped at its bound, each reported in a `.gap` frame | ENFORCING | PARTIAL — `SseStreamBoundTests.Stream_ShouldCountDropsOnAMetricAndWarnOncePerEpisode_WhenTheBoundDropsFrames` asserts that the counters on a `Verbara.Sdk.Push*` meter, other than the bus's own instruments, sum to what the `.gap` frames report, on both Kestrel settings; the instrument's **name** is not asserted |
| `Verbara.Sdk.Push/README.md:116-139` | the trace tree: `push deliver` is a child of the event's `TraceContext`, subscriber spans and the wire `traceparent` (HTTP, NATS) are in the publisher's trace; a missing or malformed context gives a root; the bus builder's activity is not inherited; with no listener nothing is parsed | ENFORCING | OK — `PushDeliveryTracingTests` (`Push.Tests`: `DispatchLoop_ShouldParentDeliveryOnPublisher_WhenActivityIsAmbient`, `…ShouldNotInheritConstructorActivity_WhenBusIsBuiltInsideAnActivity`, `…ShouldStartRootDelivery_WhenNoActivityIsAmbient`, `…WhenTraceContextIsMalformed`, `PublishAsync_ShouldPreserveTraceContext_WhenActivityIsAmbient`, `…ShouldKeepExplicitTraceContext_WhenAnotherActivityIsAmbient`); `Push.Webhooks.IntegrationTests` `PushDeliveryTracingTests.Delivery_ShouldPostInsideThePublishersTrace_WhenPublishedUnderAnActivity` and `…DispatchLoop_ShouldNotInheritFirstRequestActivity_WhenBusIsFirstResolvedInARequest`; `PushDeliveryTracingNatsTests.NatsPublish_ShouldCarryThePublishersTraceOnTheWire_WhenPublishedUnderAnActivity` (`Category=Integration`, Docker lane). The sampler note (`:136-138`) and the no-listener cost (`:139`) are not asserted: the first follows from the parenting and was not measured with the OpenTelemetry SDK; the second is read from `PushActivitySource.StartDelivery` (`HasListeners()` first) |
| `Verbara.Sdk.Push.AspNetCore/README.md:45-50` | the stream's status-code contract: an unparseable topic → `400`, the authorizer asked nothing; every requested topic denied (no topic = `**`) → `403` with the fixed body; at least one allowed → `200 text/event-stream` with the allowed topics only; refusals written before any event-stream byte and before subscribing; a denied topic never widened to `**` (`:52-53`) | ENFORCING | PARTIAL — `SseAdmissionTests.StreamRequest_ShouldBeServedOnlyWhatWasAskedAndAllowed_WhenMeasuredScenarioRuns`, 12 scenarios × `AllowSynchronousIO` false/true (status, the patterns the authorizer was asked, the events received, isolation, content type, no bus subscription on a refusal, the reason not echoed). The `Missing tenantId claim.` row (`:47`): `SseSubscriberIdentityTests.StreamRequest_ShouldBeRefusedBeforeAskingOrSubscribing_WhenOnlyTidCarriesTheTenant` and `SseSubscriberClaimTypesHostTests.StreamRequest_ShouldBeAnsweredMissingTenant_WhenAnUnvalidatedHostEmptiedTheTenantList` (the exact body, nothing asked; both Kestrel settings) |
| `Verbara.Sdk.Push.AspNetCore/README.md:53-55` | one `Warning` per refused request, carrying the tenant, the user, the denied topics percent-encoded and the first reason; none on a `400` or a `200` | ENFORCING | OK — `SseAdmissionLogTests.StreamRequest_ShouldLogOneWarningWithTheReason_WhenEveryTopicIsDenied` (both Kestrel settings) and `…ShouldLogNoRefusal_WhenTheRequestIsNotDenied` |
| `Verbara.Sdk.Push.AspNetCore/README.md:67-70` | `X-Push-Denied-Topics` lists the denied topics as requested, each `Uri.EscapeDataString`-encoded, comma-joined; absent when nothing was denied | ENFORCING | OK — the admission theory's `queue + billing, allow only queue` (`billing.%2A%2A`) and `queue + CR/LF and comma topic, allow only queue` (`bill%0D%0Aing%2Cx.%2A%2A`) rows, and every other `200` row asserting the header absent. The `EventSource` and CORS caveats (`:72-76`) are browser behaviour, not tested here |
| `Verbara.Sdk.Push.AspNetCore/README.md:12,137` | heartbeat every 15 seconds | ENFORCING | GAP — the interval is the internal `SsePushStreamOptions.HeartbeatInterval` default (`TimeSpan.FromSeconds(15)`); every test sets its own interval and none reads the default. That heartbeats keep arriving while a slow client is written to is asserted (`SseFrameTests.Stream_ShouldDeliverWholeFramesInOrderAndKeepHeartbeating_WhenReaderIsSlow`) |
| `Verbara.Sdk.Push.AspNetCore/README.md:13,140-141` | the per-connection bound is 1 MiB by default, counted in the frames' UTF-8 bytes | ENFORCING | OK — `SseFrameQueueTests.Options_ShouldDefaultTheBoundTo1MiB` and `…ShouldBeTheDefaults_WhenOnlyAddVerbaraPushIsRegistered` (1,048,576); `SseStreamBoundTests.Stream_ShouldSendOneGapWithTheDroppedEventCount_WhenStoppedReaderResumesAfterTheBound` asserts the event bytes after the last `.gap` are at most 1,048,576 and more than 1 MiB − 2 frames, and the queue-depth hook never above the bound; `…ShouldUseTheHostsBound_WhenThePublicOptionIsSet` (256 KiB) |
| `Verbara.Sdk.Push.AspNetCore/README.md:140-163` | drop oldest; one `.gap` before the next event with `{"dropped":N}`, the count of dropped event frames; heartbeats outside the bound; a frame larger than the bound delivered alone; `.gap` reserved (`%2Egap`); one `Warning` per run of drops | ENFORCING | OK — `SseStreamBoundTests` (`…SendOneGapWithTheDroppedEventCount…` with 16 KB and 0.5 KB events, `…KeepHeartbeatsOutOfTheBound_WhenTheQueueIsFull`, `…DeliverAFrameLargerThanTheBoundWhole_WhenItIsAlone`, `…NeverWriteAnEventAsTheGapMarker_WhenItsTypeIsTheReservedName`, `…CountDropsOnAMetricAndWarnOncePerEpisode…`) and `SseFrameQueueTests` (16 cases, the gap frame's exact bytes included). The `3413` at `:147` is an example value, taken from a measured episode; nothing pins it |
| `Verbara.Sdk.Push.AspNetCore/README.md:57-65,87-88` | an event is matched, and named on the wire, by its `TopicPath`, or by its `EventType` when the topic path is null or empty; `{self}` resolves in the type; a type that is not a topic is never matched by its text and reaches only `**` streams; a non-empty `TopicPath` that does not parse reaches no stream, never falls back to the type, and is logged once per event (not per stream) with the type and path percent-encoded; isolation unchanged | ENFORCING | OK — `SseTopicLessEventTests` (10 methods: partial admission, narrow streams, `{self}` own/other user, `.gap` and `billing..x` types on `**` and narrow streams, empty topic path named by type, T1/u1, T1/u2, T2 isolation on one host) and `SseUnparseableTopicPathTests` (5 methods: no delivery on `**` streams, one `Warning` for three evaluating streams, CR/LF percent-encoded, no fall-back, nothing logged when no stream evaluated it), each × `AllowSynchronousIO` false/true; `SseTopicResolverTests` (8 methods, 11 cases) on the pure resolver. The NATS-bridge sentence (`:64-65`) states another package's unchanged code, not tested here |
| `Verbara.Sdk.Push.AspNetCore/README.md:158-159` | `.gap` is not a valid topic, so a topic-less event of that type reaches only `**` streams | ENFORCING | OK — `SseTopicLessEventTests.Stream_ShouldDeliverATopicLessEventWhoseTypeIsTheGapName_WhenTheStreamIsCatchAll` and `…ShouldNotDeliver…WhenTheStreamIsNotCatchAll` (both Kestrel settings) |
| `Verbara.Sdk.Push.AspNetCore/README.md:93-131` | the four claim-type lists and their defaults (`tenantId`; `sub`, `ClaimTypes.NameIdentifier`; `ClaimTypes.Role`, `role`, `roles`; `permission`); first listed type with a value wins for tenant and user; roles and permissions are unions, each identity's `RoleClaimType` always read; types ignore case, values whole and ordinal, empty values ignored; `JwtBearer` with `MapInboundClaims` true and false finds user and roles; `tid` not a default; assignment replaces, configuration binding appends; start-up validation naming the list; an `AddVerbaraPush()`-only host answers `400` on an empty tenant list | ENFORCING | OK — `SseSubscriberIdentityFromTests` (precedence, empty-value skip, `NameIdentifier`, union + identity `RoleClaimType`, empty role list, case-insensitive types, whole ordinal values, several identities, null/blank lists, `Options_ShouldHaveTheDocumentedClaimTypeDefaults_WhenNothingIsConfigured`, `Options_ShouldGetTheConfiguredEntriesAppendedToTheDefaults_WhenBoundFromConfiguration`, validation ×7, empty role/permission lists accepted); `SseSubscriberIdentityTests` (real `JwtBearer` tokens, `MapInboundClaims` true and false; each default role and permission type; `tid` alone → `400`; types differing only in case); `SseSubscriberClaimTypesHostTests` (`org` tenant, `scp` permission, empty lists, unvalidated empty tenant list → `400` with no logged exception); every host case × `AllowSynchronousIO` false/true |
| `Verbara.Sdk.Push.Webhooks/README.md:37-48` | accepted values: `MaxRetries` ≥ 0; `InitialDelay` 0 to `int.MaxValue` ms (about 24.8 days); `MaxDelay` from `InitialDelay` to `int.MaxValue` ms; `TimeoutPerAttempt` > 0 and at most `int.MaxValue` ms, or infinite; the defaults 5 / 1 s / 60 s / 10 s; an `OptionsValidationException` naming the option at start, an `ArgumentOutOfRangeException` from both constructors | ENFORCING | OK — `WebhookDeliveryOptionsRuleTests`: `Validation_ShouldFailNamingTheOption_WhenSettingIsUnusable` (11 unusable settings, `int.MaxValue + 1` ms, 60 days and a 30-day `TimeoutPerAttempt` included), `Validation_ShouldAccept_WhenSettingIsUsable` (the defaults and the boundaries, exactly `int.MaxValue` ms for a delay and for `TimeoutPerAttempt` included), `HostStart_ShouldFailNamingMaxDelay_WhenMaxDelayIsBelowInitialDelay`, `PublicConstructor_…`/`InternalConstructor_ShouldThrowNamingTheOption_WhenSettingIsUnusable`. The 24.8 days is the conversion; the default values themselves are read from `WebhookDeliveryOptions`, not asserted one by one |
| `Verbara.Sdk.Push.Webhooks/README.md:50-53,88-97` | a delay made unusable after start dead-letters that delivery with exactly one `Error` (EventId 8), later events delivered; a dispatch failure logs one `Error` (EventId 9); a failure outside the attempts dead-letters with EventId 10; no faulted task left behind | ENFORCING | PARTIAL — `WebhookDeliveryGuardTests.Delivery_ShouldDeadLetterWithOneBackoffError_WhenOptionsBecomeUnusableAfterConstruction` (EventId 8, not 5, `dead_letter` 1), `…ShouldStillDeliverLaterEvents_WhenAnEarlierBackoffFailed`, `Dispatch_ShouldLogOneErrorAndKeepDelivering_WhenTopicPathDoesNotParse` (one `Error`, no unobserved task; its EventId 9 is not asserted). EventId 10 has no test |
| `Verbara.Sdk.Resilience/README.md:8` | maxAttempts capped at 10, ±20% jitter | ENFORCING | — | GAP — see *Unresolved* |
| `Verbara.Sdk.Hosting/README.md:112` | 0 trim warnings | ENFORCING | AotCanary | OK |
| `VoiceAi.Tts/README.md:3` | 6 providers | ENFORCING | — | GAP |
| `VoiceAi.Tts/README.md:9-14,73,81` | per-vendor TTFA (~150 ms, 40-90 ms, sub-100 ms, …) | ATTRIBUTED | — | GAP — no citation; the same vendor figure appears three times with three values |
| `VoiceAi.Stt/README.md:3` | 7 providers | ENFORCING | — | GAP |
| `VoiceAi.Stt/README.md:9` | "lowest latency in the catalog (~150ms)" | ATTRIBUTED | — | GAP |
| `VoiceAi.TurnDetection/README.md:3` | smart-turn-**v3** | ATTRIBUTED | — | WRONG — contradicts `:11` in the same file |
| `VoiceAi.TurnDetection/README.md:11` | bundles `smart-turn-v3.2-cpu.onnx` | ATTRIBUTED | — | GAP — no content-hash pin |
| `VoiceAi.TurnDetection.csproj:3` `<Description>` | smart-turn-**v3** | ATTRIBUTED | — | WRONG — ships to nuget.org; resource at `:26` is v3.2 |
| `VoiceAi.AudioSocket/README.md:90,95` | a connection presenting a UUID that a live session holds waits at most 1 second for it; a UUID still live after 1 second is refused | ENFORCING | `AudioSocketServer.SameIdGrace`; `AudioSocketServerEdgeCaseTests.HandleConnectionAsync_ShouldServeAConnectionThatPresentsAHeldId_WhenTheHolderHangsUpWithinTheGrace` asserts the wait's timer is due at 1 s, and `…_ShouldLogTheChannelIdNotTheLimit_WhenItRefusesASameIdConnection` that the refusal comes once 1 s has passed on the manual clock and logs `1000 ms` | OK |
| `VoiceAi.AudioSocket/README.md:99-101` | after the server's hangup frame, `AudioSocket()` returns and the dialplan goes on, on Asterisk 20 and later; on Asterisk 18 any end from the server hangs the call up | **EVIDENCE** | — | dated record — measured 2026-09-28 against Asterisk 20.20.1, 22.9.0 and 23.4.1 with a bare TCP listener in place of the server: two calls presenting one UUID, the second refused four ways, immediately or after 300 ms of unread audio, 10 calls per refusal style, route (`AudioSocket()` and `Dial(AudioSocket/…,,g)`) and version. After a hangup frame, immediate or late, `AudioSocket()` returned and the dialplan went on in every call; a bare close after unread audio (a reset) or an error frame failed the application and hung the call up on the `AudioSocket()` route. The same day, the server's own refusal, 10 calls per route and version: the refused call's dialplan went on in 60 of 60 on the three versions, and in 10 of 10 on 22.9.0 on a second harness. Re-measured 2026-09-29 with this server on 22.9.0 and 23.4.1, 10 calls per route and version: the refused call's dialplan went on in 40 of 40, refused 998–1006 ms after it connected, with the call holding the UUID untouched. On 18.26.4, measured 2026-09-28, 10 calls: any end from the server, a hangup frame included, failed the application and both calls were hung up |
| `VoiceAi.AudioSocket/README.md:46-56` | `HangupAsync` writes at most one hangup frame per session and nothing on a session already ended; a hangup waits for the audio write in flight | ENFORCING | `AudioSocketSessionHangupTests` (`HangupAsync_ShouldLeaveOneHangupFrameInAll_WhenCalledTwice`, `…ShouldCompleteAndWriteNothing_WhenTheCallerAlreadyHungUp`, `…WhenTheOwnerDisposedTheSession`, `…ShouldWaitForTheAudioWriteInFlight_WhenCalledBeforeThatWriteHasFinished`) | OK |
| `VoiceAi.AudioSocket/README.md:74-81` | a burst that identifies at once admits exactly `MaxConcurrentSessions` and refuses the rest | ENFORCING | `AudioSocketServerAdmissionTests.HandleConnectionAsync_ShouldAnnounceNoMoreThanTheLimit_WhenABurstPassesTheCheckBeforeAnyRegisters`, `…ShouldStillAdmitExactlyTheLimit_WhenTheHolderIsReleasedBetweenTheCheckAndTheRegistration`, `…ShouldAdmitTheLimitAgain_WhenEverySessionEndedByEitherSide` | OK |
| `VoiceAi.OpenAiRealtime/README.md:52-57` | when the vendor closes, the dialplan goes on in 60 of 60 calls within 2 ms on 20.20.1, 22.9.0 and 23.4.1; `StasisEnd` 60 of 60 with the caller bridged | **EVIDENCE** | — | dated record — measured 2026-10-02 against Asterisk 20.20.1, 22.9.0 and 23.4.1 with the SDK's own `AudioSocketServer` and `VoiceAiSessionBroker`, both routes (`AudioSocket()` and ARI `externalMedia` with `encapsulation=audiosocket`), 20 calls per handler outcome (returned, threw, the Realtime bridge against a loopback vendor that closed with 1000), route and version, 3 s of tone then a 15 s watch: session ended 360 of 360 within 8 ms of the handler finishing, dialplan went on 180 of 180, `StasisEnd` 180 of 180 with the caller still bridged in 180; Realtime cells 60 of 60 per route, within 2 ms. The *before* column is the same harness on the 2.6.1 code, measured 2026-09-30: 360 of 360 lines open at the end of the watch, 30 of 30 still open after 300 s (5 calls per route and version). Asterisk 18.26.4, 2026-10-02, 20 calls per cell: session ended 120 of 120, the application failed 60 of 60 on `AudioSocket()`, `StasisEnd` 60 of 60. The *now* half's shape is also pinned by `VoiceAiSessionBrokerEndingTests` and `OpenAiRealtimeBridgeBrokerEndingTests` (one hangup frame then EOF on loopback) |
| `VoiceAi.OpenAiRealtime/README.md:42` | `FunctionCallTimeout` defaults to 30 s | ENFORCING | `FunctionCallTimeoutTests.FunctionCallTimeout_ShouldDefaultToThirtySeconds` | OK — the same guard as `voice-bounds-migration.md:11,22,38` |
| `VoiceAi/README.md:76`, `VoiceAi.Tts/README.md:41-43` | every started recognition and synthesis ends in exactly one of `completed`, `failed`, `cancelled`; four `voiceai.ending` values | ENFORCING | `VoiceAiPipelineTurnAccountingTests.HandleSessionAsync_ShouldCountEveryStartedTurnInExactlyOneEnding_WhateverEndsTheSession` and `…ShouldCountTheTurnCancelledWithWhatEndedIt_WhenSomeoneOutsideTheProviderEndsIt` | OK |

The other 28 `<Description>` values carry no quantitative claim.
| `Verbara.Sdk.Sessions/README.md:32,94` | `QueueMetricsWindow` 30 minutes and `SlaThreshold` 20 s by default | — | — | GAP — documented defaults, true against `SessionOptions` as of 2026-10-03, unclassed until *Unresolved* 1 is ruled, as the `troubleshooting.md:198,202` defaults |
| `Verbara.Sdk.Sessions/README.md:51-54` | on Asterisk 20.20.1, 22.9.0 and 23.4.1, 320 calls per version over every way of leaving a queue: after each call (each burst of ten), `CallsAbandoned` moved by app_queue's `Abandoned` and `CallsTimedOut` by its `EXITWITHTIMEOUT` count; `CallsWaiting` equalled app_queue's `Calls` 700 ms after each of 140 leaves per version | **EVIDENCE** | — | dated record — measured 2026-10-03 with an AMI user reading `dialplan`, a live harness placing calls from a second Asterisk into one queue per exit (answered, caller hang-up answered and unanswered, `Queue()` timeout with and without a closing announcement, the `n` option, a full queue, join-empty, leave-when-empty, exit key, AMI `Redirect`, `QueueWithdrawCaller`, and a dialplan that loops a caller back into the queue, answered or not), 10 calls per exit and 10 bursts of 10 for the timeout and hang-up bursts, and reading app_queue's `QueueStatus` and `queue_log` before and after each call; 140 of 140 per-call (per-burst) checks per version for each of the two counters, 0 timeouts counted on the 190 other-exit calls per version. Without `dialplan`, `CallsTimedOut` stayed 0 and `CallsAbandoned` still matched (300 of 300) |
| `Verbara.Sdk.Sessions/README.md:61-64` | the `dialplan` class was 44 % to 51 % of the events a `read = all` user received on a test dialplan; behind the recommended filter, 1.28 % more bytes than no `dialplan` | **EVIDENCE** | — | dated record — the same measurements as `troubleshooting.md:109,118` |
| `Verbara.Sdk.Sessions/README.md:93` | on a queue whose callers all time out, `ServiceLevel`, app_queue's `ServiceLevelPerf` and `ServiceLevelPerf2` read 0 %, 0 % and 100 % | **EVIDENCE** | — | dated record — measured 2026-10-03 on Asterisk 20.20.1, 22.9.0 and 23.4.1 on four queues whose every caller timed out (`Queue()`'s timeout argument, with and without a closing announcement, the `n` option, and a burst of 100): `ServiceLevel` 0.0 %, `ServiceLevelPerf` 0.0, `ServiceLevelPerf2` 100.0 on every queue and version |

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

`docs/guides/README.md` carries no quantitative claim beyond `:10` (below).

| location | claim | class | status |
|---|---|---|---|
| `high-load-tuning.md:70` | "All **five** VoiceAi packages publish a Meter + ActivitySource + IHealthCheck" | ENFORCING | **WRONG** — same falsity as `README-technical.md:216`: only 3 ActivitySources exist, so Stt and Tts publish a Meter and a HealthCheck but no source |
| `high-load-tuning.md:106` | 9 sources, 15 meters | ENFORCING | OK — `MarketingClaimsTests` |
| `high-load-tuning.md:19-24` | RAM per buffer (est.) | ENFORCING | GAP — derivable from capacity × entry size |
| `high-load-tuning.md:19-24,26,279` | events/sec per agent tier; 200K/sec queue storm; VarSet 50%+ of volume | — | GAP — workload estimates about the reader's PBX; see *Unresolved* |
| `high-load-tuning.md:149-151` | pauseWriter 1 MB / resumeWriter 512 KB / segment 4 KB "hardcoded" | ENFORCING | GAP |
| `high-load-tuning.md:223` | EventPumpCapacity 20,000 | ENFORCING | OK — matches source; `README-technical.md:503` is the wrong one |
| `high-load-tuning.md:194,196-203` | with `MaxReconnectAttempts = N` the client makes N reconnect attempts; while `AutoReconnect` is on the backoff options are accepted from `00:00:00` / `ReconnectInitialDelay` to `24.20:31:23.647` (`int.MaxValue` ms), a multiplier of at least `1.0`, and `MaxReconnectAttempts` of 0 or more | ENFORCING | OK — `AmiConnectionOptionsValidatorTests` / `AriClientOptionsValidatorTests` `Validate_ShouldAgreeWithTheBackoffScheduleAndTheWaitLimit_OverTheBoundarySet` (the boundary set holds exactly `int.MaxValue` ms, accepted, and 60 days, rejected) and `…ShouldAcceptOnlyValuesTheBackoffComputesAtEveryAttempt`; `AmiReconnectLoopTests.ReconnectLoop_ShouldMakeExactlyNConnects_WhenEveryReconnectIsRefused` and `AriClientStateTests.ReconnectLoop_ShouldMakeExactlyNDials_WhenEveryReconnectIsRefused` (N ∈ {1, 2, 4}) |
| `ami-connection-state-and-health-migration.md:124-125` | a `StateChanged` handler that has not returned after 30 s is logged once at Warning | ENFORCING | OK — the same guard as `troubleshooting.md:215` (`AmiConnection.StuckNotificationBound`, `AmiConnectionStuckNotificationTests`) |
| `ari-connection-state-and-accept-loop-migration.md` | accept backoff 100 ms doubling to a 5 s cap; at most 12 Error lines a minute at the cap | ENFORCING | GAP — the bounds are `internal` constants in `AriOutboundListener`; the per-minute figure is 60/5 s, arithmetic over them. Both are restated from the `[Unreleased]` #291 entry, not newly derived |
| `ari-connection-state-and-accept-loop-migration.md:17,41-45,65-71,255` | the caller's first ARI connect is bounded at 5 s, then throws `WebSocketException` carrying a `TimeoutException` and leaves `Faulted` | ENFORCING | OK — `AriConnectBound.Default`; `AriClientStateTests.ConnectAsync_ShouldFaultWithinItsBound_WhenTheUpgradeIsNeverAnswered` drives the dial on a manual clock and asserts the bound armed at 5 s, the exception shape and `Faulted`; `…_ShouldStillBeConnecting_WhenTheBoundHasNotElapsed` the side before it. The message text at `:45` is not asserted |
| `ari-connection-state-and-accept-loop-migration.md:108-112,132-134` | before this change: a dispose with 20 events buffered and a 250 ms observer took about 5 s, with 200 about 50 s; `DisconnectAsync` returned at once and every buffered event was delivered after it; a connect after a disconnect left two consumers and broke the order in about half the runs | **EVIDENCE** | dated record — measured 2026-10-02 on `965fc86c` with a loopback events socket, 10 runs per cell: dispose N = 20 / 200 with a 250 ms observer 5,009.8–5,024.5 ms / 50,024.5–50,100.2 ms, every buffered event delivered after the ending; disconnect N = 20 / 200, 20/20 and 200/200 delivered after it returned in 0.1–0.4 ms; connect → disconnect → connect, two live consumers 10 of 10, order broken in 5 of 10. The code it describes no longer exists, so nothing re-checks it; the *now* half is pinned by `AriClientEventDeliveryTests` |
| `audiosocket-wire-format-migration.md:10-18` | a three-byte header; Asterisk closed the connection "two seconds later"; "1,411 frames" captured against Asterisk 22.9.0; identification `01 00 10` plus sixteen bytes of UUID, audio `10 01 40` plus 320 bytes | — | GAP — no class declared; added by #302 (`24855e10`, 2026-09-24), which edited this registry in the same PR (the ADR-count row) without adding one |
| `externalmedia-channel-id-migration.md:154-155` | `ConnectionTimeout` "30 seconds by default" | — | GAP — a documented default, unclassed until *Unresolved* 1 is ruled; added by #305 (`9f2cbde7`, 2026-09-24) without a row |
| `reconnect-options-migration.md:16,56-59,111-124` | AMI `MaxReconnectAttempts = N` now makes N reconnect attempts; before it made N − 1, and `= 1` never reconnected | ENFORCING | OK — `AmiReconnectLoopTests.ReconnectLoop_ShouldMakeExactlyNConnects_WhenEveryReconnectIsRefused` (N ∈ {1, 2, 4}), the same guard as `high-load-tuning.md:194`. The "before" half describes the code at `e3ace608` (the limit checked after the backoff, `attempt >= N`), a historical statement nothing re-checks |
| `reconnect-options-migration.md:17-20` | before this change `AddVerbara` dropped 12 of the 18 AMI options and 5 of the 9 ARI options; `MaxReconnectAttempts = 3` reached the client as `0` | ENFORCING | PARTIAL — the 18 and the 9 are this repository's option types, pinned by `AddVerbaraOptionsDeliveryTests.OptionLists_ShouldNameEverySettableOption_OfBothOptionTypes`, and every one is now delivered (`…ShouldDeliverEveryAmiOption…`, `…ShouldDeliverEveryAriOption…`). The 12 and the 5 count what `AddVerbara` did not copy at `e3ace608` (it copied 6 AMI and 4 ARI options): a historical figure, true against that commit, that nothing re-checks |
| `reconnect-options-migration.md:31` | the longest accepted delay is `int.MaxValue` milliseconds, about 24.8 days | ENFORCING | OK — the same guard as `high-load-tuning.md:196-203` (`…ShouldAgreeWithTheBackoffScheduleAndTheWaitLimit_OverTheBoundarySet`, which holds exactly `int.MaxValue` ms, accepted); the 24.8 days is its conversion |
| `reconnect-options-migration.md:61-69` | with the credentials rejected and `N = 4` on Asterisk 20, 22 and 23: 3 reconnect connects before and 4 now, each in 60 of 60 runs; with 1 s ×2, 17.2–17.6 s from the restart to `Disconnected` before and 17.2–18.8 s now | **EVIDENCE** | dated record — the same measurement as `troubleshooting.md:200` (2026-10-01, 20.20.1, 22.9.0 and 23.4.1, n = 10 per version and configuration), and its control on the code before N meant N attempts |
| `push-stream-and-webhook-options-migration.md:55-60,65-76` | the stream's status-code contract and each request's answer now (the *Now* column) | ENFORCING | OK — the same guards as `Verbara.Sdk.Push.AspNetCore/README.md:45-50` (`SseAdmissionTests`, one row per scenario; the tenant-claim row at `:57` by the two `Missing tenantId claim.` tests named there) |
| `push-stream-and-webhook-options-migration.md:30-31,62-76` | before this change: every stream request answered `500` on a default Kestrel host; with `AllowSynchronousIO = true`, the *Before* column (the `**` fallback serving every event, the empty streams, the silent drops) | **EVIDENCE** | dated record — measured 2026-09-29 on 2.6.1 (`a549cde1`) with a real Kestrel host, 3 runs per scenario, and again 2026-10-01 on `972c04e9` by the admission theory run against the unfixed endpoint, 5 of 5 runs identical; the code it describes no longer exists, so nothing re-checks it |
| `push-stream-and-webhook-options-migration.md:141-142,351,366` | heartbeat every 15 seconds | ENFORCING | GAP — the same as `Verbara.Sdk.Push.AspNetCore/README.md:12,137` |
| `push-stream-and-webhook-options-migration.md:145-164,303` | the 1 MiB default bound, drop oldest, the `.gap` marker and its count, heartbeats outside the bound, the oversize frame, `%2Egap` | ENFORCING | OK — the same guards as `Verbara.Sdk.Push.AspNetCore/README.md:13,140-141` and `:140-163`; the `3413` at `:151` is the same example value |
| `push-stream-and-webhook-options-migration.md:170-187` | the trace tree before and after | ENFORCING | OK — the *Now* half is the same guard as `Verbara.Sdk.Push/README.md:116-139`; the *Before* half is the dated record measured 2026-09-29 on 2.6.1, 30 publications per scenario (`push deliver` in the publisher's trace 0 of 30, a root 30 of 30) |
| `push-stream-and-webhook-options-migration.md:191-211` | the accepted webhook option values, `int.MaxValue` ms (about 24.8 days), the two exception shapes, EventIds 8, 9 and 10 | ENFORCING | the same guards and gaps as `Verbara.Sdk.Push.Webhooks/README.md:37-48` (OK) and `:50-53,88-97` (PARTIAL — EventId 9 not asserted, EventId 10 untested) |
| `push-stream-and-webhook-options-migration.md:91-112,158-159,244-257,360-362` | a topic-less event is matched and named by its event type; a type that is not a topic reaches only `**` streams; an unparseable non-empty `TopicPath` reaches no stream and is logged once per event (EventId 4); isolation unchanged | ENFORCING | OK — the same guards as `Verbara.Sdk.Push.AspNetCore/README.md:57-65,87-88` and `:158-159` |
| `push-stream-and-webhook-options-migration.md:113-116,347` | the NATS bridge names a null topic path by the event type and an empty one by the bare subject prefix; webhooks deliver no event whose topic path is null or empty | ENFORCING | PARTIAL — the empty path to the bare prefix: `NatsSubjectTranslatorTests.ToNatsSubject_ShouldPreservePrefix_WhenTopicPathIsEmpty`; the bridge's `TopicPath ?? EventType` and the webhook drop are unchanged code with no test of their own |
| `push-stream-and-webhook-options-migration.md:118-136,259-283,345,363-364` | the four claim-type lists, their defaults, precedence, unions, ignore-case types, whole ordinal values (an `scp` of `read write` is one permission), `JwtBearer` either way, `tid` not a default, assignment replaces and configuration binding appends (`TenantIdClaimTypes:0 = tid` → `["tenantId", "tid"]`), start-up validation, the unvalidated `400` | ENFORCING | OK — the same guards as `Verbara.Sdk.Push.AspNetCore/README.md:93-131`; the binding example is the measured case of `Options_ShouldGetTheConfiguredEntriesAppendedToTheDefaults_WhenBoundFromConfiguration`, the whole-value example `From_ShouldKeepValuesWholeAndOrdinal_WhenTheyDifferOnlyInCaseOrCarrySeparators` (`billing:read queue:read`) |
| `push-stream-and-webhook-options-migration.md:20-29` | before this change: a topic-less event reached every admitted stream of its tenant, and an unparseable topic path was skipped silently; the authorizer and the delivery filter saw empty roles and permissions, and under default `JwtBearer` a null user | **EVIDENCE** | dated record — measured 2026-10-02 on `e9739e54` by the topic-less and subscriber-identity tests run against the unfixed endpoint, 3 runs each with identical results (16 of 30 and 16 of 24 cases red, both Kestrel settings); the code it describes no longer exists, so nothing re-checks it |
| `session-store-backends.md:5,56,70,76` | three backends, three overloads, three indexes, pageSize 500 | ENFORCING | GAP |
| `session-store-backends.md:9-11,22,27,35` at `e250182e` | read latency <0.1 ms / <1 ms / 5-10 ms; the "sub-millisecond" InMemory and Redis bullets; "5-10 ms read latency is acceptable" | COHERENCE | **DELETED** — nothing measures InMemory, the record binds no read figure, and the only read the committed measurements time, `GetAsync` over loopback, put Postgres at p50 48 µs, not 5-10 ms; Postgres `GetAsync` measures under a millisecond too, so "sub-millisecond reads" did not tell Redis apart. This row cited `:26` until then; the bullets are `:22` and `:27`. The words that replaced these figures are the next two rows |
| `session-store-backends.md:9-11,22,35` | what a store call costs, in words: InMemory in-process, with no I/O and no serialization; Redis one `GET` for `GetAsync` and two for `GetByLinkedIdAsync`; Postgres one `SELECT` per read, one WAL flush per single save under the default `synchronous_commit=on`, and one shared by a `SaveBatchAsync` | ENFORCING | GAP — this repository's own code: true against `InMemorySessionStore.cs`, `RedisSessionStore.cs` and `PostgresSessionStore.cs` as of 2026-09-12 (`GetByLinkedIdAsync` sends one `GET` when the linked index misses), and nothing counts the commands |
| `session-store-backends.md:27` | of the two networked backends, Redis measures the faster single save | COHERENCE | PARTIAL — follows from the two `SaveAsync` p50s the guide test binds, but that test checks the `## Benchmarks` section as a whole, so which backend holds which figure is not asserted |
| `session-store-backends.md:175,177,179-180` | Redis `SaveAsync` p50 30 µs, batch 91,021 sess/sec; Postgres p50 1.97 ms, batch 13,489 sess/sec; batches of 500 sessions; measured 2026-09-12 on .NET 10.0.12 | COHERENCE | PARTIAL — `PerformanceTableCoherenceTests` — the guide test binds each session-store row's latency and batch, as whole figures, and its date and runtime to the `## Benchmarks` section, and fails if the record has no session-store row. Replaces ~250 µs / ~1.5 ms. Machine, server versions, instrument, loopback-without-TLS, run count and the batch size are stated, not asserted — the record holds no batch size. The check is section-wide, so the two backends' figures swapped between table rows still pass, and so does an unbound figure added to the section; and it binds only rows whose `operation` starts with `Session store`, so renaming one of the two off that prefix drops its backend from the check without a failure |
| `session-store-backends.md:177-182` at `e250182e` | the old Benchmarks table's InMemory column, its `GetAsync` ~200 µs / ~1.2 ms, `GetActiveAsync (1,000 active)` ~8 ms / ~12 ms and `SaveBatchAsync (100 sessions)` ~3 ms / ~15 ms | COHERENCE | **DELETED** — nothing records a measurement of InMemory, `GetActiveAsync` or a 100-session batch: the `Fact`s the table credited time only `SaveAsync`, `GetAsync` and 500-session batches, and `SessionsBackendsBenchmark`, which defines `GetActive_1000` and `SaveBatch_100`, has no committed result, no baseline entry and no workflow. `GetAsync` is measured, in the addendum, but the record does not bind it, so the guide points there instead of publishing it |
| `troubleshooting.md:38` | `manager reload` does not end the session of an AMI user deleted from `manager.conf`; the application stays connected until its session ends | **EVIDENCE** | dated record — the user's section removed from `manager.conf`, `manager reload` run, then read 5 s later: the SDK's connection still read `Connected` and `manager show connected` still listed the session in every run, on 2026-09-28 on Asterisk 20.20.1, 22.9.0 and 23.4.1, and on 2026-09-29 on the same three versions (60 of 60 runs); the next login after a restart failed with `AmiAuthenticationException` |
| `troubleshooting.md:109` | the `dialplan` class was 44 % to 51 % of all the events a `read = all` user received on a test dialplan (Asterisk 18.26.4, 22.9.0 and 23.4.1); with `setqueuevar` and `setqueueentryvar` on, adding it to a user's `read` added 125.5 % bytes (20.20.1, 22.9.0 and 23.4.1) | **EVIDENCE** | dated record — the share measured 2026-09-28 on a tap of the AMI stream of a `read = all` user during a queue test plan: 43.6 % (18.26.4), 51.4 % (22.9.0) and 51.3 % (23.4.1) of the events, 45–53 % of the bytes, of which 75 were `QUEUESTATUS`; the increase measured 2026-10-02 with three raw AMI users logged in to the same Asterisk during the same calls (10 calls per exit over nine queue exits, per version), one with the SDK's usual read classes and one with them plus `dialplan`: 3 582 703–3 582 719 bytes against 1 588 905–1 588 911 |
| `troubleshooting.md:111` | the filter text works on Asterisk 20, 22 and 23, whose `manager.conf.sample` documents its syntax | **EVIDENCE** | dated record — 2026-10-02: the `manager.conf.sample` at tags 20.20.1, 22.9.0 and 23.4.1 is the same file and documents the `eventfilter(...)` syntax; on each version a user with the two lines received, on a timed-out call, one `VarSet` (`QUEUESTATUS`) and no `Newexten`, where the same user without them received 42 and 7 |
| `troubleshooting.md:118` | behind the filter, 1.28 % more bytes than without `dialplan` (4.36 % on calls that time out), and `CallsTimedOut` counted every timeout | **EVIDENCE** | dated record — 2026-10-02, the same three users as `:109` plus one with `read = all` behind the filter, on 20.20.1, 22.9.0 and 23.4.1: 1 609 245–1 609 251 bytes against 1 588 905–1 588 911 (+1.28 % on each version; the extra events were the 50 `QUEUESTATUS` `VarSet`s), the worst exit +4.36 %; the SDK reading through the filter counted `CallsTimedOut` equal to `EXITWITHTIMEOUT` on 90 of 90 calls per version (30 timeouts each) with no other count changed, and the repository's functional test lane on 22 and 23 with its SDK users behind the filter showed no failure the filter added |
| `troubleshooting.md:155` | a load asks `QueueStatus` or `Agents` again every 200 ms while Asterisk refuses it as an unknown command and the AMI user has not received `FullyBooted` | ENFORCING | OK — `VerbaraServer.NotRegisteredRetryInterval`; `VerbaraServerBootWindowTests.StartAsync_ShouldAskAgainAtTheInterval_WhenTheUserNeverReceivesFullyBootedDuringTheBoot` drives the load on a manual clock and asserts three waits of 200 ms, with one ask after each |
| `troubleshooting.md:156,158` | a load stops asking 10 s after its first such refusal, once per load; so a user without `system`, on a PBX where app_queue or app_agent_pool is not loaded, waits 10 s on every load | ENFORCING | OK — `VerbaraServer.NotRegisteredRetryBudget`; `VerbaraServerBootWindowTests.StartAsync_ShouldWaitTheBudgetOnce_WhenTheUserNeverReceivesFullyBootedAndAppQueueIsAbsent` asserts that the load completes at exactly 10 s of manual time, with one Warning, and `…AndBothModulesAreAbsent` that two absent modules still wait the 10 s once |
| `troubleshooting.md:160` | after a restart, `Agents` works 50–111 ms and `QueueStatus` 80–131 ms after the AMI login, and `FullyBooted` arrives 17–28 ms after `QueueStatus` works | **EVIDENCE** | dated record — measured 2026-09-28 on Asterisk 20.20.1, 22.9.0 and 23.4.1, over 180 raw AMI sessions (30 per version after a `docker kill` and 30 after a `docker stop`, each followed by `docker start`), each logged in as soon as the AMI port accepted and asking `QueueStatus` then `Agents` every 10 ms; minimum to maximum over the three versions. Every session was refused as an unknown command first (180 of 180), and `FullyBooted` came after `QueueStatus` worked in 152 of 152 sessions that saw both. In a second series, on the same three versions and 18.26.4, a user without `system` in `read` received no `FullyBooted` in 40 of 40 sessions |
| `troubleshooting.md:181` | a reload's `Status` read lasts at most `DefaultEventTimeout`, 5 s by default, over an `AmiConnection` | — | GAP — a documented default, true against `AmiConnectionOptions.DefaultEventTimeout` as of 2026-10-02, unclassed until *Unresolved* 1 is ruled, as the `troubleshooting.md:198,202` defaults |
| `troubleshooting.md:198,202` | the defaults `HeartbeatInterval` 30 s, `HeartbeatTimeout` 10 s, `DefaultResponseTimeout` 2 s and `MaxReconnectAttempts` 0; a silent peer seen "up to about 32 s" after it went silent (30 s + the 2 s `Ping` wait) | — | GAP — documented defaults, true against `AmiConnectionOptions` as of 2026-09-29, unclassed until *Unresolved* 1 is ruled; the 32 s is their sum |
| `troubleshooting.md:198` | with the defaults, a silent peer was seen 2.2–32.0 s after it went silent | **EVIDENCE** | dated record — measured 2026-09-28 on Asterisk 22.9.0 and 23.4.1 with the default heartbeat, n = 40 (the container paused, or disconnected from its network, at a random point of the heartbeat period), from the fault to the connection's `State` leaving `Connected`; minimum to maximum |
| `troubleshooting.md:200` | the give-up after N failed attempts, as examples measured from Asterisk's restart with the credentials rejected: 17.2–18.8 s with 1 s ×2 and N = 4; 8.1–9.2 s with 0.5 s ×2 capped at 2 s and N = 4 | **EVIDENCE** | dated record — from `docker start` of an Asterisk whose AMI user had been deleted to the connection's `State` reading `Disconnected`, measured 2026-10-01 on 20.20.1, 22.9.0 and 23.4.1, n = 10 per version and configuration (60 runs); every run made 4 reconnect connects, logged 4 `[AMI] Reconnecting` and 4 `[AMI] Reconnect attempt failed` lines, and made no connect after the give-up. 17.2–18.8 s with 1 s ×2; 8.1–9.2 s with 0.5 s ×2 capped at 2 s. Control, the same harness on the code before N meant N attempts: 3 connects in every run (60 of 60), 17.2–17.6 s and 7.0–8.2 s — the figures this row recorded until then (17.2–17.7 s, 2026-09-28/29, n = 80; 6.9–8.6 s, 2026-09-28, n = 20) |
| `troubleshooting.md:215` | an AMI notification handler still running 30 s after its notification began is logged once at Warning | ENFORCING | OK — `AmiConnection.StuckNotificationBound`; `AmiConnectionStuckNotificationTests.Notify_ShouldLogOneWarning_WhenAHandlerHasNotReturnedOnceTheBoundHasPassed` drives the queue on a manual clock and asserts no Warning 1 ms before 30 s, one at 30 s and still one at ten times the bound, and `…_ShouldLogNoWarning_WhenEveryHandlerReturnedBeforeTheBound` that a handler that returned is never reported |
| `troubleshooting.md:244` | designed for zero trim warnings | ENFORCING | PARTIAL — 22/29 |
| `troubleshooting.md:282,286-294` | 9 registered sources, enumerated by name | ENFORCING | PARTIAL — count pinned, the by-name list is not |
| `troubleshooting.md:322` | reconcile burst over 5-30 seconds | — | GAP |
| `troubleshooting.md:347,349` | `ChannelIdInUse` reports about 1000 ms waited; a call that comes back is waited for up to 1 second | ENFORCING | OK — the same guard as `VoiceAi.AudioSocket/README.md:50,55` (`SameIdGrace`, asserted by `…_WhenTheHolderHangsUpWithinTheGrace` and `…_ShouldLogTheChannelIdNotTheLimit_WhenItRefusesASameIdConnection`) |
| `troubleshooting.md:355,363` | after a refusal's hangup frame the dialplan goes on on Asterisk 20 and later; Asterisk 18 hangs the call up | **EVIDENCE** | dated record — the same measurement as `VoiceAi.AudioSocket/README.md:59-61` |
| `troubleshooting.md:361` | a burst arriving together is admitted up to the limit | ENFORCING | OK — the same guard as `VoiceAi.AudioSocket/README.md:74-81` |
| `voice-bounds-migration.md:11,22,38` | `FunctionCallTimeout` defaults to 30 s | ENFORCING | OK — `FunctionCallTimeoutTests.FunctionCallTimeout_ShouldDefaultToThirtySeconds` |
| `voice-bounds-migration.md:74` | `FunctionCallTimeout` accepts more than zero and at most `int.MaxValue` ms (about 24.8 days); rejected by the validator, the constructor and before each call | ENFORCING | OK — `FunctionCallTimeoutTests.Validate_ShouldFailNamingTheOption_WhenFunctionCallTimeoutIsUnusable`, `…Validate_ShouldSucceed_WhenFunctionCallTimeoutIsUsable`, `…Ctor_ShouldThrowNamingTheOption_…` and `…HandleSessionAsync_ShouldThrowNamingTheOptionBeforeTheCall_…`; the 24.8 days is `int.MaxValue` ms, arithmetic |
| `voice-bounds-migration.md:60` | the 10 s wait for OpenAI's close answer restarts at a function's abandonment | ENFORCING | OK — `FunctionCallTimeoutTests.HandleSessionAsync_ShouldWarnCountPublishAndRestartTheCloseBound_WhenAFunctionAtTheHangupOutlivesItsBound`; the 10 s is the same figure `RealtimeMetrics.SessionsCloseUnanswered` documents |
| `voice-bounds-migration.md:7,13,26,115-122,139,152` | the speech providers' `ConnectTimeoutSeconds` and LMNT's `HttpTimeoutSeconds` accept 1 to 600; their defaults 5, 10 (Speechmatics TTS) and 30 (LMNT HTTP) | ENFORCING | OK — the range: `ConnectTimeoutRangeTests` in the STT and TTS test projects (validator, constructor and per-dial cells at 0, -1 and 601 rejected, 1 and 600 accepted) and the two validators' tests; the defaults: read from the options classes as of 2026-10-02, not asserted by a test of their own (GAP) |
| `voice-bounds-migration.md:144` | seven of the nine providers already shipped a validator, two gain one | ENFORCING | GAP — a count of this repository's own validators as of 2026-10-02 (STT: AssemblyAI, Cartesia, Speechmatics; TTS: Cartesia, Deepgram, LMNT, Speechmatics), with no test of its own |
| `voice-session-ending-migration.md:41,56-70,233-235` | the line before and after the change: 360 of 360 open vs 0 of 360, ended within 8 ms, dialplan 180 of 180, `StasisEnd` 180 of 180, 15 s of silence, still open after 300 s; Asterisk 18 120 / 60 / 60; Realtime 60 of 60 within 2 ms | **EVIDENCE** | dated record — measured 2026-10-02 against Asterisk 20.20.1, 22.9.0 and 23.4.1 with the SDK's own `AudioSocketServer` and `VoiceAiSessionBroker`, both routes (`AudioSocket()` and ARI `externalMedia` with `encapsulation=audiosocket`), 20 calls per handler outcome (returned, threw, the Realtime bridge against a loopback vendor that closed with 1000), route and version, 3 s of tone then a 15 s watch: session ended 360 of 360 within 8 ms of the handler finishing, dialplan went on 180 of 180, `StasisEnd` 180 of 180 with the caller still bridged in 180; Realtime cells 60 of 60 per route, within 2 ms. The *before* column is the same harness on the 2.6.1 code, measured 2026-09-30: 360 of 360 lines open at the end of the watch, 30 of 30 still open after 300 s (5 calls per route and version). Asterisk 18.26.4, 2026-10-02, 20 calls per cell: session ended 120 of 120, the application failed 60 of 60 on `AudioSocket()`, `StasisEnd` 60 of 60. The *now* half's shape is also pinned by `VoiceAiSessionBrokerEndingTests` and `OpenAiRealtimeBridgeBrokerEndingTests` (one hangup frame then EOF on loopback) |
| `voice-session-ending-migration.md:158-160` | a second `HangupAsync` and one after the caller hung up complete 20 of 20 (one frame / none); both threw `ObjectDisposedException` 20 of 20 before | ENFORCING + **EVIDENCE** | the *now* half OK — the same guard as `VoiceAi.AudioSocket/README.md:46-56`, and the loopback measurement of 2026-10-02 on the branch build, 20 runs per case; the *before* half a dated record, measured 2026-09-30 on 2.6.1's `HangupAsync` (loopback, 20 runs per case), that nothing re-checks |
| `voice-session-ending-migration.md:209-225`, `high-load-tuning.md:91-95` | exactly one of `completed`, `failed`, `cancelled` per started turn; the four `voiceai.ending` values; the old `tts.syntheses.completed` recovered as `completed` plus the non-`session-cancelled` increments | ENFORCING | OK — the same guards as `VoiceAi/README.md:76`; the recovery formula is arithmetic over the same buckets |
| `voice-session-ending-migration.md:253` | the same-UUID wait of up to 1 second | ENFORCING | OK — the same guard as `VoiceAi.AudioSocket/README.md:90,95` |
| `call-session-ending-migration.md:74-79` | a call answered with no dial (an IVR hung up by either side, an originate answered by an endpoint or a `Local` channel) ended `Failed` with no talk time before and ends `Completed` with a talk time of 2.0–3.0 s now; the dialed, queued and unanswered calls end as before | ENFORCING + **EVIDENCE** | the shape OK — `CallShapeCaptureReplayTests.AnsweredCallsNoDialOrQueueReached_ShouldEndCompletedWithATalkTime_WhenACallShapeCaptureIsReplayed` and `…QueuedDialedAndUnansweredCalls_ShouldKeepTheirFlowAndTheirEvents_…` replay the same twelve shapes captured from Asterisk 20.20.1, 22.9.0 and 23.4.1; the durations a dated record — measured 2026-10-02 live against the same three versions with a consumer-shaped host (`AddVerbara` + `AddVerbaraSessions`), one call per shape and version, talk times 1997–3005 ms; the *before* half is the same harness on `9866b2ef` the same day |
| `call-session-ending-migration.md:114-121` | a taken queued call's `ConnectedAt` is the queue's `AgentConnect`, the instant `CallConnectedEvent` carries; with a member that answers at once its wait and talk times moved by no more than the run-to-run variation (at most 25 ms) | ENFORCING + **EVIDENCE** | the instant OK — `CallShapeCaptureReplayTests.QueuedCalls_ShouldConnectWhenTheQueueReportsAMemberAndOnlyThen_…` (connected no earlier than the queue's report, no later than the announcement) and `…QueuedCalls_ShouldStayQueued_WhenTheQueueNeverReportsTheConnection`; the 25 ms a dated record — measured 2026-10-02 against Asterisk 20.20.1, 22.9.0 and 23.4.1, one call per taken shape (originated, and after an IVR) and version on `9866b2ef` and on the change: wait 1005–1022 ms and 1501–1502 ms, talk 3488–3513 ms and 3502–3505 ms, on both; the largest difference between the two codes on one version and shape was 25 ms of talk (22.9.0, originated) |
| `log-analysis-reference.md:5,22` | SDK Tags (11), Dashboard Tags (8) | ENFORCING | GAP |
| `asterisk-version-compatibility.md:9,157` | "**no data is ever lost**", "zero data loss" | ENFORCING | GAP — absolute claim, testable against the `RawFields` fallback |
| `provider-test-substrate.md:6` | fourteen provider surfaces | ENFORCING | GAP |
| `provider-test-substrate.md:33,36,114` | ~30 assemblies, coverage 80.42% → 61.96%, six defects | **EVIDENCE** | dated record |
| `provider-recording-protocol.md:6-7,54` | 14 surfaces = 6 HTTP + 8 WebSocket; five of six automated | ENFORCING | GAP |
| `provider-recording-protocol.md:317,512-513` | five of eight not-cleared; 80,608 bytes over ten reads | **EVIDENCE** | dated record |
| `provider-recording-protocol.md:707,712-717` | 256 KiB cap, 819 frames, ~3 MiB | ENFORCING | GAP — repo size policy |
| `provider-wire-conformance.md:568-573` | 46 / 220 / 174 / 126 / 48 test counts | ENFORCING | GAP — **counts of our own tests: not EVIDENCE** |
| `provider-wire-conformance.md:802` | the OpenAI Realtime client's read buffer is 64 KiB | ENFORCING | GAP — this repository's own code: `new byte[1024 * 64]` in `OpenAiRealtimeBridge.OutputLoop` as of 2026-09-29, and no test reads the size |
| `provider-wire-conformance.md:801-805` | the bound running out inside a read that returns part of a message, through Asterisk 18.26.4, 20.20.1, 22.9.0 and 23.4.1: completed with the close unanswered on every run with the fix, failed on every run before it; 3.7–12.7 % of unanswered closes failed in an in-process race no hook forced | **EVIDENCE** | dated record — measured 2026-09-28 with no live vendor: Asterisk hangs up a call bridged over AudioSocket after 3 s, the client closes toward a local fake of the vendor that never answers, and the bound runs out, forced by a hook on the runtime's receive, inside the read that returns the first 64 KiB of a 70,000-character message. Two independent harnesses: 50 calls per version on 20.20.1, 22.9.0 and 23.4.1 (hook at the read's lock release), and 20 per version on 18.26.4, 22.9.0 and 23.4.1 (hook after the payload read); 10 control calls per version whose close was answered completed in both builds. The range is in process, without Asterisk and without the hook, 300 sessions per series, the clock advanced at a random instant while the fake streamed: 3.7 % and 12.7 % with back-to-back 70,000-character frames in the two harnesses, 7.7 % and 4.0 % with fragmented messages; 0 of 300 in every series with the fix. The instant itself is also pinned by deterministic tests (`OpenAiRealtimeBridgeEndingTests`) |
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
  The open change `longevity-soak-and-chaos` is the natural vehicle if it adopts a scale target;
  until it does, nothing in flight discharges this.


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

## Rulings settled 2026-09-24

**The AMI surface counts are counted by type, not by file**, and the definition is the one the
shipping source generator already implements: a class-level `[VerbaraMapping]` on a non-abstract
type (`EventRegistryGenerator.cs:51,60`). Measured against the tree that day: **148 actions, 269
events, 17 typed responses**.

No published figure was coherent before this ruling. `README.md:45` read 148 / 278 / 18 — one type
count and two file counts in the same sentence — which is why no guard could be written against it.

The two rejected definitions, and what each would have to call an AMI action or event:

- *By file* (149 / 278 / 18) counts `Actions/IEventGeneratingAction.cs`, an interface, as an action,
  and `Responses/ConfigCategory.cs`, a helper `record`, as a typed response.
- *By `[VerbaraMapping]` occurrence* (149 / 270 / 17) counts two **property-level** mappings as
  types: `Async` in `OriginateAction.cs:19` and `100rel` in `EndpointDetail.cs:74`. Both map an AMI
  field name that is not a valid C# identifier, which is the whole reason the attribute is there.

The nine event base types — `Events/ResponseEvent.cs` plus the eight under `Events/Base/` — carry no
`[VerbaraMapping]` at all, so no definition reaches them. None is `abstract`, so a filter written on
`IsAbstract` alone would not exclude them either.

| Where | Before | Now |
|---|---|---|
| `README.md:45` | 148 / 278 / 18 | 148 / 269 / 17 |
| `README.md:65` | 278 events | 269 events |
| `docs/README-technical.md:543-544` | 111 actions, 215 events | 148 actions, 269 events |
| `src/Verbara.Sdk.Ami/README.md:7` | 111 / 261 / 17 | 148 / 269 / 17 |

Corrected in the same pass: `docs/README-technical.md:543-544` named `AmiAction` and `AmiEvent`, in
`Verbara.Sdk.Ami.Actions` and `.Events`. Neither type exists. The bases are `ManagerAction` and
`ManagerEvent`, both in `Verbara.Sdk` (`src/Verbara.Sdk/IAmiConnection.cs:70,79`).

**The guard is deliberately not in this change.** A guard on `src/*/README.md` cannot run today:
`scripts/ci/classify-docs-only.sh:25` treats every `*/README.md` as docs-only, so the PR that breaks
the figure skips `Unit Tests` — the job the guard would live on. That carve-out moves with
*Unresolved* 1, and the guard goes in with it. Until then these rows stay `GAP`: the figures are
right and nothing stops them drifting again.

Left alone deliberately: ADR-0001 and ADR-0015 state 278 event types, ADR-0003 states 278 events,
and dated research notes state 278 and 111. Both are **Out** of scope above. The ADR figure was a
file count on the day it was written (`229145b8` and `4c2d0644` each hold 278 event files and 269
mapped types). Each of the three ADRs records the type count in its 2026-09-26 addendum, because an
Accepted ADR's body is never edited.

## Rulings settled 2026-09-20

Four of the six were decided and discharged in one change; each is recorded where it applies, and
the rows above and in the tables carry the result.

- **Vendor latency and pricing under D8's pin** → split. Latency stays as the vendor's own figure,
  cited with an access date, licensed by the new **D8a** (the ADR-0042 amendment).
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
are struck through, and the 94.26% figure survives at `:463` with its citation and hash pin. A
registry keyed by line number does not survive an edit above the line it points at, and nothing
enforces that — the check is manual and belongs in any PR that adds or removes README lines.

Corrected in the same pass, and **pre-existing** rather than caused by that edit: the AMI
parse+dispatch row transcribed the claim as `1.53M events/sec (653 ns)` while `README.md` has said
`1.62M events/sec (617.6 ns)` on `main` for some time. The registry's own copy of a figure had
drifted from the figure — the exact failure it exists to detect, one level up.
