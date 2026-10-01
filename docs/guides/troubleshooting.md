# Troubleshooting Guide

## Connection Issues

### AMI: Connection refused / timeout

**Symptoms:** `SocketException: Connection refused` or timeout on `ConnectAsync`.

**Checklist:**
1. Verify Asterisk is running: `asterisk -rx "core show version"`
2. Check AMI is enabled in `/etc/asterisk/manager.conf`:
   ```ini
   [general]
   enabled = yes
   port = 5038
   bindaddr = 0.0.0.0
   ```
3. Verify firewall allows port 5038
4. Check AMI user has correct permissions:
   ```ini
   [admin]
   secret = your_password
   read = all
   write = all
   ```
5. Increase `ConnectionTimeout` if network is slow:
   ```json
   { "Asterisk": { "Ami": { "ConnectionTimeout": "00:00:10" } } }
   ```

### AMI: Authentication failed

**Symptoms:** `AmiAuthenticationException` after connect.

**Checklist:**
1. Verify username/password match `manager.conf`
2. Check `deny`/`permit` ACL in the AMI user section
3. Reload manager config: `asterisk -rx "manager reload"`. A reload does not end the session of an AMI user deleted from `manager.conf`: an application logged in as that user stays connected until its session ends, and is refused only when it logs in again.

### ARI: WebSocket connection failed

**Symptoms:** `WebSocketException` on `ConnectAsync`.

**Checklist:**
1. Verify ARI is enabled in `/etc/asterisk/ari.conf`:
   ```ini
   [general]
   enabled = yes

   [admin]
   type = user
   password = secret
   read_only = no
   ```
2. Check HTTP server in `/etc/asterisk/http.conf`:
   ```ini
   [general]
   enabled = yes
   bindaddr = 0.0.0.0
   bindport = 8088
   ```
3. Verify firewall allows port 8088
4. Reload: `asterisk -rx "ari reload"` and `asterisk -rx "http show status"`

---

## Event Issues

### Events dropped (AMI)

**Symptoms:** `[AMI_EVENT] Dropped` in logs, `ami.events.dropped` counter increasing with `reason=buffer_full`. (`reason=caller_ending`, logged once as `[AMI_EVENT] Discarded on caller ending: count=<n>`, is expected: your `DisposeAsync` or `DisconnectAsync` ended the connection with events still buffered, which it does not deliver — see [the migration guide](ami-caller-ending-buffered-events-migration.md). It needs no tuning.)

**Cause:** Event pump buffer is full. Subscribers are processing events slower than they arrive.

**Solutions:**
1. Increase buffer capacity:
   ```json
   { "Asterisk": { "Ami": { "EventPumpCapacity": 50000 } } }
   ```
2. Speed up event handlers — offload heavy work to background queues
3. Filter high-volume events (e.g., `VarSet`) early in your observer
4. Monitor with: `dotnet-counters monitor --process-id <pid> Verbara.Sdk.Ami`

See [High-Load Tuning Guide](high-load-tuning.md) for sizing recommendations.

### Events dropped (ARI)

**Symptoms:** `ari.events.dropped` counter increasing.

**Cause:** Same as AMI — ARI event pump buffer full.

**Solutions:** Same approach as AMI. ARI typically has lower event volume than AMI.

### Missing events

**Symptoms:** Expected events never arrive.

**Checklist:**
1. Verify AMI user has `read = all` (or specific classes like `read = system,call,agent`: queue events are in the `agent` class, since `queue` is not an AMI class, and `system` carries `FullyBooted`, which the live state's load waits for)
2. Check ARI application name matches your Stasis app
3. Ensure you subscribe before the events fire (subscribe before `ConnectAsync` or use `ReplaySubject`)
4. Events with both an `Event` and a `Response` header (`OriginateResponse`, `ChallengeResponseFailed`, a `UserEvent` sent with a `Response` header) were taken for responses and never delivered up to 2.6.1. Upgrade.
5. Every event of a session stopped after one `OnEvent` handler threw: up to 2.6.1 a throwing or faulting handler ended delivery silently. Upgrade; a failing handler is now logged as `[AMI_EVENT] OnEvent handler threw on <EventType>` at Warning, counted on `ami.events.handler_faults`, and delivery goes on.

### Every originate times out after 5 s

**Symptoms:** `VerbaraServer.OriginateAsync` throws `OperationCanceledException` after `DefaultEventTimeout` (5 s by default), even though the call was placed; `SendEventGeneratingActionAsync` with an `OriginateAction` never yields its `OriginateResponse`.

**Cause:** up to 2.6.1 the SDK took `OriginateResponse`, which carries a `Response` header, for an action response and dropped it, so the outcome never arrived. And the wait was bounded by `DefaultEventTimeout`, shorter than a destination that rings.

**Solution:** upgrade. `OriginateAsync` then returns the outcome once the destination answers or the originate's `Timeout` (30 s when not given) runs out; over an `AmiConnection` it waits up to that `Timeout` plus `DefaultResponseTimeout`, whatever `DefaultEventTimeout` is. Remove any workaround that raised `DefaultEventTimeout` or caught the timeout for this.

---

## Reconnection Issues

### Infinite reconnection loop

**Symptoms:** Continuous reconnect attempts filling logs.

**Solution:** Set `MaxReconnectAttempts` to a finite value:
```json
{
  "Asterisk": {
    "Ami": { "MaxReconnectAttempts": 10 },
    "Ari": { "MaxReconnectAttempts": 10 }
  }
}
```

### State lost after reconnect

**Symptoms:** After an AMI reconnect, an Asterisk restart or the application's start, channels, queues or agents are missing from `VerbaraServer`'s managers.

**Expected behavior:** `VerbaraServer.StartAsync` loads the current state from Asterisk, and the `Reconnected` event loads it again after every reconnect. A load asks for the channels (`Status`), the queues with their members and waiting callers (`QueueStatus`), and the agents (`Agents`). On a reload the channel table is reconciled against Asterisk's snapshot, so a call that ended during the outage ends, while the queues and the agents are cleared and loaded again. There may be a brief gap during reload.

**Right after Asterisk starts.** Asterisk accepts an AMI login before its modules have loaded. Until app_queue has registered `QueueStatus` and app_agent_pool has registered `Agents`, it refuses them as an unknown command (`Response: Error`, `Message: Invalid/unknown command: …`), which is also its answer when the module is not loaded at all. A load does not take that refusal as "no queues" or "no agents":

- It asks again once Asterisk reports `FullyBooted` on the AMI session. A refusal of a request sent after the report is final: the module is not loaded, and the load does not wait.
- Asterisk sends `FullyBooted` only to an AMI user with `system` in `read`. For a user without it, the load asks again every 200 ms, and logs `[LIVE] QueueStatus not registered yet …` (or `Agents`) at Information.
- The load stops asking 10 s after its first such refusal, logs `[LIVE] QueueStatus never registered …` (or `Agents`) at Warning, and completes without that state. The 10 s is spent once per load, however many of its requests are refused.

So an AMI user without `system`, on a PBX where app_queue or app_agent_pool is not loaded, waits 10 s on every load: the start's and every reload's. Put `system` in the user's `read` line in `manager.conf` (`read = all` includes it) and the load does not wait there.

Measured on 2026-09-28 against Asterisk 20.20.1, 22.9.0 and 23.4.1, with raw AMI sessions logged in as soon as the AMI port accepted after a container restart: `Agents` worked 50–111 ms after the login, `QueueStatus` 80–131 ms after it, and `FullyBooted` arrived 17–28 ms after `QueueStatus` worked. A load whose login lands in that window takes longer, by the time Asterisk still needs to finish starting.

**When the AMI session ends during the load.** Asterisk may close a session it has just opened while it starts, or the network may drop it. The load then stops: it sends nothing more on that session, and it ends no call on a channel snapshot it did not finish reading.

- With `AutoReconnect` on (the default) and the connection reconnecting, `StartAsync` returns without an exception and logs `[LIVE] Initial state load interrupted …` at Warning. The reload after the reconnect loads the state; until then the managers hold what the load had read. A reload cut short the same way logs `[LIVE] Reconnect reload interrupted …` at Warning, and the next reconnect reloads.
- With `AutoReconnect` off, or once the reconnect has given up or the connection has been disconnected, `StartAsync` throws `AmiNotConnectedException`: nothing will reload the state.
- `StartAsync` called before the connection is established (`Initial`, or `Connecting` while a connect attempt runs) throws `AmiNotConnectedException`, as it always did. Call it after `ConnectAsync` returns.
- A direct call to `RequestInitialStateAsync` throws `AmiNotConnectedException` whenever its session ends before the load completes, reconnecting or not.

**Live lost its channels after a reconnect** (up to 2.6.1). When Asterisk refused the load's `Status` — `Response: Error`, `Permission denied` for an AMI user whose `write` allows none of `system`, `call` or `reporting` — the load read the refusal as "no channels up" and removed every channel it held, ending every live call in `Verbara.Sdk.Sessions`. Upgrade: a refused `Status` now removes no channel, the load goes on to the queues and the agents, and it logs `[LIVE] Status refused: <Asterisk's message>; the channel table was not reconciled …` at Warning and tags the load's activity `live.status.refused`. Then grant the user `Status`: put `system`, `call` or `reporting` in its `write` line in `manager.conf` (`write = all` includes them). Only an `AmiConnection` reports the refusal; an `IAmiConnection` of your own that wraps one still reads it as an empty snapshot.

**A channel that ended during an outage stays in Live.** With an AMI user that may not run `Status`, no load can reconcile the channel table: a call that ended while the connection was down stays held until its own `Hangup` is seen, which never comes for a hangup lost in the outage. The only sign is the `[LIVE] Status refused` Warning on every load. Grant the user `Status` as above.

### Detecting an AMI loss

**Symptoms:** after a PBX crash or a network cut, `VerbaraServer`'s managers show calls, queue callers or agents that Asterisk no longer has, until the connection is back.

**What the SDK reports.** `VerbaraServer.ConnectionLost` (the same event as `IVerbaraServer.ConnectionLost`) is raised once for each loss of the established AMI connection that the application did not ask for, with what ended it:

- no exception (`null`) when the connection's stream ended: Asterisk stopped or crashed, or the connection was reset;
- a `TimeoutException` when the heartbeat's `Ping` went unanswered: a frozen PBX, or one the network no longer reaches;
- the reader's exception when the AMI stream could not be read.

It is raised after the connection's `State` has left `Connected`, whether `AutoReconnect` is on or off, and before the `Reconnected` of the same outage. It is not raised for the application's own `DisconnectAsync` or `DisposeAsync`, nor a second time when the reconnect gives up. From `ConnectionLost` until the reload after the reconnect has completed, the channels, queues and agents the server holds are not being updated: treat them as stale.

**How soon a loss is seen.**

- A stream Asterisk closes or resets is seen at once.
- A peer that goes silent is seen by the heartbeat, within one `HeartbeatInterval` plus the `Ping` wait, which is the smaller of `HeartbeatTimeout` and `DefaultResponseTimeout`. With the defaults (30 s, 10 s and 2 s) that is up to about 32 s after the peer went silent; measured, 2.2–32.0 s. A shorter `HeartbeatInterval` sees it sooner.

**When the reconnect gives up.** With `MaxReconnectAttempts` set to N, the connection gives up after N failed attempts, each made after its backoff delay, and then reads `Disconnected`. The first delay is `ReconnectInitialDelay`; each next one is multiplied by `ReconnectMultiplier` and capped at `ReconnectMaxDelay`. As examples, measured from the moment Asterisk was started again with the application's credentials rejected, not from the loss: 17.2–18.8 s with 1 s ×2 and N = 4; 8.1–9.2 s with 0.5 s ×2 capped at 2 s and N = 4. The give-up is announced on the connection's `StateChanged`, below: its last change, to `Disconnected`, has `IsFinal` set and carries the last attempt's exception as `Cause` (`AmiAuthenticationException` when the credentials were rejected). `ConnectionLost` is not raised for it.

**With `MaxReconnectAttempts = 0`** (the default) the connection never gives up: it retries for ever, rejected credentials included, each attempt failing with `AmiAuthenticationException`. `ConnectionLost` is raised once, for the loss, and nothing after it until the connection is back; `StateChanged` reports each attempt, `Reconnecting → Connecting` and, when it fails, `Connecting → Reconnecting` with the attempt's exception as `Cause`.

**Watching the connection's state.** `IAmiConnection.StateChanged` is raised for every change of `State`, once per change, with `Previous`, `Current`, `Cause` and `ByCaller` (whether the application's own `ConnectAsync`, `DisconnectAsync` or `DisposeAsync` made it). Subscribe to it instead of polling `State`:

- `IsLoss` (the connection left `Connected` without the application asking) marks the start of an outage, once per outage, and is delivered before `ConnectionLost`.
- A reconnect's change to `Connected` is delivered before its `Reconnected`.
- `IsFinal` (a change to `Disconnected` the application did not ask for) means nothing will reconnect the connection: `AutoReconnect` is off, or the reconnect gave up. Only a new `ConnectAsync` connects it again.
- The application's own connect and ending report only changes with `ByCaller` set, and the ending's last ones can arrive after `DisconnectAsync` or `DisposeAsync` has returned.

The changes form a chain: each one's `Previous` is the `Current` of the one before it. An `IAmiConnection` of your own that does not implement the event raises nothing.

**After a failed `ConnectAsync`.** A `ConnectAsync` that throws (nothing listening, the login refused, the connect timed out) leaves `State` at `Disconnected` and announces `Connecting → Disconnected` with `ByCaller` set and the exception as `Cause` (no cause when your own cancellation token withdrew the attempt); the attempt's socket is released. Call `ConnectAsync` again to retry. Until 2.6.1 the connection read `Connecting` after such a failure, although no attempt was running. A `ConnectAsync` on a connection that is `Connected`, `Reconnecting` or already `Connecting` throws `InvalidOperationException` and changes nothing: a lost connection reconnects on its own.

**Keep handlers short.** `ConnectionLost` handlers and the connection's `StateChanged` and `Reconnected` handlers run one at a time on a thread-pool thread, in the order the changes, the loss and the reconnect happened. They never hold the connection's reader, heartbeat or reconnect, but the reload after the reconnect waits for every one of them, so a slow handler delays it. A handler that throws is logged (`[AMI] State-change handler error` for `StateChanged`) and the handlers after it still run; a handler that never returns stops every later notification, the reload of every later reconnect included. A handler still running 30 s after its notification began is logged once at Warning, `[AMI] A StateChanged handler has not returned after 30 s; later notifications wait for it` (`Lost` for a `ConnectionLost` handler, `Reconnected` for a `Reconnected` one); the notification is not skipped, and the later ones still wait for it.

---

## AOT / Trimming Issues

### JsonSerializer throws at runtime

**Symptoms:** `NotSupportedException: Serialization and deserialization of 'MyType' is not supported`.

**Cause:** Type not registered with `[JsonSerializable]` in the source-generated context.

**Solution:** Add `[JsonSerializable(typeof(MyType))]` to `AriJsonContext`.

### Source generator not running

**Symptoms:** Missing generated code, `CS0103` errors on generated types.

**Checklist:**
1. Ensure `<OutputItemType>Analyzer</OutputItemType>` is set in the generator project reference
2. Clean and rebuild: `dotnet clean && dotnet build`
3. Check generator output in `obj/Debug/net10.0/generated/`

### Trim warnings

**Symptoms:** `IL2026`, `IL2072` warnings during publish.

**Cause:** Code path uses reflection that the trimmer can't analyze.

**Solution:** The SDK is designed for zero-trim-warnings. If you see warnings from SDK code, please report it. For your own code, use `[DynamicDependency]` or source generators.

---

## Logging Configuration

Enable detailed logging for diagnostics:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Verbara.Sdk.Ami": "Debug",
      "Verbara.Sdk.Ari": "Debug",
      "Verbara.Sdk.Live": "Debug"
    }
  }
}
```

For AMI protocol-level debugging (very verbose):
```json
{
  "Logging": {
    "LogLevel": {
      "Verbara.Sdk.Ami.Connection": "Trace"
    }
  }
}
```

---

## ActivitySource Diagnostics (v1.9.0+)

**When to use:** correlating AMI auth failures with the specific action that triggered the reconnect, measuring per-utterance VoiceAi latency, tracing a session across AGI → Live → Sessions, or diagnosing event-pump lag under load without writing extra logging.

**Registered sources** (9 total):

| ActivitySource | Representative spans |
|----------------|----------------------|
| `Verbara.Sdk.Ami` | connect / login / send-action / receive-event |
| `Verbara.Sdk.Ari` | request / websocket-event |
| `Verbara.Sdk.Agi` | session / command |
| `Verbara.Sdk.Live` | manager-load / entity-update |
| `Verbara.Sdk.Sessions` | session-start / state-transition / reconcile |
| `Verbara.Sdk.Push` | publish / deliver / authorize |
| `Verbara.Sdk.VoiceAi` | pipeline-session / stt-recognition / tts-synthesis |
| `Verbara.Sdk.VoiceAi.AudioSocket` | inbound-connection / frame-roundtrip |
| `Verbara.Sdk.VoiceAi.OpenAiRealtime` | realtime-session / turn |

Discover them at runtime without hard-coding names:

<!-- skip-doc-snippet -->
```csharp
using Verbara.Sdk.Hosting;

builder.Services
    .AddOpenTelemetry()
    .WithTracing(t => t.AddSource([.. VerbaraTelemetry.ActivitySourceNames])
                       .AddOtlpExporter());  // or AddConsoleExporter()
```

**Quick capture without OpenTelemetry:**
```sh
dotnet-trace collect --process-id <pid> \
    --providers "System.Diagnostics.Metrics,Verbara.Sdk.Ami,Verbara.Sdk.VoiceAi"
```
Open the resulting `.nettrace` in PerfView or Chromium `about:tracing`.

**Common issue:** activities report zero spans. Cause: the `ActivitySource` has no listener — add the source to your OpenTelemetry `AddSource(...)` call or set `ActivityListener.ShouldListenTo`.

---

## Session Reconciliation Backpressure (v1.7.0+)

**Symptoms:**
- After an AMI reconnect, a large spike in `asterisk.sdk.sessions.state_changed` counter over 5-30 seconds.
- Transient climb of `ami.events.dropped` (`reason=buffer_full`) during the same window.
- `SessionReconciliationService` background task logs a large batch of `Reconciling orphaned session ...` entries.

**Cause:** `SessionReconciliationService` runs every `SessionOptions.ReconciliationInterval` (default 30s). After a reconnect it has to re-scan all active sessions to detect orphans / timeouts; if the previous connection was lost with many sessions in flight, the scan enqueues a burst of state-change events.

**Solutions:**
1. **Increase EventPumpCapacity** to absorb the burst (see [high-load-tuning.md](high-load-tuning.md)):
   ```json
   { "Asterisk": { "Ami": { "EventPumpCapacity": 50000 } } }
   ```
2. **Stagger reconciliation** if the burst is disruptive — lengthen the interval and accept slightly slower orphan detection:
   ```json
   { "Sessions": { "ReconciliationInterval": "00:01:00" } }
   ```
3. **Opt out entirely** in non-critical deployments by not registering `SessionReconciliationService` (skip `AddSessions(reconcile: true)` and call the reconciler manually on demand).

**Observability:** watch the `Verbara.Sdk.Sessions` activity source — `reconcile` spans carry a `sessions.scanned` tag so you can correlate burst size with reconnect events.

---

## Voice AI / AudioSocket

### `ChannelIdInUse`: a connection presented a UUID that another call still holds

**Symptoms:** `[AudioSocket] Channel <uuid> still has a live session after <n> ms, refusing the connection that presented it again`, at Warning, from `Verbara.Sdk.VoiceAi.AudioSocket.AudioSocketServer`; `<n>` is about 1000.

**Cause:** two calls that are live at the same time presented one AudioSocket UUID, usually a dialplan that takes the UUID from a global variable or a fixed string. A call that comes back to the bot with the UUID it saved (a second `AudioSocket()`, a transfer, a redirect) is not a refusal: the server waits up to 1 second for the previous session with that UUID to end and then serves it.

**Solutions:**
1. Give every concurrent AudioSocket call its own UUID, for example `Set(BOTID=${UUID()})` once per call, and reuse it only when the same call comes back.
2. If the log shows the call that came back was the same call (its previous session ended just after the Warning), look for an `OnHangup` handler that does slow work: the previous session keeps its UUID until every `OnHangup` handler has returned. Hand slow work off from the handler.

The refused connection receives a hangup frame, so on Asterisk 20 and later `AudioSocket()` returns and the dialplan goes on; on Asterisk 18 any end from the server, a hangup frame included, hangs the call up. The call already holding the UUID is untouched.

### `SessionLimitReached`: the server is at `MaxConcurrentSessions`

**Symptoms:** `[AudioSocket] Session limit reached (<limit>), rejecting connection`, at Warning.

**Cause:** the number of live sessions has reached `AudioSocketOptions.MaxConcurrentSessions`. A call that comes back with a UUID a live session holds is not counted against the limit, since it takes that session's place.

**Solutions:** raise `MaxConcurrentSessions` if the host has room, or spread calls over more servers. The refused connection receives a hangup frame, with the same Asterisk 20 and later / Asterisk 18 behaviour as above. Refused connections are not counted in `audiosocket.connections.accepted` and open no `audiosocket.session` activity.
