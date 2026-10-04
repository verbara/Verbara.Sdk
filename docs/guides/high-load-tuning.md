# High-Load Tuning Guide

> Guidance for configuring Verbara.Sdk in high-load scenarios (1K-100K+ agents).

---

## EventPump Sizing

Both AMI (`AsyncEventPump`) and ARI (`AriEventPump`) use bounded `Channel<T>` buffers. When the buffer fills, new events are **dropped** and counted via metrics.

### Recommended `EventPumpCapacity` by Scale

> The **Events/sec** column describes *your* PBX, not this SDK.
> Those are planning assumptions for a typical contact centre, offered so the capacity column has a
> derivation you can check against your own traffic — they are not measurements of anything in this
> repository, and nothing here guards them (ADR-0042 D1a: the subject is outside this repo).
> Measure your own event rate and re-derive.

| Agents | Events/sec (est.) | `EventPumpCapacity` | RAM per buffer (est.) |
|--------|-------------------|---------------------|-----------------------|
| 100 | ~50-200 | 20,000 (default) | ~5 MB |
| 1,000 | ~500-2,000 | 20,000 (default) | ~5 MB |
| 10,000 | ~5,000-20,000 | 50,000 | ~12 MB |
| 100,000 | ~50,000-200,000 | 100,000-200,000 | ~25-50 MB |

> **Rule of thumb:** Set capacity to handle ~10 seconds of peak event volume. At 100K agents, a queue storm can generate 200K events/sec for a few seconds.

### Configuration

`AddVerbara(builder.Configuration)` reads the AMI options from the `Asterisk:Ami` section and the ARI options from `Asterisk:Ari`, with the option names as keys; the inline `AddVerbara(options => …)` sets the same options on `options.Ami` and `options.Ari`.

```json
{
  "Asterisk": {
    "Ami": {
      "EventPumpCapacity": 50000
    }
  }
}
```

```csharp
var services = new ServiceCollection();
services.AddVerbara(options =>
{
    options.Ami.EventPumpCapacity = 50_000;
});
```

---

## Metrics to Monitor

Use `dotnet-counters`, OpenTelemetry, or Prometheus to track these metrics.

### AMI Metrics (`Verbara.Sdk.Ami`)

| Metric | Type | Alert Threshold | Description |
|--------|------|-----------------|-------------|
| `ami.events.received` | Counter | — | Total events received from Asterisk |
| `ami.events.dropped` | Counter | > 0 with `reason=buffer_full` | Events the consumer never saw, tagged `reason`: `buffer_full` — dropped by a full buffer, one per event. **Action:** increase `EventPumpCapacity`; `caller_ending` — still buffered when the caller's `DisposeAsync`/`DisconnectAsync` ended the connection, one measurement per ending with the count. Expected; alert and tune on `reason=buffer_full` only ([migration](ami-caller-ending-buffered-events-migration.md)) |
| `ami.events.dispatched` | Counter | — | Events successfully dispatched to observers |
| `ami.events.handler_faults` | Counter | > 0 | Event handler failures (`OnEvent`, or the token overload of `Subscribe`): a handler threw, or its task faulted, one per failure (a token handler that stops with `OperationCanceledException` after your own ending is not counted), each also logged as `[AMI_EVENT] OnEvent handler threw on <EventType>` at Warning. Delivery goes on. **Action:** fix the handler. Every `OnEvent` handler is awaited before the next event, so a slow one holds the pump whatever its position ([migration](onevent-await-all-migration.md)) |
| `ami.events.observer_faults` | Counter | > 0 | Observer failures: an observer's `OnNext` threw, one per failure, each also logged as `[AMI_EVENT] Observer threw on <EventType>` at Warning. The observer stays subscribed and delivery goes on. Counted apart from `ami.events.handler_faults`. **Action:** fix the observer |
| `ami.event.dispatch` | Histogram (ms) | p99 > 50ms | Time to dispatch one event. High values indicate slow observers |
| `ami.action.roundtrip` | Histogram (ms) | p99 > 2000ms | Action send-to-response time. High values indicate Asterisk overload |
| `ami.reconnections` | Counter | > 0 | Connection drops. Investigate network or Asterisk stability |

### ARI Metrics (`Verbara.Sdk.Ari`)

| Metric | Type | Alert Threshold | Description |
|--------|------|-----------------|-------------|
| `ari.events.received` | Counter | — | Total WebSocket events received |
| `ari.events.dropped` | Counter | > 0 with `reason=buffer_full` | Events the observers never saw, tagged `reason`: `buffer_full` — discarded by a full buffer (the oldest buffered event, one per event). **Action:** speed up or offload the observers; `caller_ending` — still buffered when the caller's `DisposeAsync`/`DisconnectAsync` ended the connection, one measurement per ending with the count. Expected; alert and tune on `reason=buffer_full` only ([migration](ari-connection-state-and-accept-loop-migration.md#the-dropped-events-counter-counts-a-full-buffer-and-carries-a-reason)) |
| `ari.events.dispatched` | Counter | — | Events dispatched to observers |
| `ari.event.dispatch` | Histogram (ms) | p99 > 50ms | Event dispatch time |
| `ari.rest.roundtrip` | Histogram (ms) | p99 > 5000ms | REST API roundtrip time |
| `ari.reconnections` | Counter | > 0 | WebSocket reconnection attempts |

### VoiceAi Telemetry (v1.9.0+)

All five VoiceAi packages publish a `Meter` + `ActivitySource` + `IHealthCheck`. Auto-registered by `AddVoiceAiPipeline<THandler>()` in `Verbara.Sdk.VoiceAi` and the `AddStt*` / `AddTts*` / `AddAudioSocketServer()` / `AddOpenAiRealtime*` DI helpers.

| Meter | Key Instruments |
|-------|-----------------|
| `Verbara.Sdk.VoiceAi` | `voiceai.sessions.started` / `.completed` / `.failed`, `voiceai.session.duration_ms` (histogram) |
| `Verbara.Sdk.VoiceAi.Stt` | `stt.transcriptions.started` / `.completed` / `.failed` / `.cancelled`, `stt.transcription.latency_ms` |
| `Verbara.Sdk.VoiceAi.Tts` | `tts.syntheses.started` / `.completed` / `.failed` / `.cancelled` / `.silent`, `tts.synthesis.latency_ms`, `tts.synthesis.ttfa_ms`, `tts.synthesis.characters` |
| `Verbara.Sdk.VoiceAi.AudioSocket` | `audiosocket.connections.{accepted,closed}`, `audiosocket.frames.{received,sent}`, `audiosocket.bytes.{received,sent}`, `audiosocket.session.duration_ms` |
| `Verbara.Sdk.VoiceAi.OpenAiRealtime` | `openai_realtime.sessions.{started,completed,failed,close_unanswered}`, `openai_realtime.session.duration_ms` |

Every recognition and every synthesis the pipeline starts ends in exactly one of `.completed`, `.failed` or
`.cancelled`, so per arm `started - completed - failed - cancelled` is what is still in flight. `.cancelled` is
tagged `voiceai.ending`: `session-cancelled` (the session's token — the host's or the session broker's stop or
disposal), and for syntheses also `barge-in`, `disposal` and `far-end` (a write found the session already ended).
`.completed` counts only a synthesis whose audio was all written to the session.

HealthChecks exposed via `/health` when using the standard ASP.NET Core pipeline:
`VoiceAiHealthCheck`, `SttHealthCheck`, `TtsHealthCheck`, `AudioSocketHealthCheck`, `OpenAiRealtimeHealthCheck`.

**Discovery at runtime** (avoid hard-coding strings):

<!-- skip-doc-snippet -->
```csharp
using Verbara.Sdk.Hosting;

builder.Services
    .AddOpenTelemetry()
    .WithTracing(t => t.AddSource([.. VerbaraTelemetry.ActivitySourceNames]))
    .WithMetrics(m => m.AddMeter([.. VerbaraTelemetry.MeterNames]));
```

`ActivitySourceNames` contains 9 sources, `MeterNames` contains 15 meters. Both lists grow automatically as new packages register; consumer code written today keeps working when future packages join the stack.

### Provider Identification on the Hot Path (v1.10.0+)

STT and TTS activity spans tag each utterance with the provider name. Prior to v1.10 the SDK called `_stt.GetType().Name` (reflection) once per utterance; v1.10 introduced a virtual `ProviderName` property so built-in providers return a cached literal (`"Deepgram"`, `"Azure"`, `"ElevenLabs"`, etc.).

**Custom providers should override it** to avoid the `GetType().Name` fallback:

<!-- skip-doc-snippet -->
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
dotnet-counters monitor --process-id <pid> Verbara.Sdk.Ami

# Real-time ARI metrics
dotnet-counters monitor --process-id <pid> Verbara.Sdk.Ari

# Core + VoiceAi meters simultaneously
dotnet-counters monitor --process-id <pid> \
    Verbara.Sdk.Ami Verbara.Sdk.Ari Verbara.Sdk.Live \
    Verbara.Sdk.Sessions Verbara.Sdk.Push \
    Verbara.Sdk.VoiceAi Verbara.Sdk.VoiceAi.Stt Verbara.Sdk.VoiceAi.Tts
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
  "Asterisk": {
    "Ami": {
      "AutoReconnect": true,
      "MaxReconnectAttempts": 0,
      "ReconnectInitialDelay": "00:00:01",
      "ReconnectMaxDelay": "00:00:30",
      "ReconnectMultiplier": 2.0
    }
  }
}
```

### ARI (`AriClientOptions`)

```json
{
  "Asterisk": {
    "Ari": {
      "AutoReconnect": true,
      "MaxReconnectAttempts": 0,
      "ReconnectInitialDelay": "00:00:01",
      "ReconnectMaxDelay": "00:00:30",
      "ReconnectMultiplier": 2.0
    }
  }
}
```

> `MaxReconnectAttempts = 0` means unlimited. Set to a positive value (e.g., 10) to prevent infinite loops in production: with `MaxReconnectAttempts = N` the client makes N reconnect attempts, each after its backoff delay, and then gives up (AMI reads `Disconnected`, ARI `Faulted`).

**Accepted values.** While `AutoReconnect` is on, both clients accept only values the backoff can use:

| Option | Accepted |
|--------|----------|
| `ReconnectInitialDelay` | from `00:00:00` to `24.20:31:23.647` (`int.MaxValue` ms, the longest delay .NET can wait) |
| `ReconnectMaxDelay` | from `ReconnectInitialDelay` to `24.20:31:23.647` |
| `ReconnectMultiplier` | a finite number of at least `1.0` |
| `MaxReconnectAttempts` | `0` (unlimited) or more |

A value outside these ranges fails the host's start with an `OptionsValidationException` naming the option (through `AddVerbara`, or any registration that validates on start), and a client constructed directly throws `ArgumentOutOfRangeException` whose `ParamName` is the option. With `AutoReconnect` off the backoff values are not checked, except AMI's `MaxReconnectAttempts`, which must be 0 or more either way.

---

## Session Reconciliation

`AddVerbaraSessions` registers a reconciliation sweep that runs every `SessionOptions.ReconciliationInterval` (default **30s**). When at least one held call is older than `SessionOptions.DialingTimeout` (default **60s**) and the channel table holds one of its channels, the sweep reconciles the server's channels once against Asterisk's `Status` snapshot (`VerbaraServer.ReconcileChannelsAsync`); a call whose channels the snapshot no longer lists ends as a reload ends it. In a contact center with traffic some call is nearly always older than the dialing timeout, so count on one `Status` snapshot per interval. It does not ask for the queues or the agents, and it never ends a call for its age.

**High-load impact:** the cost is one snapshot per sweep, which grows with the number of channels Asterisk holds. Measured on 2026-10-03 against Asterisk 22.10.1 (a container limited to 4 CPUs, `Local` channels, no traffic, 25 reconciliations per run, two runs per size): at 1,000 channels one reconciliation took 5.5 ms of wall time at the median, sent 703 KiB over AMI and allocated 4.3 MB in the SDK; at 5,000 channels, 27–28 ms, 3.3 MiB and 21.4–21.5 MB, with a median of 22–23 ms of Asterisk CPU. No AMI event was dropped. The sweep skips a tick while a load of the same server is running, rather than reading a second snapshot beside it.

**Tuning levers:**

| Option | Default | When to change |
|--------|---------|----------------|
| `SessionOptions.ReconciliationInterval` | 30s | Lengthen it to send fewer snapshots: a call whose hangup was lost then ends up to one interval later. `Timeout.InfiniteTimeSpan` switches the sweep off |
| `SessionOptions.DialingTimeout` | 60s | The age a held call must reach before the sweep asks about it; it never ends a call by itself |
| `SessionOptions.SlaThreshold` | 20s | Align with your contact-center SLA |
| `SessionOptions.QueueMetricsWindow` | 30min | Rolling window for `QueueSessionTracker` — reduce if per-queue RAM matters |
| `SessionOptions.WrapUpDuration` | 30s | Not a lever: nothing in the SDK reads it, and the SDK raises no `CallWrapUpEvent`. An agent that `IAgentSessionTracker` moves to wrap-up when its call ends stays there until its next call connects, whatever this is set to |
| `AmiConnectionOptions.EventPumpCapacity` | 20,000 | Size to absorb 10s of peak event rate |

```csharp
var services = new ServiceCollection();
services.AddVerbara(options =>
{
    options.Ami.EventPumpCapacity = 100_000;
});
services.AddVerbaraSessions(options =>
{
    options.ReconciliationInterval = TimeSpan.FromMinutes(1);
});
```

**Observability:** the `Verbara.Sdk.Sessions` `ActivitySource` emits a `session reconciliation` span per sweep, tagged `sessions.candidates` (held calls old enough, with a channel the table holds), `sessions.unverifiable` (held calls old enough, none of whose channels the table holds: left alone), `sessions.ended` (candidates that had ended when the sweep finished) and `verification` (`run`, or `skipped:<reason>`). The snapshot itself is a `live channel-reconcile` span of the `Verbara.Sdk.Live` source. A call the sweep ends is counted in `sessions.completed` or `sessions.failed` and carries `Metadata["cause"] == "reload"`.

---

## Example: 10K Agent Configuration

```json
{
  "Asterisk": {
    "Ami": {
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
}
```

## Example: 100K Agent Configuration (Multi-Server)

At 100K+ agents, use `VerbaraServerPool` to distribute load across multiple Asterisk servers:

```csharp
var services = new ServiceCollection();
services.AddVerbara(options =>
{
    options.Ami.EventPumpCapacity = 200_000;
    options.Ami.MaxReconnectAttempts = 20;
    options.Ami.DefaultResponseTimeout = TimeSpan.FromSeconds(10);
});
```

Key considerations:
- **Multi-server:** Use `VerbaraServerPool` to federate N servers with agent routing
- **Observer speed:** Keep event handlers fast (< 10ms). Offload heavy work to background queues
- **VarSet filtering:** `VarSet` events are typically the largest single share of event volume on a busy dialplan. Filter early in observers, or better at the source, in `manager.conf`: the [`eventfilter` in Missing events](troubleshooting.md#the-cost-of-dialplan-and-a-filter-that-removes-it) drops them all except the `QUEUESTATUS` that the queue metrics read
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
