# Asterisk.Sdk — Functional Testing Suite Design

**Date:** 2026-03-20
**Status:** Approved
**Scope:** Comprehensive functional testing for MIT and Pro repos
**Repos:** MIT (`Asterisk.Sdk`) + Pro (`Asterisk.Sdk.Pro`)

---

## 1. Goal

Build a reproducible, realistic, self-contained functional testing suite that validates the entire Asterisk.Sdk stack — from protocol parsing to full Voice AI pipelines — without requiring external API keys or infrastructure. Tests must run identically on any machine with Docker installed.

## 2. Problem

Current testing covers unit tests (878+ MIT, 520+ Pro) and basic integration tests against Asterisk in Docker. However:

- No reconnection/failover testing (the #1 production failure mode)
- No concurrent operations testing (race conditions in ConcurrentDictionary/Lock patterns)
- No graceful shutdown testing (5 IHostedService implementations)
- No queue behavior testing (backbone of call centers)
- No real SIP call flow testing (DTMF, media, recording)
- No network partition testing (TCP half-open, heartbeat timeout)
- No Voice AI E2E with real audio (AudioSocket → STT → Handler → TTS)
- No PbxAdmin component testing
- No multi-Asterisk-version compatibility testing
- No stress/quality testing (packet loss, jitter, audio quality)
- No AOT trim verification in CI

## 3. Architecture

### 3.1 Test Pyramid (7 layers, 24 test areas)

```
          ┌─────────────┐
          │  7. Stress   │  SIPp load, tc netem, audio quality
          │   & Quality  │  (Docker + netem)
          ├─────────────┤
          │  6. E2E      │  pjsua → SIP → Asterisk → AudioSocket
          │  Full Stack  │  → Whisper.cpp → Handler → Piper TTS
          ├─────────────┤  (Docker Compose, ~60s)
          │  5. Integration   │  Real Asterisk: AMI events, AGI,
          │  (Asterisk)       │  ARI, Live, Sessions, Queues,
          │                   │  Reconnection, Cluster, Failover
          ├───────────────────┤  (Docker Compose, ~30s)
          │  4. Functional (pipeline)    │  AudioSocket test client
          │                              │  + eSpeak/Piper audio
          │                              │  + mock STT
          ├──────────────────────────────┤  (Docker optional, ~5s)
          │  3. Contract (replay)                │  Recorded API
          │                                      │  responses replayed
          ├──────────────────────────────────────┤  (no deps, <1s)
          │  2. Unit (protocol)                          │  AudioSocket
          │                                              │  frames, AMI
          │                                              │  parsing, ARI JSON
          ├──────────────────────────────────────────────┤  (no deps, <1s)
          │  1. Unit (text)                                      │  Handlers,
          │                                                      │  state machines,
          │                                                      │  providers
          └──────────────────────────────────────────────────────┘  (no deps, <1s)
```

### 3.2 Project Structure

```
MIT repo:
  Tests/
    Asterisk.Sdk.FunctionalTests/           ← New project
      Layer1_UnitText/
      Layer2_UnitProtocol/
      Layer3_Contract/
      Layer4_FunctionalPipeline/
      Layer5_Integration/
      Layer6_E2E/
      Layer7_StressQuality/
      Infrastructure/
        AsteriskFixture.cs                  ← Enhanced (Testcontainers)
        AudioSocketTestClient.cs            ← TCP client for AudioSocket
        AudioSocketTestServer.cs            ← Server wrapper with dynamic port
        TestAudio.cs                        ← Generate/load test audio
        AudioMetrics.cs                     ← Cross-correlation, SNR, RMS
        PjsuaClient.cs                      ← Wrapper for pjsua Docker
        WhisperClient.cs                    ← HTTP client for whisper.cpp
        PiperClient.cs                      ← HTTP client for Piper TTS
        LogCapture.cs                       ← InMemory ILogger for assertions
        MetricsCapture.cs                   ← InMemory metrics exporter
        ToxiproxyFixture.cs                 ← Network fault injection
        DockerControl.cs                    ← Kill/restart containers
      Fixtures/
        deepgram-transcript.json
        elevenlabs-audio.bin
        openai-realtime-session.json
      Audio/
        librispeech-clip-01.pcm             ← 3-5s, 8kHz, 16-bit mono
        librispeech-clip-02.pcm
        sine-440hz-1s.pcm
        dtmf-sequence-123.pcm
  docker/
    functional/
      docker-compose.functional.yml
      asterisk-config/
        manager.conf
        ari.conf
        http.conf
        pjsip.conf
        extensions.conf
        queues.conf
        confbridge.conf
        cdr_manager.conf
        prometheus.conf
        modules.conf
      pjsua-scenarios/
        make-call.sh
        answer-call.sh

Pro repo:
  tests/
    Asterisk.Sdk.Pro.FunctionalTests/       ← New project
      Layer1_UnitText/                      ← Pro handlers/providers
      Layer5_Integration/                   ← Cluster, Dialer, Routing
      Layer6_E2E/                           ← AgentAssist, CallAnalytics
      Infrastructure/
        ClusterFixture.cs                   ← 2-3 Asterisk containers
        DialerFixture.cs                    ← Campaign test helpers
  docker/
    functional/
      docker-compose.functional.yml         ← Extends MIT compose
```

### 3.3 Docker Compose Stack

```yaml
# docker/functional/docker-compose.functional.yml
services:
  asterisk:
    image: andrius/asterisk:21-alpine
    ports: [5038, 4573, 8088]
    volumes:
      - ./asterisk-config:/etc/asterisk
      - recordings:/var/spool/asterisk/monitor
    healthcheck:
      test: asterisk -rx "core show version"
      interval: 5s
      retries: 10

  pjsua:
    build:
      context: ./pjsua
      dockerfile: Dockerfile.pjsua
    depends_on:
      asterisk: { condition: service_healthy }
    command: --null-audio --auto-answer 200
    volumes:
      - test-audio:/audio
    # Note: Custom Dockerfile based on alpine + pjproject build.
    # Supports --play-file, --rec-file, --null-audio for headless testing.

  whisper:
    image: onerahmet/openai-whisper-asr-webservice:latest
    environment:
      ASR_MODEL: tiny.en
      ASR_ENGINE: faster_whisper
    ports: [9000]

  piper:
    image: rhasspy/piper:latest
    entrypoint: ["python3", "-m", "http.server", "10200"]
    # Note: Piper CLI generates WAV files. For HTTP, use a thin wrapper
    # or call piper CLI from TestAudio helper via docker exec.
    ports: [10200]

  sipp:
    image: ctaloi/sipp:latest
    depends_on:
      asterisk: { condition: service_healthy }
    network_mode: service:asterisk
    # Scenarios mounted at runtime by tests

  espeak:
    image: alpine:latest
    command: sleep infinity
    # eSpeak-NG installed at build time for audio generation
    # Tests call: docker exec espeak espeak-ng -w /audio/test.wav "text"
    volumes:
      - test-audio:/audio

  toxiproxy:
    image: ghcr.io/shopify/toxiproxy:latest
    ports: [8474, 15038, 18088]  # API, proxied AMI, proxied ARI
    # ToxiproxyFixture creates proxies on startup:
    #   ami_proxy: listen :15038 → asterisk:5038
    #   ari_proxy: listen :18088 → asterisk:8088
    # Tests connect to toxiproxy ports instead of Asterisk directly.
    # Then inject toxics: latency, bandwidth, timeout, reset_peer.

  postgres:
    image: postgres:16-alpine
    environment:
      POSTGRES_DB: functional_tests
      POSTGRES_USER: test
      POSTGRES_PASSWORD: test
    ports: [5432]

  redis:
    image: redis:7-alpine
    ports: [6379]

  prometheus:
    image: prom/prometheus:latest
    volumes:
      - ./prometheus.yml:/etc/prometheus/prometheus.yml
    ports: [9090]

volumes:
  recordings:
  test-audio:
```

## 4. Test Areas by Phase

### Phase 1: Critical (ship-blocking)

#### 4.1 Reconnection Testing
**Location:** MIT `Layer5_Integration/Reconnection/`

Tests:
- Kill Asterisk container mid-stream → verify SDK transitions to `Reconnecting` state
- Verify reconnect succeeds → state goes back to `Connected`
- Verify `Reconnected` event fires on `IAmiConnection`
- Verify `AsteriskServer` reloads channels/queues/agents after reconnect (state recovery)
- Verify in-flight `SendActionAsync` calls receive `OperationCanceledException` or timeout, not hang
- Verify pending `TaskCompletionSource` correlations are cleaned up
- Test exponential backoff timing (1s, 2s, 4s, ...)
- Test max reconnect attempts

Tools: Testcontainers (programmatic container kill/restart)

#### 4.2 Concurrent Operations Testing
**Location:** MIT `Layer5_Integration/Concurrency/`

Tests:
- 50+ concurrent `SendActionAsync` calls → verify all get correct correlated responses (no cross-talk)
- Concurrent subscribe/unsubscribe while events are flowing
- Concurrent `AsteriskServer` state updates (100 channel events hitting managers simultaneously)
- Multiple AGI sessions arriving on `FastAgiServer` at once
- Concurrent ARI WebSocket messages being deserialized
- `AsyncEventPump` under heavy load — verify no event loss when capacity is reached
- `ChannelManager` concurrent create/update/delete with secondary indices

Tools: `Task.WhenAll`, `SemaphoreSlim`, `Barrier`

#### 4.3 Graceful Shutdown Testing
**Location:** MIT `Layer5_Integration/Shutdown/`

Tests:
- Start `IHost` with all services, call `StopAsync()` → verify all connections close cleanly
- Verify `AmiConnection.DisposeAsync` releases TCP socket
- Verify `FastAgiServer` stops accepting new connections, drains active sessions
- Verify `AudioSocketServer` stops listener
- Verify cancellation tokens propagate through the chain
- Verify shutdown completes within default timeout (5 seconds)
- Verify no orphaned tasks after shutdown

Tools: `Microsoft.Extensions.Hosting` test host

#### 4.4 Security Basics
**Location:** MIT `Layer5_Integration/Security/`

Tests:
- AMI login with wrong credentials → clean error, no crash, no hang
- AMI login with wrong MD5 challenge → failure reported cleanly
- ARI request with invalid credentials → HTTP 401 handled
- AMI connection to port that refuses → timeout, not hang
- Verify SDK does not log passwords in plain text
- AMI action field injection (newlines in header values) → protocol writer escapes correctly

Tools: xUnit, no special infrastructure

#### 4.5 Event Ordering
**Location:** MIT `Layer5_Integration/EventOrdering/`

Tests:
- `HangupEvent` arriving before `NewChannelEvent` → no phantom channels in `ChannelManager`
- `LinkedIdEndEvent` before all channel events processed → `SessionEngine` handles gracefully
- Rapid-fire events from multiple channels → verify `ChannelManager` and `QueueManager` consistency
- Out-of-order bridge events → verify `BridgeActivity` state machine handles correctly

Tools: Mock AMI event sequence injection

#### 4.6 Backpressure Testing
**Location:** MIT `Layer2_UnitProtocol/Backpressure/` (AsyncEventPump tests) + MIT `Layer5_Integration/Backpressure/` (PipelineSocketConnection tests)

Tests:
- `AsyncEventPump` at capacity → verify `OnEventDropped` callback fires (Layer 2, no deps)
- `AsyncEventPump` drop metrics increment correctly (Layer 2, no deps)
- `PipelineSocketConnection` with slow consumer → verify backpressure thresholds trigger (Layer 5, needs TCP)
- Pipeline memory pool doesn't leak under sustained backpressure (Layer 5, needs TCP)

Tools: Controlled event injection, `Channel<T>` at capacity, TcpClient for pipeline tests

### Phase 2: Pre-v1.0

#### 4.7 Queue Behavior
**Location:** MIT `Layer5_Integration/Queues/`

Tests:
- `QueueAddAction` / `QueueRemoveAction` → `QueueManager` state updates
- Queue member login/logout → events fire, Live API reflects changes
- Queue statistics (`QueueStatusAction`) → counters (waiting, logged in, completed, abandoned)
- Member penalty changes → `QueueManager` tracks correctly
- Queue pause/unpause → agent state transitions
- SIPp call enters queue, agent answers → full event sequence verified

Tools: Asterisk with `queues.conf`, SIPp

Asterisk config for queues:
```ini
; queues.conf
[test-queue]
strategy = ringall
timeout = 15
wrapuptime = 0
joinempty = yes
leavewhenempty = no
```

#### 4.8 Health Checks
**Location:** MIT `Layer5_Integration/HealthChecks/`

Tests:
- `AmiHealthCheck` returns `Healthy` when connected
- `AmiHealthCheck` returns `Unhealthy` when disconnected
- `AmiHealthCheck` returns `Degraded` during reconnection
- Health check under high load (doesn't timeout due to contention)
- `AriHealthCheck` when WebSocket drops but HTTP still up
- `AgiHealthCheck` when server is listening vs stopped

Tools: xUnit, Testcontainers for disconnect scenarios

#### 4.9 CDR / CEL
**Location:** MIT `Layer5_Integration/Cdr/`

Tests:
- Make call via `OriginateAction`, let it complete → `CdrEvent` received with correct fields
- Verify `CelEvent` sequence (CHAN_START, ANSWER, HANGUP, CHAN_END, LINKEDID_END)
- Verify CDR `billsec`, `duration`, `disposition` fields are accurate
- With realtime DB: verify CDR rows written to PostgreSQL

Asterisk config:
```ini
; cdr_manager.conf
[general]
enabled = yes

[mappings]
; Include all fields in AMI CDR events
```

#### 4.10 AOT Trim CI
**Location:** CI pipeline (not a test project)

Verification:
```bash
dotnet publish src/Asterisk.Sdk.Hosting/ -r linux-x64 -c Release \
  --self-contained -p:PublishAot=true 2>&1 | grep -i "warning"
# Must exit 0 with 0 warnings
```

Add as CI gate that fails on any trim/AOT warning.

#### 4.11 Source Generator Tests
**Location:** MIT `Tests/Asterisk.Sdk.Ami.SourceGenerators.Tests/` (existing, expand)

Tests:
- Verify generated code compiles for all 111 actions
- Verify generated code compiles for all 215 events
- Verify edge cases: events with no fields, actions with special characters
- Verify new unknown event types are handled gracefully (tolerant reader)

### Phase 3: v1.0 Hardening

#### 4.12 Network Partition
**Location:** MIT `Layer5_Integration/NetworkPartition/`

Tests:
- Add 30-second latency to AMI port via Toxiproxy → verify heartbeat timeout triggers reconnect
- Cut TCP entirely (Toxiproxy `down`) → verify SDK doesn't hang forever
- 50% packet loss → verify action timeouts work correctly
- Bandwidth throttle → verify pipeline backpressure handles slow reads
- Restore connectivity → verify clean reconnect

Tools: Toxiproxy + Testcontainers

#### 4.13 Multi-Server / Cluster
**Location:** Pro `Layer5_Integration/Cluster/`

Tests:
- Connect to 2 Asterisk servers simultaneously via `AsteriskServerPool`
- Verify agent routing table routes queries to correct server
- Kill one server → pool degrades gracefully (no crash)
- Dead server returns → pool reconnects and reloads
- Weighted routing distributes load correctly
- `ClusterManager` with Redis transport → verify node registry syncs
- Drain one node → verify calls migrate to remaining nodes

Docker: 2-3 Asterisk containers + Redis

#### 4.14 Asterisk Version Compatibility
**Location:** CI matrix

Run full integration suite against:
- Asterisk 18 (LTS, widely deployed)
- Asterisk 20 (current certified)
- Asterisk 21 (current LTS)
- Asterisk 22 (latest)

Verify:
- Event deserialization handles unknown fields (tolerant reader)
- Actions that exist in newer versions fail gracefully on older ones
- No version-specific crashes

### Phase 4: Post-v1.0

#### 4.15 DTMF
**Location:** MIT `Layer5_Integration/Dtmf/`

Tests:
- SIPp sends DTMF digits → `DtmfEvent` arrives via AMI with correct digit and duration
- All digits (0-9, *, #, A-D)
- Both RFC 2833 and SIP INFO methods

Tools: SIPp with DTMF scenarios

#### 4.16 Conference (ConfBridge)
**Location:** MIT `Layer5_Integration/Conference/`

Tests:
- Create conference, 2 participants join → `ConfbridgeJoinEvent` for each
- Participant leaves → `ConfbridgeLeaveEvent`
- Mute/unmute via AMI → events fire
- Conference ends when last participant leaves
- `BridgeActivity` state machine transitions match

Tools: SIPp (multiple instances), Asterisk with `confbridge.conf`

#### 4.17 Recording / MixMonitor
**Location:** MIT `Layer5_Integration/Recording/`

Tests:
- Start recording via `MonitorAction`, make call with SIPp + audio → stop recording
- Verify file exists on disk and is non-zero size
- Verify `MixMonitorStartEvent` and `MixMonitorStopEvent` fire
- Verify audio contains actual content (RMS energy > threshold)

Tools: SIPp with RTP audio, shared Docker volume

#### 4.18 PbxAdmin (bUnit)
**Location:** MIT `Tests/PbxAdmin.Tests/` (existing, expand with bUnit)

Tests:
- Razor pages render correctly with mock services
- Form validation (extension CRUD, trunk CRUD, queue config)
- Real-time data binding (channel list updates when AMI events arrive)
- Error states (disconnected server, failed operations)

Tools: bUnit (NuGet), no browser needed

#### 4.19 Realtime Database
**Location:** MIT `Layer5_Integration/Realtime/`

Tests:
- Create PJSIP endpoint via database INSERT → Asterisk loads it
- Modify queue member via database → AMI reports change after reload
- Compare behavior: same operations on realtime vs file-based Asterisk

Tools: PostgreSQL container, existing `Dockerfile.asterisk-realtime`

#### 4.20 IVR / Dialplan
**Location:** MIT `Layer5_Integration/Ivr/`

Tests:
- Originate call to IVR, send DTMF via SIPp → verify AMI events reflect correct path
- Multi-level IVR traversal (press 1, then 2) → `AsteriskChannel` tracks context/exten changes
- Verify `NewExtenEvent` sequence matches dialplan flow

Tools: SIPp with DTMF scenarios

### Phase 5: Maturity

#### 4.21 Memory Leak / Soak Tests
**Location:** Separate console app or CI job

Tests:
- 10K event processing cycles → GC Gen2 collections stay flat
- Repeated connect/disconnect → socket handles don't leak
- `PipelineSocketConnection` memory pool returns all buffers
- `AsyncEventPump` channel doesn't grow unbounded

Tools: dotMemory Unit, dotnet-counters

#### 4.22 Metrics Cross-Validation
**Location:** MIT `Layer5_Integration/Metrics/`

Tests:
- After call, verify SDK's `live.channels.active` matches Asterisk's `asterisk_channels_count`
- Verify `ami.events.received` counter increments correctly
- Verify `ami.actions.roundtrip` histogram has reasonable values

Tools: HTTP client scraping Asterisk `res_prometheus`, Prometheus text parser

#### 4.23 Voicemail
**Location:** MIT `Layer5_Integration/Voicemail/` (if SDK supports it)

Tests:
- Call unanswered extension → lands in voicemail → `VoicemailEvent` fires
- MWI events for message waiting indicator

#### 4.24 Playwright E2E (PbxAdmin)
**Location:** MIT `Tests/PbxAdmin.E2E.Tests/`

Tests:
- Login flow, navigate, create extension, verify
- Real-time dashboard updates during live call
- Multi-server switching in UI

Tools: Playwright for .NET

## 5. Voice AI Pipeline Testing (Layers 4-6)

### 5.1 Audio Sources (all self-contained, no API keys)

| Source | Use | Format | Size | License |
|--------|-----|--------|------|---------|
| Sine wave (C# generated) | Protocol/frame tests | PCM 8kHz 16-bit | bytes | N/A |
| eSpeak-NG (Docker) | Fast deterministic speech | PCM/WAV | <5MB engine | GPL-3.0 |
| Piper TTS (Docker) | Realistic human speech | PCM/WAV | ~100MB model | MIT |
| LibriSpeech clips | Known-transcript STT validation | FLAC→PCM | ~200KB (5-10 clips) | CC BY 4.0 |
| Asterisk sound files | In-call Playback() injection | slin/ulaw | Built into image | CC BY-SA 3.0 |

### 5.2 STT Options (all local, no API keys)

| Engine | Docker Image | Model Size | Speed (40s audio) | Use |
|--------|-------------|-----------|-------------------|-----|
| whisper.cpp tiny.en | ghcr.io/ggml-org/whisper.cpp | 75MB | ~3s CPU | E2E tests |
| Fake (SDK built-in) | N/A | 0 | <1ms | Unit/functional |
| Recorded replay | N/A | fixture files | <1ms | Contract tests |

### 5.3 E2E Voice Pipeline Test Flow

```
┌──────────┐     SIP/RTP     ┌──────────┐   AudioSocket  ┌──────────────┐
│  pjsua   │ ──────────────→ │ Asterisk │ ─────────────→ │ Our SDK      │
│  (caller)│ ←────────────── │   PBX    │ ←───────────── │ AudioSocket  │
│  plays   │     SIP/RTP     │          │   AudioSocket  │ Server       │
│  WAV     │                 │ ext 400  │                │              │
│  records │                 │ AudioSkt │                │  ┌─────────┐ │
│  response│                 │ app      │                │  │whisper  │ │
└──────────┘                 └──────────┘                │  │.cpp STT │ │
                                                        │  └────┬────┘ │
                                                        │       │      │
                                                        │  ┌────▼────┐ │
                                                        │  │ Handler │ │
                                                        │  └────┬────┘ │
                                                        │       │      │
                                                        │  ┌────▼────┐ │
                                                        │  │ Piper   │ │
                                                        │  │ TTS     │ │
                                                        │  └─────────┘ │
                                                        └──────────────┘
```

### 5.4 Audio Quality Verification

```csharp
// Cross-correlation: verify audio passes through pipeline intact
public static class AudioMetrics
{
    public static double CrossCorrelate(ReadOnlySpan<short> reference, ReadOnlySpan<short> test);
    public static double RmsEnergy(ReadOnlySpan<short> pcm);
    public static double SignalToNoiseRatio(ReadOnlySpan<short> signal, ReadOnlySpan<short> noise);
}
```

## 6. Infrastructure Helpers

### 6.1 New Helper Classes (MIT)

| Class | Purpose |
|-------|---------|
| `AudioSocketTestClient` | TcpClient that speaks AudioSocket protocol (send UUID, send PCM, read responses) |
| `AudioSocketTestServer` | Wrapper of real server with dynamic port allocation |
| `TestAudio` | Generate sine waves, load PCM files, call eSpeak/Piper for speech generation |
| `AudioMetrics` | Cross-correlation, RMS energy, SNR measurement |
| `PjsuaClient` | Docker wrapper: make calls, play files, record responses, send DTMF |
| `WhisperClient` | HTTP client for whisper.cpp container (POST audio → GET transcript) |
| `PiperClient` | HTTP client for Piper TTS container (POST text → GET PCM audio) |
| `LogCapture` | `InMemoryLoggerProvider` for asserting on log entries |
| `MetricsCapture` | `InMemoryExporter` for `System.Diagnostics.Metrics` assertions |
| `ToxiproxyFixture` | Toxiproxy API client for network fault injection |
| `DockerControl` | Testcontainers wrapper: kill, restart, pause containers |

### 6.2 New Helper Classes (Pro)

| Class | Purpose |
|-------|---------|
| `ClusterFixture` | Spin up 2-3 Asterisk containers for cluster tests |
| `DialerFixture` | Campaign/contact test data helpers |
| `QueueFixture` | Queue configuration and member management helpers |

### 6.3 Test Traits/Attributes

```csharp
[AsteriskAvailableFact]     // Skip if Asterisk not reachable (existing)
[E2EFact]                   // Skip unless E2E environment is up
[StressFact]                // Skip unless stress testing enabled
[RequiresDocker]            // Skip if Docker not available
[RequiresToxiproxy]         // Skip if Toxiproxy not available
[AsteriskVersion(">=20")]   // Skip if Asterisk version doesn't match
```

## 7. Observability During Tests

Every functional test automatically captures:

- **Logs** — `InMemoryLoggerProvider` captures all `ILogger` output for assertions
- **Metrics** — `InMemoryExporter` captures `System.Diagnostics.Metrics` (AmiMetrics, LiveMetrics)
- **AMI events** — Raw event capture for debugging failed tests
- **Prometheus** — Optional scrape of Asterisk `res_prometheus` endpoint

```csharp
using var logCapture = new LogCapture();
using var metricsCapture = new MetricsCapture();

// ... execute test ...

logCapture.Should().NotContainErrors();
metricsCapture.Get<long>("ami.events.received").Should().BeGreaterThan(0);
metricsCapture.Get<long>("ami.events.dropped").Should().Be(0);
```

## 8. Test Execution

### 8.1 Commands

```bash
# Layer 1-3: No dependencies (fast, CI-friendly)
dotnet test Tests/Asterisk.Sdk.FunctionalTests/ \
  --filter "Category=Unit|Category=Contract"

# Layer 4: Optional Docker (pipeline tests)
dotnet test Tests/Asterisk.Sdk.FunctionalTests/ \
  --filter "Category=Functional"

# Layer 5-6: Docker Compose required
docker compose -f docker/functional/docker-compose.functional.yml up -d
dotnet test Tests/Asterisk.Sdk.FunctionalTests/ \
  --filter "Category=Integration|Category=E2E"

# Layer 7: Stress tests (separate run)
dotnet test Tests/Asterisk.Sdk.FunctionalTests/ \
  --filter "Category=Stress"

# Pro tests
docker compose -f docker/functional/docker-compose.functional.yml up -d
dotnet test tests/Asterisk.Sdk.Pro.FunctionalTests/ \
  --filter "Category!=Stress"
```

### 8.2 CI Matrix (Phase 3+)

```yaml
strategy:
  matrix:
    asterisk: [18, 20, 21, 22]
env:
  ASTERISK_IMAGE: andrius/asterisk:${{ matrix.asterisk }}-alpine
```

## 9. Asterisk Configuration for Tests

### 9.1 extensions.conf (functional tests)

```ini
[test-functional]
; Basic call (5 seconds)
exten => 100,1,Answer()
 same => n,Wait(5)
 same => n,Hangup()

; AGI test
exten => 200,1,Answer()
 same => n,AGI(agi://${AGI_HOST}:4573/test-script)
 same => n,Hangup()

; ARI Stasis test
exten => 300,1,Answer()
 same => n,Stasis(test-app)
 same => n,Hangup()

; AudioSocket → Voice AI pipeline (fixed port 9091 for dialplan compatibility)
exten => 400,1,Answer()
 same => n,AudioSocket(${AUDIOSOCKET_UUID},${AUDIOSOCKET_HOST}:9091)
 same => n,Hangup()
; Note: AudioSocket uses fixed port 9091 in dialplan because Asterisk
; extensions.conf cannot use dynamic ports. AudioSocketTestServer in
; Layer 4 (no Asterisk) uses dynamic ports; Layer 5+ uses fixed 9091.

; Queue test
exten => 500,1,Answer()
 same => n,Queue(test-queue)
 same => n,Hangup()

; Conference test
exten => 600,1,Answer()
 same => n,ConfBridge(test-conf)
 same => n,Hangup()

; Echo test (keeps call alive, echoes audio back)
exten => 700,1,Answer()
 same => n,Echo()

; Playback known audio (for STT validation)
exten => 800,1,Answer()
 same => n,Playback(digits/1&digits/2&digits/3)
 same => n,Hangup()

; Recording test
exten => 900,1,Answer()
 same => n,MixMonitor(test-recording.wav)
 same => n,Wait(5)
 same => n,StopMixMonitor()
 same => n,Hangup()

; IVR test
exten => 1000,1,Answer()
 same => n,Background(press-1)
 same => n,WaitExten(5)

exten => 1,1,Playback(digits/1)
 same => n,Hangup()
exten => 2,1,Playback(digits/2)
 same => n,Hangup()

; DTMF detection
exten => 1100,1,Answer()
 same => n,Read(DTMF_INPUT,,4)
 same => n,Hangup()
```

## 10. Implementation Phases

| Phase | Areas | New Tools | Sprints |
|-------|-------|-----------|---------|
| **Phase 1** (critical) | Reconnection, concurrency, shutdown, security, event ordering, backpressure | Testcontainers | 2-3 |
| **Phase 2** (pre-v1.0) | Queues, health checks, CDR/CEL, AOT trim CI, source gen tests | SIPp | 2-3 |
| **Phase 3** (v1.0 hardening) | Network partition, multi-server cluster, version compat matrix | Toxiproxy | 2-3 |
| **Phase 4** (post-v1.0) | DTMF, conference, recording, PbxAdmin bUnit, realtime DB, IVR | bUnit | 3-4 |
| **Phase 5** (maturity) | Soak tests, metrics cross-validation, Playwright E2E | dotMemory, Playwright | Ongoing |

## 11. Dependencies (NuGet)

### MIT FunctionalTests project:
```xml
<PackageReference Include="Testcontainers" />
<PackageReference Include="Testcontainers.Redis" />
<PackageReference Include="Testcontainers.PostgreSql" />
<PackageReference Include="Testcontainers.Toxiproxy" />  <!-- Phase 3 -->
<PackageReference Include="WireMock.Net" />
<PackageReference Include="bunit" />              <!-- Phase 4 -->
<PackageReference Include="Microsoft.Playwright" /> <!-- Phase 5 -->
```

### Pro FunctionalTests project:
```xml
<PackageReference Include="Testcontainers" />
<PackageReference Include="Testcontainers.Redis" />
<PackageReference Include="Testcontainers.PostgreSql" />
```

## 12. Success Criteria

- All tests reproducible on any machine with Docker installed
- No external API keys required for any test layer
- Layers 1-3 run in <10 seconds total
- Layers 4-6 run in <5 minutes total
- Layer 7 (stress) runs in <10 minutes
- Zero flaky tests (deterministic audio, deterministic timing with generous timeouts)
- Test failures produce actionable diagnostics (captured logs, metrics, AMI event traces)

## 13. Estimated Test Counts

| Phase | Area | Estimated Tests |
|-------|------|----------------|
| Phase 1 | Reconnection | ~10 |
| Phase 1 | Concurrency | ~15 |
| Phase 1 | Graceful shutdown | ~8 |
| Phase 1 | Security | ~8 |
| Phase 1 | Event ordering | ~6 |
| Phase 1 | Backpressure | ~6 |
| Phase 2 | Queue behavior | ~12 |
| Phase 2 | Health checks | ~8 |
| Phase 2 | CDR/CEL | ~6 |
| Phase 2 | AOT trim CI | 1 (CI gate) |
| Phase 2 | Source gen tests | ~10 |
| Phase 3 | Network partition | ~8 |
| Phase 3 | Multi-server cluster | ~10 |
| Phase 3 | Version compat | ~4 (matrix runs existing suite) |
| Phase 4 | DTMF | ~6 |
| Phase 4 | Conference | ~8 |
| Phase 4 | Recording | ~5 |
| Phase 4 | PbxAdmin bUnit | ~20 |
| Phase 4 | Realtime DB | ~6 |
| Phase 4 | IVR/Dialplan | ~6 |
| Phase 5 | Soak tests | ~4 |
| Phase 5 | Metrics validation | ~6 |
| Phase 5 | Voicemail | ~3 |
| Phase 5 | Playwright E2E | ~10 |
| **Total** | | **~186** |

## 14. Asterisk Version Notes

- Default test image: `andrius/asterisk:21-alpine` (latest LTS, stable)
- CI matrix: 18, 20, 21 (verified available on Docker Hub)
- Asterisk 22: include when stable tag is published by `andrius/asterisk`
- Required modules (verify in each version): `app_audiosocket`, `res_ari`, `res_prometheus`, `app_confbridge`, `app_queue`, `app_mixmonitor`, `app_agi`

## 15. Pro Compose Strategy

Pro `docker-compose.functional.yml` uses Docker Compose `include` (v2.20+) to reuse MIT services:

```yaml
# Pro docker/functional/docker-compose.functional.yml
include:
  - path: /media/Data/Source/IPcom/Asterisk.Sdk/docker/functional/docker-compose.functional.yml

services:
  # Additional Asterisk instances for cluster testing
  asterisk-2:
    image: andrius/asterisk:21-alpine
    volumes:
      - ../../docker/functional/asterisk-config:/etc/asterisk
    ports: [5039, 4574, 8089]
    healthcheck:
      test: asterisk -rx "core show version"
      interval: 5s
      retries: 10

  asterisk-3:
    image: andrius/asterisk:21-alpine
    volumes:
      - ../../docker/functional/asterisk-config:/etc/asterisk
    ports: [5040, 4575, 8090]
    healthcheck:
      test: asterisk -rx "core show version"
      interval: 5s
      retries: 10
```

## 16. Test Prerequisites

Before running functional tests, ensure:

1. **Docker Desktop or Docker Engine** installed with Compose v2.20+
2. **Pull required images** (first run only):
   ```bash
   docker pull andrius/asterisk:21-alpine
   docker pull onerahmet/openai-whisper-asr-webservice:latest
   docker pull ghcr.io/shopify/toxiproxy:latest
   docker pull postgres:16-alpine
   docker pull redis:7-alpine
   docker pull prom/prometheus:latest
   docker pull ctaloi/sipp:latest
   ```
3. **Custom image builds** (first run only):
   ```bash
   docker build -t sdk-pjsua docker/functional/pjsua/
   docker build -t sdk-espeak docker/functional/espeak/
   ```
4. **Disk space:** ~2GB for images, ~500MB for whisper model, ~100MB for Piper voice
