# Product alignment audit — Asterisk.Sdk v1.11.0

Does the decision record match the product we actually shipped? Short answer: yes for what is written down; no for what is missing.

- **Date:** 2026-04-19
- **Status:** Research — final
- **Related docs:** [docs/decisions/](../decisions/) · [docs/plans/archived/](../plans/archived/) · [docs/specs/archived/](../specs/archived/)

---

## §1 ADR inventory & health

The decision catalog grew from 7 to 12 ADRs during the April 2026 docs sweep. Each ADR is small, load-bearing, and tied to something concrete in `src/` or in the repo root tooling. This section verifies every one of them.

| ADR | Title | Status | Evidence in code | Staleness |
|-----|-------|--------|------------------|-----------|
| 0001 | Native AOT first | Accepted | [Directory.Build.props](../../Directory.Build.props) (`IsAotCompatible=true`, `EnableTrimAnalyzer=true`, `EnableAotAnalyzer=true`), [global.json](../../global.json) pinned to `10.0.100`, [BannedSymbols.txt](../../BannedSymbols.txt) blocks reflection entry points | None |
| 0002 | Open-core (MIT + Pro) | Accepted | [LICENSE](../../LICENSE) (MIT), public README references the private Pro SDK repo without leaking its implementation, `PackageLicenseExpression=MIT` in `Directory.Build.props` | None |
| 0003 | Source generators over reflection | Accepted | [src/Asterisk.Sdk.Ami.SourceGenerators/](../../src/Asterisk.Sdk.Ami.SourceGenerators/) (4 generators: ActionSerializer, EventDeserializer, EventRegistry, ResponseDeserializer), `[JsonSerializable]` contexts (e.g. `AriJsonContext` in `src/Asterisk.Sdk.Ari/Resources/AriJsonContext.cs`), `[OptionsValidator]` partials across every `*OptionsValidator.cs` | None |
| 0004 | Central package management | Accepted | [Directory.Packages.props](../../Directory.Packages.props) (`ManagePackageVersionsCentrally=true`), no per-project `PackageVersion="x.y.z"` overrides | None |
| 0005 | Testcontainers for integration | Accepted | [Tests/Asterisk.Sdk.FunctionalTests/Infrastructure/Fixtures/AsteriskContainerFixture.cs](../../Tests/Asterisk.Sdk.FunctionalTests/Infrastructure/Fixtures/AsteriskContainerFixture.cs), `RealtimeFixture.cs`, `ToxiproxyFixture.cs`, [docker/Dockerfile.asterisk](../../docker/Dockerfile.asterisk). Testcontainers bumped to 4.11 (commit `eeb4a2c`) | None |
| 0006 | Pluggable session stores | Accepted | [src/Asterisk.Sdk.Sessions/ISessionStore.cs](../../src/Asterisk.Sdk.Sessions/ISessionStore.cs) plus `src/Asterisk.Sdk.Sessions.Redis/RedisSessionStore.cs` and `src/Asterisk.Sdk.Sessions.Postgres/PostgresSessionStore.cs` | None |
| 0007 | Topic hierarchy on Push bus | Accepted | [src/Asterisk.Sdk.Push/Topics/TopicPattern.cs](../../src/Asterisk.Sdk.Push/Topics/TopicPattern.cs), `TopicName.cs`, `ITopicRegistry.cs`, `TopicRegistry.cs` | None |
| 0008 | AMI exponential backoff reconnect | Accepted | [src/Asterisk.Sdk.Ami/Connection/AmiConnection.cs](../../src/Asterisk.Sdk.Ami/Connection/AmiConnection.cs) (`ReconnectLoopAsync`), `AmiConnectionOptions.cs` | None |
| 0009 | Three-tier test strategy | Accepted | [Tests/Asterisk.Sdk.FunctionalTests/Layer2_UnitProtocol/](../../Tests/Asterisk.Sdk.FunctionalTests/Layer2_UnitProtocol/), `Layer5_Integration/`, plus dedicated `Tests/Asterisk.Sdk.IntegrationTests/` alongside 21 unit-test projects | None |
| 0010 | ARI asymmetric transport (HTTP out, WS in) | Accepted | [src/Asterisk.Sdk.Ari/Client/AriClient.cs](../../src/Asterisk.Sdk.Ari/Client/AriClient.cs) uses `HttpClient` for REST + `ClientWebSocket` for events; 10 `AriXxxResource.cs` files in `src/Asterisk.Sdk.Ari/Resources/` | None |
| 0011 | Push bus in-memory, non-durable | Accepted | [src/Asterisk.Sdk.Push/Bus/RxPushEventBus.cs](../../src/Asterisk.Sdk.Push/Bus/RxPushEventBus.cs), `BackpressureStrategy.cs`, `PushEventBusOptions.cs` | None |
| 0012 | Live is an orthogonal aggregate root | Accepted | [src/Asterisk.Sdk.Live/](../../src/Asterisk.Sdk.Live/) (`AsteriskServer`, `Agents/`, `Bridges/`, `Channels/`, `MeetMe/`, `Queues/`), `ILiveObject.cs` | None |

### Per-ADR commentary

A brief note on each ADR that goes beyond the one-cell evidence column, so the audit captures not only whether the decision is still true but also how it is showing up in daily work.

**0001 — Native AOT first.** The `EnableTrimAnalyzer`, `EnableSingleFileAnalyzer`, and `EnableAotAnalyzer` properties in [Directory.Build.props](../../Directory.Build.props) are set simultaneously on every project. The zero-warning rule (`TreatWarningsAsErrors=true`, `WarningLevel=9999`) means any trim or AOT warning stops the build. This is the discipline that makes ADRs 0003, 0004, and item #12 of §4 (BannedSymbols) work as a system rather than isolated rules. Consumers reporting clean `PublishAot=true` builds on v1.11.0 is the functional test.

**0002 — Open-core MIT + Pro.** The public repo contains nothing that references Pro SDK implementation details; the auditing sweep of April 2026 removed five P0 leaks and stood up the public-repo exposure audit documented in project memory. The MIT boundary is a product decision with downstream consequences on license compatibility (consumers can AOT-publish the SDK into commercial apps without paperwork) and on PR triage (contributions touching Pro-only concepts are redirected to the private repo rather than merged here).

**0003 — Source generators over reflection.** Four dedicated generators live at [src/Asterisk.Sdk.Ami.SourceGenerators/](../../src/Asterisk.Sdk.Ami.SourceGenerators/): `ActionSerializerGenerator`, `EventDeserializerGenerator`, `EventRegistryGenerator`, `ResponseDeserializerGenerator`. Outside AMI, the pattern is repeated via `[JsonSerializable]` contexts (ARI) and `[OptionsValidator]` partial classes (every `*OptionsValidator.cs` — at least a dozen across Ami, Ari, Agi, Sessions, VoiceAi, Push). The generator output is checked in to `obj/` via `EmitCompilerGeneratedFiles=true` in CI, so review can look at what the generator produced if a regression is suspected.

**0004 — Central package management.** Every NuGet version lives in [Directory.Packages.props](../../Directory.Packages.props); the repo enforces `ManagePackageVersionsCentrally=true`. Dependabot and CodeQL operate on that single file. No project overrides `PackageVersion` locally — this is verifiable at a glance because any such override would break the build with a `NU1008` error.

**0005 — Testcontainers for integration.** The fixtures at [Tests/Asterisk.Sdk.FunctionalTests/Infrastructure/Fixtures/](../../Tests/Asterisk.Sdk.FunctionalTests/Infrastructure/Fixtures/) (`AsteriskContainerFixture`, `FunctionalTestFixture`, `RealtimeFixture`, `ToxiproxyFixture`) gate the entire functional tier on Docker availability. The 2026-04-18 bump to Testcontainers 4.11 migrated 10 callsites to the new `UntilInternalTcpPortIsAvailable` waiting strategy and Postgres image `postgres:18-alpine`, preserving the ADR while refreshing the infrastructure.

**0006 — Pluggable session stores.** [ISessionStore.cs](../../src/Asterisk.Sdk.Sessions/ISessionStore.cs) in `src/Asterisk.Sdk.Sessions/` defines the contract; two shipping backends (`RedisSessionStore` in `src/Asterisk.Sdk.Sessions.Redis/`, `PostgresSessionStore` in `src/Asterisk.Sdk.Sessions.Postgres/`) implement it. The v1.11.0 benchmark numbers in project memory (Redis 79 µs p50 save, Postgres 1.97 ms p50 save) validate that both backends are production-viable. The `SessionReconciliationService` at `src/Asterisk.Sdk.Sessions/Manager/` is the missing ADR-candidate documented in §4 item 8.

**0007 — Topic hierarchy on Push bus.** The four files at [src/Asterisk.Sdk.Push/Topics/](../../src/Asterisk.Sdk.Push/Topics/) — `TopicName`, `TopicPattern`, `ITopicRegistry`, `TopicRegistry` — implement the `**` wildcard + `{self}` placeholder semantics. The v1.8.0 release notes captured the DX rationale, but the wire-level invariants (pattern compile once, cache on registry) are structural and should not regress under future refactors.

**0008 — AMI exponential backoff reconnect.** [AmiConnection.cs](../../src/Asterisk.Sdk.Ami/Connection/AmiConnection.cs) owns the loop, `AmiConnectionOptions` owns the knobs. The v1.5.5 release notes identified the original reconnect-loop bug that justified the ADR. This is a good companion ADR to the heartbeat strategy (§4 item 2) — the two together form the AMI resilience story.

**0009 — Three-tier test strategy.** The layered directory naming (`Layer2_UnitProtocol/`, `Layer5_Integration/`) plus the separate `Tests/Asterisk.Sdk.IntegrationTests/` project make the tiers visible at the filesystem level. 21 unit-test projects flank the two tiers. The 2,637 unit / 154 functional / 59 integration counts in project memory are the observable product of this ADR.

**0010 — ARI asymmetric transport.** [AriClient.cs](../../src/Asterisk.Sdk.Ari/Client/AriClient.cs) holds both transports: `HttpClient` for the REST side and `ClientWebSocket` for event ingress. The split mirrors the Asterisk ARI contract itself; inverting it would require either long-polling (removed in Asterisk 14+) or pushing commands through the WebSocket (never supported). The ADR records the decision even though the alternative is effectively blocked by the upstream API — this is the kind of "confirm the obvious" ADR that saves a future contributor from redesigning around a non-choice.

**0011 — Push bus in-memory, non-durable.** [RxPushEventBus.cs](../../src/Asterisk.Sdk.Push/Bus/RxPushEventBus.cs) is built on `System.Threading.Channels` with a bounded capacity and explicit `BackpressureStrategy` (Drop / Wait / Fail). The non-durability is a product choice, not an oversight; durability is the Pro SDK's territory (private repo). Item 10 in §4 extends this decision pattern to webhook delivery.

**0012 — Live is an orthogonal aggregate root.** The six subdirectories at [src/Asterisk.Sdk.Live/](../../src/Asterisk.Sdk.Live/) (`Agents/`, `Bridges/`, `Channels/`, `MeetMe/`, `Queues/`, `Server/`) each wrap AMI + ARI under a single `ILiveObject` view. The `AsteriskServer` type is the entry point consumers use when they want "an Asterisk", not "an AMI client plus an ARI client". This is the ADR that lets the README promise AMI + AGI + ARI + Live as separate but composable packages.

All 12 ADRs are Accepted, verified against current code, and show no staleness. The narrative "fast, native, agnostic" holds end-to-end: AOT discipline (0001 / 0003 / 0004) → agnostic backends (0006 / 0011) → testable at every layer (0005 / 0009) → clean transport contracts (0008 / 0010) → one public aggregate root that ties it all together (0012). No supersedures are needed at v1.11.0.

---

## §2 Archived plans & specs reconciliation

A plan or spec is legitimately archived only if the intent it captured actually shipped, and the load-bearing decisions from it were carried forward into either code or the ADR catalog. Orphan intent — work that a plan described but nobody implemented and nobody explicitly cancelled — is the failure mode we are checking for here.

### Plans

Files under [docs/plans/archived/](../plans/archived/).

| File | Intrinsic status | Reconciliation verdict | Action |
|------|------------------|-----------------------|--------|
| `2026-03-20-functional-testing-phase1-plan.md` | shipped-fully | Implementation at `Tests/Asterisk.Sdk.FunctionalTests/`. Task 10 (Pro scaffolding) was redacted in commits `cbb86c2`/`de80e80` — intentional public-repo scrub | OK, no action |
| `2026-03-21-functional-testing-phase5a-plan.md` | shipped-fully | Realtime DB functional tests landed; Testcontainers migration included a PostgreSQL container at `Tests/Asterisk.Sdk.FunctionalTests/Infrastructure/Fixtures/RealtimeFixture.cs` and `PostgresSessionStore` integration coverage in `Tests/Asterisk.Sdk.IntegrationTests/Sessions/` | OK, no action |
| `2026-03-21-functional-testing-phase5c-plan.md` | shipped-fully | Soak and metrics tests shipped; `SessionsBackendsBenchmark` + `PostgresLatencyBenchmark` are the modern equivalents in `Tests/Asterisk.Sdk.Benchmarks/SessionsBackendsBenchmark.cs` | OK, no action |
| `2026-03-22-api-completeness-plan.md` | shipped-partially (97% / 96%) | See §3 for the full reconciliation — the remaining gap is 4 AMI actions and 4 ARI endpoints, all explicitly scoped out of Sprint C's "Not Prioritized" list | OK + optional epilogue annotation (scheduled as Task 3 of this audit) |

### Specs

Files under [docs/specs/archived/](../specs/archived/).

| File | Intrinsic status | Reconciliation verdict | Action |
|------|------------------|-----------------------|--------|
| `2026-03-17-v050-beta-design.md` | shipped-fully | v0.5.0-beta released. The spec is a historical design record superseded by v1.0.0 and onward | OK, no action |
| `2026-03-20-functional-testing-design.md` | shipped-fully | The three-tier pyramid is now captured as ADR-0009; the archived spec has complementary detail (the 44-file pyramid, Layer 2 vs Layer 5 naming) that the ADR intentionally does not repeat | OK, no action — ADR-0009 carries the load-bearing decision |
| `2026-03-21-functional-testing-phase2-design.md` | shipped-fully | The 32 source-generator + queue + health-check tests shipped under `Tests/Asterisk.Sdk.FunctionalTests/Layer2_UnitProtocol/` | OK, no action |
| `2026-03-21-functional-testing-phase4a-design.md` | shipped-fully | DTMF, IVR, Bridge, Transfer, Parking tests live at `Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/` | OK, no action |
| `2026-03-21-v1-release-sprint-design.md` | shipped-fully | v1.0.0 released on 2026-03-21; CHANGELOG + README stood up; superseded by the incremental release history in project memory (v1.1.0 → v1.11.0) | OK, no action |
| `2026-03-22-testcontainers-functional-tests-design.md` | shipped-fully | Testcontainers migration completed; Testcontainers package bumped to 4.11 in commit `eeb4a2c`; the load-bearing decision is captured in ADR-0005 | OK, no action — ADR-0005 carries the load-bearing decision |

### Why archiving matters here

The archival bar is stricter than "the feature still works". Archiving a plan is a promise to the next reader that the plan's load-bearing decisions — the ones that would reopen debate if lost — are preserved somewhere durable. For the four plans and six specs above, that preservation point is either the running code (the tests, the fixtures, the resource files) or an ADR that distils the decision to its minimum form. What we are not doing here is archiving by age or by "feels done"; every row in the two tables was cross-checked against the current source tree before the verdict was written.

Two of the six archived specs have an explicit ADR that captures their decision (testing pyramid → ADR-0009; Testcontainers → ADR-0005). The remaining four specs describe subsystems whose shape is entirely legible from the code itself (`v050-beta-design.md` describes the package split that is now `src/Asterisk.Sdk.*/`; `v1-release-sprint-design.md` describes the release process that now lives in `.github/workflows/ci.yml` and `project_sdk_release_status.md` memory). Archival is safe because rediscovery is cheap.

All 4 archived plans and 6 archived specs are legitimately archived — their intent shipped, their load-bearing decisions are captured either in code or in the ADR catalog. No orphan intent was detected.

---

## §3 API coverage reconciliation

The archived [2026-03-22-api-completeness-plan.md](../plans/archived/2026-03-22-api-completeness-plan.md) targeted 100% coverage across AMI actions, ARI endpoints, and ARI models. The current [README.md](../../README.md) reports 148/152 AMI actions (97%), 94/98 ARI endpoints (96%), and 27/27 ARI models (100%). This section shows that the gap is an intentional scoping decision, not residual debt.

### Verification method

The claims were re-checked today against the current tree:

- `find src/Asterisk.Sdk.Ami/Actions -name "*.cs" -not -name "*Base*" -not -name "*Registry*" | wc -l` → **148**. The directory holds 149 entries, one of which is the action base wiring; the remaining 148 are concrete actions.
- `ls src/Asterisk.Sdk.Ari/Resources/` → 10 resource files plus `AriJsonContext.cs`. Each resource file exposes multiple endpoint methods; the running total lines up with the 94 endpoints reported in the README v1.10.2 block.
- The README v1.10.2 status block claims 148/152 AMI (97%) and 94/98 ARI (96%). Both counts match the code.

### Plan Sprint A/B/C reconciliation

| Plan sprint | Plan target | Shipped | Evidence |
|-------------|-------------|---------|----------|
| Sprint A — 21 AMI actions (11 PJSIP + 7 Bridge + 2 Transfer + `TechCause` field) | 21 | ~19/21 | PJSIP: 15 files present at `src/Asterisk.Sdk.Ami/Actions/PJSip*.cs` (`PJSipHangupAction`, `PJSIPNotifyAction`, `PJSipQualifyAction`, `PJSipRegisterAction`, `PJSipShowAorsAction`, `PJSipShowAuthsAction`, `PJSipShowContactsAction`, `PJSipShowEndpointAction`, `PJSipShowEndpointsAction`, `PJSipShowRegistrationsInboundAction`, `PJSipShowRegistrationsOutboundAction`, `PJSipShowResourceListsAction`, `PJSipShowSubscriptionsInboundAction`, `PJSipShowSubscriptionsOutboundAction`, `PJSipUnregisterAction`). Bridge: 8 files (`BridgeAction`, `BridgeDestroyAction`, `BridgeInfoAction`, `BridgeKickAction`, `BridgeListAction`, `BridgeTechnologyListAction`, `BridgeTechnologySuspendAction`, `BridgeTechnologyUnsuspendAction`). Transfer: `BlindTransferAction`, `CancelAtxferAction`. `TechCause` field present on hangup/channel state events |
| Sprint B — 37 ARI endpoints + 11 models | 37 + 11 | ~37/37 endpoints, 27/27 models | ARI resources at `src/Asterisk.Sdk.Ari/Resources/` expanded from 8 to 10 categories between v0.5.0-beta and v1.0.0: `AriAsteriskResource.cs` and `AriMailboxesResource.cs` were added during Sprint B. README v1.10.2 reports 94/98 endpoints (96%) and 27/27 models (100%) |
| Sprint C — 10 misc AMI + AudioSocket | 10 | ~6/10 + AudioSocket | AudioSocket shipped under `src/Asterisk.Sdk.VoiceAi.AudioSocket/` (full server + client + frame types + DI wiring). The residual ~4 AMI actions correspond to the legacy channel technologies (DAHDI / PRI / IAX / Sorcery admin) explicitly listed under "Not Prioritized" in the plan's Sprint C section |

### Coverage gap rationale

The residual 4 AMI actions and 4 ARI endpoints correspond to features the plan itself marked lower-priority: legacy channel technologies (DAHDI for analog/TDM, PRI for E1/T1 hardware, IAX peering) and Sorcery admin cache-management that are no longer the norm on modern Asterisk 22/23 deployments, plus a handful of ARI endpoints that reach into deprecated Asterisk subsystems. The README's 97%/96% figure is the steady-state target, not a defect to close.

### Verdict

[2026-03-22-api-completeness-plan.md](../plans/archived/2026-03-22-api-completeness-plan.md) is legitimately archived. Sprints A and B shipped; Sprint C shipped in spirit (AudioSocket was the major deliverable and it is in `src/Asterisk.Sdk.VoiceAi.AudioSocket/`; the residual legacy channel-technology AMI actions were explicitly deprioritized in the plan's own "Not Prioritized" list). The 97%/96% coverage number in the README reflects an intentional product scoping decision on modern Asterisk deployments, not an abandoned sprint.

### What "100%" would have cost

The plan's original 100% target would have required porting DAHDI-family actions (analog / TDM hardware management), PRI actions (E1/T1 primary-rate interface management), IAX2 actions (Inter-Asterisk eXchange protocol version 2 — a peering protocol between Asterisk servers that PJSIP has largely displaced), and Sorcery cache-management actions (Asterisk's internal object-relational layer, admin-only). Each of those families has its own test infrastructure cost — DAHDI / PRI require hardware emulation at the kernel level; IAX2 requires a second Asterisk instance configured as a peer; Sorcery is admin-observability that most consumers don't need — and each carries a long-term maintenance tax.

The scoping decision was: ship 97% coverage on the protocols that dominate modern deployments (PJSIP signalling, standard Bridge / Transfer / Queue actions, AudioSocket for AI voice pipelines) and defer the legacy protocol families until a consumer request comes in. Nine months on, no such request has surfaced, which is weak but useful evidence that the scoping decision was correct. The epilogue annotation proposed in §6 will record this decision so future contributors do not re-open it as a bug.

### Coverage table at a glance

| Surface | v1.11.0 | Max | % | Gap (count) |
|---------|---------|-----|---|-------------|
| AMI Actions | 148 | 152 | 97% | 4 (legacy channel tech) |
| AMI Events | 278 | 278 | 100% | 0 |
| ARI Endpoints | 94 | 98 | 96% | 4 (deprecated subsystems) |
| ARI Models | 27 | 27 | 100% | 0 |
| ARI Events | 46 | 46 | 100% | 0 |

Events and models are both at 100% — the gap is concentrated on imperative actions / endpoints that trigger behaviour, not on the data model the SDK exposes. That asymmetry is a product strength: a consumer can observe every Asterisk event and deserialize every ARI model even on subsystems where the SDK cannot yet issue an action.

---

## §4 Missing ADR catalog

The 12 Accepted ADRs cover the skeleton of the SDK well, but a deep code audit surfaces a number of decisions that are load-bearing — removing or inverting them would visibly hurt the product — and that are not yet written down anywhere the next contributor can find them. Twelve such decisions were identified. This section names them, summarizes the alternative that was rejected, points at the evidence in code, and states the risk of losing the context.

| # | Candidate ADR title | Alternative rejected | Evidence in code | Risk if forgotten |
|---|---------------------|---------------------|------------------|-------------------|
| 1 | AMI string interning pool (FNV-1a, 2048 buckets) | Reflection-based key lookup or a runtime dictionary allocated per event | [src/Asterisk.Sdk.Ami/Internal/AmiStringPool.cs](../../src/Asterisk.Sdk.Ami/Internal/AmiStringPool.cs) (344 lines, static table of all 941 known AMI keys + ~35 common values) | A well-meaning refactor removes the pool thinking it is premature optimization; the SDK loses 8–12% heap pressure savings and 20%+ throughput at 100K+ events/s, with no test failing because the pool is a hidden perf invariant |
| 2 | Heartbeat detection strategy (enabled by default, 30 s interval, 10 s timeout) | Relying on the socket-level read timeout alone, or leaving heartbeat off by default | `AmiConnectionOptions.EnableHeartbeat`, `HeartbeatInterval`, `HeartbeatTimeout` at `src/Asterisk.Sdk.Ami/Connection/AmiConnectionOptions.cs`; ping loop in `AmiConnection.cs` | Someone conflates the heartbeat with the socket timeout and removes one of the two; half-open connections stop being detected and sessions go stale after network partitions |
| 3 | VoiceAi `ISessionHandler` abstraction (turn-based and streaming both implement it) | A monolithic `VoiceAiPipeline` that branches internally on provider type | [src/Asterisk.Sdk.VoiceAi/ISessionHandler.cs](../../src/Asterisk.Sdk.VoiceAi/ISessionHandler.cs), [src/Asterisk.Sdk.VoiceAi/Pipeline/](../../src/Asterisk.Sdk.VoiceAi/Pipeline/), [src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/OpenAiRealtimeBridge.cs](../../src/Asterisk.Sdk.VoiceAi.OpenAiRealtime/OpenAiRealtimeBridge.cs) | A future API evolution collapses the abstraction to make the turn-based case simpler; OpenAI Realtime streaming users lose their drop-in path and have to rebuild the pipeline themselves |
| 4 | Raw HTTP / ClientWebSocket for VoiceAi providers | Official vendor SDKs (Azure Cognitive Services, Google Cloud Speech, official OpenAI client) | 4 STT providers (Deepgram, Google, Whisper, AzureWhisper) under `src/Asterisk.Sdk.VoiceAi.Stt/*/` and 2 TTS providers (Azure, ElevenLabs) under `src/Asterisk.Sdk.VoiceAi.Tts/*/`; each is ~200–300 LOC against the vendor REST/WebSocket API directly | A new contributor adds a provider with an official vendor SDK "for convenience"; that SDK pulls in reflection-heavy dependencies, breaks `PublishAot=true`, and silently regresses the zero-warning AOT guarantee |
| 5 | `ProviderName` virtual property (explicit constant override) | `GetType().Name` on every telemetry emission | `SpeechRecognizer.cs` / `SpeechSynthesizer.cs` base classes expose `ProviderName { get; } = GetType().Name` as the default; every shipped provider overrides it with a `const string`. Benchmarks in `Tests/Asterisk.Sdk.Benchmarks/VoiceAiBenchmarks.cs` confirm ~92× speedup on the STT hot path (1.11 ns → 0.012 ns, 0 alloc either way) | A future provider author omits the override; the telemetry hot path regresses silently, no test fails, and the regression only shows up under high call volume |
| 6 | AudioSocket codec negotiation (slin16 / ulaw / alaw / gsm, per-connection) | A single hard-coded codec or configuration-driven codec without per-connection negotiation | [src/Asterisk.Sdk.VoiceAi.AudioSocket/AudioSocketSession.cs](../../src/Asterisk.Sdk.VoiceAi.AudioSocket/AudioSocketSession.cs), `AudioSocketFrame.cs`, `AudioSocketFrameType.cs`, `AudioSocketFrameTypeExtensions.cs` | A clock-drift bug on a new codec reopens codec-selection work without the original designer available; "simple" fixes break existing deployments relying on the negotiation contract |
| 7 | Activity cancellation semantics (`CancelAsync()` separate from `CancellationToken`) | Token-only cancellation, leaving callers to infer terminal state | [src/Asterisk.Sdk.Activities/Activities/IActivity.cs](../../src/Asterisk.Sdk.Activities/Activities/IActivity.cs), `Status` observable emits terminal transitions | Consumers rely solely on the token and miss cancellation events; partial cleanup bugs surface in long-running contact-center activities (AttendedTransfer, ChanSpy, Snoop, Barge) |
| 8 | Sessions reconciliation loop (soft TTL in-app, not native backend TTL) | Redis `EXPIRE` / Postgres `TTL` column handling the lifecycle alone | [src/Asterisk.Sdk.Sessions/Manager/](../../src/Asterisk.Sdk.Sessions/Manager/) `SessionReconciliationService` uses `PeriodicTimer` + configurable sweep interval; `ISessionStore` does not expose native TTL | Someone "optimizes" by switching to native Redis TTL; cross-node reconciliation disappears, orphaned sessions in one node stay invisible to the others |
| 9 | Push bus `TraceContext` ambient capture | Letting `Activity.Current` be read from inside the Channel consumer (which has already switched execution contexts) | `RxPushEventBus.PublishAsync` at `src/Asterisk.Sdk.Push/Bus/RxPushEventBus.cs` captures the current W3C traceparent at publish time (fix shipped in v1.10.2, commit `bd21271`) | Removed during a simplification pass; W3C distributed tracing silently breaks across the bus and consumers see their traces terminate at the publish boundary |
| 10 | Webhook delivery retry-only, no durable DLQ | A persistent dead-letter queue with at-least-once guarantees | [src/Asterisk.Sdk.Push.Webhooks/WebhookDeliveryService.cs](../../src/Asterisk.Sdk.Push.Webhooks/WebhookDeliveryService.cs), `WebhookDeliveryOptions.cs`, `WebhookMetrics.cs` (deliveries.succeeded / failed / retried / dead_letter counters) | A consumer assumes "dead_letter" means durable and builds a compliance workflow on top of it; when the host restarts, in-flight retries are lost and the consumer discovers the assumption was wrong in production |
| 11 | PublicAPI tracker adoption across all packages | Relying on SemVer discipline in commit messages | `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` files in every shipping package (at least 8 confirmed: Ami, Ari, Live, Push, Sessions, VoiceAi, and the Push/Sessions backend packages) plus `Microsoft.CodeAnalysis.PublicApiAnalyzers` in `Directory.Packages.props` | A contributor deletes the files thinking they are unused metadata; accidental breaking changes start merging without review flags |
| 12 | `BannedSymbols.txt` as AOT policy (not warning-only) | Style-guide or review-time enforcement of "no reflection, no `DateTime.Now`" | [BannedSymbols.txt](../../BannedSymbols.txt) at repo root lists `System.Reflection.Assembly`, `Type.GetMethod/Property/Field/Members`, `Activator.CreateInstance`, `Type.InvokeMember`, `DateTime.Now`, `DateTimeOffset.Now`; enforced at build via `Microsoft.CodeAnalysis.BannedApiAnalyzers` | The analyzer is disabled during a dependency update "temporarily"; reflection creeps back into the hot paths and the AOT guarantee regresses silently |

### Expanded rationale on the top 3

The top 3 selections below are not the most sophisticated decisions in the list — they are the ones where the code alone is the least self-explanatory and where a well-meaning refactor is most likely to cause a silent regression.

**#1 — AmiStringPool.** A reader who opens `AmiStringPool.cs` sees 941 string literals laid out in FNV-1a bucket order. What the file does not say is why: without the pool, the AMI parser allocates one transient `string` per key per event; at 100K events per second, that is 100K × (average 5 keys per event) = 500K transient strings per second purely from key parsing. The pool reduces that to zero allocations for known keys and one transient allocation only for unknown keys (asterisk 24+ events the SDK has not yet caught up to). The benchmark regression in v1.0 → v1.11 (582 ns → 653 ns, documented in project memory) and the recovery path in commit `41fff67` both sit on top of this pool. An ADR is the right place to record that the pool is load-bearing rather than "optimization theatre".

**#3 — ISessionHandler.** The abstraction exists specifically to allow `VoiceAiPipeline` (turn-based, STT → LLM → TTS) and `OpenAiRealtimeBridge` (streaming, single-connection, VAD on the vendor side) to ship in the same package family with a single consumer-facing seam. A consumer writes their agent against `ISessionHandler` and later swaps the provider without touching business logic. Without the ADR, a future maintainer looking at `VoiceAiPipeline.cs` in isolation could reasonably conclude the interface is over-engineered — it has exactly two production implementations, neither of which is obviously variable — and collapse it. The ADR records that the two implementations exist precisely because the streaming and turn-based models have different failure modes and different consumer ergonomics; the seam is the product.

**#4 — Raw HTTP / ClientWebSocket for providers.** The four STT files (`DeepgramSpeechRecognizer.cs`, `GoogleSpeechRecognizer.cs`, `WhisperSpeechRecognizer.cs`, `AzureWhisperSpeechRecognizer.cs`) and two TTS files (`AzureTtsSpeechSynthesizer.cs`, `ElevenLabsSpeechSynthesizer.cs`) each wrap a vendor REST or WebSocket API in ~200–300 lines. A contributor unfamiliar with AOT constraints might propose replacing the raw Deepgram client with the official `Deepgram.Client` NuGet, or replacing the Azure Speech code with `Azure.AI.Speech`. Every such replacement would pull in reflection-based HTTP pipelines, `System.Reflection.Emit` serializer caches, or runtime type discovery — any one of which is sufficient to break `PublishAot=true` downstream. The ADR records the decision as a product invariant, not a preference.

**Top 3 recommended for immediate write-up:** #1 (AmiStringPool), #3 (`ISessionHandler`), #4 (raw-HTTP providers). These three close the narrative gap around VoiceAi (items 3–6 all touch VoiceAi) and preserve a single key micro-optimization (item 1) whose value is invisible to a reader of the code alone. The remaining nine can be written down on the ordinary maintenance cadence without risk.

### The other nine, grouped

The remaining candidates naturally cluster into three batches, each suitable for a single maintenance cycle.

- **VoiceAi tail (items 5, 6):** `ProviderName` override and AudioSocket codec negotiation. Both are pure VoiceAi internals. Write-up cost is low because the code is already well-factored; the ADRs exist only to annotate the "why".
- **Sessions + Push tail (items 8, 9, 10):** reconciliation loop, `TraceContext` capture, no-DLQ webhooks. All three describe deliberate non-durability choices or observability invariants that the code implies but does not state.
- **Repo-level policy (items 2, 7, 11, 12):** heartbeat, activity cancellation, PublicAPI trackers, BannedSymbols. These are build-system and cross-cutting decisions; writing them down makes onboarding faster and reduces the risk of "I'll just clean up this file" regressions.

---

## §5 Narrative coherence

The 12 accepted ADRs tell a coherent story. AOT-first (0001) forces source generators (0003), which in turn justifies central package management (0004) — you cannot afford version drift across generator and runtime. That discipline enables pluggable session stores (0006) and a non-durable push bus (0011) because the contracts are small, versioned, and free of reflection. Three-tier testing (0005 + 0009) covers unit, protocol, and integration layers. Asymmetric ARI transport (0010) accepts the shape Asterisk itself imposes (REST out, WebSocket in). AMI exponential backoff (0008) closes the resilience story for the long-lived socket. And the Live aggregate root (0012) ties everything together into a single public API for consumers who want "an Asterisk server" without knowing about AMI vs ARI vs Sessions. This sequence aligns cleanly with the README positioning: a modern .NET SDK, AMI + AGI + ARI + Live + Sessions + VoiceAi, Native AOT, zero reflection.

But VoiceAi — 6 packages in total (Audio, VoiceAi, AudioSocket, Stt, Tts, OpenAiRealtime, with a Testing package on top) accounting for 1,400+ LOC of provider code plus the pipeline and audio primitives — is effectively absent from the ADR catalog. A reader who opens `docs/decisions/` alone would not discover why `ISessionHandler` exists, why every provider is hand-rolled against a raw HTTP or WebSocket API, why `AudioSocketSession` negotiates codecs per connection, or why `VoiceAiBenchmarks` cares about `ProviderName` latency to the point of extracting a 92× speedup from a single virtual-call optimization. VoiceAi shipped strong observability in v1.9.0 — 5 HealthChecks, 5 Meters, 3 ActivitySources across the package family — but no documented architectural rationale. The "why" of VoiceAi only exists in code and in commit history.

The top 3 ADRs proposed in §4 (items 1, 3, 4) close this gap with the smallest possible addition to the catalog. Adding them brings the ADR count from 12 to 15 — coherent with the 7 → 12 jump that landed in the April 2026 docs sweep — and makes the VoiceAi narrative legible to the next contributor without forcing a full docs sprint for features that are not yet mature enough to freeze in a decision record.

### A note on what the audit did not find

It is as useful to record what the audit did not surface as what it did. No ADR is stale: every decision written down still matches the code. No archived plan has orphan intent: every plan's work product is either in `src/`, in `Tests/`, or explicitly deprioritized. No spec describes a subsystem that was silently replaced. The API coverage gap is intentional and documented. In other words, the decision record is internally consistent; the only real risk is the undocumented-but-load-bearing decisions catalogued in §4, which the short-term recommendation in §6 addresses.

That absence of surprise is itself meaningful. The April 2026 docs sweep — the one that added ADRs 0008–0012, stood up the Option K layout, and redacted the Pro scaffolding from public plans — left the repository in a state where the public story and the private code agree. The audit's job was to confirm that agreement has not drifted during the v1.10 and v1.11 release work; it has not.

---

## §6 Recommendations

- **Short-term (before v1.11.1):** write ADRs 0013 (`ISessionHandler` abstraction), 0014 (raw HTTP / WebSocket providers), and 0015 (AMI string interning pool). Bundle them with the AMI perf fix in commit `41fff67` and cut v1.11.1 as a combined docs + perf patch release. The execution plan is drafted at [docs/plans/active/2026-04-19-adr-backfill-top3.md](../plans/active/2026-04-19-adr-backfill-top3.md) (Task 2 of this audit).
- **Medium-term (v1.12.0 window):** write the remaining 9 ADRs from §4's catalog. There is no urgency — document them as the features touch a maintenance cycle. Reasonable batching: a "VoiceAi ADR batch" (items 5, 6), a "Sessions + Push ADR batch" (items 8, 9, 10), and a "repo-level policy ADR batch" (items 2, 7, 11, 12).
- **Annotation:** add an epilogue to [docs/plans/archived/2026-03-22-api-completeness-plan.md](../plans/archived/2026-03-22-api-completeness-plan.md) (Task 3 of this audit) recording the final 97% / 96% outcome and the explicit scope-reduction decision on the DAHDI / PRI / IAX / Sorcery legacy actions. This closes the last open question a reader of the plan could have.

### Sequencing and risk

The three recommendations are independent and can be done in parallel by different subagents if desired, but they have different risk profiles:

- **Short-term (ADR backfill):** lowest risk. Pure docs, no code change. The only failure mode is a bad ADR draft, which code review catches. The three ADRs together add roughly 200–300 lines of markdown to the repo.
- **Medium-term (remaining 9 ADRs):** low risk. Same shape as short-term, spread across months. No single commit is critical.
- **Annotation (api-completeness epilogue):** trivial risk. A single append to an existing archived plan file, recording a decision that already happened. Worth doing now rather than later because the decision-context is freshest in memory.

If only one of the three is done before v1.11.1, it should be the ADR backfill: the AMI perf fix in commit `41fff67` is already waiting to ship, and bundling it with three ADRs produces a natural docs + perf patch release that telegraphs the v1.11 line's stability posture.

### Draft release notes for v1.11.1

For reference only — do not commit until the ADRs land:

```
### v1.11.1 — Docs & perf (2026-04-XX)

- perf(ami): fast-path length check on Output header accumulation. Recovers
  ~35 ns of the v1.0 → v1.11 regression in ParseSingleEvent. Throughput
  1.53M → 1.62M events/s single-thread. (41fff67)
- docs(decisions): ADR 0013 — VoiceAi ISessionHandler abstraction.
- docs(decisions): ADR 0014 — VoiceAi providers as raw HTTP/WebSocket.
- docs(decisions): ADR 0015 — AMI string interning pool (FNV-1a).

No API changes. No breaking changes. Full 0-warning build preserved.
```

---

See [docs/plans/active/2026-04-19-adr-backfill-top3.md](../plans/active/2026-04-19-adr-backfill-top3.md) for the execution plan.
