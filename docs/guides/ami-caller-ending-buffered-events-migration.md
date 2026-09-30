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
- **The discarded events are counted, not lost silently.** One `Warning` per ending, with the count:

  ```
  [AMI_EVENT] Discarded on caller ending: count=<n>
  ```

  and one measurement of `n` on the existing `ami.events.dropped` counter, tagged `reason=caller_ending`.
  Nothing is logged or counted when the buffer was empty.
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
| `caller_ending` | your `DisposeAsync` or `DisconnectAsync` found events still buffered (one measurement per ending, with the count) | expected; nothing to tune |

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
