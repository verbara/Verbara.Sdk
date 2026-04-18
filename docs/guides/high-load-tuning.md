# High-Load Tuning Guide

> Guidance for configuring Asterisk.Sdk in high-load scenarios (1K-100K+ agents).

---

## EventPump Sizing

Both AMI (`AsyncEventPump`) and ARI (`AriEventPump`) use bounded `Channel<T>` buffers. When the buffer fills, new events are **dropped** and counted via metrics.

### Recommended `EventPumpCapacity` by Scale

| Agents | Events/sec (est.) | `EventPumpCapacity` | RAM per buffer (est.) |
|--------|-------------------|---------------------|-----------------------|
| 100 | ~50-200 | 20,000 (default) | ~5 MB |
| 1,000 | ~500-2,000 | 20,000 (default) | ~5 MB |
| 10,000 | ~5,000-20,000 | 50,000 | ~12 MB |
| 100,000 | ~50,000-200,000 | 100,000-200,000 | ~25-50 MB |

> **Rule of thumb:** Set capacity to handle ~10 seconds of peak event volume. At 100K agents, a queue storm can generate 200K events/sec for a few seconds.

### Configuration

```json
{
  "AmiConnection": {
    "EventPumpCapacity": 50000
  }
}
```

```csharp
services.AddAsterisk(options =>
{
    options.AmiConnection.EventPumpCapacity = 50_000;
});
```

---

## Metrics to Monitor

Use `dotnet-counters`, OpenTelemetry, or Prometheus to track these metrics.

### AMI Metrics (`Asterisk.Sdk.Ami`)

| Metric | Type | Alert Threshold | Description |
|--------|------|-----------------|-------------|
| `ami.events.received` | Counter | — | Total events received from Asterisk |
| `ami.events.dropped` | Counter | > 0 | Events dropped due to full buffer. **Action:** increase `EventPumpCapacity` |
| `ami.events.dispatched` | Counter | — | Events successfully dispatched to observers |
| `ami.event.dispatch` | Histogram (ms) | p99 > 50ms | Time to dispatch one event. High values indicate slow observers |
| `ami.action.roundtrip` | Histogram (ms) | p99 > 2000ms | Action send-to-response time. High values indicate Asterisk overload |
| `ami.reconnections` | Counter | > 0 | Connection drops. Investigate network or Asterisk stability |

### ARI Metrics (`Asterisk.Sdk.Ari`)

| Metric | Type | Alert Threshold | Description |
|--------|------|-----------------|-------------|
| `ari.events.received` | Counter | — | Total WebSocket events received |
| `ari.events.dropped` | Counter | > 0 | Events dropped due to full buffer |
| `ari.events.dispatched` | Counter | — | Events dispatched to observers |
| `ari.event.dispatch` | Histogram (ms) | p99 > 50ms | Event dispatch time |
| `ari.rest.roundtrip` | Histogram (ms) | p99 > 5000ms | REST API roundtrip time |
| `ari.reconnections` | Counter | > 0 | WebSocket reconnection attempts |

### VoiceAi Telemetry (v1.9.0+)

All five VoiceAi packages publish a `Meter` + `ActivitySource` + `IHealthCheck`. Auto-registered by `AddVoiceAiPipeline<THandler>()` in `Asterisk.Sdk.VoiceAi` and the `AddStt*` / `AddTts*` / `AddAudioSocketServer()` / `AddOpenAiRealtime*` DI helpers.

| Meter | Key Instruments |
|-------|-----------------|
| `Asterisk.Sdk.VoiceAi` | `voiceai.sessions.started` / `.completed` / `.failed`, `voiceai.session.duration` (histogram) |
| `Asterisk.Sdk.VoiceAi.Stt` | `stt.transcriptions.started` / `.completed` / `.failed`, `stt.transcription.latency` |
| `Asterisk.Sdk.VoiceAi.Tts` | `tts.syntheses.started` / `.completed` / `.failed`, `tts.synthesis.latency`, `tts.synthesis.characters` |
| `Asterisk.Sdk.VoiceAi.AudioSocket` | `audiosocket.frames.{in,out}`, `audiosocket.bytes.{in,out}` |
| `Asterisk.Sdk.VoiceAi.OpenAiRealtime` | `openai.realtime.sessions.{started,completed,failed}`, `openai.realtime.session.duration` |

HealthChecks exposed via `/health` when using the standard ASP.NET Core pipeline:
`VoiceAiHealthCheck`, `SttHealthCheck`, `TtsHealthCheck`, `AudioSocketHealthCheck`, `OpenAiRealtimeHealthCheck`.

**Discovery at runtime** (avoid hard-coding strings):

```csharp
using Asterisk.Sdk.Hosting;

builder.Services
    .AddOpenTelemetry()
    .WithTracing(t => t.AddSource([.. AsteriskTelemetry.ActivitySourceNames]))
    .WithMetrics(m => m.AddMeter([.. AsteriskTelemetry.MeterNames]));
```

`ActivitySourceNames` contains 9 sources, `MeterNames` contains 12 meters. Both lists grow automatically as new packages register; consumer code written today keeps working when future packages join the stack.

### Provider Identification on the Hot Path (v1.10.0+)

STT and TTS activity spans tag each utterance with the provider name. Prior to v1.10 the SDK called `_stt.GetType().Name` (reflection) once per utterance; v1.10 introduced a virtual `ProviderName` property so built-in providers return a cached literal (`"Deepgram"`, `"Azure"`, `"ElevenLabs"`, etc.).

**Custom providers should override it** to avoid the `GetType().Name` fallback:

```csharp
public sealed class MyRecognizer : SpeechRecognizer
{
    public override string ProviderName => "MyRecognizer";
    // ...StreamAsync impl...
}
```

Skipping the override is correct but keeps the reflection call on the hot path — measurable in a tight conversation (hundreds of utterances per session). Overriding drops the call entirely.

### Monitoring Commands

```sh
# Real-time AMI metrics
dotnet-counters monitor --process-id <pid> Asterisk.Sdk.Ami

# Real-time ARI metrics
dotnet-counters monitor --process-id <pid> Asterisk.Sdk.Ari

# Core + VoiceAi meters simultaneously
dotnet-counters monitor --process-id <pid> \
    Asterisk.Sdk.Ami Asterisk.Sdk.Ari Asterisk.Sdk.Live \
    Asterisk.Sdk.Sessions Asterisk.Sdk.Push \
    Asterisk.Sdk.VoiceAi Asterisk.Sdk.VoiceAi.Stt Asterisk.Sdk.VoiceAi.Tts
```

---

## PipelineSocketConnection Backpressure

The AMI TCP layer uses `System.IO.Pipelines` with built-in backpressure:

| Parameter | Value | Effect |
|-----------|-------|--------|
| `pauseWriterThreshold` | 1 MB | Pipe pauses reads when buffer exceeds 1 MB |
| `resumeWriterThreshold` | 512 KB | Pipe resumes reads when buffer drains to 512 KB |
| `minimumSegmentSize` | 4 KB | Minimum buffer allocation unit |
| Memory pool | `MemoryPool<byte>.Shared` | Pooled allocations, reduces GC pressure |

These values are hardcoded and suitable for most scenarios. At 100K+ agents, the bottleneck is typically the event pump dispatch speed, not the TCP pipe buffer.

---

## Reconnection Tuning

Both AMI and ARI support exponential backoff reconnection.

### AMI (`AmiConnectionOptions`)

```json
{
  "AmiConnection": {
    "AutoReconnect": true,
    "MaxReconnectAttempts": 0,
    "ReconnectInitialDelay": "00:00:01",
    "ReconnectMaxDelay": "00:00:30",
    "ReconnectMultiplier": 2.0
  }
}
```

### ARI (`AriClientOptions`)

```json
{
  "AriClient": {
    "AutoReconnect": true,
    "MaxReconnectAttempts": 0,
    "ReconnectInitialDelay": "00:00:01",
    "ReconnectMaxDelay": "00:00:30",
    "ReconnectMultiplier": 2.0
  }
}
```

> `MaxReconnectAttempts = 0` means unlimited. Set to a positive value (e.g., 10) to prevent infinite loops in production.

---

## Session Reconciliation (v1.7.0+)

`Asterisk.Sdk.Sessions` runs a `SessionReconciliationService` (`IHostedService` with `PeriodicTimer`) that scans in-flight sessions every `SessionOptions.ReconciliationInterval` (default **30s**) to detect orphans and timeouts.

**High-load impact:** after an AMI reconnect, the first reconciliation pass re-validates every active session in a single tick. At 100K active sessions this can enqueue a burst of `SessionStateChanged` events that competes with the normal AMI event stream and may trigger `ami.events.dropped`.

**Tuning levers:**

| Option | Default | When to change |
|--------|---------|----------------|
| `SessionOptions.ReconciliationInterval` | 30s | Increase to 1-2min under very heavy load to smooth the burst |
| `SessionOptions.SlaThreshold` | 20s | Align with your contact-center SLA |
| `SessionOptions.QueueMetricsWindow` | 30min | Rolling window for `QueueSessionTracker` — reduce if per-queue RAM matters |
| `SessionOptions.WrapUpDuration` | 30s | Post-call wrap-up tracked per `AgentSession` |
| `AmiConnection.EventPumpCapacity` | 20,000 | Size to absorb 10s of peak event rate **plus** the expected reconcile burst |

```csharp
services.AddAsterisk(options =>
{
    options.AmiConnection.EventPumpCapacity = 100_000;
});
services.AddSessionsCore(options =>
{
    options.ReconciliationInterval = TimeSpan.FromMinutes(1);
});
```

**Observability:** `Asterisk.Sdk.Sessions` `ActivitySource` emits a `reconcile` span per scan with tags `sessions.scanned` and `sessions.marked_orphaned`. Correlate these tags with `ami.reconnections` counter to identify whether a spike in `ami.events.dropped` came from reconciliation or from Asterisk itself.

---

## Example: 10K Agent Configuration

```json
{
  "AmiConnection": {
    "Hostname": "pbx.example.com",
    "Port": 5038,
    "Username": "sdk",
    "Password": "secret",
    "EventPumpCapacity": 50000,
    "AutoReconnect": true,
    "MaxReconnectAttempts": 10,
    "ReconnectInitialDelay": "00:00:01",
    "ReconnectMaxDelay": "00:00:30",
    "DefaultResponseTimeout": "00:00:05"
  }
}
```

## Example: 100K Agent Configuration (Multi-Server)

At 100K+ agents, use `AsteriskServerPool` to distribute load across multiple Asterisk servers:

```csharp
services.AddAsterisk(options =>
{
    options.AmiConnection.EventPumpCapacity = 200_000;
    options.AmiConnection.MaxReconnectAttempts = 20;
    options.AmiConnection.DefaultResponseTimeout = TimeSpan.FromSeconds(10);
});
```

Key considerations:
- **Multi-server:** Use `AsteriskServerPool` to federate N servers with agent routing
- **Observer speed:** Keep event handlers fast (< 10ms). Offload heavy work to background queues
- **VarSet filtering:** `VarSet` events can be 50%+ of total volume. Filter early in observers
- **GC tuning:** Consider `ServerGC` and `gcServer=true` in `runtimeconfig.json`

```json
{
  "runtimeOptions": {
    "configProperties": {
      "System.GC.Server": true,
      "System.GC.Concurrent": true
    }
  }
}
```
