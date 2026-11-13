# Upgrading from 2.9.0 to 2.10.0

This is the one migration guide for 2.10.0. It has one section per change a reader may have to act on, and says who
each one reaches and what to do. The [CHANGELOG](../../CHANGELOG.md) has the full entry for each.

| Package | Change | Kind |
|---|---|---|
| `Verbara.Sdk.Sessions` | [A call is found on the server that reported it](#a-call-is-found-on-the-server-that-reported-it) | lookups, behaviour, BREAKING |
| `Verbara.Sdk.Sessions` | [A server is never attached from inside a session notification](#a-server-is-never-attached-from-inside-a-session-notification) | behaviour, BREAKING |
| `Verbara.Sdk.Sessions` | [A call ends only when its server holds none of its channels](#a-call-ends-only-when-its-server-holds-none-of-its-channels) | fix, new member, behaviour |
| `Verbara.Sdk.Sessions` | [Knowing whether a call's events are complete](#knowing-whether-a-calls-events-are-complete) | new members, equality |
| `Verbara.Sdk.Resilience` | [`circuit.state` reports the breaker's current state](#circuitstate-reports-the-breakers-current-state) | metric meaning, BREAKING |
| `Verbara.Sdk.Ari` | [A client that only makes REST calls can turn its events off](#a-client-that-only-makes-rest-calls-can-turn-its-events-off) | new option, new state |
| `Verbara.Sdk.Ari` | [An observer that throws no longer stops delivery](#an-observer-that-throws-no-longer-stops-delivery) | behaviour |
| `Verbara.Sdk.Ari` | [`OriginateAsync` with an extension runs the dialplan](#originateasync-with-an-extension-runs-the-dialplan) | fix, behaviour |
| `Verbara.Sdk.Ari` | [`STATUS`, `MEDIA_BUFFERING_COMPLETED` and `QUEUE_DRAINED` read as typed records](#status-media_buffering_completed-and-queue_drained-read-as-typed-records) | behaviour, new records |
| `Verbara.Sdk.Ari` | [A WebSocket audio stream whose connection failed ends with `Error`](#a-websocket-audio-stream-whose-connection-failed-ends-with-error) | behaviour |
| `Verbara.Sdk`, `Verbara.Sdk.Ari` | [`AriRtpStats` reads a real reply, and a channel state never drops an event](#arirtpstats-reads-a-real-reply-and-a-channel-state-never-drops-an-event) | fix, changed types, new members, BREAKING |
| `Verbara.Sdk.Live` | [A channel subscriber's failure stays with that subscriber](#a-channel-subscribers-failure-stays-with-that-subscriber) | behaviour, BREAKING |
| `Verbara.Sdk.Live` | [A live channel follows its dialplan location and caller ID](#a-live-channel-follows-its-dialplan-location-and-caller-id) | fix, behaviour |
| `Verbara.Sdk.Live` | [A refused `Status` is counted and reported by the pool health check](#a-refused-status-is-counted-and-reported-by-the-pool-health-check) | new members, health check |
| `Verbara.Sdk.Ami` | [A parking event names its channel and where it is parked](#a-parking-event-names-its-channel-and-where-it-is-parked) | fix, new members |
| `Verbara.Sdk.Ami` | [`NewExtenEvent` and `NewCallerIdEvent` carry what the wire sends](#newextenevent-and-newcalleridevent-carry-what-the-wire-sends) | fix, new members |
| `Verbara.Sdk.Ami` | [An action the SDK does not type is sent with `RawAction`](#an-action-the-sdk-does-not-type-is-sent-with-rawaction) | new type, behaviour, BREAKING |
| `Verbara.Sdk.VoiceAi.Stt`, `Verbara.Sdk.VoiceAi.Tts` | [A message a recognizer or synthesizer cannot read no longer ends the session](#a-message-a-recognizer-or-synthesizer-cannot-read-no-longer-ends-the-session) | fix, behaviour, new metric, BREAKING |
| `Verbara.Sdk.VoiceAi.OpenAiRealtime` | [An OpenAI Realtime audio delta that is not valid base64 no longer ends the call](#an-openai-realtime-audio-delta-that-is-not-valid-base64-no-longer-ends-the-call) | behaviour, metric, BREAKING |
| `Verbara.Sdk.VoiceAi.Tts` | [A Cartesia or ElevenLabs audio frame that is not valid base64 no longer ends the synthesis](#a-cartesia-or-elevenlabs-audio-frame-that-is-not-valid-base64-no-longer-ends-the-synthesis) | fix, behaviour, BREAKING |
| `Verbara.Sdk.Ami`, `Verbara.Sdk.Agi`, `Verbara.Sdk.Activities`, `Verbara.Sdk.Live` | [The types deprecated with `VSDK0005` are removed](#the-types-deprecated-with-vsdk0005-are-removed) | removed types and member, behaviour, BREAKING |
| `Verbara.Sdk.Ami`, `Verbara.Sdk.Ari`, `Verbara.Sdk.Sessions` | [`VSDK0002`, `VSDK0003`, `VSDK0004` and `VSDK0006` name their removal release](#vsdk0002-vsdk0003-vsdk0004-and-vsdk0006-name-their-removal-release) | warning text |
| `Verbara.Sdk.Ami` | [Seven AMI types whose wire name no Asterisk knows are deprecated](#seven-ami-types-whose-wire-name-no-asterisk-knows-are-deprecated) | new warning, BREAKING |

## Sessions

### A call is found on the server that reported it

**What changed.** `CallSessionManager` used to keep one channel-id index, one `linkedid` index and one bridge index
for every server attached to it. It now keeps them per server id: a channel event of one server is correlated only with
calls of that server, and never reaches a call of another. The same channel id, `linkedid` or bridge id reported by two
servers is two calls.

The unscoped lookups stay, are not obsolete, and now answer only when the id is unambiguous:
`GetByChannelId(uniqueId)`, `GetByLinkedId(linkedId)` and `GetByBridgeId(bridgeId)` return the call of the one server
that holds the id, and `null` when two or more servers hold it. They used to return whichever call last wrote the
shared index. Three scoped lookups are new, on `CallSessionManager` and on `ICallSessionManager`:
`GetByChannelId(serverId, uniqueId)`, `GetByLinkedId(serverId, linkedId)` and `GetByBridgeId(serverId, bridgeId)`.
Each returns the call that server's index holds for the id, or `null`.

`RegisterReconstructedSession` is scoped to the session's server too. It refuses, logged at Information, a session id
already held, a null or empty `linkedid`, a `linkedid` its server already holds, and a call carrying a leg its server
maps to another call; a refusal leaves every index as it was. An accepted call joins the legs its server's channel
table holds for its `linkedid`.

The CHANGELOG labels this `Changed — BREAKING`: a lookup that found one call now finds none when two servers report the
same id, and one Asterisk attached under two server ids now yields two calls per call.

**Who is affected.** Nobody whose servers each have their own `systemname` and are each attached once: their ids never
collide, and every lookup answers as before. Otherwise:

- A caller of an unscoped lookup on a pool whose servers share ids (two Asterisk servers without `systemname`) gets
  `null` where it used to get one of the calls, sometimes the wrong one.
- A host that attaches one Asterisk under two server ids gets two sessions per call, one per id, where it got one.
  The session engine already logged that as a misconfiguration, and still does.
- An implementation of `ICallSessionManager` of your own keeps compiling: the three scoped lookups have a default
  implementation that calls the unscoped lookup and keeps its answer only when the call belongs to the named server. So
  the default returns `null` for an id two servers hold, even where your own indexes could tell the calls apart.
  Override the three members to answer per server.

**What to do.**

- Where you know the server, call the scoped lookup: the server id is the one you passed to `AttachToServer`, and a
  session carries it as `CallSession.ServerId`. A host built with `AddVerbaraSessions` attaches its one server under
  `default`.
- Give every Asterisk of a pool its own `systemname` in `asterisk.conf` (`[options]`), and attach each Asterisk once.
  Calls of two servers are no longer mixed without it, but every consumer that keys a call by `linkedid` alone, the
  unscoped lookups and `ISessionStore.GetByLinkedIdAsync` included, still cannot tell them apart. The collision Warning
  says so.
- Treat a `null` from an unscoped lookup as "not found or not unique", and fall back to the scoped lookup when it
  matters which.

### A server is never attached from inside a session notification

**What changed.** `AttachToServer` called from inside a notification of the session engine (a subscriber of `Events`)
or from an attachment handler now throws `InvalidOperationException` naming the server id, before it installs anything.
That includes an attach of the instance already attached under that id, which is otherwise a no-op. In 2.9.0 only an
attach that had to wait for a release in progress of its id was refused there. The reason: an attach now reconciles the
calls the server left (below) under each call's lock, and a subscriber runs with one call's lock held.

**Who is affected.** Code that attaches a server from a session subscriber, for example a failover that reacts to a
notification. A subscriber that detaches with `DetachFromServer(id)` (the `Keep` form) and then attaches in the same
callback ends up detached: the detach stands, and the attach throws.

**What to do.** Catch the exception inside the subscriber, or better, never attach there: hand the attach to another
thread and do not wait for it inside the subscriber, because it takes the lock of each call the server left, the one
your subscriber holds included. The refusal also covers a subscriber of another session engine's notifications. Two
neighbours have a related rule: do not call `RegisterReconstructedSession` synchronously from a subscriber, and do not
wait there for `AdmitExistingCalls`.

### A call ends only when its server holds none of its channels

**What changed.** A call the engine did not observe from its start (a call registered with
`RegisterReconstructedSession`, a call a reload or `AdmitExistingCalls` opened, or a call a server left behind when it
was detached) used to be ended from its participant list. It is now decided against its server's channel table, by its
`linkedid`, and only once that table is complete on its current AMI session: it ends when the table holds none of its
channels, and is kept while it holds one. A call the engine observed from its start ends as before.

`AdmitExistingCalls(serverId)` is new on `CallSessionManager` and on `ICallSessionManager`. Called after
`AttachToServer` (and, in a failover, after `RegisterReconstructedSession`), it opens a session, as a reload does, for
every call the server's channel table holds that no session holds, and joins the legs of calls already held, so a
reconstructed call gains its legs. Each session it opens raises `CallStartedEvent`. A channel another call of that
server already owns keeps its owner. Repeating the admission adds nothing. Its walk never runs on the caller's thread.
When the server's table is not complete it first asks Asterisk for one `Status`. A detach stops it without an error. An
id with no instance attached throws `InvalidOperationException`; the interface's default implementation admits nothing.

A server that comes back under an id whose calls it left (detached with `DetachFromServer(id)` and attached again)
reconciles those calls once its table is complete: at the attach itself if it already is, otherwise at the next
completed `Status`. A left call of which the table holds no channel ends as a reload ends it, with
`Metadata["cause"] == "reload"`; one with a channel still up is kept and ends by its own hangup. In 2.9.0 a left call
whose hangup fell while its server was away stayed open for the life of the process, counted `sessions.unverifiable` on
every tick.

**Who is affected.** A host that attaches a server by hand after its start, such as a cluster's node attach: it used to
get no session for the calls the server already held. A host that re-attaches a server under the same id. And a server
whose AMI user may not run `Status`: there such a call is never decided, because its table never completes. That
includes every session `AdmitExistingCalls` opens, and every call left before a re-attach of that server, which 2.9.0
ended by its legs' observed hangups. Such calls are counted `sessions.unverifiable` on every sweep tick, and the pool
health check reads `Degraded` (see [Live](#a-refused-status-is-counted-and-reported-by-the-pool-health-check)).

**What to do.**

- After attaching a server that may already carry calls, call `AdmitExistingCalls` with the same id:

  ```csharp
  sessions.AttachToServer(server, "pbx-east");
  // A cluster's failover registers its reconstructed calls here, then admits.
  await sessions.AdmitExistingCalls("pbx-east", cancellationToken);
  ```

- Give every server's AMI user `Status`: `system`, `call` or `reporting` in its `write` line.
- For a server whose `Status` stays refused, release its waiting calls with
  `DetachFromServer(id, DetachedCalls.Release)` once you know it is gone.
- Keep an alert on `sessions.unverifiable > 0`: the calls waiting on a server whose `Status` is refused are counted there.

### Knowing whether a call's events are complete

**What changed.** Every notification of the session engine (`ICallSessionManager.Events`) carries `Epoch`, a
`SessionEventEpoch?`: what the SDK may have lost of that notification's own server's events before the point the
notification reflects. Two marks are compared with `SessionEventEpoch.LossBetween(earlier, later)`, which returns a
`SessionEventLoss`:

- `NoneKnown`: the marks are equal. The SDK missed no event of that server it could know of between the two.
- `Possible`: the marks differ. Between the two the connection reconnected, dropped an event because its buffer was
  full, or the server was detached and attached again, or the two came from different connection instances (a
  failover, a restart). The engine may have missed a hold, a transfer or a hangup.
- `Unknown`: either mark is `null`. The SDK cannot tell.

The values have no order: never combine verdicts with `Max`, `Min` or by comparing them; combine them as the example
under *What to do* does.

`SessionEventEpoch` is a new record struct with four fields: `ConnectionId` (the AMI connection instance, unique across
processes), `AmiSession` (that connection's session ordinal), `DroppedBefore` (how many events that session had
dropped) and `Attachment` (the engine's attachment of the server: a number no earlier attach in the process used).
`AgentSessionStateChanged`, on `IAgentSessionTracker.StateChanges`, carries the `Epoch` of the session notification
that caused it.

In 2.9.0 a consumer had only `IAmiConnection.StateChanged` and `Reconnected`, which are not ordered with the event
pump, so the first notifications of a new AMI session could reach it before it heard of the reconnect. A dropped
event was only counted (`ami.events.dropped`).

What a mark means:

- `null` means the engine could not read a mark, never that nothing was lost. It is `null` when the server's
  connection is not the SDK's AMI connection (a wrapper or a fake of `IAmiConnection`), and on a wrap-up raised after
  its server was detached.
- Compare marks for equality only, through `LossBetween`. The fields have no order. Do not compare two nullable marks
  with `==`: two `null` marks are equal there, which would read as complete.
- A mark is a position on the wire, not a time. A notification raised while one of its server's events is delivered
  carries that event's mark, so the first notification of a new AMI session carries the new session even while a
  `StateChanged` handler is still running. A notification raised outside a delivery (the wrap-up timer, a reload or
  sweep that ends a call, `AdmitExistingCalls`, a detach that releases calls, `RegisterReconstructedSession`) carries
  a mark taken no later than its content when it opens a call's record, and no earlier when it closes one.
- Compare each notification with the previous one of the same call. A loss between a call's first and last
  notification shows as `Possible` on at least one pair. A call queued from a reload's snapshot may show it on the
  pair before its `CallQueuedEvent` instead of the one after.
- A loss after a call's last notification shows on no pair of that call.
- A real `AttachToServer` changes `Attachment`, so a detach and re-attach reads `Possible`. Attaching the same instance
  again while it is attached changes nothing.
- The field set of `SessionEventEpoch` is closed until the next major version.

**Who is affected.** Nobody has to change code. Two kinds of consumer see a difference:

- A consumer that compares notifications by record equality or by `ToString`, for example to drop duplicates: both
  now include `Epoch`, so two notifications that were equal in 2.9.0 can differ by their marks.
- An implementation of `ICallSessionManager` of your own: its notifications carry `null` unless it sets `Epoch`, so a
  consumer reads `Unknown` for them.

**What to do.**

- To know whether a stored call is complete, store `Epoch` (its four fields, or `null`) beside each notification and
  compare each one with the previous one of the same call:

```csharp
using Verbara.Sdk.Sessions;

// The notifications stored for one call, in the order they were raised.
static SessionEventLoss Completeness(IReadOnlyList<SessionDomainEvent> call)
{
    if (call.Count == 0 || call[0].Epoch is null)
        return SessionEventLoss.Unknown;

    var verdict = SessionEventLoss.NoneKnown;
    for (var i = 1; i < call.Count; i++)
    {
        var loss = SessionEventEpoch.LossBetween(call[i - 1].Epoch, call[i].Epoch);
        if (loss == SessionEventLoss.Possible)
            return SessionEventLoss.Possible;   // the SDK may have missed one of this call's events
        if (loss == SessionEventLoss.Unknown)
            verdict = SessionEventLoss.Unknown; // cannot tell, never complete
    }

    return verdict;
}

// Two notifications that differ only by their marks, compared as 2.9.0 compared them.
static bool SameNotification(SessionDomainEvent a, SessionDomainEvent b) =>
    a with { Epoch = null } == b with { Epoch = null };
```

- Where you drop duplicate notifications by equality, compare them without `Epoch`, as `SameNotification` does.

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

### `STATUS`, `MEDIA_BUFFERING_COMPLETED` and `QUEUE_DRAINED` read as typed records

**What changed.** `ChanWebSocketControlMessageSerializer.Deserialize`, both overloads, used to read these three
chan_websocket messages as the generic `ChanWebSocketUnknownMessage`, with the message's name in `Event`. It now reads
them, in either control format, as the new `ChanWebSocketStatus`, `ChanWebSocketMediaBufferingCompleted` and
`ChanWebSocketQueueDrained`. Asterisk sends them in reply to `GET_STATUS`, `STOP_MEDIA_BUFFERING` and
`REPORT_QUEUE_DRAINED`, which the session now writes through `SendGetStatusAsync`, `SendStopMediaBufferingAsync` and
`SendReportQueueDrainedAsync`; the replies reach `IChanWebSocketSession.ControlMessages` as the typed records.
`ChanWebSocketUnknownMessage` is left for a message no capture names.

**Who is affected.** Code that passes a frame it read itself to `Deserialize` and matches `ChanWebSocketUnknownMessage`
whose `Event` is `STATUS`, `MEDIA_BUFFERING_COMPLETED` or `QUEUE_DRAINED`: that arm no longer matches. A session could
not receive these messages before, because no SDK method wrote the commands that provoke them.

**What to do.** Match the typed record instead. `ChanWebSocketStatus` carries the frame's `queue_length`, `xon_level`,
`xoff_level`, `queue_full`, `bulk_media` and `media_paused` as typed members; `ChanWebSocketMediaBufferingCompleted`
carries the `correlation_id` the stop sent. `ChanWebSocketMediaBuffering`, deprecated with `VSDK0004`, now names
`ChanWebSocketStatus.QueueLength` as the queue length to read.

### A WebSocket audio stream whose connection failed ends with `Error`

**What changed.** A WebSocket audio stream whose connection failed while it was live (a reset, or an end with no close
frame from the peer) publishes `AudioStreamState.Error` and then `Disconnected` on `StateChanges`. In 2.9.0 it published
`Disconnected` alone. A peer's close frame and an ending on this side still publish `Disconnected` alone. Disposing a stream
whose peer never answers the close now gives up after 1 s, not 2 s.

**Who is affected.** Code that subscribes to `StateChanges` of a WebSocket stream and treats `Error` as something only an
AudioSocket error frame produces, or that waits for `Disconnected` as the first ending state.

**What to do.** Handle `Error` as "the connection failed" and keep waiting for `Disconnected` as the last state, as an
AudioSocket stream already required. AudioSocket streams are unchanged.

### `AriRtpStats` reads a real reply, and a channel state never drops an event

**What changed.** `Channels.GetRtpStatisticsAsync` threw `JsonException` on every reply a real Asterisk sends: Asterisk
writes `rtt`, `local_maxrxploss` and `local_minrxploss` as reals, and an SSRC as an unsigned 32-bit integer, while
`AriRtpStats` typed all five as `int?`. Captured on 20.20.1, 22.11.0, 23.5.0 and the 22.12, 23.6 and 24.0 release
candidates, no reply bound. Now:

| Member | 2.9.0 | 2.10.0 |
|---|---|---|
| `Rtt` | `int?` | `double?`, in seconds |
| `LocalMaxrxploss`, `LocalMinrxploss` | `int?` | `double?` |
| `LocalSsrc`, `RemoteSsrc` | `int?` | `uint?` |

`AriRtpStats` also gains the 26 statistics Asterisk sends and the model lacked: `Txoctetcount` and `Rxoctetcount`
(`uint?`), the `Remote*jitter` and `Remote*rxploss` figures, `LocalNormdevrxploss`, `LocalStdevrxploss`, `Maxrtt`,
`Minrtt`, `Normdevrtt`, `Stdevrtt`, and the media experience scores `Txmes`, `Rxmes`, `Remote*mes` and `Local*mes`
(`double?`).

A channel's `state` is read as Asterisk writes it. `"Dialing Offhook"` and `"Pre-ring"` read as `DialingOffhook` and
`PreRing`; `"Mute"` reads as the new `AriChannelState.Mute`, appended after `Unknown` so no existing value changes its
number; `"Rsrvd"` and `"Rsrved"` read as `Rsrvd`; and `"Unknown (N)"`, or any state the SDK has no member for, reads as
`Unknown`. In 2.9.0 each of those strings except `"Rsrvd"`, which is the member's own name, dropped the whole event, or
made a REST call that returns a channel throw. These states come from analog and DAHDI channels, not PJSIP. The reader
is the new public `AriChannelStateConverter`, declared on `AriChannelState` itself. It writes Asterisk's spelling, so
the SDK's own serializer now writes `"Pre-ring"` and `"Dialing Offhook"` where 2.9.0 wrote `"PreRing"` and
`"DialingOffhook"`. It reads a number from 0 to 11 as that member (as 2.9.0 did) and any other number as `Unknown`,
and it handles a state used as a dictionary key the same way. It also applies to a consumer's own source-generated
context or `JsonSerializerOptions`, unless those options list an enum converter of their own, such as a global
`JsonStringEnumConverter`: a converter in the options' `Converters` wins over one declared on the type.

Five members that were always empty now carry what Asterisk sends: `AriChannel.Protocol` (from `protocol_id`),
`AriChannel.ChannelVars` (from `channelvars`, when `ari.conf` names channel variables), `ChannelHoldEvent.MusicClass`
(from `musicclass`), `AriConfigInfo.SetId` (from `setid`) and `BridgeBlindTransferEvent.Transferer` (from `channel`,
the transferring channel; Asterisk never writes `transferer`). No property is renamed.

**Who is affected.** Code that reads `Rtt`, `LocalMaxrxploss`, `LocalMinrxploss`, `LocalSsrc` or `RemoteSsrc` into an
`int`, or assigns them from one: it no longer compiles. Code that reads `Rtt` into `var`, a `double` or an arithmetic
expression compiles unchanged, and gets seconds: check what unit it assumed. Code with a `switch` over `AriChannelState` and no default arm,
which now sees `Mute`. Code that built its own JSON with the old names (`protocol`, `channel_vars`, `music_class`,
`set_id`, `transferer`) and read it through the SDK: those names are no longer read. Code that serializes an
`AriChannel` or an `AriChannelState` with its own `JsonSerializerOptions` or its own source-generated context and no
enum converter of its own: the state is now written as Asterisk's text (`"state": "Pre-ring"`), where 2.9.0 wrote its
number (`"state": 9`). Code whose options list a `JsonStringEnumConverter` keeps 2.9.0's behaviour: it writes the
member name (`"PreRing"`), as 2.9.0 did, and still cannot read `"Pre-ring"`, `"Dialing Offhook"`, `"Rsrved"` or
`"Unknown (N)"`.

**What to do.**

- Read the round-trip time as seconds: `stats.Rtt * 1000` is milliseconds. Do not cast it to `int`, which truncates every
  value Asterisk measured on a LAN to 0.
- Hold an SSRC as `uint` (or `long`). A cast to `int` turns any SSRC from 2^31 up negative; Asterisk generates its own
  below 2^31, but a far end that is not Asterisk may not.
- Handle `AriChannelState.Mute`, and treat `Unknown` as "a state the SDK has no name for": the event is delivered.
- Feed the SDK the names Asterisk sends, if you build test frames by hand.
- If you store channels with your own serializer, expect the state as text in what you write from now on. Nothing to
  convert: a state stored as a number by 2.9.0 still reads back as its member.
- If your options carry a global `JsonStringEnumConverter` and you read ARI channels through them, remove it, or insert
  `new AriChannelStateConverter()` into the options' `Converters` ahead of it.

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

### A refused `Status` is counted and reported by the pool health check

**What changed.** A `Status` that Asterisk refuses (an AMI user whose `write` has none of `system`, `call` or
`reporting`) used to leave only a log line. Each refusal on a load, a reload or a reconciliation now adds one to
`live.status.refused` (meter `Verbara.Sdk.Live`), tagged `live.status.reader` (`load` or `reconciliation`) and
`server.id` when the server is in a pool. `VerbaraServer.LastStatusRefusal` is new: the last refusal's `Message` and
its UTC `OccurredAt`, or `null`; a later `Status` read in full clears it. The `verbara-pool` health check reads
`Degraded`, where it read `Healthy`, while every server is connected and at least one has a last refusal, and its data
gains one `status-refused` entry: `serverId: message` per refused server, joined by `; `. Each server's own entry still
holds its connection state.

**Who is affected.** A pool with a server whose AMI user may not run `Status`: its health check moves from `Healthy` to
`Degraded`. A reader of the check's data that expects only server ids as keys: `status-refused` is a reserved id, which
the pool has refused as a server id since 2.9.0.

**What to do.** Give the server's AMI user `Status`, or alert on `live.status.refused` and on the `Degraded` check. On
such a server the session engine cannot decide the calls it did not see whole, see
[above](#a-call-ends-only-when-its-server-holds-none-of-its-channels).

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

### An action the SDK does not type is sent with `RawAction`

**What changed.** `RawAction` (`Verbara.Sdk.Ami.Actions`) sends an AMI action by its name and its fields through
every send method of `IAmiConnection`: `Action: <name>`, the connection's `ActionID`, then each field in the order it
was added. The name must be ASCII letters and digits; a key ASCII letters, digits, `-`, `_`, `(` or `)`, and not
`Action` or `ActionID`; a value holds no CR or LF; each is checked where the action is built. The answer is read by
the name, as for a typed action.

A `ManagerAction` subclass the SDK does not declare used to be written as its class name without `Action`, with none
of its properties and none of its extra fields. The connection now throws `ArgumentException` naming its type and
`RawAction`, before any byte is written; the connection stays usable.

**Who is affected.** Code that sends its own `ManagerAction` subclass: it compiles, and throws at the send. No
signature changed.

**What to do.** Replace `new MyAction { Foo = "x" }` with `new RawAction("<ActionName>").Add("Foo", "x")`, where
`<ActionName>` is the name Asterisk registers (`manager show commands`). This is also the route for the `DAHDI*`
actions that replace the removed `Zap*` types (`new RawAction("DAHDIDNDon").Add("DAHDIChannel", "1")`).

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

## Voice AI

### A message a recognizer or synthesizer cannot read no longer ends the session

**What changed.** Seven streaming clients parsed each vendor message with nothing around the parse, so a message they
could not read ended the session with a `JsonException` out of the caller's enumeration, a type that `StreamAsync` and
`SynthesizeAsync` do not document. The seven are the Speechmatics, AssemblyAI and Cartesia recognizers
(`StreamAsync`) and the Deepgram, LMNT, Cartesia and ElevenLabs synthesizers (`SynthesizeAsync`). Behind
`VoiceAiPipeline` that was a failed transcription or synthesis. 2.9.0 made the same change on the Deepgram recognizer. Now:

- A text message the vendor sends in fragments, or one larger than the 64 KiB receive buffer, is assembled and read
  whole. This is new on the Speechmatics, AssemblyAI and Cartesia recognizers and the Deepgram and LMNT synthesizers.
  Binary audio is still delivered per read.
- A message that cannot be read but whose envelope names the vendor's failure ends the session with
  `SpeechProviderFailureException` (`Signal` = `ErrorFrame`). That is the failure a readable error message raises.
  Its message carries the raw text of what the vendor sent. A message names the failure when it passes the same test as
  a readable failure message for that vendor:
  - Speechmatics: `"message": "Error"`
  - AssemblyAI, and the Deepgram synthesizer: `"type": "Error"`
  - Cartesia: `"type": "error"`
  - ElevenLabs: a non-empty `error` member
  - LMNT: either of the last two

  `Code` is read from the member that the readable branch reads it from, when that member is a string or a number, and
  is otherwise null. LMNT reads no code.
- Any other message that cannot be read, JSON or not, is skipped and counted. A recognizer counts it on
  `stt.frames.unparseable` (meter `Verbara.Sdk.VoiceAi.Stt`). A synthesizer counts it on the new
  `tts.frames.unparseable` (meter `Verbara.Sdk.VoiceAi.Tts`). Both counters are tagged `voiceai.provider`. The results
  or audio before and after the skipped message are delivered. If every message in a recognition was skipped, it
  completes with no results. If every message in a synthesis was skipped, it ends with
  `SpeechProviderEmptyResultException`, as a synthesis that produced no audio does.

No signature changes.

**Who is affected.** Code that catches `JsonException` from `StreamAsync` or `SynthesizeAsync` on these clients: it no
longer receives one. A dashboard or alert on `stt.transcriptions.failed` or `tts.syntheses.failed` behind
`VoiceAiPipeline`: an unreadable vendor message no longer fails the transcription or synthesis.

**What to do.** Catch `SpeechProviderFailureException` instead of `JsonException`; `StreamAsync` and `SynthesizeAsync`
document it. Watch `stt.frames.unparseable` and `tts.frames.unparseable` for the messages that the clients skipped.

### An OpenAI Realtime audio delta that is not valid base64 no longer ends the call

**What changed.** Since 2.9.0 an event the bridge cannot decode is skipped and the call goes on, but an audio delta that
decoded and whose `delta` was not valid base64 still ended the session: the caller was hung up under the session broker
and the session counted in `openai_realtime.sessions.failed`. Now such a delta is skipped, logged once at Warning and
counted on `openai_realtime.frames.unparseable` (meter `Verbara.Sdk.VoiceAi.OpenAiRealtime`, tagged `voiceai.provider` =
`OpenAiRealtime`), and the transcript and audio that follow reach the caller. An audio delta whose `delta` is an explicit
null, which ended the session with an `ArgumentNullException`, is now counted and skipped the same way; one whose `delta`
is empty, or too short to make one sample at the caller's rate, is skipped without a count and no longer sends a
zero-length audio frame to the caller. An `Events` subscriber that throws still ends the session. No signature changes.

**Who is affected.** A dashboard or alert on `openai_realtime.sessions.failed`: a session with such a delta no longer counts
there. A caller that hears a short gap where the bad delta was.

**What to do.** Nothing in code. Watch `openai_realtime.frames.unparseable` for the deltas the bridge skipped. See the
bridge's README, *When the conversation ends*.

### A Cartesia or ElevenLabs audio frame that is not valid base64 no longer ends the synthesis

**What changed.** `CartesiaSpeechSynthesizer` and `ElevenLabsSpeechSynthesizer` decoded the base64 audio of each text
frame with nothing around the decode, so a frame whose `data` or `audio` was not valid base64 ended the synthesis with a
`FormatException` out of the caller's enumeration, a type `SynthesizeAsync` does not document; behind `VoiceAiPipeline`
that was a failed synthesis. Now the frame is skipped and counted on `tts.frames.unparseable` (meter
`Verbara.Sdk.VoiceAi.Tts`, tagged `voiceai.provider`), and the audio around it is delivered. A session whose every frame
was such a frame ends with `SpeechProviderEmptyResultException`, as a session that produced no audio does. No signature
changes.

**Who is affected.** Code that catches `FormatException` from `SynthesizeAsync`: it no longer receives one.

**What to do.** Catch the three exceptions `SynthesizeAsync` documents. To see the frames the synthesizer skipped, watch
`tts.frames.unparseable`.

## Deprecated surface

### The types deprecated with `VSDK0005` are removed

**What changed.** 2.9.0 marked 49 types and one member `[Obsolete]` with `VSDK0005` and said they leave in 2.10.0.
They are removed:

- from `Verbara.Sdk.Ami`, the 28 actions and 3 responses of chan_sip, res_monitor, app_meetme, Skype for Asterisk,
  the Zaptel action names, chan_agent and app_setcdruserfield, `MeetMeEventBase`, `MeetmeCompatibility`, and the 15
  event types of dead modules or of wire names Asterisk 10 or 12 renamed or removed;
- from `Verbara.Sdk.Agi`, `MeetmeCommand`;
- from `Verbara.Sdk.Activities`, `MeetmeActivity.UseConfBridge`. `MeetmeActivity` stays and always runs ConfBridge,
  which was already its default.

The [VSDK0005 tables](obsolete-ami-events-removal-migration.md#vsdk0005) list every symbol and its replacement; the
link in each 2.9.0 warning opens them. No pinned image of a supported Asterisk registers a removed action or sends a
removed event. app_meetme, which registers `MeetmeMute` and `MeetmeUnmute`, is still in the Asterisk tree, deprecated
and not built by default.

What the wire still carries is not lost:

- **An event whose type was removed arrives as a base `ManagerEvent`.** Its name, spelled as Asterisk sent it, is in
  `EventType`, and every header is in `RawFields`. Every subscriber and `OnEvent` handler still receives it; nothing
  is dropped, and nothing is logged above Debug.
- **`AsyncAGI` is no longer a `ResponseEvent`.** An `AsyncAGI` that carries the `ActionID` of an event-generating
  action still reaches that action's sequence, untyped. Code that matched it as `ResponseEvent` no longer does.
- **`DTMF`, `Transfer` and `MusicOnHold` are not rewritten** into `DtmfEndEvent`, `BlindTransferEvent` or
  `MusicOnHoldStartEvent`. Those types keep their own wire names, which every supported Asterisk sends.
- **`VerbaraServer` no longer reads `QueueMemberPaused`.** Only Asterisk 11 and earlier send it; on those versions a
  member's pause state no longer follows it. `QueueMemberPause`, which every supported version sends, is unchanged.
- **An action the SDK does not type is sent as a `RawAction`.** A consumer's own `ManagerAction` subclass is not
  a route to another action: the connection refuses it with `ArgumentException` (see
  [An action the SDK does not type is sent with `RawAction`](#an-action-the-sdk-does-not-type-is-sent-with-rawaction)).
  The `DAHDI*` actions the Zaptel rows name, and `MeetmeMute` and `MeetmeUnmute` on a server built with
  app_meetme, are sent by name: `new RawAction("DAHDIDNDon").Add("DAHDIChannel", "1")`.

**Who is affected.** Code that names a removed type or `UseConfBridge`: it no longer compiles. A removed type reports
`CS0246`, or `CS0234` when written fully qualified; `UseConfBridge` reports `CS0117` in an object initializer and
`CS1061` when read.
A binary built against 2.9.0 or earlier that names one fails to load it (`TypeLoadException`, `MissingMethodException`).
Code that matched `AsyncAgiEvent` or `ResponseEvent` for `AsyncAGI`. Live users on Asterisk 11 or earlier who relied
on `QueueMemberPaused` for a member's pause state.

**What to do.**

1. **Move every `Verbara.Sdk.*` package to 2.10.0 together.** `Verbara.Sdk.Live` 2.9.0 names `QueueMemberPausedEvent`
   in its event router, so loaded next to `Verbara.Sdk.Ami` 2.10.0 it cannot resolve it.
2. **Recompile**, and replace each use with the replacement in the
   [VSDK0005 tables](obsolete-ami-events-removal-migration.md#vsdk0005). Remove any `NoWarn` or pragma you kept for
   `VSDK0005`; the id no longer reports anything.
3. **Read a removed event by name** if your Asterisk still sends one: match `EventType` and read `RawFields`, as the
   [2.9.0 removal guide](obsolete-ami-events-removal-migration.md#if-your-asterisk-still-sends-one-of-these-events)
   shows for the event types removed then.
4. **Remove `UseConfBridge = …`** from a `MeetmeActivity`. To join an app_meetme room on an Asterisk that still builds
   it, call `IAgiChannel.ExecAsync("MeetMe", args)`, the call the activity made.

### `VSDK0002`, `VSDK0003`, `VSDK0004` and `VSDK0006` name their removal release

**What changed.** The warning message of these 14 members keeps its text and now ends `Removed in Verbara.Sdk 2.11.0.`
Nothing is removed in 2.10.0, and nothing else changes: the ids, the replacements the messages name and the published
instruments are as in 2.9.0.

From 2.10.0 each of these warnings links to its id's heading below, not to the compiler's generic `CS0618` page.

#### VSDK0002

| Id | Member | Removed in |
|---|---|---|
| `VSDK0002` | `StatusEvent.State`, `CallerId`, `Account`, `BridgedChannel`, `Link`, `BridgedUniqueId` | 2.11.0 |

#### VSDK0003

| Id | Member | Removed in |
|---|---|---|
| `VSDK0003` | `AudioStreamMetrics.BufferUnderruns` (`audio.buffer.underruns`) | 2.11.0 |

The instrument leaves the meter with its field; see **Who is affected** below.

#### VSDK0004

| Id | Member | Removed in |
|---|---|---|
| `VSDK0004` | `IChanWebSocketSession.SendXonAsync`, `SendXoffAsync` | 2.11.0 |
| `VSDK0004` | `ChanWebSocketHangup`, `ChanWebSocketMediaBuffering` | 2.11.0 |
| `VSDK0004` | `ChanWebSocketDtmf.DurationMs` | 2.11.0 |

#### VSDK0006

| Id | Member | Removed in |
|---|---|---|
| `VSDK0006` | `SessionMetrics.SessionsTimedOut` (`sessions.timed_out`), `SessionsOrphaned` (`sessions.orphaned`) | 2.11.0 |

The instruments leave the meter with their fields; see **Who is affected** below.

**Who is affected.** Code that reads one of these members: the warning it already reported now names 2.11.0. For
`VSDK0003` and `VSDK0006`, a dashboard or exporter that lists the meter's instruments: each field is its instrument's
only creation, so in 2.11.0 `audio.buffer.underruns`, `sessions.timed_out` and `sessions.orphaned` leave the meter
with their fields. `audio.buffer.underruns` never recorded a measurement, so no exporter has emitted a point under
that name. The SDK has recorded nothing on `sessions.timed_out` or `sessions.orphaned` since 2.7.0; through 2.6.x the
reconciliation sweep added to both, so a series an exporter stored from 2.6.x or earlier keeps that history, and a
panel that reads it has shown no new point since 2.7.0.

**What to do.** Move to what each message names before 2.11.0. A `NoWarn` you keep for one of these ids keeps working
in 2.10.0.

### Seven AMI types whose wire name no Asterisk knows are deprecated

**What changed.** Seven types of `Verbara.Sdk.Ami` are marked `[Obsolete]` with `VSDK0007`, a warning, and leave in
2.11.0. Each one names an AMI action that no Asterisk registers, or an event that no supported Asterisk (22, 23, 24)
emits, read in the whole upstream source of 22.12.0, 23.6.0 and 24.0.0. Sending one of the two actions is answered
with an error; a handler for one of the five events never runs.

#### VSDK0007

| Type | Wire name | What to use | Removed in |
|---|---|---|---|
| `DbPrefixGetAction` | `DBPrefixGet`, never registered | `DbGetTreeAction` (`DBGetTree`): the entries of a family, optionally under a key | 2.11.0 |
| `ExecAction` | `exec`, never registered | `CommandAction` to run a CLI command; `RawAction` to send an action by its name | 2.11.0 |
| `JitterBufStatsEvent` | `JitterBufStats`, last emitted by chan_iax2 in Asterisk 11 | nothing: no supported Asterisk sends it | 2.11.0 |
| `MasqueradeEvent` | `Masquerade`, last emitted by the core in Asterisk 11 | nothing: no supported Asterisk sends it | 2.11.0 |
| `PriEventEvent` | `PRIEvent`, emitted by chan_dahdi in Asterisk 1.6, gone by 11 | nothing: no supported Asterisk sends it | 2.11.0 |
| `RtpReceiverStatEvent` | `RTPReceiverStat`, emitted by the core in Asterisk 1.6, gone by 11 | nothing: no supported Asterisk sends it | 2.11.0 |
| `RtpSenderStatEvent` | `RTPSenderStat`, emitted by the core in Asterisk 1.6, gone by 11 | nothing: no supported Asterisk sends it | 2.11.0 |

**Who is affected.** Code that names one of these types. A build with TreatWarningsAsErrors fails on `VSDK0007`
until the code moves or suppresses the id; any other build shows the warning. Nothing changes at run time.

**What to do.** Replace the two actions as the table says. Delete a handler or a type check for one of the five
events: it never ran against a supported Asterisk. If you cannot move before upgrading, suppress `VSDK0007` where you
name the type (`#pragma warning disable VSDK0007`, or `NoWarn` in the project); the suppression works until 2.11.0
removes the types.
