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

**Symptoms:** `ari.events.dropped` counter increasing with `reason=buffer_full`.

**Cause:** Same as AMI — the ARI event buffer was full, and each new event discarded the oldest one buffered. Measurements tagged `reason=caller_ending` are not this: they count the events still buffered when your own `DisconnectAsync` or `DisposeAsync` ended the connection, are expected, and need no tuning ([migration](ari-connection-state-and-accept-loop-migration.md#the-dropped-events-counter-counts-a-full-buffer-and-carries-a-reason)).

**Solutions:** Speed up or offload the observers, as for AMI; the ARI buffer's capacity is fixed. ARI typically has lower event volume than AMI. Alert on `reason=buffer_full` only.

### Missing events

**Symptoms:** Expected events never arrive.

**Checklist:**
1. Verify AMI user has `read = all` (or specific classes like `read = system,call,agent`: queue events are in the `agent` class, since `queue` is not an AMI class, and `system` carries `FullyBooted`, which the live state's load waits for). Without the `agent` class Asterisk sends none of app_queue's events, so the SDK sees a queued call as a plain dial: no `CallQueuedEvent`, no `QueuedAt`, no queue metrics, and no member in `CallSession.AgentInterface`
2. Check ARI application name matches your Stasis app
3. Ensure you subscribe before the events fire (subscribe before `ConnectAsync` or use `ReplaySubject`)
4. Events with both an `Event` and a `Response` header (`OriginateResponse`, `ChallengeResponseFailed`, a `UserEvent` sent with a `Response` header) were taken for responses and never delivered up to 2.6.1. Upgrade.
5. Every event of a session stopped after one `OnEvent` handler threw: up to 2.6.1 a throwing or faulting handler ended delivery silently. Upgrade; a failing handler is now logged as `[AMI_EVENT] OnEvent handler threw on <EventType>` at Warning, counted on `ami.events.handler_faults`, and delivery goes on.
6. `QueueSession.CallsTimedOut` stays 0 although callers time out of the queue: the AMI user lacks the `dialplan` class. app_queue reports a timeout only by setting `QUEUESTATUS` on the caller's channel, and Asterisk sends that `VarSet` only to a user whose `read` includes `dialplan` (`read = all` includes it). Without it the timed-out callers are still counted in `CallsAbandoned`; nothing else in the queue metrics needs the class. Add it behind the filter below. See [Queue metrics](../../src/Verbara.Sdk.Sessions/README.md#queue-metrics).
7. A caller who left the queue while the AMI connection was down keeps counting in `QueueSession.CallsWaiting` until the reload after the reconnect has read the queues: a completed `QueueStatus` that no longer lists the caller ends the wait and counts the visit abandoned. A reload whose `QueueStatus` did not complete closes nothing. An answer during the outage cannot be observed, so such a visit counts abandoned too.

#### The cost of `dialplan`, and a filter that removes it

The class carries every `VarSet` and `Newexten` of every channel. Measured on a test dialplan, it was 44 % to 51 % of all the events a `read = all` user received (Asterisk 18.26.4, 22.9.0 and 23.4.1); on queues with `setqueuevar` and `setqueueentryvar` on, adding it to a user's `read` added 125.5 % bytes (Asterisk 20.20.1, 22.9.0 and 23.4.1).

**Recommended:** read `dialplan` behind an `eventfilter` that drops the class's events except the `VarSet` of `QUEUESTATUS`, and passes every other event. The same text works on Asterisk 20, 22 and 23, whose `manager.conf.sample` documents this filter syntax. Put it in the AMI user's section of `manager.conf`:

```ini
eventfilter(action(exclude),name(Newexten)) =
eventfilter(action(exclude),name(VarSet),header(Variable),method(regex)) = ^([^Q]|Q([^U]|$)|QU([^E]|$)|QUE([^U]|$)|QUEU([^E]|$)|QUEUE([^S]|$)|QUEUES([^T]|$)|QUEUEST([^A]|$)|QUEUESTA([^T]|$)|QUEUESTAT([^U]|$)|QUEUESTATU([^S]|$)|QUEUESTATUS.)
```

The second line excludes every `VarSet` whose variable is not `QUEUESTATUS`; the pattern spells "not `QUEUESTATUS`" out because the filter's regular expressions have no lookahead. Behind it, on Asterisk 20.20.1, 22.9.0 and 23.4.1, the user received 1.28 % more bytes than the same user without `dialplan` (4.36 % more on calls that time out), and `CallsTimedOut` counted every timeout. The consequence: the user receives no other `VarSet` and no `Newexten`, so your own `VarSetEvent` observers see only `QUEUESTATUS`. No feature of the SDK reads another `VarSet`.

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

**A call that ended during a reload came back, or a call was opened twice** (up to 2.6.1). A load reads the channels with `Status` while events keep arriving. When a channel hung up while that answer was being read, the load took the older snapshot for the newer news and put the channel back: Live raised `ChannelAdded` for it again, and in `Verbara.Sdk.Sessions` it came back as a second copy of a leg in its call, or, for a channel the SDK had never held, as a ghost call of its own. Its hangup had already been seen, so a call with such a leg still up never ended. A call that started during a reload could also be opened twice for one `linkedid`. Upgrade:

- A hangup seen while a load reads `Status`, and a channel another load removed meanwhile, are newer than the snapshot: the snapshot no longer brings that channel back.
- A channel is admitted once, whether the snapshot or its `Newchannel` comes first. A second `Newchannel` for a channel Live already holds raises nothing.
- A `linkedid` holds one call. A leg that arrives after its call ended opens a new call, which `GetByLinkedId` then returns; the ended call keeps its participants and its ending.

**A call ended by a reload carries `cause` = `reload` although it was hung up.** When events back up — handlers slower than the rate Asterisk sends them — a reload can end a call before its `Hangup` is processed, because the answer to `Status` does not wait behind the events already queued. The call ends on time, with the reload marker (`cause` = `reload` in its metadata) and without the hangup's cause; when the channel's answer was still queued too, the call's state does not reflect it either. Keep event handlers short so the event queue does not fall behind.

**How long a reload remembers what it saw.** While a load reads `Status`, Live records every hangup it sees so the snapshot cannot bring the channel back; the record is dropped when the read ends. Over an `AmiConnection` the read lasts at most `DefaultEventTimeout` (5 s by default). With `DefaultEventTimeout` set to `TimeSpan.Zero`, or with an `IAmiConnection` of your own, the read, and what it keeps, lasts as long as that connection lets a `Status` go unanswered.

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

**After a failed `ConnectAsync`.** A `ConnectAsync` that throws (nothing listening, the login refused, the connect timed out) leaves `State` at `Disconnected` and announces `Connecting → Disconnected` with `ByCaller` set and the exception as `Cause` (no cause when your own cancellation token withdrew the attempt); the attempt's socket is released. Call `ConnectAsync` again to retry. Until 2.6.1 the connection read `Connecting` after such a failure, although no attempt was running. A `ConnectAsync` on a connection that is `Connected`, `Reconnecting` or already `Connecting` throws `InvalidOperationException` and changes nothing: with `AutoReconnect` on, a lost connection reconnects on its own.

**Connecting again after a loss.** With `AutoReconnect` off, or once the reconnect has given up, nothing reconnects the connection: call `ConnectAsync` as soon as you see the loss, on `Disconnecting`, `Disconnected`, `IsLoss`, `IsFinal` or by polling `State`. A `ConnectAsync` made while the lost session is still being released (its socket, reader and heartbeat, and the delivery of the events it had buffered) waits for that release, then connects. The wait is bounded by `ConnectionTimeout` (then it throws `OperationCanceledException`, "The connection was ended during the connect.", and a later call waits again), by your cancellation token, and by your own `DisconnectAsync` or `DisposeAsync` (then it throws `ObjectDisposedException`), so a connect can take up to twice `ConnectionTimeout`. A `ConnectAsync` made from inside the connection's own event dispatch (an `OnEvent` handler, an observer's `OnNext`) does not wait, because the release waits for that dispatch: it throws that `OperationCanceledException` at once. Start the connect from a `StateChanged` handler without blocking on it (`_ = Task.Run(...)`): a handler that blocks on it holds every later notification for up to `ConnectionTimeout`. See [connecting again after a loss](ami-connection-state-and-health-migration.md#connecting-again-after-a-loss).

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

## Session Reconciliation Sweep

The sweep `AddVerbaraSessions` registers checks the held calls against Asterisk's channel snapshot once a held call is older than `SessionOptions.DialingTimeout`, and ends a call whose channels Asterisk no longer reports. See [the migration guide](call-session-ending-migration.md#the-reconciliation-sweep-and-long-answered-calls) for what changed in 2.7.0.

**Calls end with no hangup cause and `Metadata["cause"] == "reload"`, outside any reconnect.** The sweep found their channels gone: their `Hangup` never reached the SDK, most often because the AMI user filters it out or because the event pump dropped it (`ami.events.dropped`). Each such call ends once, `Completed` if it was answered and `Failed` otherwise. If they are frequent, find out why the hangups are missing; the sweep is what ends those calls at all.

**Calls whose hangup was lost are never ended.** Check, in this order:
- the server log for `[LIVE] Status refused: …` (Warning, once per AMI session): the AMI user may not run `Status`. Put `system`, `call` or `reporting` in its `write` line in `manager.conf` (`write = all` includes them);
- the `verification` tag on the `session reconciliation` span: `skipped:not-connected` while the connection is down, `skipped:outcome-not-reported` when the server's `IAmiConnection` does not report how an action ended (`ReportsEventActionOutcome` is false: a connection of your own that does not forward it and the outcome overload), `skipped:load-in-flight` while a load of the same server runs (the next tick verifies);
- `sessions.unverifiable` on the same span: a held call none of whose channels the channel table holds cannot be proved gone by a snapshot and is left alone;
- the registration: `AddVerbaraSessionsMultiServer` registers no sweep. Call `VerbaraServer.ReconcileChannelsAsync()` per server on a schedule of your own.

**The snapshots are too heavy for the PBX.** One `Status` per interval is sent whenever some held call is older than the dialing timeout; its size grows with the channels Asterisk holds (see [high-load-tuning.md](high-load-tuning.md#session-reconciliation)). Lengthen the interval:
```json
{ "Sessions": { "ReconciliationInterval": "00:01:00" } }
```
or switch the sweep off with `ReconciliationInterval = Timeout.InfiniteTimeSpan` — no timer, no snapshot; calls whose hangup was lost then stay held until a reconnect's reload. Any other interval of zero or less fails the host's start with `ArgumentOutOfRangeException`.

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

**Cause:** the number of live sessions has reached `AudioSocketOptions.MaxConcurrentSessions`. The limit is checked and taken in one atomic step, so a burst of calls arriving together is admitted up to the limit and the rest are refused; a deployment that ran past its limit in bursts on 2.6.1 and earlier now sees this Warning instead. A call that comes back with a UUID a live session holds is not counted against the limit, since it takes that session's place.

**Solutions:** raise `MaxConcurrentSessions` if the host has room, or spread calls over more servers. The refused connection receives a hangup frame, with the same Asterisk 20 and later / Asterisk 18 behaviour as above. Refused connections are not counted in `audiosocket.connections.accepted` and open no `audiosocket.session` activity.

### `StreamLimitReached`: an ARI audio server is at `MaxConcurrentStreams`

**Symptoms:** `[AudioSocket] Stream limit reached (<limit>), rejecting connection` from `Verbara.Sdk.Ari.Audio.AudioSocketServer`, or `[WebSocketAudio] Stream limit reached (<limit>), rejecting connection` from `Verbara.Sdk.Ari.Audio.WebSocketAudioServer`, at Warning. The ExternalMedia call whose connection was refused gets no audio.

**Cause:** the connections held by the ARI audio servers have reached `AudioServerOptions.MaxConcurrentStreams`. The count belongs to the options instance, not to one server: every `AudioSocketServer` and `WebSocketAudioServer` built with the same instance, as `AddVerbara` registers them, draws from it, so connections held by one server can fill the limit for the other. The Warning comes from the server that refused the connection, which is not always the one holding the places. A connection takes a place from the moment it is accepted, before it has identified itself.

**Solutions:** raise `MaxConcurrentStreams` if the host has room, or build the two servers with separate `AudioServerOptions` instances so each has its own count. The refused connection is closed before anything is read from it, and each refusal is counted on `audio.connections.refused` (meter `Verbara.Sdk.Ari.Audio`); it opens no stream, so no `audio.streams.*` count moves. A connection refused while the server is stopping is closed the same way but neither logged nor counted, since the server is stopping, not full.
