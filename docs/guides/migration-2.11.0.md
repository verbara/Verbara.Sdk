# Upgrading from 2.10.x to 2.11.0

This is the one migration guide for 2.11.0. It has one section per change a reader may have to act on, and says who
each one reaches and what to do. The [CHANGELOG](../../CHANGELOG.md) has the full entry for each.

| Package | Change | Kind |
|---|---|---|
| `Verbara.Sdk.Ami` | [Six `StatusEvent` members no Asterisk fills are removed](#six-statusevent-members-no-asterisk-fills-are-removed) | removed members, BREAKING |
| `Verbara.Sdk.Ami` | [Seven AMI types whose wire name no Asterisk knows are removed](#seven-ami-types-whose-wire-name-no-asterisk-knows-are-removed) | removed types, BREAKING |
| `Verbara.Sdk.Ari` | [Two flow-control send methods Asterisk never acted on are removed](#two-flow-control-send-methods-asterisk-never-acted-on-are-removed) | removed members, BREAKING |
| `Verbara.Sdk.Ari` | [Two WebSocket control records nothing published are removed](#two-websocket-control-records-nothing-published-are-removed) | removed types, BREAKING |
| `Verbara.Sdk.Ari` | [`ChanWebSocketDtmf` carries the digit alone: its constructor and `Deconstruct` change](#chanwebsocketdtmf-carries-the-digit-alone-its-constructor-and-deconstruct-change) | removed member, changed constructor and `Deconstruct`, BREAKING |
| `Verbara.Sdk.Ari`, `Verbara.Sdk.Sessions` | [Three meter instruments nothing records are removed](#three-meter-instruments-nothing-records-are-removed) | removed fields and meter instruments, BREAKING |
| `Verbara.Sdk`, `Verbara.Sdk.Ari` | [The bridge `format` arguments are named for what they format](#the-bridge-format-arguments-are-named-for-what-they-format) | renamed arguments, BREAKING (named arguments only) |
| `Verbara.Sdk.Live` | [A `QueueManager` or `MeetMeManager` subscriber that throws stops nothing else](#a-queuemanager-or-meetmemanager-subscriber-that-throws-stops-nothing-else) | behavior, BREAKING (no compiler error) |
| `Verbara.Sdk.Live` | [An `AgentManager` or `BridgeManager` subscriber that throws stops nothing else](#an-agentmanager-or-bridgemanager-subscriber-that-throws-stops-nothing-else) | behavior, BREAKING (no compiler error) |
| `Verbara.Sdk.Ari` | [An `OnStreamConnected` observer that throws loses no connection](#an-onstreamconnected-observer-that-throws-loses-no-connection) | behavior, BREAKING (no compiler error) |
| `Verbara.Sdk.Ari` | [An ARI outbound observer that throws keeps its connection](#an-ari-outbound-observer-that-throws-keeps-its-connection) | behavior, BREAKING (no compiler error) |

## Before you start: move every package at once

Move every `Verbara.Sdk.*` package to 2.11.0 together, then recompile. The packages are built and tested as one
release; a 2.10.x package loaded next to a 2.11.0 one is not a combination anything verifies.

## Deprecated surface removed

2.10.0 printed "Removed in Verbara.Sdk 2.11.0." in the warning of each symbol below, and linked the warning to its id's
heading in the [2.10.0 guide](migration-2.10.0.md#deprecated-surface). Those symbols are gone. The ids are retired:
nothing in the SDK carries them any more, so a `NoWarn` or `#pragma warning disable` you kept for one of them
suppresses nothing and can be deleted. One change below had no warning: the `ChanWebSocketDtmf` constructor and
`Deconstruct`, which lose their second value with `DurationMs`.

A binary built against 2.10.x or earlier that names a removed symbol fails when it reaches it, with
`MissingMethodException` for a removed method or property, `MissingFieldException` for a removed field (the three
meter fields below) and `TypeLoadException` for a removed type, until it is recompiled
against 2.11.0 and the use is moved as below.

### Six `StatusEvent` members no Asterisk fills are removed

**What changed.** `StatusEvent.State`, `CallerId`, `Account`, `BridgedChannel`, `Link` and `BridgedUniqueId` are
removed (marked with `VSDK0002` since 2.9.0). Each named a header that no supported Asterisk sends on a `Status`
frame, so each was always null. What a frame carries is read from the member that binds it:

| Removed member | Read instead |
|---|---|
| `State` | `ChannelStateDesc` (text, for example `Up`) or `ChannelState` (numeric, for example `6`) |
| `CallerId` | `CallerIdNum` (number) or `CallerIdName` (name) |
| `Account` | `AccountCode` |
| `BridgedChannel` | `BridgeId`: a `Status` frame names the bridge, never the peer channel |
| `Link` | `BridgeId` |
| `BridgedUniqueId` | `BridgeId` |

Nothing changes at run time for any frame a supported Asterisk sends. A `Status` frame that did carry one of these
headers would keep it in `RawFields`, under the name it arrived with.

**Who is affected.** Code that reads or sets one of the six members. A read reports `CS1061`
(`'StatusEvent' does not contain a definition for 'State'`); a member set in an object initializer reports `CS0117`.

**What to do.** Move each read to the member in the table. Before (2.10.x: warned `VSDK0002`, always null; does
not compile against 2.11.0):

<!-- skip-doc-snippet -->
```csharp
var state = status.State;
var caller = status.CallerId;
```

After:

```csharp
using Verbara.Sdk.Ami.Events;

static (string? State, string? Caller) Read(StatusEvent status) =>
    (status.ChannelStateDesc, status.CallerIdNum);
```

### Seven AMI types whose wire name no Asterisk knows are removed

**What changed.** Seven types of `Verbara.Sdk.Ami` are removed (marked with `VSDK0007` since 2.10.0). Each named an
AMI action that no Asterisk registers, or an event that no supported Asterisk (22, 23, 24) emits:

| Removed type | Wire name | What to use |
|---|---|---|
| `DbPrefixGetAction` | `DBPrefixGet`, never registered | `DbGetTreeAction` (`DBGetTree`): the entries of a family, optionally under a key |
| `ExecAction` | `exec`, never registered | `CommandAction` to run a CLI command; `RawAction` to send an action by its name |
| `JitterBufStatsEvent` | `JitterBufStats`, last emitted by chan_iax2 in Asterisk 11 | nothing: delete the handler |
| `MasqueradeEvent` | `Masquerade`, last emitted by the core in Asterisk 11 | nothing: delete the handler |
| `PriEventEvent` | `PRIEvent`, emitted by chan_dahdi in Asterisk 1.6, gone by 11 | nothing: delete the handler |
| `RtpReceiverStatEvent` | `RTPReceiverStat`, emitted by the core in Asterisk 1.6, gone by 11 | nothing: delete the handler |
| `RtpSenderStatEvent` | `RTPSenderStat`, emitted by the core in Asterisk 1.6, gone by 11 | nothing: delete the handler |

**An event of a removed type's name still arrives.** If a server did send one, it reaches every subscriber as a base
`ManagerEvent`: its name, spelled as sent, is in `EventType`, and every header is in `RawFields`. Nothing is dropped.
This is how the event names removed in 2.9.0 and 2.10.0 arrive too.

**Who is affected.** Code that names one of the seven types. It reports `CS0246`
(`The type or namespace name 'MasqueradeEvent' could not be found`), or `CS0234` when written fully qualified.

**What to do.** Replace the two actions as the table says. Before (2.10.x: warned `VSDK0007`, and Asterisk answered
with an error; does not compile against 2.11.0):

<!-- skip-doc-snippet -->
```csharp
await connection.SendActionAsync(new DbPrefixGetAction { Family = "cidname", Key = "123" });
```

After:

```csharp
using Verbara.Sdk;
using Verbara.Sdk.Ami.Actions;

static async Task ReadFamilyAsync(IAmiConnection connection)
{
    await foreach (var entry in connection.SendEventGeneratingActionAsync(new DbGetTreeAction { Family = "cidname", Key = "123" }))
    {
        // one event per entry of the family under the key; its headers are in entry.RawFields
    }
}
```

Delete a handler or a type check for one of the five events: it never ran against a supported Asterisk. To read one
by name on a server that sends it anyway, match `EventType` and read `RawFields`:

```csharp
using Verbara.Sdk;

static (string? Clone, string? Original) ReadMasquerade(ManagerEvent evt) =>
    evt.EventType == "Masquerade" && evt.RawFields is { } fields
        ? (fields.GetValueOrDefault("Clone"), fields.GetValueOrDefault("Original"))
        : (null, null);
```

### Two flow-control send methods Asterisk never acted on are removed

**What changed.** `IChanWebSocketSession.SendXonAsync` and `SendXoffAsync` are removed (marked with `VSDK0004` since
2.9.0). chan_websocket has no XON/XOFF command in any spelling: every supported Asterisk answers one with
`command unknown`, and both methods threw `NotSupportedException` and wrote nothing. Flow control is Asterisk's to
send: it arrives on `ControlMessages` as `ChanWebSocketMediaXoff` when its queue of your audio is full, then
`ChanWebSocketMediaXon` when it has drained.

**Who is affected.** Code that calls either method, which reports `CS1061`
(`'IChanWebSocketSession' does not contain a definition for 'SendXonAsync'`). A class of your own that implements
`IChanWebSocketSession` explicitly (`ValueTask IChanWebSocketSession.SendXonAsync(…)`) reports `CS0539` on that member
until you delete it; one that declares the two as ordinary public methods still compiles.

**What to do.** Delete the calls, and pause or resume your audio on the messages Asterisk sends. Before (2.10.x:
warned `VSDK0004` and threw; does not compile against 2.11.0):

<!-- skip-doc-snippet -->
```csharp
await session.SendXoffAsync();
```

After:

```csharp
using Verbara.Sdk.Ari.Audio;

static IDisposable PauseOnXoff(IChanWebSocketSession session, Action<bool> setPaused) =>
    session.ControlMessages.Subscribe(message =>
    {
        if (message is ChanWebSocketMediaXoff)
            setPaused(true);
        else if (message is ChanWebSocketMediaXon)
            setPaused(false);
    });
```

### Two WebSocket control records nothing published are removed

**What changed.** `ChanWebSocketHangup` and `ChanWebSocketMediaBuffering` are removed (marked with `VSDK0004` since
2.9.0), with their `kind` rows on `ChanWebSocketControlMessage` and their `ChanWebSocketJsonContext` getters. No
session ever published either: chan_websocket ends a call by closing the connection, never with a text message, and no
message it sends reports a buffer fill in bytes.

| Removed type | Read instead |
|---|---|
| `ChanWebSocketHangup` | `IAudioStream.StateChanges`: the session publishes `Disconnected` once, last, when Asterisk closes the connection |
| `ChanWebSocketMediaBuffering` | `ChanWebSocketMediaXoff` / `ChanWebSocketMediaXon` on `ControlMessages` for flow control; `ChanWebSocketStatus.QueueLength`, the reply to `SendGetStatusAsync`, for the queue length |

`ChanWebSocketMediaBufferingCompleted`, Asterisk's reply to `STOP_MEDIA_BUFFERING`, is not affected.

**Who is affected.** Code that names either type: as a type it reports `CS0246`
(`The type or namespace name 'ChanWebSocketMediaBuffering' could not be found`), and in a `case ChanWebSocketHangup:`
label `CS0103` (`The name 'ChanWebSocketHangup' does not exist in the current context`). Code that reads
`ChanWebSocketJsonContext.Default.ChanWebSocketHangup` or `.ChanWebSocketMediaBuffering` reports `CS1061`. JSON you
stored through `ChanWebSocketJsonContext` with `"kind":"hangup"` or `"kind":"media_buffering"` no longer deserializes
as `ChanWebSocketControlMessage`: the discriminator is unknown. Only an application that built these records itself
can hold such JSON, because no session published them.

**What to do.** Delete a `case` for either record; it never ran. Before (2.10.x: warned `VSDK0004`; does not compile
against 2.11.0):

<!-- skip-doc-snippet -->
```csharp
case ChanWebSocketHangup:
    OnCallEnded();
    break;
```

After, the ending read where the session publishes it:

```csharp
using Verbara.Sdk;
using Verbara.Sdk.Ari.Audio;

static IDisposable OnEnded(IAudioStream stream, Action onEnded) =>
    stream.StateChanges.Subscribe(state =>
    {
        if (state == AudioStreamState.Disconnected)
            onEnded();
    });
```

### `ChanWebSocketDtmf` carries the digit alone: its constructor and `Deconstruct` change

**What changed.** `ChanWebSocketDtmf.DurationMs` is removed (marked with `VSDK0004` since 2.9.0). It was always 0:
chan_websocket's `DTMF_END` carries `digit` and `channel_id` only, on every supported version and in both control
formats. The property was the record's second positional parameter, so its removal also changes two members no 2.10.x
warning named:

**The constructor `ChanWebSocketDtmf(string Digit, int DurationMs)` becomes `ChanWebSocketDtmf(string Digit)`, and
`Deconstruct(out string Digit, out int DurationMs)` becomes `Deconstruct(out string Digit)`.**

`ChannelId` stays an init member. The `asterisk.dtmf.received` activity event carries no duration tag, as before.

**Who is affected.** Code that builds the record (a test double, a replay of stored messages), deconstructs it into two
values, matches it with a two-value positional pattern, or reads `DurationMs`. Each reports a compiler error against
2.11.0: `new ChanWebSocketDtmf("5", 0)` reports `CS1729`
(`'ChanWebSocketDtmf' does not contain a constructor that takes 2 arguments`); `var (digit, ms) = dtmf;` and the
pattern `ChanWebSocketDtmf(var pressed, _)` report `CS1501` (`No overload for method 'Deconstruct' takes 2 arguments`)
with `CS8129`; and `dtmf.DurationMs` reports `CS1061`. An application that serializes the record through `ChanWebSocketJsonContext` no longer writes
`duration_ms`.

**What to do.** Drop the second value. C# has no one-value deconstruction syntax, so a `var (digit, ms) = dtmf;`
becomes `dtmf.Deconstruct(out var digit)`, or a read of `dtmf.Digit`. Before (2.10.x; `DurationMs` warned `VSDK0004`, the constructor and
`Deconstruct` did not; does not compile against 2.11.0):

<!-- skip-doc-snippet -->
```csharp
var dtmf = new ChanWebSocketDtmf("5", 0);
var (digit, ms) = dtmf;
if (message is ChanWebSocketDtmf(var pressed, _))
    OnDigit(pressed);
```

After:

```csharp
using Verbara.Sdk.Ari.Audio;

static string? Digit(ChanWebSocketControlMessage message)
{
    var dtmf = new ChanWebSocketDtmf("5");
    dtmf.Deconstruct(out var digit);
    return message is ChanWebSocketDtmf(var pressed) ? pressed : null;
}
```

The duration is read from the channel that received the digit, never from the WebSocket channel: the ARI
`ChannelDtmfReceived` event of the bridged peer (`ChannelDtmfReceivedEvent.DurationMs`; seen when the peer is in
Stasis or the application subscribes to all), or AMI `DTMFEnd` on that peer (`DtmfEndEvent.DurationMs`).

### Three meter instruments nothing records are removed

**What changed.** `AudioStreamMetrics.BufferUnderruns` (marked with `VSDK0003` since 2.9.0) and
`SessionMetrics.SessionsTimedOut` and `SessionsOrphaned` (marked with `VSDK0006` since 2.9.0) are removed. Each field
was its instrument's only creation, so **`audio.buffer.underruns` leaves the `Verbara.Sdk.Ari.Audio` meter, and
`sessions.timed_out` and `sessions.orphaned` leave the `Verbara.Sdk.Sessions` meter.**

- `audio.buffer.underruns` never recorded a measurement: the SDK has no write pump, so it never sees the far end
  starve. No exporter has emitted a point under that name.
- `sessions.timed_out` and `sessions.orphaned` have recorded nothing since 2.7.0, when the reconciliation sweep began
  ending a call it finds gone as a reload ends it, counted once in `sessions.completed` or `sessions.failed`. Through
  2.6.x the sweep added to both, so a series an exporter stored from 2.6.x or earlier keeps that history.

**Who is affected.** Code that reads one of the three fields, which reports `CS0117`
(`'SessionMetrics' does not contain a definition for 'SessionsTimedOut'`). An exporter that subscribes by meter name
builds and runs unchanged, and no longer lists the three instruments; a dashboard panel or alert on one of them has
nothing to read.

**What to do.** Delete the reads, and the panels and alerts on the three names. Count call endings from
`sessions.completed` and `sessions.failed`, which the sweep's endings also feed. Before (2.10.x: warned `VSDK0003` and
`VSDK0006`; does not compile against 2.11.0):

<!-- skip-doc-snippet -->
```csharp
var underruns = AudioStreamMetrics.BufferUnderruns;
var timedOut = SessionMetrics.SessionsTimedOut;
```

After, the instruments that count endings:

```csharp
using System.Diagnostics.Metrics;
using Verbara.Sdk.Sessions.Diagnostics;

static (Counter<long> Completed, Counter<long> Failed) Endings() =>
    (SessionMetrics.SessionsCompleted, SessionMetrics.SessionsFailed);
```

## Renamed arguments

### The bridge `format` arguments are named for what they format

**What changed.** The optional `format` argument of `Bridges.PlayAsync` and `Bridges.RecordAsync` is renamed, on
`IAriBridgesResource` and on `AriBridgesResource`:

| Member | 2.10.x | 2.11.0 | Sent as |
|---|---|---|---|
| `PlayAsync` | `format` | `announcerFormat` | `announcer_format`, the Announcer channel's audio format |
| `RecordAsync` | `format` | `recorderFormat` | `recorder_format`, the Recorder channel's audio format |

`RecordAsync` keeps `recordingFormat`, the recording's file format (`format` on the wire). Each argument keeps its
position, type and default, and the request on the wire is the same byte for byte. Under the old name one
`RecordAsync` call carried two "formats" whose names inverted their wire keys: `RecordAsync(id, "rec", "slin16",
format: "wav")` compiled and asked for a `slin16` file recorded through a `wav` channel.

**Who is affected.** A call site that names `format:` on either member. It reports `CS1739` against 2.11.0
(`The best overload for 'RecordAsync' does not have a parameter named 'format'`); no call binds the old name to another
argument. A call that passes the argument by position and a binary compiled against 2.10.x are not affected. An
implementation of `IAriBridgesResource` keeps compiling; rename its parameters too if it enables CA1725 (parameter
names should match the base declaration), and its own named callers keep binding to whatever it names them. The
channel `Channels.RecordAsync(…, format, …)`, whose `format` is the file format, is not renamed.

**What to do.** Rename the argument at the call site. Before (2.10.x; does not compile against 2.11.0):

<!-- skip-doc-snippet -->
```csharp
await ari.Bridges.PlayAsync(bridgeId, "tone:440", format: "slin16");
await ari.Bridges.RecordAsync(bridgeId, "call-1", "wav", format: "slin16");
```

After:

```csharp
using Verbara.Sdk;

static async Task PlayAndRecord(IAriClient ari, string bridgeId)
{
    await ari.Bridges.PlayAsync(bridgeId, "tone:440", announcerFormat: "slin16");
    await ari.Bridges.RecordAsync(bridgeId, "call-1", recordingFormat: "wav", recorderFormat: "slin16");
}
```

## Subscribers that throw

From 2.11.0 the Live managers and the ARI audio and outbound servers raise each event subscriber by subscriber, as `ChannelManager` has since 2.10.0. Code
that changes nothing on a subscriber's failure is not affected; code that threw from a handler to stop something is.
The [troubleshooting guide](troubleshooting.md#what-a-live-event-handler-may-do) has the full rule for each manager.

### A `QueueManager` or `MeetMeManager` subscriber that throws stops nothing else

**What changed.** `QueueManager` (`QueueUpdated`, `MemberAdded`, `MemberRemoved`, `MemberStatusChanged`,
`MemberPaused`, `CallerJoined`, `CallerLeft`) and `MeetMeManager` (`UserJoined`, `UserLeft`, `UserStateChanged`)
deliver each event to every subscriber, in subscription order, whatever an earlier one threw. A failure is counted on
`live.queues.subscriber_faults` or `live.meetme.subscriber_faults` (meter `Verbara.Sdk.Live`), then logged once at
Warning with the exception:

- `[QUEUE] Subscriber failed: event=<Event> queue=<Queue> subject=<Subject>; …`, where the subject is the member's
  interface, the caller's channel or, for `QueueUpdated`, the queue;
- `[CONFERENCE] Subscriber failed: event=<Event> room=<Room> channel=<Channel>; …`.

Nothing is thrown from any call: the live AMI event that made the change, `StartAsync`, a reconnect's reload and
`ReconcileQueuesAsync` all finish their work. The subscriber stays subscribed. `OutOfMemoryException` is not caught,
and only what throws on the delivering thread is. The last-resort Error line of the channel, queue and conference
notification queues now reads
`[CHANNEL|QUEUE|CONFERENCE] Event notification failed outside its subscribers; nothing was thrown from the call`.

**Who is affected.** Nothing reports a compiler error: no public symbol changes, and a binary built against 2.10.x
runs unchanged. What changes is behavior that 2.10.x documented in the troubleshooting guide:

- a subscriber after one that throws now receives the event (up to 2.10.x it did not);
- the first exception is no longer rethrown from the manager call that made the change, so a live event's failure no
  longer surfaces as `[AMI_EVENT] Observer threw on <EventType>`, and `StartAsync` or a reload no longer fails with it;
- a log query or alert on `[AMI_EVENT] Observer threw on` for these events sees nothing; read the Warning above or the
  counter instead.

**What to do.** If a handler threw to stop a start or a reload, make that decision in your own code. Before (2.10.x:
the exception left `StartAsync`; from 2.11.0 it is counted and `StartAsync` completes):

<!-- skip-doc-snippet -->
```csharp
server.Queues.MemberAdded += (queue, member) =>
{
    if (member.Penalty > 10)
        throw new InvalidOperationException($"{member.Interface} is misconfigured in {queue}");
};
await server.StartAsync(ct); // threw up to 2.10.x
```

After, the handler records what it found and the caller decides once the start is done:

```csharp
using Verbara.Sdk.Live.Server;

static async Task<IReadOnlyList<string>> StartAndCheckAsync(VerbaraServer server, CancellationToken ct)
{
    var misconfigured = new System.Collections.Concurrent.ConcurrentQueue<string>();
    server.Queues.MemberAdded += (queue, member) =>
    {
        if (member.Penalty > 10)
            misconfigured.Enqueue($"{member.Interface} in {queue}");
    };
    await server.StartAsync(ct);
    return [.. misconfigured];
}
```

To watch for subscriber failures, read the counters by name; they are not exposed as fields:

```csharp
using System.Diagnostics.Metrics;

static MeterListener CountSubscriberFaults(Action<string, long> onFault)
{
    var listener = new MeterListener
    {
        InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Verbara.Sdk.Live" && instrument.Name.EndsWith(".subscriber_faults", StringComparison.Ordinal))
                l.EnableMeasurementEvents(instrument);
        },
    };
    listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => onFault(instrument.Name, value));
    listener.Start();
    return listener;
}
```

### An `AgentManager` or `BridgeManager` subscriber that throws stops nothing else

**What changed.** `AgentManager` (`AgentLoggedIn`, `AgentLoggedOff`, `AgentStateChanged`, `AgentConnected`) and
`BridgeManager` (`BridgeCreated`, `BridgeDestroyed`, `ChannelEntered`, `ChannelLeft`, `TransferOccurred`) follow the
same rule: every subscriber receives the event, in subscription order, whatever an earlier one threw. A failure is
counted on `live.agents.subscriber_faults` or `live.bridges.subscriber_faults` (meter `Verbara.Sdk.Live`), then logged
once at Warning with the exception:

- `[AGENT] Subscriber failed: event=<Event> subject=<AgentId>; …`;
- `[BRIDGE] Subscriber failed: event=<Event> bridge_id=<BridgeId> channel=<Channel>; …`, the channel being the one
  that entered or left, or a blind transfer's target.

The events that follow in the same report are still raised: an `AgentConnect` raises `AgentStateChanged` and then
`AgentConnected` whatever a state subscriber threw, and a `BridgeEnter` still links the bridge's two channels
(`AsteriskChannel.LinkedChannel`) after `ChannelEntered`. Nothing is thrown from any call: the live AMI event,
`StartAsync`, a reconnect's reload (its agents' `AgentLoggedIn` and its bridge reconciliation's `BridgeDestroyed`
included) and `ReconcileQueuesAsync` when it settles an agent all finish their work. The subscriber stays subscribed,
and `OutOfMemoryException` is not caught.

**Who is affected.** Nothing reports a compiler error, and a binary built against 2.10.x runs unchanged. What changes:

- up to 2.10.x a throwing subscriber skipped the later subscribers **and the events after it in the same report**: a
  throwing `AgentStateChanged` subscriber skipped `AgentConnected`, and a throwing `ChannelEntered` one left the two
  channels unlinked. Code that compensated for a missing `AgentConnected` or an unset `LinkedChannel` after its own
  handler failed no longer needs to;
- the exception no longer leaves the call: a live event's no longer surfaces as `[AMI_EVENT] Observer threw on
  <EventType>`, a load or a reload no longer fails with it, and `ReconcileQueuesAsync` no longer rethrows it after
  settling the other agents.

**What to do.** As for the queue and conference managers: if a handler threw to stop a start, a reload or a
reconciliation, record the finding in the handler and decide in the caller. Before (2.10.x: the exception left
`StartAsync`, raised from the load's `AgentLoggedIn`; from 2.11.0 it is counted and the start completes):

<!-- skip-doc-snippet -->
```csharp
server.Agents.AgentLoggedIn += agent =>
{
    if (!allowed.Contains(agent.Id))
        throw new InvalidOperationException($"agent {agent.Id} is not provisioned");
};
await server.StartAsync(ct); // threw up to 2.10.x
```

After:

```csharp
using Verbara.Sdk.Live.Server;

static async Task<IReadOnlyList<string>> StartAndFindUnprovisionedAsync(
    VerbaraServer server, IReadOnlySet<string> allowed, CancellationToken ct)
{
    var unprovisioned = new System.Collections.Concurrent.ConcurrentQueue<string>();
    server.Agents.AgentLoggedIn += agent =>
    {
        if (!allowed.Contains(agent.Id))
            unprovisioned.Enqueue(agent.Id);
    };
    await server.StartAsync(ct);
    return [.. unprovisioned];
}
```

### An `OnStreamConnected` observer that throws loses no connection

**What changed.** The ARI `AudioSocketServer` and `WebSocketAudioServer` guard each `OnStreamConnected` subscription on
its own, and `CompositeAudioServer` (which `AddVerbara` registers) inherits that guard. An observer that throws on a new
stream loses nothing but its own call: the connection stays open and registered (`GetStream` finds it,
`ActiveStreamCount` counts it, its audio flows), and every observer subscribed after it still receives the stream. An
observer that throws on the completion a server's disposal raises no longer stops the disposal. The failure is counted
on `audio.stream_connected.subscriber_faults` (meter `Verbara.Sdk.Ari.Audio`, tag `audio.transport`: `audiosocket` or
`websocket`), then logged once at Warning: `[AudioSocket] OnStreamConnected observer failed; the connection and the
other observers go on: channel_id=<id>` (or `[WebSocketAudio] …`). The observer stays subscribed; one composed through
an Rx operator (`Where`, `Select`, …) is ended by that operator on its first exception. `OutOfMemoryException` is not
caught.

**Who is affected.** Nothing reports a compiler error. Up to 2.10.x a throwing observer lost that connection (the
server logged `Connection error` at Error, then closed and deregistered it), the observers after it never received the
stream, and a throw on the completion made `DisposeAsync` throw. An observer that threw `ObjectDisposedException` while
the server was stopping was dropped with no trace; it is now counted and logged like any other failure. Code that threw
from an observer **to refuse a stream** now leaves that stream open.

**What to do.** Refuse a stream by disposing it. Before (2.10.x: the throw closed the connection; from 2.11.0 the
connection stays open):

<!-- skip-doc-snippet -->
```csharp
server.OnStreamConnected.Subscribe(stream =>
{
    if (!IsExpected(stream.ChannelId))
        throw new InvalidOperationException("unexpected stream");
});
```

After:

```csharp
using Verbara.Sdk;

static IDisposable RefuseUnexpected(IAudioServer server, Func<string, bool> isExpected) =>
    server.OnStreamConnected.Subscribe(stream =>
    {
        if (!isExpected(stream.ChannelId))
            _ = stream.DisposeAsync().AsTask();
    });
```

### An ARI outbound observer that throws keeps its connection

**What changed.** `AriOutboundListener.OnConnectionAccepted` and `AriOutboundConnection.Events` guard each subscription
on its own, as `IAriClient` does. An observer that throws from `OnNext`, `OnCompleted` or `OnError` loses only that
call: the connection stays open, tracked (`ActiveConnectionCount`, `GetByApplication`) and read; the later observers are
still told about the connection, the event or the end of the stream; and a throw on the listener's completion no longer
keeps `DisposeAsync` from finishing. The failure is counted on `ari.outbound.subscriber_faults` (meter
`Verbara.Sdk.Ari`, tag `surface`: `connection_accepted` or `events`) and logged once at Warning:
`[AriOutbound] OnConnectionAccepted observer threw on <Notification>: remote=<RemoteEndpoint> app=<Application>; …` or
`[AriOutbound] Events observer threw on <EventType>: remote=<RemoteEndpoint> app=<Application>; …`.
`OutOfMemoryException` is not caught.

**Who is affected.** Nothing reports a compiler error. Up to 2.10.x an `OnConnectionAccepted` observer that threw closed
the connection it was told about, kept the later observers from it and was logged as `[AriOutbound] Connection error`
at Error; an `Events` observer that threw ended the read pump and closed the connection; and one that threw on a
completion kept the later observers from it. Code that threw from an observer **to refuse a connection** now leaves it
open.

**What to do.** Refuse it yourself: call `DisconnectAsync`, or restrict `AllowedApplications` in the listener's options.
Before (2.10.x: the throw closed the connection; from 2.11.0 it stays open):

<!-- skip-doc-snippet -->
```csharp
listener.OnConnectionAccepted.Subscribe(connection =>
{
    if (connection.ApplicationName != "ivr")
        throw new InvalidOperationException("not ours");
});
```

After:

```csharp
using Verbara.Sdk.Ari.Outbound;

static IDisposable RefuseOthers(IAriOutboundListener listener, IReadOnlySet<string> ours) =>
    listener.OnConnectionAccepted.Subscribe(connection =>
    {
        if (!ours.Contains(connection.ApplicationName))
            _ = connection.DisconnectAsync().AsTask();
    });
```
