# Changelog

All notable changes to this project will be documented in this file.

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
