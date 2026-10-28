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
4. Check AMI user has correct permissions. `read = all` and `write = all` work, but grant only what the SDK uses: see [AMI: the classes the SDK's user needs](#ami-the-classes-the-sdks-user-needs).
   ```ini
   [verbara]
   secret = your_password
   read = system,call,agent
   write = agent,reporting,originate
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

### AMI: the classes the SDK's user needs

Asterisk checks each action against the `write` line of the AMI user and sends each event only to users whose `read` line holds its class. The actions the SDK sends on its own, with the classes Asterisk 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2 register them under (`manager show command <Action>`; the three agree):

| Action | Sent by | `write` needs one of |
|--------|---------|----------------------|
| `CoreSettings` | the connection, after login, to read the Asterisk version (if refused, it tries the CLI `core show version` through `Command`, which needs `command`, and then records the version as unknown) | `system`, `reporting` |
| `Status` | the live state's load and the reconciliation sweep | `system`, `call`, `reporting` |
| `QueueStatus` | the live state's load | none |
| `Agents` | the live state's load | `agent` |
| `Ping` | the heartbeat | none |
| `Originate` | `VerbaraServer.OriginateAsync` | `originate` |
| `Atxfer` | `AttendedTransferActivity` | `call` |
| `BridgeList` | the live state's load, after a completed `Status` | none |
| `ConfbridgeListRooms`, `ConfbridgeList` | the live state's load, after a completed `Status`, to restore the conference rooms; and your code, if it lists conferences | `reporting` |

So `write = agent,reporting,originate` covers them, plus `call` if you run the attended-transfer activity; drop `originate` if you never originate through the SDK, and add the class of any other action your own code sends. For `read`, see [Missing events](#missing-events): `system`, `call` and `agent`, plus `dialplan` for the queue time-out count.

A refused action is answered `Response: Error` / `Message: Permission denied`, and Asterisk also raises a `RequestNotAllowed` security event to every AMI session whose `read` includes `security`. A user without `reporting` is refused every conference listing, whether a room exists or not. The live state's load asks `ConfbridgeListRooms` on every load, so such a user costs one security event per load, the conference rooms are not restored after a reconnect, and the load logs `[LIVE] ConfbridgeListRooms refused: …` at Warning once per AMI session (Debug after that). Put `reporting` in the SDK's user's `write` line to restore the rooms.

**No AMI account is read-only.** Asterisk 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2 register 21 actions with no class at all, so any user that has logged in can run them, whatever its `write` line says, even an empty one. Most only read or manage the session (`Login`, `Ping`, `QueueStatus`, `BridgeList`, …), but eight change the server's state: `BridgeKick`, `BridgeDestroy`, `BridgeTechnologySuspend`, `BridgeTechnologyUnsuspend`, `QueueWithdrawCaller`, `QueueChangePriorityCaller`, `QueueReset` and `QueueReload`. They cannot be restricted by class: limit who can log in (`deny`/`permit`, the secret) instead of relying on `write` to make an account harmless.

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

**Symptoms:** `[AMI_EVENT] Dropped` in logs, `ami.events.dropped` counter increasing with `reason=buffer_full`. The log reports an overflow as an episode: `[AMI_EVENT] Dropped: event_type=<type> channel=<channel>` at Warning for the first event the full buffer refuses, the same line at Debug for every later one, and one Warning with the count and the duration, `[AMI_EVENT] Dropped <n> events while the buffer was full (<ms> ms)`, when the buffer accepts an event again or is released (the session is lost, or your `DisposeAsync` or `DisconnectAsync` ends the connection). Count the drops on the summary line or on the counter, which still counts every refused event; the Warning lines no longer do. (`reason=caller_ending`, logged once per released buffer as `[AMI_EVENT] Discarded on caller ending: count=<n>`, is expected: your `DisposeAsync` or `DisconnectAsync` ended the connection with events still buffered, which it does not deliver — see [the migration guide](ami-caller-ending-buffered-events-migration.md). It needs no tuning.)

**Cause:** Event pump buffer is full. Subscribers are processing events slower than they arrive.

**Solutions:**
1. Increase buffer capacity:
   ```json
   { "Asterisk": { "Ami": { "EventPumpCapacity": 50000 } } }
   ```
2. Speed up event handlers — offload heavy work to background queues
3. Filter high-volume events (e.g., `VarSet`) early in your observer
4. Monitor with: `dotnet-counters monitor --process-id <pid> Verbara.Sdk.Ami`

A dropped `Hangup` prompts no reconciliation of its own: the session sweep does not verify on a drop alone, so the call ends at the sweep's next verification, which needs some held call older than `DialingTimeout`, or at the next reconnect's reload (see [Session Reconciliation Sweep](#session-reconciliation-sweep)). On a short-call workload that is `DialingTimeout` after the call opened at the earliest; with `Verbara.Sdk.Live` alone, the next reconnect.

See [High-Load Tuning Guide](high-load-tuning.md) for sizing recommendations, `DialingTimeout` as the knob for a dropped `Hangup`, and the route without Hosting.

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
8. An observer subscribed with `Subscribe(IObserver<ManagerEvent>)` misses what it should have done for some events, with nothing in the log: up to 2.7.0 an exception from its `OnNext` was swallowed without a trace. Upgrade; each failure is now logged as `[AMI_EVENT] Observer threw on <EventType>` at Warning with the exception and counted on `ami.events.observer_faults`. The observer stays subscribed and receives the next event; fix what throws.
9. A subscriber of `ICallSessionManager.Events` (or `IAgentSessionTracker.StateChanges`) misses notifications, the queue or agent metrics miss calls, or a call whose channels are gone keeps its session, its ending never saved: up to 2.8.x a subscriber that threw stopped that notification for every subscriber after it, the SDK's own trackers included, and the exception unwound through the engine's work for the call and through a reload's reconciliation. In 2.8.x, when a server event raised it, it showed only as `[AMI_EVENT] Observer threw on <EventType>`, naming the live state's AMI observer; before 2.8.0 it left no trace. Upgrade; each failure is now caught at the subscriber, logged at Warning as `A subscriber of the session events threw on <EventType> for session <SessionId>; …` (or `A subscriber of the agent state changes threw …`) with the exception, and counted on `sessions.events.subscriber_faults`; the others and the engine go on. A subscriber subscribed directly stays subscribed; one composed through an operator (`Where`, `Select`, `OfType`, …) is ended on its first failure, so subscribe directly and filter inside the handler. What a subscriber throws on another thread (`ObserveOn`, `async void`, `Task.Run`) is not caught. See [Domain Events](../../src/Verbara.Sdk.Sessions/README.md#domain-events).

#### The cost of `dialplan`, and a filter that removes it

The class carries every `VarSet` and `Newexten` of every channel. Measured on a test dialplan, it was 44 % to 51 % of all the events a `read = all` user received (Asterisk 18.26.4, 22.9.0 and 23.4.1); on queues with `setqueuevar` and `setqueueentryvar` on, adding it to a user's `read` added 125.5 % bytes (Asterisk 20.20.1, 22.9.0 and 23.4.1).

**Recommended:** read `dialplan` behind an `eventfilter` that drops the class's events except the `VarSet` of `QUEUESTATUS`, and passes every other event. The same text works on Asterisk 20, 22 and 23, whose `manager.conf.sample` documents this filter syntax. Put it in the AMI user's section of `manager.conf`:

```ini
eventfilter(action(exclude),name(Newexten)) =
eventfilter(action(exclude),name(VarSet),header(Variable),method(regex)) = ^([^Q]|Q([^U]|$)|QU([^E]|$)|QUE([^U]|$)|QUEU([^E]|$)|QUEUE([^S]|$)|QUEUES([^T]|$)|QUEUEST([^A]|$)|QUEUESTA([^T]|$)|QUEUESTAT([^U]|$)|QUEUESTATU([^S]|$)|QUEUESTATUS.)
```

The second line excludes every `VarSet` whose variable is not `QUEUESTATUS`; the pattern spells "not `QUEUESTATUS`" out because the filter's regular expressions have no lookahead. Behind it, on Asterisk 20.20.1, 22.9.0 and 23.4.1, the user received 1.28 % more bytes than the same user without `dialplan` (4.36 % more on calls that time out), and `CallsTimedOut` counted every timeout. The consequence: the user receives no other `VarSet` and no `Newexten`, so your own `VarSetEvent` observers see only `QUEUESTATUS`. No feature of the SDK reads another `VarSet`.

### Shutdown waits for an event handler

**Symptoms:** `DisposeAsync` or `DisconnectAsync` of an `AmiConnection` takes as long as the event handler that was running when it was called.

**Cause:** the ending waits for the event being dispatched, so a handler doing long work holds it. An `OnEvent` handler has no way to learn that the connection is ending.

**Solution:** subscribe the handler with the overload that also takes a token. On an `AmiConnection` the token is cancelled when your own `DisconnectAsync` or `DisposeAsync` ends the connection, and by nothing else (not by a loss, a reconnect or a give-up). Pass it to the handler's work; dispose the subscription to remove the handler.

```csharp
static async ValueTask ArchiveAsync(ManagerEvent evt, CancellationToken cancellationToken) =>
    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken); // long work that honours the token

static IDisposable SubscribeArchiver(IAmiConnection connection) =>
    connection.Subscribe((evt, ct) => ArchiveAsync(evt, ct));
```

The ending still waits for the handler to return: the token asks it to stop, it does not abandon it. A handler that ends with `OperationCanceledException` once your ending has cancelled its token is not a fault (one Debug line, `[AMI_EVENT] Event handler stopped on the caller's ending`); any other failure is logged and counted as an `OnEvent` handler's is. Token handlers and `OnEvent` handlers are called in the order they subscribed. An `IAmiConnection` of your own that does not forward the overload (a wrapper that forwards only the other members) still delivers every event to the handler, through its `OnEvent`, but with a token that is never cancelled; forward `Subscribe(Func<ManagerEvent, CancellationToken, ValueTask>)` to the inner connection to pass the token on.

### What a Live event handler may do

The events of `ChannelManager` (`ChannelAdded`, `ChannelRemoved`, `ChannelStateChanged`, `ChannelDialBegin`, `ChannelDialEnd`, `ChannelHeld`, `ChannelUnheld`), `QueueManager` (`QueueUpdated`, `MemberAdded`, `MemberRemoved`, `MemberStatusChanged`, `CallerJoined`, `CallerLeft`) and `MeetMeManager` (`UserJoined`, `UserLeft`, `UserStateChanged`) are raised with none of the manager's locks held, also when a reload's reconciliation raises them. The delivery rules below apply to these three managers.

`BridgeManager` and `AgentManager` also raise their events with none of their locks held.

- **A handler may call into the manager**, on its own thread or by waiting for another thread that does: a live event method, a reload's reconciliation, `Clear`, `RemoveQueue`. These calls do not need the handler to release a manager lock.
- **Each manager queues notifications in the order of the changes it reports.** While earlier notifications finish within the one-second turn wait, a channel's admission is reported before its later changes and removal, a leave follows the join it ends, and a member's removal follows its addition.
- **A change no handler is subscribed to is not queued**, and costs nothing, in each of the three managers: a handler subscribed after the change does not receive it, as with any .NET event.
- **An outer call (one not made from a handler) raises its own notifications before it returns, on its own thread, if it claims them for delivery before a competing call's one-second wait expires.** A call paused after queuing a notification but before claiming it can lose that claim: once another call's wait expires, that call may adopt the unclaimed notification even while the original call is still active. The adopted callback runs on the adopting thread; its handler failure is logged as unowned and is thrown from neither call. A pause alone does not cause adoption without another call delivering events.
- **An event waits for the earlier ones of the same manager to finish, for up to one second.** When a handler runs longer (for example, because it waits for another thread that calls into the manager), events of other threads may be raised alongside it once that second has passed. Strictly non-overlapping delivery is not guaranteed after that bound.
- **A call a handler makes into the manager**, on its own thread or on a thread or task it starts or awaits, is reported after that handler returns, and after the other subscribers of the event being raised, never nested inside it. Such a call can return before its notifications are raised.
- **A handler must not wait for another event of the same manager caused by its own calls** (for example, a `ChannelAdded` handler blocking until the `ChannelRemoved` of a hangup it handed to a task has been raised): that event is raised only after the waiting handler returns, so the wait never ends. Hand such work off instead of blocking on it.
- **The objects an event carries are the same live objects held by the manager.** A handler reads them as they are when it runs, which can be newer than the change that raised the event: after a reload refreshes both fields of a participant, both `UserStateChanged` notifications read both new values. A `ChannelAdded` handler can likewise read `State == Down` after another thread has hung up the channel. Sessions internally preserves only the state reported at snapshot admission for its initial session classification; public channel arguments remain live, with no frozen state or metadata.
- **A handler that throws does not stop the remaining notifications from being raised.** On the timely-claim path above, the first exception is rethrown from the manager call that made the change (for a call made from a handler, the call that raised that handler's event), not another thread's call. An adopted notification uses the unowned-failure rule above. Within one notification, a throwing subscriber can still prevent later subscribers from running.

Keep handlers short all the same: every later event of a `ChannelManager`, `QueueManager` or `MeetMeManager` waits for the handler running, up to that second.

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

**Expected behavior:** `VerbaraServer.StartAsync` loads the current state from Asterisk, and the `Reconnected` event loads it again after every reconnect. A load asks for the channels (`Status`); then, only when `Status` was answered, the bridges (`BridgeList`) and the conference rooms (`ConfbridgeListRooms`, then one `ConfbridgeList` per room); then the queues with their members and waiting callers (`QueueStatus`), and the agents (`Agents`). On a reload the channel, bridge, conference and queue tables are reconciled against Asterisk's answers, so a call, a bridge, a participant or a waiting caller that ended during the outage is removed, and what is still there is kept as it was. Only the agents table is cleared and loaded again. See [Counters and tables across a reconnect](#counters-and-tables-across-a-reconnect).

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

**A call ended by a reload carries `cause` = `reload` although it was hung up** (up to 2.8.x). When events backed up — handlers slower than the rate Asterisk sends them — a reload could end a call before its `Hangup` was processed, because the answer to `Status` did not wait behind the events already queued. The call ended with the reload marker (`cause` = `reload` in its metadata) and without the hangup's cause. Upgrade: over an `AmiConnection`, before a completed listing removes anything (a conference listing answered `No active conferences.` included), the reload now waits until every event received before that listing ended has reached the handlers, so a queued `Hangup` ends its call with its own cause. Two cases remain:

- The wait is bounded at 10 s per load. When a handler holds the events longer, the reload applies only what the listings add, removes and ends nothing, logs `[LIVE] <List> barrier ran out after 10 s …` at Warning, tags its activity `live.reconcile.deferred`, and runs one full reload once the handlers have caught up. A reconciliation started from inside an event handler does not wait at all. Keep event handlers short.
- Asterisk can write a channel's `Hangup` just after it answered `Status` without that channel. That call ends `cause` = `reload`, and its `Hangup` arrives after the ending: the marker means "no hangup was seen before the call ended", not "the call did not hang up".

**How long a reload remembers what it saw.** While a load reads `Status`, Live records every hangup it sees so the snapshot cannot bring the channel back; the record is dropped when the read ends. Over an `AmiConnection` the read lasts at most `DefaultEventTimeout` (5 s by default). With `DefaultEventTimeout` set to `TimeSpan.Zero`, or with an `IAmiConnection` of your own, the read, and what it keeps, lasts as long as that connection lets a `Status` go unanswered.

**Queue listing delivery.** A load buffers the complete `QueueStatus` answer before it changes queues or reports snapshot joins. It applies that answer after the queue's own dispatch fence, so a live join already received reaches Sessions first and opens one visit. A refused, interrupted or cancelled answer applies no partial rows, including rows from a rejected startup retry. Waiting behind dispatch does not shift the reported visit start: Live dates a new entry at the row's read instant, and Sessions subtracts its reported `Wait` at that same instant.

When the 10 s bound runs out, the completed queue listing admits its non-destructive table half and removes nothing. Its snapshot join notifications stay pending until the queue's own fence is reached, then report eligible callers once before the scheduled full reload. A second timeout in that scheduled load still delivers or cancels its reports without scheduling another reload. Session end or server disposal cancels pending reports and releases their read window, even while a handler still holds dispatch. That cancellation also stops the scheduled load's active read or wait and does not roll back entries already admitted. Cancellation of a caller token after its timeout load has returned does not cancel transferred work. Queue members keep newer live add/remove, pause/reason and status reports during replay, including device reports for the exact interface. Untouched member fields still refresh from the listing.

### Counters and tables across a reconnect

From 2.9.0 a reconnect no longer empties the bridge, conference and queue tables: the reload reconciles them against Asterisk's listings. What that means for what you read:

- **`live.queue.calls.joined`** counts each caller once, when it first enters Live's queue table, from a live join or from a reload's `QueueStatus`. A caller the reload finds already held is not counted again; it keeps its `JoinedAt` and reported wait. Its position follows the listing unless a newer live join/rejoin supplied a later position. Up to 2.8.x every reconnect counted every waiting caller as joined again.
- **`live.queue.calls.left`** counts every caller that leaves the table, also one the reload proves gone (it left during the outage) and one whose channel a reconciliation removed. Those removals record no `live.queue.wait_time` sample, raise `CallerLeft` and never `CallerLeaveReported`.
- **The invariant** `live.queue.calls.joined − live.queue.calls.left` = callers held holds **per process, summed over every `VerbaraServer`**, because the `live.*` instruments are shared and carry no server tag. The guarantee covers SDK-managed transitions, including `RemoveQueue`. A consumer calling `QueueManager.Clear()` or directly mutating the public collections is outside that guarantee: `Clear` removes without counting. For "waiting now", read `AsteriskQueue.Entries`, not the difference of the counters.
- **`BridgeCount`** counts the bridges inserted since the server started, destroyed or not: each bridge Asterisk reported created, and each bridge a reload found that the table did not hold. A reconnect no longer resets it; `Clear()` does. **`ActiveBridgeCount`** after a reload equals the bridges Asterisk lists, conference and holding bridges included. A bridge the reload proves destroyed raises `BridgeDestroyed` and advances `live.bridges.destroyed`; a bridge restored from the listing raises no `BridgeCreated` or `ChannelEntered`.
- **Bridge membership and lookup** follow the same manager transition, including when a barrier timeout applies only admissions. A live leave or destruction during replay cannot leave `GetBridgeForChannel` pointing at that departed bridge; cleanup of an old bridge preserves a newer live move. Separate reads or direct edits of the exposed collections do not form an atomic snapshot. Destroyed bridge objects retain their channel IDs during their existing retention period; their channels no longer resolve to that destroyed bridge through the lookup.
- **Conference rooms** are restored from `ConfbridgeList` with their participants and their muted and talking state, without `UserJoined`. A participant the listing no longer reports raises `UserLeft`, once. This needs `reporting` in the AMI user's `write` line; without it Asterisk refuses the listings and the rooms keep what they held (see [AMI: the classes the SDK's user needs](#ami-the-classes-the-sdks-user-needs)).

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

**When the reconnect gives up.** With `MaxReconnectAttempts` set to N, the connection gives up after N failed attempts, each made after its backoff delay, and then reads `Disconnected`. The first delay is `ReconnectInitialDelay`; each next one is multiplied by `ReconnectMultiplier` and capped at `ReconnectMaxDelay`. As examples, measured from the moment Asterisk was started again with the application's credentials rejected, not from the loss: 17.2–18.8 s with 1 s ×2 and N = 4; 8.1–9.2 s with 0.5 s ×2 capped at 2 s and N = 4. The give-up is announced on the connection's `StateChanged`, below: its last change, to `Disconnected`, has `IsFinal` set and carries the last attempt's exception as `Cause` (`AmiAuthenticationException` when the credentials were rejected). `ConnectionLost` is not raised for it. The log says it once, at Error, before `[AMI] Disconnected`: `[AMI] Reconnect gave up after <n> attempts; the connection will not come back`, with the same exception attached. A reconnect that your own `DisconnectAsync` or `DisposeAsync` ends writes no such line.

**With `MaxReconnectAttempts = 0`** (the default) the connection never gives up: it retries for ever, rejected credentials included, each attempt failing with `AmiAuthenticationException`. `ConnectionLost` is raised once, for the loss, and nothing after it until the connection is back; `StateChanged` reports each attempt, `Reconnecting → Connecting` and, when it fails, `Connecting → Reconnecting` with the attempt's exception as `Cause`.

**Watching the connection's state.** `IAmiConnection.StateChanged` is raised for every change of `State`, once per change, with `Previous`, `Current`, `Cause` and `ByCaller` (whether the application's own `ConnectAsync`, `DisconnectAsync` or `DisposeAsync` made it). Subscribe to it instead of polling `State`:

- `IsLoss` (the connection left `Connected` without the application asking) marks the start of an outage, once per outage, and is delivered before `ConnectionLost`.
- A reconnect's change to `Connected` is delivered before its `Reconnected`.
- `IsFinal` (a change to `Disconnected` the application did not ask for) means nothing will reconnect the connection: `AutoReconnect` is off, or the reconnect gave up. Only a new `ConnectAsync` connects it again.
- The application's own connect and ending report only changes with `ByCaller` set, and the ending's last ones can arrive after `DisconnectAsync` or `DisposeAsync` has returned.

The changes form a chain: each one's `Previous` is the `Current` of the one before it. An `IAmiConnection` of your own that does not implement the event raises nothing.

**The reconnect does not wait for the lost session's events** (since 2.9.0). With `AutoReconnect` on, the connection logs in again after the backoff while the events the lost session had buffered are still being delivered; the new session's events are buffered meanwhile and delivered after the last of them, so the order holds and nothing is discarded. During that overlap `State` already reads `Connected` and an action sent from a handler goes out on the new session. `Reconnected` is raised once the lost session's last event has been delivered, and only for a session still current then: when the redialled session is lost again first, it reports none (`[AMI] Reconnected not delivered: session <n> is no longer current`, Debug) and the next one reports its own. The overlap is logged at Information, `[AMI] Logged in again while the lost session's events are still being delivered: pending=<n>; …`, and its end at Debug, `[AMI] The lost session's delivery ended <ms> ms after the new login`. `ami.event_pump.pending` counts every buffer still being delivered plus the new one, so it can read above `EventPumpCapacity` during the overlap. Until `Reconnected` the session reconciliation sweep skips its verification (`skipped:reconnect-pending`, below). See [the migration guide](ami-caller-ending-buffered-events-migration.md#290-a-lost-connection-logs-in-again-while-its-events-are-still-being-delivered).

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

The sweep `AddVerbaraSessions` registers checks the held calls against Asterisk's channel snapshot once a held call is older than `SessionOptions.DialingTimeout`, and ends a call whose channels Asterisk no longer reports. `AddVerbaraSessionsMultiServer` registers the same sweep for every server of the pool, each server checked on its own. See [the migration guide](call-session-ending-migration.md#the-reconciliation-sweep-and-long-answered-calls) for what changed in 2.7.0, and [the sweep on a multi-server host](call-session-ending-migration.md#the-sweep-on-a-multi-server-host-280) for 2.8.0.

**Calls end with no hangup cause and `Metadata["cause"] == "reload"`, outside any reconnect.** The sweep found their channels gone: their `Hangup` never reached the SDK, most often because the AMI user filters it out or because the event pump dropped it (`ami.events.dropped`). Each such call ends once, `Completed` if it was answered and `Failed` otherwise. If they are frequent, find out why the hangups are missing; the sweep is what ends those calls at all.

**Calls whose hangup was lost are never ended.** Check, in this order:
- the server log for `[LIVE] Status refused: …` (Warning, once per AMI session): the AMI user may not run `Status`. Put `system`, `call` or `reporting` in its `write` line in `manager.conf` (`write = all` includes them);
- the `verification` tag on the `session reconciliation` span: `skipped:not-connected` while the connection is down, `skipped:outcome-not-reported` when the server's `IAmiConnection` does not report how an action ended (`ReportsEventActionOutcome` is false: a connection of your own that does not forward it and the outcome overload), `skipped:reconnect-pending` from a reconnect's login until the server has handled its `Reconnected` (the lost session's events may still be in delivery and the reload has not run; the next tick verifies), `skipped:load-in-flight` while a load of the same server runs (the next tick verifies);
- `sessions.unverifiable` on the same span: a held call none of whose channels the channel table holds cannot be proved gone by a snapshot and is left alone;
- `ami.events.dropped` with `reason=buffer_full`: a `Hangup` the full buffer dropped prompts no reconciliation of its own. Its call ends only at the sweep's next verification or at the next reconnect's reload, whichever comes first. The sweep verifies only while some held call is older than `DialingTimeout`, so the call becomes eligible at the first tick that is both after the drop and after the call is `DialingTimeout` old (with the defaults and no other call that old, 60–90 s after it opened). That is not a ceiling: a skip above moves the verification to the next tick, and a reconciliation whose 10 s wait for the buffered events runs out removes nothing. Lowering `DialingTimeout` makes the call eligible sooner, at one `Status` per tick whenever any held call is older than it ([high-load-tuning.md](high-load-tuning.md#session-reconciliation)). Without the sweep (`Verbara.Sdk.Live` alone, or `ReconciliationInterval = Timeout.InfiniteTimeSpan`) only the next reconnect ends it, unless the host reconciles on `ami.events.dropped` itself (same guide). Size `EventPumpCapacity` and the handlers so the buffer does not fill (see [Events dropped (AMI)](#events-dropped-ami));
- on a multi-server host, the id the session engine was attached with: the pool sweep picks a server's candidates by its id in the pool, so attach each server with `AttachToServer(server, id)` under that id. A session of a server that left the pool is never verified; the tick's Debug line counts it as `serverless`.

**On a multi-server host, a server receives two `Status` per interval.** The host still runs a loop of its own over `pool.Servers` that calls `ReconcileChannelsAsync`, as the 2.7.0 documentation advised. Since 2.8.0 the registration runs that sweep: remove the loop, or switch the pool sweep off.

**The snapshots are too heavy for the PBX.** One `Status` per interval is sent whenever some held call is older than the dialing timeout — on a multi-server host, to each server holding such a call; its size grows with the channels Asterisk holds (see [high-load-tuning.md](high-load-tuning.md#session-reconciliation)). Lengthen the interval in the registration's `configure` delegate:
```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddVerbaraSessions(o => o.ReconciliationInterval = TimeSpan.FromMinutes(1));
```
or switch the sweep off with `ReconciliationInterval = Timeout.InfiniteTimeSpan` — no timer, no snapshot; calls whose hangup was lost then stay held until a reconnect's reload. Any other interval of zero or less fails the host's start with `ArgumentOutOfRangeException`. No SDK registration binds a `Sessions` configuration section: to set these from configuration, bind the section inside the delegate (`o => builder.Configuration.GetSection("Sessions").Bind(o)`, with `<EnableConfigurationBindingGenerator>true</EnableConfigurationBindingGenerator>` for Native AOT). There, `Timeout.InfiniteTimeSpan` is written `"-00:00:00.001"`.

---

## Multi-Server Pools

**`Server b reported channel id … which the session indexes already hold for a call of server a`** (Warning, once per pair of servers, then Debug): the session manager keeps one channel-id index and one `linkedid` index for every server attached to it, so two servers that report the same id can have their calls correlated together; give every Asterisk its own `systemname` in `asterisk.conf`, or, if both server ids watch the same Asterisk, attach it once.

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

**Solutions:** raise `MaxConcurrentStreams` if the host has room, or build the two servers with separate `AudioServerOptions` instances so each has its own count. The refused connection is closed before anything is read from it, and each refusal is counted on `audio.connections.refused` (meter `Verbara.Sdk.Ari.Audio`, tagged `audio.transport` with `audiosocket` or `websocket` for the server that refused it); it opens no stream, so no `audio.streams.*` count moves. A connection whose session had already started (an AudioSocket connection that identified itself, or a WebSocket one past its upgrade) and that the server closes because its stop began before announcing it is neither logged nor counted on `audio.connections.refused`, since the server is stopping, not full; it counts one `audio.streams.opened` and one `audio.streams.closed`.
