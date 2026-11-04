# Upgrading from 2.9.0 to 2.10.0

This is the one migration guide for 2.10.0. It has one section per change a reader may have to act on, and says who
each one reaches and what to do. The [CHANGELOG](../../CHANGELOG.md) has the full entry for each.

| Package | Change | Kind |
|---|---|---|
| `Verbara.Sdk.Resilience` | [`circuit.state` reports the breaker's current state](#circuitstate-reports-the-breakers-current-state) | metric meaning, BREAKING |
| `Verbara.Sdk.Ari` | [A client that only makes REST calls can turn its events off](#a-client-that-only-makes-rest-calls-can-turn-its-events-off) | new option, new state |
| `Verbara.Sdk.Ari` | [An observer that throws no longer stops delivery](#an-observer-that-throws-no-longer-stops-delivery) | behaviour |
| `Verbara.Sdk.Ari` | [`OriginateAsync` with an extension runs the dialplan](#originateasync-with-an-extension-runs-the-dialplan) | fix, behaviour |
| `Verbara.Sdk.Ari` | [A WebSocket audio stream whose connection failed ends with `Error`](#a-websocket-audio-stream-whose-connection-failed-ends-with-error) | behaviour |
| `Verbara.Sdk.Live` | [A channel subscriber's failure stays with that subscriber](#a-channel-subscribers-failure-stays-with-that-subscriber) | behaviour, BREAKING |
| `Verbara.Sdk.Live` | [A live channel follows its dialplan location and caller ID](#a-live-channel-follows-its-dialplan-location-and-caller-id) | fix, behaviour |
| `Verbara.Sdk.Ami` | [A parking event names its channel and where it is parked](#a-parking-event-names-its-channel-and-where-it-is-parked) | fix, new members |
| `Verbara.Sdk.Ami` | [`NewExtenEvent` and `NewCallerIdEvent` carry what the wire sends](#newextenevent-and-newcalleridevent-carry-what-the-wire-sends) | fix, new members |

## ARI

### A client that only makes REST calls can turn its events off

**What changed.** `AriClientOptions.EventsEnabled` is new and defaults to `true`, which behaves as before. With `false`,
or `Asterisk:Ari:EventsEnabled` set to `false`, the client opens no events WebSocket and registers no Stasis application.
Its REST resources work as on any client. `AriConnectionState.RestOnly` is new, appended after `Faulted`, so no existing
value changes its number: such a client reads it from construction until disposal. `ConnectAsync` and `DisconnectAsync`
do nothing, `Subscribe` throws `InvalidOperationException` naming the option, and the `ari` health check asks Asterisk once
per run: one `GET /ari/asterisk/ping`, bounded at 5 s, an answer `Healthy` and anything else `Unhealthy`.

**Who is affected.** Nobody who does not set the option. A `switch` over `AriConnectionState` with no default arm sees one
more value only on a client that opted in.

**What to do, if you turn it on.** The [package README](../../src/Verbara.Sdk.Ari/README.md#rest-only-clients) lists what
such a client cannot do. Before you switch a client over:

- `Application` stays required, and now names an application **someone else** registers. Asterisk answers `200` to an
  originate into an application nobody registered and the channel is gone within 2 ms, so neither `State` nor the health
  check can tell.
- A host built by `AddVerbara` still needs AMI at start, because the AMI connection dials then and a failed dial stops the
  host. What needs no Asterisk is the ARI client, its hosted service and its check. A host that composes its own services
  is not affected.
- A health endpoint that reads `ari` now sends one request per run, counted on `ari.rest.requests.sent`, and reads
  `Unhealthy` while Asterisk is down.

**A host that keeps one client per Asterisk node for REST calls** is who this option is for. Turning events off on that
client removes a WebSocket nobody reads, and stops its replicas from replacing each other on it. Check two things first.
If the client names an application of its own so that it does not take the dialplan's, nothing registers that name any
more, so an originate into it dies: point `Application` at an application your consumer client registers, or originate
with an extension. And a boot check that the two names differ guards against something that can no longer happen once
this client registers nothing, so decide whether it stays.

### An observer that throws no longer stops delivery

**What changed.** An exception from an observer's `OnNext` used to end delivery to every observer for the rest of the
connection, reconnects included, with the client still reading `Connected` and nothing logged. One from `OnCompleted` left
`DisposeAsync` before it released the HTTP client. Now a failure reaches neither the other observers nor the client: the
event goes on to the next observer, later events keep arriving, and a direct subscriber stays subscribed. Each failure logs
one Warning, `[ARI] Observer threw on <EventType>`, and adds one to `ari.events.subscriber_faults`. This holds for every ARI
client, whatever `EventsEnabled` says.

`ari.events.dispatched` and `ari.event.dispatch` now also record an event an observer threw on.

**Who is affected.** Code that relied on a throw to stop receiving: the observer now keeps being called. `DisposeAsync` no
longer throws what an observer threw.

**What to do.** To stop receiving, dispose the subscription. Add `ari.events.subscriber_faults > 0` to your alerts. An
observer composed through an Rx operator is still ended by that operator on its first failure, and only what throws on the
delivering thread is caught.

### `OriginateAsync` with an extension runs the dialplan

**What changed.** `Channels.OriginateAsync` with an extension no longer sends `app`. It always did, and Asterisk reads the
two as exclusive with `app` winning: on all six images measured, 18/18, the channel entered Stasis and never ran the
dialplan, or died at once if nobody registered the application. A caller who passed `extension` and `context` now gets the
dialplan. A call with no extension sends `Application` as before.

**Who is affected.** A caller who passes an extension and came to rely on the channel entering Stasis.

**What to do.** To put a channel in Stasis, call `OriginateAsync` with no extension, or `Channels.CreateAsync`.

### A WebSocket audio stream whose connection failed ends with `Error`

**What changed.** A WebSocket audio stream whose connection failed while it was live (a reset, or an end with no close
frame from the peer) publishes `AudioStreamState.Error` and then `Disconnected` on `StateChanges`. In 2.9.0 it published
`Disconnected` alone. A peer's close frame and an ending on this side still publish `Disconnected` alone. Disposing a stream
whose peer never answers the close now gives up after 1 s, not 2 s.

**Who is affected.** Code that subscribes to `StateChanges` of a WebSocket stream and treats `Error` as something only an
AudioSocket error frame produces, or that waits for `Disconnected` as the first ending state.

**What to do.** Handle `Error` as "the connection failed" and keep waiting for `Disconnected` as the last state, as an
AudioSocket stream already required. AudioSocket streams are unchanged.

## Live

### A channel subscriber's failure stays with that subscriber

**What changed.** A handler of a `ChannelManager` event (`ChannelAdded`, `ChannelRemoved`, `ChannelStateChanged`,
`ChannelDialBegin`, `ChannelDialEnd`, `ChannelHeld`, `ChannelUnheld`) that throws no longer skips the handlers subscribed
after it, and its exception no longer leaves the table. Every handler receives the event in subscription order; each failure
logs one Warning, `[CHANNEL] Subscriber failed: event=<Event> unique_id=<UniqueId>; …`, with the exception, and adds one to
`live.channels.subscriber_faults` (meter `Verbara.Sdk.Live`). A reload's reconciliation finishes past a throwing handler,
where it used to stop at the first one. The SDK's own subscribers, the session engine included, are reported the same way.
`OutOfMemoryException` is not caught. `QueueManager` and `MeetMeManager` keep their rule.

The CHANGELOG labels this `Changed — BREAKING`: the 2.9.0 troubleshooting guide documented the first failure as rethrown
from the call that made the change.

**Who is affected.** Code that catches around a direct `ChannelManager.On*` call to learn that a handler failed: nothing
is thrown there any more. An alert on `[AMI_EVENT] Observer threw on <EventType>` or on `ami.events.observer_faults` that
was meant to catch a Live channel handler's failure: those no longer move for it.

**What to do.** Read `live.channels.subscriber_faults`, or the Warning above, instead. Fix what throws. Only what throws on
the delivering thread is caught; a handler that starts work on another thread or task owns that work's failures.

### A live channel follows its dialplan location and caller ID

**What changed.** `AsteriskChannel.Context`, `Extension` and `Priority` used to keep the values the channel was created
with (or that a reload read). On a connection that receives `Newexten`, they now move to each step Asterisk reports, a
`Redirect` and a `Goto` included, and `ExtensionHistory` records each visit, the last 100, oldest first; it is read as a
copy, safe on any thread. `CallerIdNum` and `CallerIdName` now take what a `NewCallerid` reports, `<unknown>` included.
No channel event is raised for either. `ChannelManager.OnNewExten` and `OnNewCallerId` are new, like the other `On*`
methods. A reload does not move a held channel back to an older snapshot's location.

**Who is affected.** A reader that took these members for where the call started or who called: they now say where the
call is and the caller ID it carries now. Only on a connection that receives `Newexten`, which needs the `dialplan` class in
the AMI user's `read`, and not behind the filter the troubleshooting guide recommends, which excludes `Newexten` on purpose.
`NewCallerid` is class `call`. A session does not move: `CallSession.Context`, `Extension` and `Direction`, and each
participant's `CallerIdNum` and `CallerIdName`, keep the values read when they were created.

**What to do.** For the start of the call, read the session's `Context` and `Extension`. To have channels follow their
location, see [the cost of `dialplan`](troubleshooting.md#the-cost-of-dialplan-and-a-filter-that-removes-it): delete the
filter's `name(Newexten)` line and accept the volume it adds.

## AMI

### A parking event names its channel and where it is parked

**What changed.** Asterisk sends the parked channel of `ParkedCall`, `UnParkedCall`, `ParkedCallTimeOut` and
`ParkedCallGiveUp` only under `Parkee`-prefixed headers, so `Channel`, `UniqueId`, `Exten` and the other channel members of
`ParkedCallEvent`, `UnparkedCallEvent`, `ParkedCallTimeOutEvent` and `ParkedCallGiveUpEvent` were always null. They now
hold the parked channel's values. The four events derive from the new `ParkingEventBase`, itself a `ChannelEventBase`,
which adds `ParkingSpace`, `ParkingLot` (from the header Asterisk spells `Parkinglot`), `ParkingTimeout`, `ParkingDuration`
and `ParkerDialString`, as strings. `ParkedCallEvent.Timeout` now holds the number of `ParkingTimeout`.

**Who is affected.** Source and binary compatible. Code that read the space from `Exten` (no supported Asterisk sends an
`Exten` on these events): `Exten` now holds the parked channel's own extension, not the space.

**What to do.** Read the space from `ParkingSpace`. To hang up or find a parked call, use `Channel`.

### `NewExtenEvent` and `NewCallerIdEvent` carry what the wire sends

**What changed.** `NewExtenEvent` gains `Context`, `Exten` and `Priority` (a number), with the names every channel event
uses; `Extension` stays and holds the same value as `Exten`. `NewCallerIdEvent.CidCallingPresTxt` now holds the
`CID-CallingPres` header whole, for example `0 (Presentation Allowed, Not Screened)`, and `CidCallingPres` the number it
starts with, or null when it starts with none; both were always null. A number assigned to `CidCallingPres` still wins.

**Who is affected.** Code that read `RawFields["Context"]` or `RawFields["Priority"]` of a `Newexten`: it still works.

**What to do.** Read the typed members.

## Resilience

### `circuit.state` reports the breaker's current state

**What changed.** The `circuit.state` gauge reports the state a key's circuit is in when it is observed, not the last
value a call wrote. A tripped circuit that sees no more calls used to read `2` (open) forever; it now reads `2` until its
open window has elapsed and `1` (half-open) after that, with no call needed. The instrument keeps its name, meter, `key`
tag and encoding (`0` closed, `1` half-open, `2` open), and a key still appears from its first success, rejection or trip.

The CHANGELOG labels this `Fixed — BREAKING`: the gauge was documented as the current state and did not deliver it, and
restoring that breaks what was built on the old reading.

The values now mean:

- `2`: a call now is rejected with `CircuitBreakerOpenException`.
- `1`: the open window has elapsed and no call has been admitted since. It does not mean that one probe is in flight.
  The breaker has no such state: every call is admitted once the window has elapsed, and the first one resets the
  circuit, so `1` lasts until that call.
- `0`: anything else. That includes a circuit whose call after the window failed below the threshold: the old reading
  stayed at `2`, and the next call is admitted. If that failure reaches the threshold, the gauge reads `2` for a full
  open window from the failure.

**Who is affected.** Anyone with an alert, a dashboard or a health check that needs `circuit.state` to stay at `2`.
That reading was unbroken only because it was stale. With traffic and a failing dependency the circuit really cycles:
open for the window, then closed until `threshold` failures trip it again, so a sample in that gap reads `0` or `1`. A
rule such as "`circuit.state` is `2` for five minutes" restarts its timer on such a sample and may not fire during the
outage it was written for, and a timer started when the state last changed is reset the same way.

**What to do.** Keep `circuit.state` for what it now answers, which is whether a call to that key would be rejected right
now: `circuit_state == 2`, under the name Prometheus exports.

Move an alert that needs an unbroken `2` to `circuit.opened`, exported as `circuit_opened_total`, which counts
trips: an increase over a window says that a key tripped, whether or not a sample lands in the gap. It is not a drop-in
replacement. The threshold and the window are yours to choose, with these in mind:

- A rule with a five-minute `for` ignored a lone trip, and an alert on any increase can page on one.
- A key's first trip since the process started may not appear in `increase()`. Nothing adds 0 before it, so a new series
  starts at `1`, and unless the window holds an earlier, higher sample of that series there is no increase to read.
- The window depends on each policy and on its traffic. Under a steady outage, the time between trips is the open duration
  plus however long the threshold's failures take to arrive. A key called once an hour trips far less often than once per
  open duration, so size the window from the key's call rate as well as its open duration.

An escalation that waits on a threshold measured from the last state change may never reach it under a real cycle,
because every change restarts the measure. Count trips, or measure from the first trip of the window.

A health check that observes the gauge through a `MeterListener` can call
`ResiliencePolicy.GetCircuitState(key)` instead. It returns the same reading as a
`ResilienceMetrics.CircuitStateValue` for that policy's own circuit, reads `Closed` for a key with no circuit, and
changes nothing. Where two policies share a key, the gauge follows the one that reported the key last, and each policy's
`GetCircuitState` keeps reading its own circuit. No code needs to change: the member is additive, and
`ResilienceMetrics.SetCircuitState` keeps its signature.
