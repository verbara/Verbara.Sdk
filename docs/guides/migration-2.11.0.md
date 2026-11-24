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
| `Verbara.Sdk.Live` | [The conference types are named for what they track](#the-conference-types-are-named-for-what-they-track) | new names, former names deprecated (`VSDK0008`, warning) |
| `Verbara.Sdk.Sessions` | [`CallFailedEvent` is never raised and is deprecated](#callfailedevent-is-never-raised-and-is-deprecated) | type deprecated (`VSDK0009`, warning) |
| `Verbara.Sdk.Live` | [A `QueueManager` or `MeetMeManager` subscriber that throws stops nothing else](#a-queuemanager-or-meetmemanager-subscriber-that-throws-stops-nothing-else) | behaviour, BREAKING (no compiler error) |
| `Verbara.Sdk.Live` | [An `AgentManager` or `BridgeManager` subscriber that throws stops nothing else](#an-agentmanager-or-bridgemanager-subscriber-that-throws-stops-nothing-else) | behaviour, BREAKING (no compiler error) |
| `Verbara.Sdk.Ari` | [An `OnStreamConnected` observer that throws loses no connection](#an-onstreamconnected-observer-that-throws-loses-no-connection) | behaviour, BREAKING (no compiler error) |
| `Verbara.Sdk.Ari` | [An ARI outbound observer that throws keeps its connection](#an-ari-outbound-observer-that-throws-keeps-its-connection) | behaviour, BREAKING (no compiler error) |
| `Verbara.Sdk.Activities`, `Verbara.Sdk.VoiceAi`, `Verbara.Sdk.VoiceAi.OpenAiRealtime` | [An activity, voice pipeline or realtime bridge subscriber that throws stops nothing else](#an-activity-voice-pipeline-or-realtime-bridge-subscriber-that-throws-stops-nothing-else) | behaviour, BREAKING (no compiler error; a cast of `StatusChanges` to `BehaviorSubject` fails at run time) |
| `Verbara.Sdk.VoiceAi.AudioSocket` | [An `OnSessionStarted` subscriber that fails ends the AudioSocket call only when every subscriber did](#an-onsessionstarted-subscriber-that-fails-ends-the-audiosocket-call-only-when-every-subscriber-did) | behaviour, BREAKING (no compiler error) |
| `Verbara.Sdk.VoiceAi.Stt` | [A Whisper or Azure Whisper reply with a `null` text makes `StreamAsync` throw](#a-whisper-or-azure-whisper-reply-with-a-null-text-makes-streamasync-throw) | behaviour, BREAKING (no compiler error) |
| `Verbara.Sdk.Sessions`, `Verbara.Sdk.Sessions.Redis`, `Verbara.Sdk.Sessions.Postgres` | [A session store's `linkedid` lookup with several servers](#a-session-stores-linkedid-lookup-with-several-servers) | behaviour, new member, fix, BREAKING (no compiler error; new Redis index key) |
| `Verbara.Sdk.Hosting` | [A host that attaches the server by hand or decorates the session manager](#a-host-that-attaches-the-server-by-hand-or-decorates-the-session-manager) | fix, behaviour (no compiler error) |
| `Verbara.Sdk.Sessions` | [`CallRingNoAnswerEvent` is raised, and `IAgentSessionTracker` lists members that only missed calls](#callringnoanswerevent-is-raised-and-iagentsessiontracker-lists-members-that-only-missed-calls) | fix, new member, behaviour (no compiler error) |
| `Verbara.Sdk.Cluster.Postgres` | [The cluster lock row says since when its owner holds it](#the-cluster-lock-row-says-since-when-its-owner-holds-it) | new nullable column, migration V002, fix (no compiler error) |
| `Verbara.Sdk.Live` | [The `live.server` span tag is obsolete](#the-liveserver-span-tag-is-obsolete) | span tag deprecated, new span tag (no compiler error) |
| `Verbara.Sdk.OpenTelemetry` | [OpenTelemetry 1.21 needs `System.Diagnostics.DiagnosticSource` 11](#opentelemetry-121-needs-systemdiagnosticsdiagnosticsource-11) | dependency floor raised (restore error `NU1605` for a lower pin) |

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

## Deprecated names

### The conference types are named for what they track

**What changed.** The Live conference table is named for conferences. Its types carried the name of MeetMe, but it
tracks ConfBridge, from the `Confbridge*` events and, on each load, Asterisk's conference listing; it can still be fed
by hand for app_meetme. 2.11.0 adds the conference names and marks the former names `[Obsolete]` with `VSDK0008`, a
warning. Nothing else changes: the old and new names reach the same objects, so the server holds one table, raises
each event once per subscriber, and every room and participant is the same instance under either name.

#### VSDK0008

| Former name (`VSDK0008`) | New name | Removed in |
|---|---|---|
| `VerbaraServer.MeetMe` | `VerbaraServer.Conferences`, which returns the same instance | 2.12.0 |
| `Verbara.Sdk.Live.MeetMe.MeetMeManager` | `Verbara.Sdk.Live.Conferences.ConferenceManager` | 2.12.0 |
| `Verbara.Sdk.Live.MeetMe.MeetMeRoom` | `Verbara.Sdk.Live.Conferences.ConferenceRoom` | 2.12.0 |
| `Verbara.Sdk.Live.MeetMe.MeetMeUser` | `Verbara.Sdk.Live.Conferences.ConferenceUser` | 2.12.0 |
| `Verbara.Sdk.Live.MeetMeUserState` | `Verbara.Sdk.Live.ConferenceUserState` (`Joined`, `Left`, same values) | 2.12.0 |

Three differences come with the new names:

- `ConferenceUserState` has no `Talking`. `MeetMeUserState.Talking` was never assigned; whether a participant is
  talking is `ConferenceUser.Talking`.
- A consumer cannot change a room or a participant through the new names. `ConferenceRoom.Users` is a read-only view
  (`IReadOnlyDictionary<string, ConferenceUser>`) of the room's participants, where `MeetMeRoom.Users` is the mutable
  dictionary itself, and the `ConferenceUser` properties have no public setter. Live is the only writer, and a write
  through the old names was outside the manager's synchronization.
- `ConferenceRoom` and `ConferenceUser` have no public constructor: the rooms and participants are the ones the
  manager reports. To feed a table yourself, construct a `ConferenceManager` and call its `OnUserJoined`, `OnUserLeft`
  and `OnConferenceEnded`, as with `MeetMeManager`.

The events, their order, the `[CONFERENCE]` log lines and the `live.meetme.subscriber_faults` counter are unchanged. A
handler subscribed under the old name and one under the new name share one subscription order.

**Who is affected.** Code that names one of the former symbols. A build with TreatWarningsAsErrors fails on
`VSDK0008` until the code moves or suppresses the id; any other build shows the warning. A binary built against 2.10.x
runs unchanged.

**What to do.** Move to the new names before 2.12.0. Before (2.10.x; compiles against 2.11.0 with `VSDK0008`):

<!-- skip-doc-snippet -->
```csharp
using Verbara.Sdk.Live.MeetMe;

server.MeetMe.UserJoined += (MeetMeRoom room, MeetMeUser user) => Console.WriteLine($"{user.Channel} joined {room.RoomNumber}");
var count = server.MeetMe.GetRoom("100")?.UserCount ?? 0;
```

After:

```csharp
using Verbara.Sdk.Live.Conferences;
using Verbara.Sdk.Live.Server;

static int Watch(VerbaraServer server)
{
    server.Conferences.UserJoined += (ConferenceRoom room, ConferenceUser user) =>
        Console.WriteLine($"{user.Channel} joined {room.RoomNumber}");
    return server.Conferences.GetRoom("100")?.UserCount ?? 0;
}
```

If you cannot move before upgrading, suppress `VSDK0008` where you name a former symbol (`#pragma warning disable
VSDK0008`, or `NoWarn` in the project); the suppression works until 2.12.0 removes them. Code that wrote
`MeetMeRoom.Users` or a `MeetMeUser` setter has no replacement: it changed the table without Live knowing.

### `CallFailedEvent` is never raised and is deprecated

**What changed.** The session manager raises every ending of a call as `CallEndedEvent`, a failed call included: its
`Cause` is the hangup cause, and the session's `State` is `Failed`. It has never raised `CallFailedEvent`, so a handler
for it never ran. 2.11.0 marks the record `[Obsolete]` with `VSDK0009`, a warning, and 2.12.0 removes it.

#### VSDK0009

| Deprecated symbol (`VSDK0009`) | Read instead | Removed in |
|---|---|---|
| `Verbara.Sdk.Sessions.CallFailedEvent` | `Verbara.Sdk.Sessions.CallEndedEvent`, raised for every ending, with its `Cause` | 2.12.0 |

**Who is affected.** Code that names `CallFailedEvent`: a `case` or `is` test on the session events, or a record built
by hand in a test or a replay. A build with TreatWarningsAsErrors fails on `VSDK0009` until the code moves or
suppresses the id; any other build shows the warning. A binary built against 2.10.x runs unchanged, and behaves as it
did: its `CallFailedEvent` handler still never runs.

**What to do.** Handle the ending in the `CallEndedEvent` handler and drop the `CallFailedEvent` one. Before (2.10.x;
compiles against 2.11.0 with `VSDK0009`):

<!-- skip-doc-snippet -->
```csharp
sessions.Events.Subscribe(evt =>
{
    switch (evt)
    {
        case CallEndedEvent ended: Console.WriteLine($"{ended.SessionId} ended"); break;
        case CallFailedEvent failed: Console.WriteLine($"{failed.SessionId} failed: {failed.Reason}"); break;
    }
});
```

After:

```csharp
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;

static IDisposable Watch(ICallSessionManager sessions) =>
    sessions.Events.Subscribe(evt =>
    {
        if (evt is CallEndedEvent ended)
            Console.WriteLine($"{ended.SessionId} ended, cause {ended.Cause?.ToString() ?? "none"}");
    });
```

If you cannot move before upgrading, suppress `VSDK0009` where you name the record (`#pragma warning disable
VSDK0009`, or `NoWarn` in the project); the suppression works until 2.12.0 removes it.

## Subscribers that throw

From 2.11.0 the Live managers, the ARI audio and outbound servers, the activities and the voice sessions raise each event subscriber by subscriber, as `ChannelManager` has since 2.10.0. Code
that changes nothing on a subscriber's failure is not affected; code that threw from a handler to stop something is.
The [troubleshooting guide](troubleshooting.md#what-a-live-event-handler-may-do) has the full rule for each manager.

### A `QueueManager` or `MeetMeManager` subscriber that throws stops nothing else

**What changed.** `QueueManager` (`QueueUpdated`, `MemberAdded`, `MemberRemoved`, `MemberStatusChanged`,
`MemberPaused`, `CallerJoined`, `CallerLeft`) and `MeetMeManager`, named `ConferenceManager` from 2.11.0 (`UserJoined`, `UserLeft`, `UserStateChanged`)
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
runs unchanged. What changes is behaviour that 2.10.x documented in the troubleshooting guide:

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

### An activity, voice pipeline or realtime bridge subscriber that throws stops nothing else

**What changed.** A subscriber of an activity's `StatusChanges` (`ActivityBase`, `AmiActivityBase`, `AriActivityBase`
and every shipped activity), of `VoiceAiPipeline.Events` or of `OpenAiRealtimeBridge.Events` has its own guard. If it
throws from `OnNext`, `OnError` or `OnCompleted`, every other subscriber still gets the notification, in subscription
order, and nothing is thrown from the call that raised it (`Subscribe`, `StartAsync`, `CancelAsync` or `DisposeAsync` for
an activity; the session loop or `DisposeAsync` for the pipeline and the bridge). The failure is counted:

| Publisher | Counter (meter) | Log |
|---|---|---|
| Activity | `activities.status.subscriber_faults`, tag `activity.type` (new meter `Verbara.Sdk.Activities`) | none: the activity bases hold no logger |
| `VoiceAiPipeline` | `voiceai.pipeline.subscriber_faults` (`Verbara.Sdk.VoiceAi`) | Warning `VoiceAi pipeline event subscriber threw on <EventType>; …` |
| `OpenAiRealtimeBridge` | `openai_realtime.subscriber_faults` (`Verbara.Sdk.VoiceAi.OpenAiRealtime`) | Warning `[<ChannelId>] A Realtime event subscriber threw on <EventType>; …` |

`VerbaraTelemetry.MeterNames` lists the new meter: 16 meters, where it had 15. `StatusChanges` is still typed
`IObservable<ActivityStatus>`, but it is no longer the activity's `BehaviorSubject`: a cast of it to
`BehaviorSubject<ActivityStatus>` or `ISubject<ActivityStatus>` now fails at run time.

**Who is affected.** Nothing reports a compiler error. Up to 2.10.x a throwing subscriber kept the later subscribers
from that notification, and its exception left the call that raised it:

- an activity could be left in `Starting`, with `StartAsync`, `CancelAsync` or `DisposeAsync` throwing the subscriber's
  exception, and an ARI activity skipped `OnCancellingAsync`;
- an activity whose subscriber threw on `InProgress` ended `Failed` **without executing**; one that threw on `Completed`
  turned a completed activity into a `Failed` one. From 2.11.0 the activity executes, so its side effects happen (a
  dial dials), and it ends `Completed`;
- the pipeline's or the bridge's session ended as failed (`voiceai.sessions.failed`, `openai_realtime.sessions.failed`);
- a pipeline subscriber that threw on `TranscriptReceivedEvent`, `ResponseGeneratedEvent` or `SynthesisEndedEvent`
  failed that step: a `VoiceAi pipeline error [<Stt|Handler|Tts>]` Warning, a `PipelineErrorEvent`, a count on
  `stt.transcriptions.failed` or `tts.syntheses.failed`, and the rest of the turn dropped. From 2.11.0 none of these
  appear for a subscriber's failure, so a dashboard or alert on them sees fewer, and the turn finishes.

**What to do.** Stop an activity with `CancelAsync`, and end a voice session by cancelling the token passed to
`HandleSessionAsync` or by returning from the conversation handler. Read the counter, or the Warning where the
publisher logs. Before (2.10.x: the throw on `InProgress` failed the activity before it executed; from 2.11.0 it
executes):

<!-- skip-doc-snippet -->
```csharp
activity.StatusChanges.Subscribe(status =>
{
    if (status == ActivityStatus.InProgress && !allowed)
        throw new InvalidOperationException("not allowed now");
});
await activity.StartAsync(ct);
```

After, decided before the start, or cancelled explicitly:

```csharp
using Verbara.Sdk.Activities.Activities;

static async ValueTask StartIfAllowedAsync(IActivity activity, Func<bool> allowed, CancellationToken ct)
{
    if (!allowed())
        return;
    using var stop = activity.StatusChanges.Subscribe(status =>
    {
        if (status == ActivityStatus.InProgress && !allowed())
            _ = activity.CancelAsync().AsTask();
    });
    await activity.StartAsync(ct);
}
```

### An `OnSessionStarted` subscriber that fails ends the AudioSocket call only when every subscriber did

**What changed.** The VoiceAi `AudioSocketServer` invokes every `OnSessionStarted` subscriber, in subscription order,
whatever an earlier one threw, and then awaits all of their tasks together. A subscriber that throws, or whose task
faults, is counted on `audiosocket.subscriber_faults` (meter `Verbara.Sdk.VoiceAi.AudioSocket`) and logged once
at Warning, `[AudioSocket] An OnSessionStarted subscriber failed for channel <uuid>`, when it fails. The connection is
closed only when **every** subscriber failed, and then with a hangup frame first, so on Asterisk 20 and later the call
goes on in the dialplan after `AudioSocket()`. `OutOfMemoryException` is not caught.

**Who is affected.** Nothing reports a compiler error.

- **Two or more subscribers** (for example a session broker from `AddVoiceAiPipeline` or `AddOpenAiRealtimeBridge` next
  to a handler of your own): up to 2.10.x only the last subscriber's task was awaited. A synchronous throw skipped every
  later subscriber and closed the connection; an earlier subscriber's faulted task was lost with no line and no count;
  and a failure of the last subscriber closed the connection even when another was serving the call. Now the call
  another subscriber completed on stays up. A subscriber that owns the call and fails next to a passive one that
  completes leaves the connection open until the far end hangs up.
- **One subscriber:** its failure still closes the connection, now with a hangup frame before the close, and is reported
  as the Warning and the counter above instead of `[AudioSocket] Error handling connection` at Error.

**What to do.** A subscriber that threw to end the call hangs up and returns instead; one that owns the call catches
its own failure and hangs up. Before (2.10.x: the throw closed the connection; from 2.11.0 it does so only if every
subscriber failed):

<!-- skip-doc-snippet -->
```csharp
server.OnSessionStarted += session =>
    IsAllowed(session.ChannelId) ? ServeAsync(session) : throw new InvalidOperationException("refused");
```

After:

```csharp
using Verbara.Sdk.VoiceAi.AudioSocket;

static void ServeOrHangUp(AudioSocketServer server, Func<Guid, bool> isAllowed, Func<AudioSocketSession, ValueTask> serve) =>
    server.OnSessionStarted += async session =>
    {
        if (!isAllowed(session.ChannelId))
        {
            await session.HangupAsync();
            return;
        }
        try
        {
            await serve(session);
        }
        catch (Exception)
        {
            await session.HangupAsync();
            throw;
        }
    };
```

## Provider replies

### A Whisper or Azure Whisper reply with a `null` text makes `StreamAsync` throw

**What changed.** When the vendor replies `{"text":null}`, `StreamAsync` throws `JsonException` from the
`await foreach`. Up to 2.10.x it returned no result and no error, so the reply could not be told from silence. Both
vendor contracts require `text`, so a `null` there is a reply the SDK cannot read, and it is now reported as one.

**Who is affected.** Code that enumerates `StreamAsync` of `WhisperSpeechRecognizer` or `AzureWhisperSpeechRecognizer`
itself. Nothing reports a compiler error. Through `VoiceAiPipeline` nothing throws: the pipeline catches the exception,
counts the transcription as failed, publishes a `PipelineErrorEvent` and goes back to listening. Before, such a reply
published no event, so a `PipelineErrorEvent` subscriber now sees one where it saw none.

**What to do.** Catch `JsonException` where you enumerate `StreamAsync` on these two recognizers, and treat it as a failed
transcription, as the pipeline does:

```csharp
using System.Text.Json;
using Verbara.Sdk.Audio;

static async Task<string?> TranscribeAsync(
    SpeechRecognizer whisper, IAsyncEnumerable<ReadOnlyMemory<byte>> audio, AudioFormat format, CancellationToken ct)
{
    try
    {
        await foreach (var result in whisper.StreamAsync(audio, format, ct))
            return result.Transcript;

        return null;
    }
    catch (JsonException)
    {
        // The vendor replied without a transcript: a failed transcription, not silence.
        return null;
    }
}
```

## Session stores

### A session store's `linkedid` lookup with several servers

**What changed.**

- `GetByLinkedIdAsync(linkedId, ct)` returns the session only when exactly one server has a session stored under that
  `linkedid`, and `null` when two or more do. Before, it returned one of them (Redis: the last saved; Postgres: the
  newest row; the default store: the first it found). It is the rule the session manager's unscoped lookups follow
  since 2.10.0.
- A session counts while the store keeps it: Redis until its `CompletedRetention` runs out, the default store until the
  session manager releases it, Postgres until its row is deleted. **In Postgres a `linkedid` that two servers ever used
  stays ambiguous to the unscoped lookup for as long as both rows are kept.**
- New: `GetByLinkedIdAsync(serverId, linkedId, ct)` returns the named server's session whatever another server holds.

**Who is affected.** Code that calls `ISessionStore.GetByLinkedIdAsync(linkedId, ct)` on one session store — the
default store, `RedisSessionStore`, `PostgresSessionStore`, or a store of your own — that holds sessions of **two or
more Asterisk servers**. Two servers report the same `linkedid` when their Asterisk servers have no `systemname` of
their own, or when one Asterisk is attached under two server ids. With one server and one session under the id nothing
changes; when one server holds two sessions under the id, the default store now answers with the newest (by
`CreatedAt`) where it returned whichever it enumerated first. A tool of your own that reads the Redis key
`{prefix}idx:linked:{linkedId}` is affected too. Nothing reports a compiler error.

**What to do.** Treat a `null` from the unscoped lookup as "absent or not unique", and name the server when you know it
(every `CallSession` carries its `ServerId`). Before (2.10.x: one of two unrelated calls when two servers share the
`linkedid`; from 2.11.0, `null`):

<!-- skip-doc-snippet -->
```csharp
var session = await store.GetByLinkedIdAsync(linkedId, ct);
```

After (the call of the server that reported the event):

```csharp
using Verbara.Sdk.Sessions;

static ValueTask<CallSession?> FindCall(ISessionStore store, string serverId, string linkedId, CancellationToken ct) =>
    store.GetByLinkedIdAsync(serverId, linkedId, ct);
```

A store of your own keeps compiling. Its scoped lookup is a default that calls your unscoped one and keeps the answer
only when it belongs to the named server, so it can return `null` for an id two servers hold. Override
`GetByLinkedIdAsync(string serverId, string linkedId, CancellationToken ct)` (on `SessionStoreBase` it is `virtual`) to
answer per server.

**Redis: a new index key, Redis 5.0 or later, scripting allowed.**

- The `linkedid` index moves from `{prefix}idx:linked:{linkedId}` (a string holding one session id) to
  `{prefix}idx:linked-by-server:{linkedId}`, a hash with one field per server id whose value is
  `<expiryUnixMs>|<sessionId>` (`0` while the session is live). Read the hash in tools of your own.
- A live save writes its server's field with `HSET` and clears the key's expiry with `PERSIST`, in the save's batch.
  Ending or deleting a session runs a single-key Lua script that reads the server clock with `TIME`: **Redis 5.0 or
  later** is required, and **the connection's user must be allowed to run scripts** — `EVALSHA`/`EVAL` and
  `SCRIPT LOAD` (ACL category `@scripting`) plus `TIME`, `HSET`, `HGET`, `HDEL`, `HGETALL`, `PERSIST`, `PEXPIREAT`,
  `GET`, `DEL` and `PEXPIRE`. A deployment or proxy that refuses scripting is not supported: live saves succeed there,
  but ending or deleting a session fails. A lookup only reads: it removes no index entry. Every script names one key,
  so none is refused with `CROSSSLOT` on Redis Cluster; there a lookup that races a session's first save can miss it
  once. The suite runs against a single node.
- Cost: a live save sends two index commands where 2.10.x sent one; a `SaveBatchAsync` of 100 live sessions measured
  ×1.22 the time of 2.10.x, a single `SaveAsync` ×1.05. Ending or deleting a session costs one script per session.
- Save a session from one place at a time: the store does not order two concurrent saves of the same session, which
  can leave the session and its index entry from different saves.
- **No migration.** The old string is still read — a session it names counts as its server's while that session exists
  and that server has no field in the hash — and is never written again. When the session it names ends, the store
  gives it that session's retention; when that session is deleted, the store removes it. Nothing is left behind.
- **Rolling upgrade.** Nodes of 2.10.x and 2.11.0 can share one Redis: the keys differ in name and type, so neither
  receives `WRONGTYPE`. During the window a 2.10.x node does not find by `linkedid` a session a 2.11.0 node created, and
  2.10.x nodes keep the old behaviour among themselves until they are upgraded. A session whose old entry another
  server overwrote before the upgrade is found again after its next save from a 2.11.0 node.

**Postgres and the default store.** No schema change, no migration, no new index: both read the same rows as before.

## Session hosts

### A host that attaches the server by hand or decorates the session manager

**What changed.** The hosting services act on the registered `ICallSessionManager` whatever its type, and the
single-server stop detaches only the attachment its own start installed:

- The single-server registrations (`AddVerbaraSessions`, `AddVerbaraSessionsBuilder`) call `AttachToServer` at start
  and `DetachFromServer` at stop on any registered manager, a decorator or a test substitute included. Up to 2.10.x a
  manager that was not the SDK's `CallSessionManager` got neither, so a decorated host opened no session at all.
- The persistence token, the reconciliation sweeps and the queue counters' visit signals go to the engine the SDK finds
  behind the registered manager: the resolved interface when it is a `CallSessionManager`, else a `CallSessionManager`
  the host registered as itself. The SDK never builds an engine of its own to reach.
- The stop releases the `default` attachment only when its start installed it. An attachment of the DI server under
  `default` that the host made before the start, or one present when the start's attach threw or never ran, survives
  the stop.
- `CallSessionManager.DisposeAsync` returns at once on a second call.

**Who is affected.** A host that attaches nothing by hand and registers no manager of its own sees no difference.
Nothing reports a compiler error. For other hosts:

- A manager whose `AttachToServer` throws now fails the host's start.
- A decorated host that also attaches the DI server by hand under another id now gets two sessions and two saves per
  call.
- A hand attach of another server under `default` now throws `InvalidOperationException` at start.
- A host whose token reaches no engine logs one Warning at start (EventId 1 in the
  `Verbara.Sdk.Hosting.SessionPersistenceTokenService` category, naming the manager's type): its wrapper builds its
  engine with `new` or `ActivatorUtilities`, or wraps no engine.
- Behind a decorator over a registered engine, the sweep now sends `Status` and ends calls, and the queue counters count
  key exits, timeouts and the end of the wait: `CallsAbandoned` drops by the key exits and `CallsTimedOut` rises from 0.
- A host that registers a `CallSessionManager` as itself beside a manager of its own that does not wrap it hands the
  token and the sweep to that idle instance.
- On an undecorated host, a `default` attachment of the DI server made before the start now survives the stop: new
  channels keep opening sessions until the host detaches it. A `default` attachment made after the start still belongs
  to the SDK, and the stop still releases it.
- A decorator that does not forward `AttachToServer` to the engine it wraps within the call is treated as a manager of
  the host's own: it is attached at start and detached at stop, so a `default` attached through it before the start is
  released at the stop.

**What to do.** Register the engine as `CallSessionManager` and your wrapper as `ICallSessionManager` over that
registered instance, as the Sessions README's recipe shows, and forward `AttachToServer` within the call. A decorated
host that attached the DI server by hand under another id removes that attach: the SDK's start now makes it. Do not
register an engine you never attach. Attach again after the stop if you need an attachment made after the start. The CHANGELOG entries for the decorated
session manager and for the single-server stop list every case.

## Events now raised

### `CallRingNoAnswerEvent` is raised, and `IAgentSessionTracker` lists members that only missed calls

**What changed.** Before 2.11.0 the event was declared and never raised, so a handler of it never ran and
`AgentSession.CallsMissed` was always 0. From 2.11.0 the event is raised once for each `AgentRingNoAnswer` Asterisk
reports. That is, a queue member rang out unanswered or answered busy. Because app_queue names no agent for an ordinary
member, `AgentId` is the member's interface (`PJSIP/agent1`). So `ActiveAgents` can now hold a session keyed by an
interface. That session has `CallsMissed` > 0 and `CallsHandled` = 0, because connected calls are still counted only
for an agent known by name.

**Who is affected.** Applications that subscribe to `ICallSessionManager.Events` and handle `CallRingNoAnswerEvent`,
and applications that list `IAgentSessionTracker.ActiveAgents`. Nothing fails to compile and nothing throws.

**What to do.**

- A real-time or interval counter of missed offers needs no change. It starts counting.
- An application that treats every `ActiveAgents` entry as a logged-in agent known by name should filter the entries.
  For example, skip a session whose `AgentId` contains `/`, or one that has no `CurrentCall` and `CallsHandled == 0`.
- An application that wants the interface even when an agent is named reads `CallRingNoAnswerEvent.AgentInterface`.
- Ignoring `CallRingNoAnswerEvent` in your own subscription keeps only your handlers as before. It does not change
  what `IAgentSessionTracker` receives, so its `CallsMissed` and `ActiveAgents` change either way.

## Cluster lock

### The cluster lock row says since when its owner holds it

**What changed.**

- `EnsureSchemaAsync` runs two migrations, V001 and then V002, in one transaction. Concurrent runs against the same
  database wait for each other behind a transaction-scoped advisory lock. V002 adds `acquired_at TIMESTAMPTZ` (nullable,
  with no default) and issues its `ALTER TABLE` only while the column is missing, so it takes no `ACCESS EXCLUSIVE`
  lock on later starts.
- `acquired_at` is the server time at which the current `owner` **name** took the lock:
  - An insert stamps `NOW()`, and so does winning an expired row, whoever held it before.
  - A renewal by the same owner before `expires_at` keeps the value.
  - Rows that existed before the upgrade read `NULL`, which means unknown, and they stay `NULL` until the lock
    expires and is taken again.

**Who is affected.** Code that uses `Verbara.Sdk.Cluster.Postgres`: you call `MigrationRunner.EnsureSchemaAsync`, or
tools of your own read `cluster_distributed_lock`. Code that only calls `IDistributedLock` keeps working as it is. Its
signatures and results do not change.

**What to do.**

- **Nothing changes in what `EnsureSchemaAsync` needs.** It must run as the role that owns the table, as on 2.10.x,
  where V001's `COMMENT ON TABLE` already required it. The first start after the upgrade adds the column. Every start
  after that issues no `ALTER TABLE`, so it never waits behind an open reader of the table.
- **If you read `acquired_at`, apply `max(acquired_at, the time you first saw this owner)` and treat `NULL` as
  unknown.** The value measures how long the owner name has held the lock, not the process. A raw `acquired_at` can be
  older than what you are watching in two cases:
  - **A restart after a crash.** The row outlives the process, so a process restarted under the same machine or pod
    name before expiry inherits the dead process's time.
  - **A staggered deploy.** A node still on 2.10.x sets only `owner` and `expires_at`. Its rows read `NULL`, and when
    it takes an expired row, the previous owner's `acquired_at` stays in place.

  Before (2.10.x): there is no column, so the only tenure you can know is your own first observation, which a restart
  of the reader resets.

  After:

  <!-- skip-doc-snippet -->
  ```sql
  SELECT owner, expires_at, acquired_at FROM cluster_distributed_lock WHERE resource = @resource;
  -- tenure_start = GREATEST(acquired_at, first_seen_by_this_reader); NULL acquired_at = unknown
  ```
- If you start several hosts at once against a database that has no lock table yet, you need nothing more. On 2.10.x,
  all but one of them could fail that start with `23505` on `pg_type_typname_nsp_index`. Hosts still on 2.10.x are not
  covered by the new advisory lock, so an old host and a new host can still collide on a fresh database during a mixed
  rollout. The host that loses succeeds on its next start.

## Span tags

### The `live.server` span tag is obsolete

**What changed.** `live.server` on the `live channel-reconcile` and `live state-load` spans holds the Asterisk version,
not a server identity. It keeps that value through 2.11.x, and 2.12.0 removes the tag. 2.11.0 adds `server.id`, the
pool id, on `live channel-reconcile`, `live state-load` and `live originate` when the server is in a
`VerbaraServerPool`; a standalone server's spans carry none.

**Who is affected.** A dashboard, query or alert that reads `live.server`. Nothing reports a compiler error and no
public symbol changes.

**What to do.** Read `server.id` to tell pooled servers apart before 2.12.0, and read `VerbaraServer.AsteriskVersion`
if you used `live.server` for the Asterisk version.

## Dependencies

### OpenTelemetry 1.21 needs `System.Diagnostics.DiagnosticSource` 11

**What changed.** `Verbara.Sdk.OpenTelemetry` now requires the five OpenTelemetry packages at 1.21
(`OpenTelemetry.Exporter.Prometheus.AspNetCore` at `1.21.0-beta.1`). `OpenTelemetry.Api` 1.21.0 requires
`System.Diagnostics.DiagnosticSource` 11.0.0 or later on `net10.0`, so the application restores that package and ships
its own `System.Diagnostics.DiagnosticSource.dll` instead of the shared framework's copy.

**Who is affected.** An application that references an OpenTelemetry package below 1.21, or
`System.Diagnostics.DiagnosticSource` below 11.0.0, directly in its own project or `Directory.Packages.props`. Restore
fails with:

```
error NU1605: Warning As Error: Detected package downgrade: System.Diagnostics.DiagnosticSource from 11.0.0 to 10.0.0.
```

An application that references neither package directly needs no change.

**What to do.** Raise those references to OpenTelemetry 1.21 and `System.Diagnostics.DiagnosticSource` 11.0.0 or
later, or remove them and let `Verbara.Sdk.OpenTelemetry` bring them. The
[CHANGELOG](../../CHANGELOG.md) lists the upstream changes an application can see in its own telemetry (Prometheus
scrape output, configuration binding, the experimental OTLP disk retry).
