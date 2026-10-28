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
| `ami.events.dropped` | Counter | > 0 with `reason=buffer_full` | Events the consumer never saw, tagged `reason`: `buffer_full` — dropped by a full buffer, one per event. **Action:** increase `EventPumpCapacity`; `caller_ending` — still buffered when the caller's `DisposeAsync`/`DisconnectAsync` ended the connection, one measurement per released buffer with the count (two when the ending lands while a reconnected session waits for the lost one's delivery). Expected; alert and tune on `reason=buffer_full` only ([migration](ami-caller-ending-buffered-events-migration.md)) |
| `ami.events.dispatched` | Counter | — | Events successfully dispatched to observers |
| `ami.event_pump.pending` | Gauge | close to `EventPumpCapacity` for long | Events received and not yet delivered to the handlers. After a reconnect it counts the new session's buffer plus every lost session's buffer still being delivered, so it can read above `EventPumpCapacity` while the lost events drain: each buffer has its own capacity. **Action:** a gauge that stays high means the handlers are slower than the event rate; see `reason=buffer_full` above |
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

### Session Metrics (`Verbara.Sdk.Sessions`)

| Metric | Type | Alert Threshold | Description |
|--------|------|-----------------|-------------|
| `sessions.events.subscriber_faults` | Counter | > 0 | Subscriber failures: a subscriber of `ICallSessionManager.Events` or `IAgentSessionTracker.StateChanges` threw, one per failure, each also logged at Warning (`A subscriber of the session events threw on <EventType> …` or `A subscriber of the agent state changes threw …`) with the exception. The other subscribers and the session engine go on. A subscriber subscribed directly stays subscribed; one composed through an operator (`Where`, `Select`, `OfType`, …) is gone after its first failure, so a single count can mean a subscriber that receives nothing more. Counted apart from `ami.events.observer_faults`, which no longer counts these. **Action:** fix the subscriber |

### VoiceAi Telemetry (v1.9.0+)

Five VoiceAi packages ship an `IHealthCheck`: `VoiceAi`, `VoiceAi.AudioSocket`, `VoiceAi.Stt`, `VoiceAi.Tts` and `VoiceAi.OpenAiRealtime`. The five meters below cover them (`Verbara.Sdk.VoiceAi` publishes the `.Stt` and `.Tts` meters for every provider), and three of them also publish an `ActivitySource`: `Verbara.Sdk.VoiceAi`, `Verbara.Sdk.VoiceAi.AudioSocket` and `Verbara.Sdk.VoiceAi.OpenAiRealtime`; the STT and TTS packages publish none. Auto-registered by `AddVoiceAiPipeline<THandler>()` in `Verbara.Sdk.VoiceAi` and the `AddStt*` / `AddTts*` / `AddAudioSocketServer()` / `AddOpenAiRealtime*` DI helpers.

| Meter | Key Instruments |
|-------|-----------------|
| `Verbara.Sdk.VoiceAi` | `voiceai.sessions.started` / `.completed` / `.failed` / `.cancelled`, `voiceai.session.duration_ms` (histogram) |
| `Verbara.Sdk.VoiceAi.Stt` | `stt.transcriptions.started` / `.completed` / `.failed` / `.cancelled`, `stt.transcription.latency_ms` |
| `Verbara.Sdk.VoiceAi.Tts` | `tts.syntheses.started` / `.completed` / `.failed` / `.cancelled` / `.silent`, `tts.synthesis.latency_ms`, `tts.synthesis.ttfa_ms`, `tts.synthesis.characters` |
| `Verbara.Sdk.VoiceAi.AudioSocket` | `audiosocket.connections.{accepted,closed}`, `audiosocket.frames.{received,sent}`, `audiosocket.bytes.{received,sent}`, `audiosocket.session.duration_ms` |
| `Verbara.Sdk.VoiceAi.OpenAiRealtime` | `openai_realtime.sessions.{started,completed,failed,cancelled,close_unanswered}`, `openai_realtime.session.duration_ms` |

Every recognition and every synthesis the pipeline starts ends in exactly one of `.completed`, `.failed` or
`.cancelled`, so per arm `started - completed - failed - cancelled` is what is still in flight. `.cancelled` is
tagged `voiceai.ending`: `session-cancelled` (the session's token — the host's or the session broker's stop or
disposal), and for syntheses also `barge-in`, `disposal` and `far-end` (a write found the session already ended, or
failed on its transport because the far end left). `.completed` counts only a synthesis whose audio was all written to
the session. The sessions follow the same rule per handler: `voiceai.sessions.*` and `openai_realtime.sessions.*`
count each started session in exactly one of `.completed`, `.failed` or `.cancelled`, and a session is `.cancelled`
only when the token its handler runs under was cancelled; a hangup, a normal vendor close or a graceful stop is
`.completed`, and a vendor failure close is `.failed`, also when it arrives after the caller hung up.
The same `voiceai.ending` value rides on the latency sample of a cancelled turn (`stt.transcription.latency_ms`,
`tts.synthesis.latency_ms`) and on the duration sample of a cancelled session (`voiceai.session.duration_ms`,
`openai_realtime.session.duration_ms`, always `session-cancelled`). Completed and failed samples carry no tag, so
filter on the tag's absence to read them, or aggregate over it to read every sample as before.

HealthChecks exposed via `/health` when using the standard ASP.NET Core pipeline:
`VoiceAiHealthCheck`, `SttHealthCheck`, `TtsHealthCheck`, `AudioSocketHealthCheck`, `RealtimeHealthCheck` (entry
`openai-realtime`). The Realtime entry reports `Degraded` once the vendor rejected the last 3 consecutive sessions and
`Healthy` again on the first one it served; `Degraded` keeps a default endpoint answering 200.

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


### Closing ARI connections

A disconnect, a host stop and an outbound listener's stop each send a close frame and wait for Asterisk's answer, so a
stop's cost is Asterisk's answer time, not a timeout. Measured on Asterisk 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2
against the functional test stack: a generic host stopping 1 `AriConnectionHostedService` took p50 0.16–0.19 ms and 10
took p50 0.55–0.65 ms (20 stops each, max 7.1 ms); Asterisk answered every close it was sent within 7.9 ms, with up to
50 outbound connections closed at once. The bounds only matter when nothing answers: a disconnect waits at most 5 s (or
until its token is cancelled), and an outbound listener's stop waits at most 1 s for all its connections together, so N
mute far ends cost one second, not N. See the [ARI package README](../../src/Verbara.Sdk.Ari/README.md#closing).

---

## Session Reconciliation

`AddVerbaraSessions` registers a reconciliation sweep that runs every `SessionOptions.ReconciliationInterval` (default **30s**); `AddVerbaraSessionsMultiServer` registers one that runs the same check for every server of the pool, so a pool pays the cost below per server, each against its own channels. When at least one held call is older than `SessionOptions.DialingTimeout` (default **60s**) and the channel table holds one of its channels, the sweep reconciles the server's channels once against Asterisk's `Status` snapshot (`VerbaraServer.ReconcileChannelsAsync`); a call whose channels the snapshot no longer lists ends as a reload ends it. In a contact center with traffic some call is nearly always older than the dialing timeout, so count on one `Status` snapshot per interval. It does not ask for the queues or the agents, and it never ends a call for its age.

**High-load impact:** the cost is one snapshot per sweep, which grows with the number of channels Asterisk holds. Measured on 2026-10-03 against Asterisk 22.10.1 (a container limited to 4 CPUs, `Local` channels, no traffic, 25 reconciliations per run, two runs per size): at 1,000 channels one reconciliation took 5.5 ms of wall time at the median, sent 703 KiB over AMI and allocated 4.3 MB in the SDK; at 5,000 channels, 27–28 ms, 3.3 MiB and 21.4–21.5 MB, with a median of 22–23 ms of Asterisk CPU. No AMI event was dropped. The sweep skips a tick while a load of the same server is running, rather than reading a second snapshot beside it.

**A dropped `Hangup` and `DialingTimeout`.** The sweep does not verify because an event was dropped: a call whose `Hangup` the full buffer dropped is verified only once some held call is older than `DialingTimeout`. In a contact center with long calls that is nearly always true, and the next verification can end the call. In a workload of short calls (IVR legs, short queue calls, outbound attempts) no other call may be that old, so the sweep sends nothing until the call itself is: it becomes eligible once it is `DialingTimeout` old, and the next tick is the first that can verify it, up to one `ReconciliationInterval` later (60–90 s after it opened, with the defaults). That says when a verification can start, not when the call ends, and no fixed ceiling applies:
- a skipped tick (`skipped:<reason>` on the `session reconciliation` span, see [troubleshooting](troubleshooting.md#session-reconciliation-sweep)) moves the verification to the next one, a full interval later;
- a reconciliation waits up to 10 s for the events received ahead of its snapshot to be delivered; when that wait runs out, as it can right after an overflow, it removes nothing and only a later tick tries again;
- a reconciliation that runs takes time of its own: the snapshot, and right after an overflow the wait for the backlog (figures below).

`DialingTimeout` is the cost knob for that delay. Lowering it makes such a call eligible sooner; the price is one `Status` per tick per server whenever any held call is older than it, which on a short-call workload is a snapshot the sweep did not send before. A snapshot costs what the figures above say while the buffer keeps up. Right after an overflow it costs more: measured on 2026-10-23 against release candidates of Asterisk 22.12, 23.6 and 24.0 (2,500 calls, 5,000 channels, a 5 s burst of about 1,100 dropped events/s, an 8 ms handler), one reconciliation took 6.3–10.3 s of SDK wall time, nearly all of it the wait for the backlog, and brought about 5,300 answer events (3.3 MiB) and 25–58 ms of Asterisk CPU. Those answer events go through the same buffer as every other event. Lowering `DialingTimeout` never ends a call because of its age.

**Live without Hosting.** `Verbara.Sdk.Live` alone has no sweep, so a call whose `Hangup` was dropped stays in the channel table until the next reconnect's reload. The connection raises no public event per drop; the route is a `MeterListener` on `ami.events.dropped` (meter `Verbara.Sdk.Ami`), and a reconciliation of your own once the drops stop, repeated until one is conclusive. `ReconcileChannelsAsync` returns nothing that says whether it removed what the snapshot no longer lists: when its 10 s wait for the backlog runs out it returns normally, having removed nothing, and only its `live channel-reconcile` span (source `Verbara.Sdk.Live`) says so, with the tag `live.reconcile.deferred`. The sample reads that tag through an `ActivityListener`:

```csharp
using System.Diagnostics;
using System.Diagnostics.Metrics;

VerbaraServer[] servers = []; // your servers
var ct = CancellationToken.None; // your host's stopping token
long drops = 0; // buffer_full measurements seen, process-wide
long lastDropAt = 0; // Environment.TickCount64 of the latest one
var settled = new long[servers.Length]; // per server: the drop count its last conclusive reconciliation covers
var deferred = 0; // 1 when a reconciliation's wait for the backlog ran out

using var meters = new MeterListener
{
    InstrumentPublished = (instrument, l) =>
    {
        if (instrument.Meter.Name == "Verbara.Sdk.Ami" && instrument.Name == "ami.events.dropped")
            l.EnableMeasurementEvents(instrument);
    },
};
meters.SetMeasurementEventCallback<long>((_, _, tags, _) =>
{
    // Runs on the thread that refused the event: record and return.
    foreach (var tag in tags)
        if (tag.Key == "reason" && tag.Value is "buffer_full")
        {
            Interlocked.Exchange(ref lastDropAt, Environment.TickCount64);
            Interlocked.Increment(ref drops);
        }
});
meters.Start();

using var spans = new ActivityListener
{
    ShouldListenTo = source => source.Name == "Verbara.Sdk.Live",
    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
    ActivityStopped = activity =>
    {
        if (activity.OperationName == "live channel-reconcile" && activity.GetTagItem("live.reconcile.deferred") is not null)
            Volatile.Write(ref deferred, 1);
    },
};
ActivitySource.AddActivityListener(spans);

using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
while (await timer.WaitForNextTickAsync(ct))
{
    var seen = Interlocked.Read(ref drops);
    if (Environment.TickCount64 - Interlocked.Read(ref lastDropAt) < 2_000)
        continue; // drops still arriving
    for (var i = 0; i < servers.Length; i++) // the measurement does not say which server dropped
    {
        if (settled[i] == seen)
            continue; // no drop since this server's last conclusive reconciliation
        Volatile.Write(ref deferred, 0);
        try
        {
            await servers[i].ReconcileChannelsAsync(ct); // one at a time per server
            if (Volatile.Read(ref deferred) == 0)
                settled[i] = seen; // a drop counted after `seen` leaves it pending
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) // AmiNotConnectedException included: this server stays pending, the others go on
        {
            Console.Error.WriteLine(ex.Message); // your logger
        }
    }
}
```

A server stays pending, and is reconciled again on a later tick, until a reconciliation of it returns without throwing, without `live.reconcile.deferred`, and with no drop counted since it started. The measurement carries only the `reason` tag and is process-wide, so every server is reconciled, one reconciliation in flight per server, spaced by the tick. Do not reconcile from the callback or from an event handler. The `deferred` flag assumes the loop is the only caller of `ReconcileChannelsAsync`; another one running beside it can only cost the loop an extra reconciliation.

**When to reconcile.** The useful moment is once the connection has delivered the events it received before the drop, and an application cannot see it: the measurement fires when an event is refused, and nothing public reports how far delivery has gone. The sample waits for 2 s without a drop, an unmeasured choice. A reconciliation that starts while the backlog is still being delivered waits up to 10 s for it; when that runs out it removes nothing and the loop tries again on a later tick. The snapshot's own answer events (about 5,300 at 5,000 channels) go through the same buffer; if they overflow it, the drops they cause leave every server pending again, at most one reconciliation per server per tick, so lengthen the tick or the quiet spell if that repeats. Expect little: the measurement above started one reconciliation as soon as the burst's drops had been delivered, a moment the test rig could see and this sample cannot, and the calls whose `Hangup` was dropped ended at a median of 17.8–24.9 s after the hangup, against 27.4–41.1 s with the 30 s sweep alone, a gain of 9.6–16.2 s that a re-run of one version did not reproduce (the result depends on where the sweep's tick falls). Nearly all of that time is the wait for the backlog. This sample's own timing was not measured.

**Tuning levers:**

| Option | Default | When to change |
|--------|---------|----------------|
| `SessionOptions.ReconciliationInterval` | 30s | Lengthen it to send fewer snapshots: a call whose hangup was lost then becomes eligible for verification up to one interval later. `Timeout.InfiniteTimeSpan` switches the sweep off |
| `SessionOptions.DialingTimeout` | 60s | The age a held call must reach before the sweep asks about it; it never ends a call by itself. On a short-call workload it is the cost knob for a dropped `Hangup`: lower it to verify sooner, at one `Status` per tick per server whenever any held call is older than it (see above) |
| `SessionOptions.SlaThreshold` | 20s | Align with your contact-center SLA |
| `SessionOptions.QueueMetricsWindow` | 30min | Rolling window for `QueueSessionTracker` — reduce if per-queue RAM matters |
| `SessionOptions.WrapUpDuration` | 30s | Set it to the queues' Asterisk `wrapuptime`: the SDK cannot read that value, and without a provider one duration applies to every queue. When queues differ, register an `IWrapUpDurationProvider` that returns each queue's `wrapuptime` per server, read from memory (it runs on the thread that delivers that server's AMI events, so it must not do I/O or wait); this option then covers a queue the provider returns `null` for, an answer it cannot use, and a wrap-up opened by the call's end. A wrap-up opens at a queue member's `AgentComplete` (for a call outside any queue that an agent known by name took, at the call's end) and closes when its duration passes (the provider's answer, else this option) or the same agent's next call connects, raising one `CallWrapUpEvent` each; one timer is held per open wrap-up. Accepted: `00:00:00` to `49.17:02:47.294` (4,294,967,294 ms); zero closes each wrap-up at once (wrap-up off), and a negative value, `Timeout.InfiniteTimeSpan` included, fails the host's start. A value above `CompletedRetention` may reach a subscriber after the call has been released; so may a provider answer above it, which is used and logged once per server and queue at Warning |
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
