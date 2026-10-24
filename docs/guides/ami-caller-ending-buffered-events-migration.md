# Migrating to an AMI ending that delivers none of the events still buffered

Required by the release cadence: a minor that carries a breaking change ships a migration guide.

One behaviour changed in `Verbara.Sdk.Ami`. No API signature changed, so nothing stops compiling. What
changes is which events reach your handlers after **you** end the connection.

## What happened

`AmiConnection` reads events off the socket into a bounded buffer, the event pump, and a consumer hands
them one at a time to `OnEvent` and to the observers. Until this release, ending the connection with
`DisposeAsync` or `DisconnectAsync` delivered **every event still in that buffer** before the call
returned. The pump only looked at its cancellation while it waited for a new event, never between the
events it already held. So the call waited for the whole buffer: with a slow handler and a large
`EventPumpCapacity`, a close could take minutes, and a host shutdown could not cut it, because the
token a host passes to `DisconnectAsync` bounds only the Logoff write.

Nothing documented that delivery. `IAmiConnection.DisconnectAsync` promised that the ending stops the
reconnect loop, releases the socket and reports `Disconnected`. And an ending asked from inside a
handler already delivered no later event.

## What the SDK does now

- **Your ending waits for the event whose handler is running, and delivers none of the events still
  buffered.** That holds wherever the ending lands: with the connection up, while a lost connection is
  still delivering its buffer, and while the reconnect loop waits out its backoff.
- **The discarded events are counted, not lost silently.** One `Warning` per released buffer — two when
  your ending lands while a redialled session is waiting for the lost one's delivery — with the count:

  ```
  [AMI_EVENT] Discarded on caller ending: count=<n>
  ```

  and, for each `Warning`, one measurement of `n` on the existing `ami.events.dropped` counter, tagged
  `reason=caller_ending`. Nothing is logged or counted for a buffer that was empty.
- **A connection lost without your ending is unchanged.** When the peer closes or the heartbeat gives
  up, the buffered events of the lost session are still delivered, in order, before `Reconnected` or
  `Disconnected`. They are events Asterisk really sent, a `Hangup` that happened, and they are not
  dropped. If you end the connection while that delivery is running, the delivery stops after the event
  in progress, as above.

## What you have to do

**Update the package.** There is nothing to edit for most consumers.

Three situations need a look.

### 1. You alert on `ami.events.dropped`

The counter now carries a `reason` tag:

| `reason` | when | what to do |
|---|---|---|
| `buffer_full` | the buffer was full and refused an event (one per event, with an `[AMI_EVENT] Dropped` Warning) | handlers are slower than the event rate: increase `EventPumpCapacity`, speed up or offload the handlers — see [high-load-tuning.md](high-load-tuning.md) |
| `caller_ending` | your `DisposeAsync` or `DisconnectAsync` found events still buffered (one measurement per released buffer, with the count: one per ending, two when it lands while a redialled session waits for the lost one's delivery) | expected; nothing to tune |

**An alert on `ami.events.dropped > 0` must filter on `reason=buffer_full`**, or it fires on every
close that finds events buffered. Before this release every measurement was a full buffer and had no
tag, so an unfiltered alert was correct; it no longer is.

### 2. You relied on receiving the tail after your own close

If your code ends the connection and then expects the handler to see the events that were already on
their way, for example the last `Hangup`s before a shutdown, it no longer does. Decide what you need
**before** you end the connection: wait in your own code for the event you are waiting on, then end it.
Once you end it, nothing still buffered reaches a handler.

If you end the connection from inside a handler, nothing changes in what you receive: that path already
delivered no later event. What changes is that the events it skipped are now counted and logged as
above, after the handler returns, and `AsyncEventPump.ProcessedEvents` no longer counts them.

### 3. You use `AsyncEventPump` directly

`AsyncEventPump.DisposeAsync` now stops after the event in progress too: it waits for the running
handler, delivers nothing after it, and does not count the undelivered events in `ProcessedEvents`.
If you need the buffer delivered before the pump is released, stop enqueueing, wait until
`PendingCount` reaches 0, then dispose.

## What did not change

- The event in progress is still awaited: the ending does not cancel a running handler.
- The `IAmiConnection` and `AsyncEventPump` signatures, the counter's name and its unit.
- `[AMI_EVENT] Dropped` keeps its meaning and its action: a full buffer.
- A lost connection's delivery, and the reconnect that follows it.

## How to check

End a connection that has a slow handler and events arriving, and look at the log: the ending returns
once the running handler returns, the handler sees nothing after it, and one
`[AMI_EVENT] Discarded on caller ending: count=<n>` Warning reports how many it did not see. On a
`MeterListener` or an OpenTelemetry exporter, the same `n` appears once on `ami.events.dropped` with
`reason=caller_ending`.

## 2.9.0: a lost connection logs in again while its events are still being delivered

**Breaking.** No API signature changed. What changes is what your handlers see while a lost connection's
buffer is being delivered with `AutoReconnect` on.

### What happened

Up to 2.8.x the reconnect loop waited for the lost session's whole buffer to be delivered before its first
attempt. With a slow handler and a full buffer the redial came buffer × handler time after the loss (20
events at 250 ms: about 5.3 s), and nothing received what Asterisk sent in the meantime: no session was
logged in to read it.

### What the SDK does now

- **The loop logs in again while the lost session's events are still being delivered.** The redial comes
  after the backoff and the login, whatever your handlers cost. The new session's events are read at once
  into a buffer of their own, so the events Asterisk sends during the delivery are kept, not missed.
- **The order is kept and nothing is discarded.** Every event of the lost session is delivered first, then
  the new session's, one at a time, as before: two handlers never run at once. If the delivery takes long
  enough to fill the new session's buffer (`EventPumpCapacity`), what it refuses is counted
  `reason=buffer_full`, with its `[AMI_EVENT] Dropped` Warning, like any full buffer.
- **`State` reads `Connected`, and sends succeed, while the lost session's events are still being
  delivered.** `StateChanged` reports the change to `Connected` when the login succeeds. An action sent from
  a handler of one of those events (`SendActionAsync`, `SendEventGeneratingActionAsync`) goes out on the new
  session, where it used to fail as not connected. Channels outlive an AMI session, so it reaches the same
  Asterisk and the same channel.
- **`Reconnected` still comes after the lost session's last event,** and before or after the new session's
  first events depending on how fast they arrive: no event of the lost session is delivered after it. A
  reload you start from `Reconnected`, such as Live's, never sees an event of the lost session afterwards.
  `Reconnected` is still raised after the reconnect's change to `Connected`.
- **Two losses in a row report one `Reconnected`.** When the redialled session is lost again before its
  `Reconnected` is delivered, that session reports none, and the session that replaces it reports one, once
  every earlier session's events have been delivered. The skipped one is logged at Debug:

  ```
  [AMI] Reconnected not delivered: session <n> is no longer current
  ```

  `ConnectionLost` (and `StateChanged`'s `IsLoss`) is still raised once per outage, before that
  `Reconnected`. Every event of every session is still delivered once, in order.
- **Your ending during that wait stops both deliveries** after the event in progress, as any ending does.
  Each released buffer reports its own remainder: two `[AMI_EVENT] Discarded on caller ending` Warnings and
  two `reason=caller_ending` measurements, whose counts add up to the undelivered events.
- **Two log lines show the overlap.** At Information, when the login lands before the delivery is over:

  ```
  [AMI] Logged in again while the lost session's events are still being delivered: pending=<n>; the new session's events follow them
  ```

  and at Debug, when the delivery is over: `[AMI] The lost session's delivery ended <ms> ms after the new login`.
- **`ami.event_pump.pending` counts every buffer.** During the overlap the gauge is the new session's
  buffer plus every lost session's buffer still being delivered, so it can read above `EventPumpCapacity`
  for a while: each buffer has its own capacity.

The 2.7.0 wording "delivered, in order, before the redial" is withdrawn. "Before `Reconnected` or
`Disconnected`", above, still holds.

### What you have to do

Nothing, unless a handler reads the connection's state to tell where it is:

- **A handler that takes `State != Connected` as "these are the lost session's events"** must use
  `Reconnected` as the boundary instead: until it is raised, the events delivered can still be the lost
  session's, with `State` already `Connected`.
- **A handler that sends an action from an event and counted on the send failing after a loss** now sends
  it. Check that the action is still what you want for an event that happened before the loss (a hang-up of
  a channel that has already ended answers with an error from Asterisk, not from the SDK).
- **A `Reconnected` handler that counts reconnects** counts one per surviving session: two losses close
  together count one, as they report one `Reconnected`.

### What did not change

- `AutoReconnect` off: the lost session's buffer is delivered, in order, before `Disconnected`.
- A caller's ending, wherever it lands, delivers nothing still buffered and waits for the event in progress.
- The backoff, `MaxReconnectAttempts` and the give-up.

### How to check

Give a handler a 250 ms delay, let 20 events buffer, and close the peer with `AutoReconnect` on. The log shows
`[AMI] Logged in again while the lost session's events are still being delivered: pending=<n>` about one
`ReconnectInitialDelay` after the loss, the handler then receives the 20 events in order, and `Reconnected`
comes after the last of them.
