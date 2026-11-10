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

| Agents | Events/sec (est.) | `EventPumpCapacity` | RAM per AMI buffer (est.) |
|--------|-------------------|---------------------|-----------------------|
| 100 | ~50-200 | 20,000 (default) | ~39 MB |
| 1,000 | ~500-2,000 | 20,000 (default) | ~39 MB |
| 10,000 | ~5,000-20,000 | 50,000 | ~97 MB |
| 100,000 | ~50,000-200,000 | 100,000-200,000 | ~194-389 MB |

> **Rule of thumb:** Set capacity to handle ~10 seconds of peak event volume. At 100K agents, a queue storm can generate 200K events/sec for a few seconds. The RAM column is that buffer full: `EventPumpCapacity` × (16 B slot + ≈ 1.9 KB event), in decimal MB, rounded, using the larger of two measured event sizes (1,928 + 16 = 1,944 B). The slot is the event's reference and its ordinal; the event is an Asterisk 22 AMI `Newchannel` or `VarSet` of 18 headers, typed object and `RawFields` together, measured at 1,928 B and 1,884 B retained. An event with fewer headers costs less, and a buffer that keeps up holds a few events, not its capacity.

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
| `ari.events.dispatched` | Counter | — | Events dispatched to observers, including an event an observer threw on |
| `ari.events.subscriber_faults` | Counter | > 0 | Observer failures: an observer of an ARI client threw, one per failure, each also logged at Warning (`[ARI] Observer threw on <EventType>`). The other observers and the client went on. **Action:** fix the observer |
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

`AddVerbaraSessions` registers a reconciliation sweep that runs every `SessionOptions.ReconciliationInterval` (default **30s**); `AddVerbaraSessionsMultiServer` registers one that runs the same check for every server of the pool, so a pool pays the cost below per server, each against its own channels. When at least one held call is older than `SessionOptions.DialingTimeout` (default **60s**) and the channel table holds one of its channels, or once an event the full buffer dropped has been delivered through (below), the sweep reconciles the server's channels once against Asterisk's `Status` snapshot (`VerbaraServer.ReconcileChannelsAsync`); a call whose channels the snapshot no longer lists ends as a reload ends it. In a contact center with traffic some call is nearly always older than the dialing timeout, so count on one `Status` snapshot per interval. It does not ask for the queues or the agents, and it never ends a call for its age.

**High-load impact:** the cost is one snapshot per sweep, which grows with the number of channels Asterisk holds. Measured on 2026-10-03 against Asterisk 22.10.1 (a container limited to 4 CPUs, `Local` channels, no traffic, 25 reconciliations per run, two runs per size): at 1,000 channels one reconciliation took 5.5 ms of wall time at the median, sent 703 KiB over AMI and allocated 4.3 MB in the SDK; at 5,000 channels, 27–28 ms, 3.3 MiB and 21.4–21.5 MB, with a median of 22–23 ms of Asterisk CPU. No AMI event was dropped. The sweep skips a tick while a load of the same server is running, rather than reading a second snapshot beside it.

**A dropped `Hangup`.** An event the full buffer dropped makes the sweep verify on its own, once the connection has dispatched to its handlers every event it received up to the drop — once the drop has been *delivered through*. From the first tick after that, every held call with a channel the table holds, or that the session engine has not observed in full, is a candidate, whatever its age, and the `session reconciliation` span reads `sessions.trigger = event-loss` when no call's age prompted the verification first. While the drop is still behind the event backlog it adds no candidate and the sweep does not wait for it: a `Status` sent then would queue behind, and compete with, the events not yet dispatched (the tick's Debug line reads `loss=pending-undelivered`). The loss stays pending until a reconciliation covers it — one whose snapshot completed on the session it began on, whose wait for the backlog was reached, that kept no channel whose creation event was received before its `Status`, and that no other channel read of the server overlapped (`live.reconcile.covered`, below) — so a verification that does not cover it is followed by another on the next tick. Measured on 2026-10-30 against release candidates of Asterisk 22.12, 23.6 and 24.0 (50 calls/s held 5–15 s, a 5 s burst of about 1,100 dropped events/s into a 22,000-event buffer, a 2 ms handler, three tick phases per version, the default interval and dialing timeout): the calls whose `Hangup` was dropped ended at a median of 12–32 s after the hangup, against 72–92 s when the sweep waited for them to be `DialingTimeout` old, two ticks earlier at every phase; the sweep sent at most one `Status` per tick, none before the drop had been delivered through, and that `Status` found no backlog ahead of it. That says when a verification can start, not when the call ends, and no fixed ceiling applies:
- a skipped tick (`skipped:<reason>` on the `session reconciliation` span, see [troubleshooting](troubleshooting.md#session-reconciliation-sweep)) moves the verification to the next one, a full interval later;
- a reconciliation waits up to 10 s for the events received ahead of its snapshot to be delivered; when that wait runs out it removes nothing and only a later tick tries again;
- a reconciliation that runs takes time of its own: the snapshot, and right after an overflow the wait for the backlog (figures below).

`DialingTimeout` no longer sets when a dropped `Hangup` is verified; it is the age at which a held call prompts a verification by itself. Lowering it costs one `Status` per tick per server whenever any held call is older than it, which on a short-call workload is a snapshot the sweep did not send before. A snapshot costs what the figures above say while the buffer keeps up. Right after an overflow it costs more: measured on 2026-10-23 against release candidates of Asterisk 22.12, 23.6 and 24.0 (2,500 calls, 5,000 channels, a 5 s burst of about 1,100 dropped events/s, an 8 ms handler), one reconciliation took 6.3–10.3 s of SDK wall time, nearly all of it the wait for the backlog, and brought about 5,300 answer events (3.3 MiB) and 25–58 ms of Asterisk CPU. Those answer events go through the same buffer as every other event. Lowering `DialingTimeout` never ends a call because of its age.

**What a dropped event costs under sustained overload.** Because the sweep verifies for a loss only once the drop has been delivered through, a backlog that persists adds no snapshot for it. Measured on 2026-10-31 against release candidates of Asterisk 22.12, 23.6 and 24.0 (70 calls/s held 5–15 s, an 8 ms handler, a 22,000-event buffer whose mean fill stayed above 16,000 in every minute from the second to the sixth, 420 s): the sweep sent the snapshots it sends anyway, at most one per tick, each prompted by a call's age and none by a loss, and the live events dropped over minutes 3–6 differed from the SDK without this verification by −0.65 % on average over nine paired runs (95 % interval −2.1 % to +0.8 %; one snapshot's answer, about 1,470 events, is 1.6 % of those drops).

**Live without Hosting.** `Verbara.Sdk.Live` alone has no sweep, so a call whose `Hangup` was dropped stays in the channel table until the next reconnect's reload. The connection raises no public event per drop; the route is a `MeterListener` on the `Verbara.Sdk.Ami` meter and a reconciliation of your own once the drop has been delivered through, repeated until one covers it. Two public readings say when, and the reconciliation's span says whether:

- *When.* At a refusal the buffer holds exactly `EventPumpCapacity` events, and `ami.events.dispatched` counts an event once every observer, the live server included, has seen it. So once that counter has advanced by more than `EventPumpCapacity` since the latest `buffer_full` drop, every event received before the drop has reached the live server. That is a bound, not an exact reading, and a stream that goes quiet need not reach it: the drop is measured a few instructions after the refusal, and the consumer can count buffered events in between, so the counter can stop at or below `EventPumpCapacity`. The buffer itself then says the same thing with no counting: `ami.event_pump.pending` at 0, pulled on the tick, means every event received before the drop has left the buffer — at most the one in dispatch is still in its observers, and a read that catches it reads `covered = false` and is retried. The sample reads both, and never treats a gauge it has not observed as 0. In the measurement above, the counter crossed the capacity 4.2 s after the last drop, about one second before the buffer was empty; under the sustained overload the buffer held 16,000–22,000 of its 22,000 events, so do not wait for 0 while traffic flows.
- *Whether.* `ReconcileChannelsAsync` returns nothing that says whether it removed what the snapshot no longer lists. Its `live channel-reconcile` span (source `Verbara.Sdk.Live`) does: `live.reconcile.covered` is `true` when the read completed on the session it began on, waited for the events received before its `Status`, kept no channel whose creation event was received before that `Status`, and overlapped no other channel read of the server; otherwise `false`, with the reason beside it where there is one — `live.status.refused` (Asterisk refused the `Status`), `live.reconcile.deferred` (the 10 s wait ran out, nothing removed), `live.reconcile.kept_stale` (the number of channels kept that the snapshot could not vouch for) or `live.reconcile.overlapped`. A read run from inside an event handler reads `false` with no reason beside it. The span carries the Asterisk version in `live.server`, not the server, so the sample attributes it by parent: it starts an `Activity` of its own around each call and reads the span whose `ParentId` is that activity's `Id`, so a reconciliation another caller runs beside it settles nothing.

`ami.event_pump.pending` is also a cost guard: the snapshot's answer events (about one per channel Asterisk holds, 5,300 at 5,000 channels) go through the same buffer, so the sample skips a tick while more than half the capacity is in use. It is an observable `int` gauge, one per connection: a `MeterListener` reads it only when the sample calls `RecordObservableInstruments()`, once per tick here, through a `SetMeasurementEventCallback<int>` (a `long` callback is never called for it), and the sample adds the connections' readings.

```csharp
using System.Diagnostics;
using System.Diagnostics.Metrics;

VerbaraServer[] servers = []; // your servers
var capacity = 20_000; // the EventPumpCapacity your connections were built with (the default)
var ct = CancellationToken.None; // your host's stopping token
long drops = 0; // buffer_full measurements seen, process-wide
long dispatched = 0; // ami.events.dispatched, process-wide
long dispatchedAtLastDrop = 0; // its value when the latest event was refused
var pending = -1; // ami.event_pump.pending, pulled each tick; -1 until the pull has run
var settled = new long[servers.Length]; // per server: the drop count its last covered reconciliation settled
string? awaited = null; // the Id of the activity around the reconciliation in flight
string? covered = null; // the Id of the latest such activity whose reconciliation read covered
using var loop = new ActivitySource("LiveReconcileLoop"); // your own source: one activity around each call

using var meters = new MeterListener
{
    InstrumentPublished = (instrument, l) =>
    {
        if (instrument.Meter.Name == "Verbara.Sdk.Ami"
            && instrument.Name is "ami.events.dropped" or "ami.events.dispatched" or "ami.event_pump.pending")
            l.EnableMeasurementEvents(instrument);
    },
};
meters.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
{
    // Runs on the SDK's threads: record and return.
    if (instrument.Name == "ami.events.dispatched")
    {
        Interlocked.Add(ref dispatched, value);
        return;
    }
    foreach (var tag in tags)
        if (tag.Key == "reason" && tag.Value is "buffer_full")
        {
            Interlocked.Exchange(ref dispatchedAtLastDrop, Interlocked.Read(ref dispatched));
            Interlocked.Add(ref drops, value);
        }
});
meters.SetMeasurementEventCallback<int>((instrument, value, _, _) =>
{
    // Reached only through RecordObservableInstruments below, on the loop's thread: one reading per connection.
    if (instrument.Name == "ami.event_pump.pending")
        pending = Math.Max(pending, 0) + value;
});
meters.Start();

using var spans = new ActivityListener
{
    ShouldListenTo = source => source.Name is "Verbara.Sdk.Live" or "LiveReconcileLoop",
    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
    ActivityStopped = activity =>
    {
        if (activity.OperationName == "live channel-reconcile"
            && activity.GetTagItem("live.reconcile.covered") is true
            && activity.ParentId is { } parent
            && parent == Volatile.Read(ref awaited))
            Volatile.Write(ref covered, parent);
    },
};
ActivitySource.AddActivityListener(spans);

using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
while (await timer.WaitForNextTickAsync(ct))
{
    var seen = Interlocked.Read(ref drops);
    var since = Interlocked.Read(ref dispatched) - Interlocked.Read(ref dispatchedAtLastDrop);
    Volatile.Write(ref pending, -1);
    meters.RecordObservableInstruments(); // pulls ami.event_pump.pending into `pending`, once per tick
    var buffered = Volatile.Read(ref pending);
    if (buffered < 0)
    {
        Console.Error.WriteLine("ami.event_pump.pending was not observed; enable it in InstrumentPublished"); // your logger, once
        continue; // an unobserved gauge is never read as empty
    }
    // The buffer held `capacity` events when it refused the latest one. More than `capacity` dispatches since the drop
    // prove that every event received before it has reached the live server; so does an empty buffer, whatever the
    // counter reads: one consumer drains it in order, so at most the event in dispatch is still in its observers, and a
    // read that catches that one keeps its channel (`kept_stale`) and is retried. The counter alone can stop below
    // `capacity` on a stream that goes quiet — the drop is measured a few instructions after the refusal — so it never
    // holds the loop on its own.
    if (since <= capacity && buffered > 0)
        continue; // events received before the latest drop may still be buffered
    if (buffered > capacity / 2)
        continue; // leave room for the snapshot's own answer events
    for (var i = 0; i < servers.Length; i++) // the measurements do not say which server dropped
    {
        if (settled[i] == seen)
            continue; // no drop since this server's last covered reconciliation
        using var call = loop.StartActivity("reconcile channels"); // the reconciliation's span is its child
        Volatile.Write(ref awaited, call?.Id);
        try
        {
            await servers[i].ReconcileChannelsAsync(ct); // one at a time per server; this loop is the only caller
            if (call is not null && Volatile.Read(ref covered) == call.Id)
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

A server stays pending, and is reconciled again on a later tick, until a reconciliation of it returns without throwing, its own span carries `live.reconcile.covered = true`, and no drop was counted since the tick began. A read that reads `false` costs one `Status` and is retried; in every case the next covering reconciliation clears it, so the loop only has to try again. A refused `Status` reads `false` beside `live.status.refused` and returns no snapshot: the SDK treats the refusal as lasting for the AMI session (warned once per session), so the server stays pending until a later `Status` completes; fix the AMI user's permission rather than the loop. The counters and the gauge carry no connection tag and are process-wide: with one AMI connection per process the rule above is a sufficient bound; with several, or while a lost session's events are still being delivered after a reconnect, it is a heuristic that can fire early, in which case the read waits for the backlog or keeps the channel, reads `covered = false`, and the loop retries at one reconciliation per server per tick. Do not reconcile from a callback or from an event handler: a read started inside an event handler cannot wait for the pump that handler holds, and never reads `covered = true`. The sample assumes it is the only caller of `ReconcileChannelsAsync`; another caller running beside it reads `live.reconcile.overlapped` on this call and only costs the loop an extra reconciliation.

**Sessions without the sweep.** The session engine decides a call it has not observed in full — one registered as
reconstructed, admitted after a manual attach, or kept across a re-attach — only on a channel table no dropped event
has left untrusted: once the full buffer has refused an event, such a call is kept, with no leg retired and no ending,
until a channel reconciliation covers the loss. With Hosting's reconciliation sweep running, that reconciliation is sent
on the first tick after the drop has been delivered through. A host that runs no sweep — `ReconciliationInterval` set
to `Timeout.InfiniteTimeSpan`, a server outside the pool, or a server attached by hand without the sweep — must send it
itself: run `ReconcileChannelsAsync` on the server once the drop has been delivered through, exactly as the loop above
does, and its completion decides the calls that were waiting. Until then they stay as they were, with no time bound; an
admission on such a table and the reload after a reconnect also cover it — the admission's read only when you do not
await the admission from inside an AMI event handler, since a read sent from there cannot wait for the pump that
handler holds and covers nothing.

**When to reconcile.** At the first tick after the drop has been delivered through, which the rule above reads from the counter and the gauge; the snapshot then finds no backlog ahead of it, and the calls whose `Hangup` was dropped end at that reconciliation, each with `Metadata["cause"] == "reload"`. A reconciliation started earlier waits up to 10 s for the backlog and, when that runs out, removes nothing and tries again on a later tick; one started while the buffer is still more than half full risks displacing live events with its own answer, the harm the guard above avoids. This sample's own timing was not measured; the figures above come from a test rig that drove the same operation at the same moment.

**Tuning levers:**

| Option | Default | When to change |
|--------|---------|----------------|
| `SessionOptions.ReconciliationInterval` | 30s | Lengthen it to send fewer snapshots: a call whose hangup was lost then becomes eligible for verification up to one interval later. `Timeout.InfiniteTimeSpan` switches the sweep off |
| `SessionOptions.DialingTimeout` | 60s | The age a held call must reach before the sweep asks about it for its age; it never ends a call by itself. A dropped `Hangup` no longer waits for it: the sweep verifies once the drop has been delivered through (see above). Lowering it costs one `Status` per tick per server whenever any held call is older than it |
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

**Observability:** the `Verbara.Sdk.Sessions` `ActivitySource` emits a `session reconciliation` span per sweep, tagged `sessions.candidates` (the calls that qualified for this tick's verification: by age with a channel the table holds, by waiting for a table that is not complete, or by a delivered loss — see `sessions.trigger`), `sessions.unverifiable` (held calls the sweep can neither verify nor decide this tick: old calls none of whose channels the table holds, and calls waiting for a covered table on a server whose last `Status` was refused, counted until a later `Status` of that server completes: left alone), `sessions.ended` (candidates that had ended when the sweep finished), `verification` (`run`, or `skipped:<reason>`) and `sessions.trigger`: what prompted the verification, `age` (a held call older than `DialingTimeout`), `table` (a call the engine has not observed in full, waiting for a channel table that is not complete) or `event-loss` (a dropped event, delivered through), the first that holds in that order, and `none` when no call qualified. Its Debug line, `Reconciliation sweep: … trigger=<trigger> loss=<loss>`, reads `loss=pending-undelivered` while a dropped event is still behind the event backlog. The snapshot itself is a `live channel-reconcile` span of the `Verbara.Sdk.Live` source, whose `live.reconcile.covered` says whether it covered the events received before its `Status` (above). A call the sweep ends is counted in `sessions.completed` or `sessions.failed` and carries `Metadata["cause"] == "reload"`.

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
