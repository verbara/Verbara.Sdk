# Changelog

All notable changes to this project will be documented in this file.

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
