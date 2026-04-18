# Changelog

All notable changes to this project will be documented in this file.

## [Unreleased] — 1.11.0 — Pluggable session backends + infra

### Added

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
