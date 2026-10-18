# Upgrading from 2.8.0 to 2.9.0

This is the one migration guide for 2.9.0. It has one section per breaking entry of the release, says who each
one reaches and what to do, and links to the longer guide where one exists. The [CHANGELOG](../../CHANGELOG.md) has the full entry for each.

## Before you start: move every package at once

Every `Verbara.Sdk.*` package moves to 2.9.0 together. Do not mix versions: `Verbara.Sdk.Live` 2.8.0 names two
of the AMI event types 2.9.0 removes, so loaded next to `Verbara.Sdk.Ami` 2.9.0 it cannot resolve them. The same
holds for a library you build on top of the SDK: upgrade it and the application that uses it to 2.9.0 in one step,
and recompile both. A binary built against 2.8.0 that names a removed type fails with `TypeLoadException` when it
loads that type, and one that calls a reshaped member fails with `MissingMethodException` when it calls it.

## How to read this guide

The sections are ordered by package: Ami, Live, Sessions, Activities, Ari audio, Voice AI, Push webhooks. Each
says:

- **What changed**, in a sentence or two.
- **Who is affected**: the API you call or the behaviour you rely on. If none of it applies to you, skip the
  section.
- **What to do**: the code change, or the exact warning suppression, and a link to the guide for the change when
  one exists.

Two entries only add warnings (`VSDK0005` and `VSDK0003`). They break nothing unless you build with
`TreatWarningsAsErrors`. Two stop compiling where you use them. The other eight change no existing signature, but change
behaviour a program can branch on, a published metric, or a health status; one of them also refuses some
configuration values at start-up.

| Package | Change | Kind |
|---|---|---|
| `Verbara.Sdk.Ami` (and `VoiceAi.Tts`) | [Obsolete AMI event types, `LegacyEventAdapter` and `DeepgramVoices.Helios` removed](#obsolete-ami-event-types-are-removed) | does not compile |
| `Verbara.Sdk.Ami`, `Agi`, `Activities` | [Types of dead Asterisk modules deprecated with `VSDK0005`](#types-of-dead-asterisk-modules-warn-vsdk0005) | warning |
| `Verbara.Sdk.Ami` | [A yes from Asterisk reads true](#a-yes-from-asterisk-reads-true) | behaviour |
| `Verbara.Sdk.Live` | [A conference counts every caller](#a-conference-counts-every-caller) | does not compile |
| `Verbara.Sdk.Live` | [A conference participant shows whether it is muted and talking](#a-conference-participant-shows-muted-and-talking) | behaviour |
| `Verbara.Sdk.Sessions` | [`CallWrapUpEvent` is raised and `WrapUpDuration` is applied](#an-agents-wrap-up-is-opened-timed-and-closed) | behaviour, start-up validation |
| `Verbara.Sdk.Activities` | [An activity ends `Cancelled` only when someone asked it to stop](#an-activity-ends-cancelled-only-when-asked-to-stop) | behaviour |
| `Verbara.Sdk.Activities` | [An attended transfer Asterisk refuses ends `Failed`](#a-refused-attended-transfer-ends-failed) | behaviour |
| `Verbara.Sdk.Ari` | [`AudioStreamMetrics.BufferUnderruns` warns `VSDK0003`](#audiostreammetricsbufferunderruns-warns-vsdk0003) | warning |
| `Verbara.Sdk.VoiceAi`, `VoiceAi.OpenAiRealtime` | [A cancelled voice session is counted cancelled](#a-cancelled-voice-session-is-counted-cancelled) | metric meaning |
| `Verbara.Sdk.VoiceAi.OpenAiRealtime` | [The `openai-realtime` health entry reports `Degraded`](#the-openai-realtime-health-entry-reports-degraded) | health status |
| `Verbara.Sdk.Push.Webhooks` | [A webhook receives a topic-less event its event type matches](#a-webhook-receives-a-topic-less-event) | behaviour |

## Ami

### Obsolete AMI event types are removed

**What changed.** Twenty-six AMI event types that were already `[Obsolete]` are removed from `Verbara.Sdk.Ami`:
seven `MeetMe*`, `MonitorStartEvent` and `MonitorStopEvent`, eight pre-Asterisk-12 bridging, dialing and queue
events (`BridgeEvent`, `DialEvent`, `JoinEvent`, `LeaveEvent`, `LinkEvent`, `UnlinkEvent`, `PausedEvent`,
`UnpausedEvent`), seven `Skype*` and the two `ZapShowChannels*`. Also removed: the public
`Verbara.Sdk.Ami.Internal.LegacyEventAdapter`, and `DeepgramVoices.Helios` in `Verbara.Sdk.VoiceAi.Tts`, an alias
of `HeliosLegacy`. `VerbaraServer.MeetMe` is now fed by ConfBridge events only.

**Who is affected.** Code that names one of those types, the adapter, or `DeepgramVoices.Helios`. Code that tracked
app_meetme rooms through `VerbaraServer.MeetMe`.

**What to do.** Recompile; each use becomes `CS0246` (a type) or `CS0117` (`Helios`). Replace each type with the
one the guide's table names, and `DeepgramVoices.Helios` with `DeepgramVoices.HeliosLegacy` (same value). An
Asterisk that still sends one of these events delivers it as a base `ManagerEvent` with its `EventType` and
`RawFields`, and the guide has the recipe to read it. `PeerEntryEvent` stays and is no longer obsolete: a
`#pragma warning disable CS0618` around it can go.

Guide: [Migrating off the AMI event types removed in 2.9.0](obsolete-ami-events-removal-migration.md).

### Types of dead Asterisk modules warn `VSDK0005`

**What changed.** Forty-nine types and one member are `[Obsolete]` with diagnostic id `VSDK0005`, a warning, never
an error. Each reaches only chan_sip, res_monitor, app_meetme, Skype for Asterisk, the Zaptel action names,
chan_agent or app_setcdruserfield, or an event name Asterisk 10 or 12 renamed or removed. The member is
`MeetmeActivity.UseConfBridge` in `Verbara.Sdk.Activities`; `MeetmeCommand` is in `Verbara.Sdk.Agi`, the rest in
`Verbara.Sdk.Ami`. Nothing changes at run time: every action is still sent, every response and event still parsed,
each wire name maps to the same type. They are removed in 2.10.0.

**Who is affected.** Code that names one of them and builds with `TreatWarningsAsErrors`: it gets
`error VSDK0005`.

**What to do.** Replace each use before 2.10.0 with the replacement the warning's link opens. Until then, silence
this deprecation alone, without hiding other obsolete warnings:

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);VSDK0005</NoWarn>
</PropertyGroup>
```

or, around one line only, `#pragma warning disable VSDK0005` / `#pragma warning restore VSDK0005`.

Guide: [VSDK0005, with the replacement for each symbol](obsolete-ami-events-removal-migration.md#vsdk0005).

### A yes from Asterisk reads true

**What changed.** Every `bool?` member of an AMI event or response read only `1` and `true` as true, so a `Yes`,
`on` or `Enabled` from Asterisk read `false`. Now `yes`, `true`, `y`, `t`, `1`, `on` and `enabled` read `true`;
`no`, `false`, `n`, `f`, `0`, `off` and `disabled` read `false`, in any case; any other value, or an empty one,
reads `null`. Sixteen event members change reading, among them `ConfbridgeListEvent.Admin` and `MarkedUser`,
`ConfbridgeTalkingEvent.TalkingStatus`, the six `VoicemailUserEntryEvent` options, `PeerEntryEvent.Dynamic` and
`Trunk`, and `LocalOptimizationEndEvent.Success` (Asterisk's header verbatim, not an outcome flag). On an
Asterisk built with chan_sip, five members of `SipShowPeerResponse` and four more of `PeerEntryEvent` change too.
Members whose wire is already `0`/`1` or `true`/`false` read as before.

**Who is affected.** Code that branched on `false` for one of these members: it now takes the other branch where
Asterisk sends a yes. Code that wrote `== false` and meant "not true" no longer catches an unreadable value, which
now reads `null`.

**What to do.** Re-read each branch on these members against the new reading; test for the value you mean
(`== true`, `== false`, or `is null`). For the yes/no members the SDK keeps as text, read them with the same
vocabulary through the new `AmiBoolean.Parse`:

```csharp
using Verbara.Sdk.Ami;
using Verbara.Sdk.Ami.Events;

static void Show(ConfbridgeListEvent participant)
{
    bool marked = participant.MarkedUser == true;           // was false for "Yes" before 2.9.0
    bool? muted = AmiBoolean.Parse(participant.Muted);      // Muted and Talking stay text
    bool? talking = AmiBoolean.Parse(participant.Talking);
    Console.WriteLine($"{participant.Channel}: marked={marked} muted={muted} talking={talking}");
}
```

No separate guide.

## Live

### A conference counts every caller

**What changed.** `VerbaraServer.MeetMe` keyed each ConfBridge participant by user number, which ConfBridge never
sends, so every participant was user 0: a room of two reported one, and the first leave removed the room. It now
keys participants by the channel name of their join, and handles `ConfbridgeEnd`. Two members change shape:

| Member | 2.8.0 | 2.9.0 |
|---|---|---|
| `MeetMeRoom.Users` | `ConcurrentDictionary<int, MeetMeUser>` | `ConcurrentDictionary<string, MeetMeUser>`, keyed by channel |
| `MeetMeManager.OnUserLeft` | `(string roomNumber, int userNum)` | `(string roomNumber, string channel)` |

Added: `MeetMeUser.UniqueId`, an `OnUserJoined` overload with a `uniqueId` argument, and
`MeetMeManager.OnConferenceEnded`. `UserNum` stays 0 for ConfBridge.

**Who is affected.** Code that indexes `Users` by `int` or calls `OnUserLeft(string, int)`: it no longer compiles.
Code that relied on one participant per room, or on the room vanishing at the first leave. Code that only reads
`Users.Values`, `UserCount`, `Rooms` or `GetRoom`, or subscribes to `UserJoined`/`UserLeft`, compiles unchanged and
now gets the right numbers.

**What to do.** Look participants up by channel name, and pass the leave's `Channel` to `OnUserLeft` if you feed a
`MeetMeManager` yourself. A reconnect no longer clears the table: the reload restores it from Asterisk's
conference listings, raising `UserLeft` for each participant they no longer report (see
[Reconnect](conference-participants-migration.md#reconnect)).

Guide, with before and after code: [Conference participants keyed by channel](conference-participants-migration.md).

### A conference participant shows muted and talking

**What changed.** `MeetMeUser.Muted` and `MeetMeUser.Talking` were always `false`. They now follow the `Muted`
header of `ConfbridgeJoin`, `ConfbridgeMute`/`ConfbridgeUnmute`, and `ConfbridgeTalking`. `Muted` is the
participant's mute request, not whether it is audible; `Talking` needs `talk_detection_events=yes` in the
ConfBridge user profile; a whole-room mute from the keypad sends no event and is not reflected. The new
`MeetMeManager.UserStateChanged` is raised once when either value changes.

**Who is affected.** Code that branched on `Muted` or `Talking` being `false`: it now takes the other branch for a
muted or talking participant. Code that read the ConfBridge mute and talking events itself to fill that state.

**What to do.** Nothing, unless you relied on the old constant `false`. If you tracked the state yourself, you can
read the participant, or subscribe to `UserStateChanged`, instead.

Guide: [Muted and Talking follow Asterisk](conference-participants-migration.md#muted-and-talking-follow-asterisk).

## Sessions

### An agent's wrap-up is opened, timed and closed

**What changed.** Nothing raised `CallWrapUpEvent` since 1.7.0, and `SessionOptions.WrapUpDuration` was never
applied. The session manager now raises one `CallWrapUpEvent` per agent leg, when the wrap-up closes. A queue
call's wrap-up opens at the member's `AgentComplete`; a named agent's call outside any queue opens it when the call
ends. It closes when `WrapUpDuration` passes (30 seconds by default), at the agent's next connect, or when its next
leg ends, and the event carries the time measured. `AgentSessionTracker` leaves `WrapUp` when the wrap-up closes,
so `TotalWrapUpTime`, `AvgHandleTime` and `IdleTime` (now counted from the end of the wrap-up) change. When one call
visits two named agents, the call now ends for both, and each counts the call's whole talk and hold time.

**Who is affected.**

- A host whose `WrapUpDuration` is negative, `Timeout.InfiniteTimeSpan`, or above 4,294,967,294 ms (about 49.7
  days): it no longer starts. The host's start throws `OptionsValidationException` naming `WrapUpDuration`.
- Dashboards and reports over agent state and the agent counters above: after each call an agent now stays in
  `WrapUp` until the wrap-up closes (at most `WrapUpDuration`), then goes `Idle`, where before it stayed in
  `WrapUp` until its next call.
- Subscribers of `ICallSessionManager.Events` that ignored `CallWrapUpEvent` because it never came.

**What to do.** Set `WrapUpDuration` to the queue's `wrapuptime`; the SDK cannot read that value from Asterisk,
and one value applies to every queue. Zero closes each wrap-up at once: it is the nearest equivalent of the old
behaviour for the counters (no wrap-up time is added), not for the state (the agent is `Idle` at once).

```csharp
using Verbara.Sdk.Sessions.Manager;

static void AddSessions(IServiceCollection services)
{
    // queues.conf: wrapuptime=15
    services.AddVerbaraSessions(options => options.WrapUpDuration = TimeSpan.FromSeconds(15));
}
```

To react to a wrap-up, subscribe directly and filter inside the handler. An operator such as `OfType` ends its
subscription on the handler's first exception:

```csharp
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;

static IDisposable WatchWrapUps(ICallSessionManager sessions) =>
    sessions.Events.Subscribe(evt =>
    {
        if (evt is CallWrapUpEvent wrapUp)
        {
            Console.WriteLine($"agent {wrapUp.AgentId} wrapped up {wrapUp.SessionId} in {wrapUp.WrapUpDuration}");
        }
    });
```

A `WrapUpDuration` longer than `CompletedRetention` (10 minutes by default) can deliver the event after the call is
released, so a lookup by its `SessionId` may find nothing. No separate guide.

## Activities

### An activity ends `Cancelled` only when asked to stop

**What changed.** All fifteen shipped activities read every `OperationCanceledException` as a cancellation. Now
an activity ends `Cancelled` only when the token passed to `StartAsync` was cancelled, `CancelAsync` was called,
or it was disposed while running; `StartAsync` returns without throwing then. Every other ending is `Failed` and
`StartAsync` rethrows it, including an SDK or transport deadline: an `AttendedTransferActivity` that Asterisk did
not answer within `AmiConnectionOptions.DefaultResponseTimeout` (2 s by default), or a `SnoopActivity` or
`ExternalMediaActivity` whose ARI request hit the `HttpClient` timeout (100 s). A `CancelAsync` during `Starting`
now stops the activity before it executes. A deadline you link into your own token still ends `Cancelled`.

**Who is affected.** Code that awaited `StartAsync` without a `try`, or read `Status == ActivityStatus.Cancelled`
after it to detect a timeout.

**What to do.** Wrap `StartAsync` where a deadline can end the activity; the snippet in the next section catches
both this case and a refused transfer.

Guide: [Activity endings that say who ended them](activity-ending-migration.md).

### A refused attended transfer ends `Failed`

**What changed.** `AttendedTransferActivity` discarded Asterisk's answer to its `Atxfer`, so a transfer Asterisk
refused with `Response: Error` (for example `Channel specified does not exist`, or
`No attended transfer feature code found` when `features.conf` sets no `atxfer` code) ended `Completed`. It now
ends `Failed`, and `StartAsync` throws `ActivityFailedException` carrying Asterisk's message. `Completed` still
means only that Asterisk accepted and queued the transfer; the outcome arrives as an `AttendedTransferEvent`.

**Who is affected.** Code that awaited `StartAsync` and branched on `Status`: a refusal is now an exception.

**What to do.**

```csharp
using Verbara.Sdk.Activities;
using Verbara.Sdk.Activities.Activities;
using Verbara.Sdk.Activities.Models;

static async Task TransferAsync(IAmiConnection ami, string customerChannel, CancellationToken cancellationToken)
{
    var transfer = new AttendedTransferActivity(ami)
    {
        Channel = customerChannel,
        Destination = new DialPlanExtension("consult", "200"),
    };

    try
    {
        await transfer.StartAsync(cancellationToken);
        // Completed: Asterisk queued the transfer. Cancelled: you asked it to stop.
    }
    catch (OperationCanceledException)
    {
        // A deadline you did not set: Asterisk did not answer within DefaultResponseTimeout. Status is Failed.
    }
    catch (ActivityFailedException ex)
    {
        // Asterisk refused the Atxfer; ex.Message carries its reason. Status is Failed.
        Console.WriteLine(ex.Message);
    }
}
```

Guide: [Activity endings, What you have to do](activity-ending-migration.md#what-you-have-to-do).

## Ari audio

### `AudioStreamMetrics.BufferUnderruns` warns `VSDK0003`

**What changed.** `audio.buffer.underruns` describes a write pump the SDK does not have, so nothing ever recorded
it. The field `AudioStreamMetrics.BufferUnderruns` (`Verbara.Sdk.Ari.Diagnostics`) is now `[Obsolete]` with
diagnostic id `VSDK0003`. Nothing is removed: the instrument is still published on the meter, and a binary built
against an earlier release that reads the field still runs.

**Who is affected.** Code that names the field and builds with `TreatWarningsAsErrors`: it gets
`error VSDK0003`. An exporter that subscribes by meter name is not affected.

**What to do.** Stop using the field; nothing records the instrument, so it never carries a measurement. Until
then, silence this deprecation alone:

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);VSDK0003</NoWarn>
</PropertyGroup>
```

No separate guide.

## Voice AI

### A cancelled voice session is counted cancelled

**What changed.** A session whose handler token was cancelled (the host cancelled the token it passed to
`HandleSessionAsync`, the broker's stop ran out of its shutdown budget, or the broker was disposed) was counted in
`voiceai.sessions.completed` or `openai_realtime.sessions.completed`. It is now counted in the new
`voiceai.sessions.cancelled` or `openai_realtime.sessions.cancelled`, and its duration sample carries
`voiceai.ending` = `session-cancelled`. A session the caller hung up, the vendor closed, or a graceful broker stop
ended is still `completed`. No signature changes, and the cancellation is still not rethrown.

**Who is affected.** Dashboards and alerts over `…sessions.completed`: the figure drops by the number of cancelled
sessions.

**What to do.** Where you need the old figure, add the two counters:

```text
old voiceai.sessions.completed         = voiceai.sessions.completed         + voiceai.sessions.cancelled
old openai_realtime.sessions.completed = openai_realtime.sessions.completed + openai_realtime.sessions.cancelled
```

Guide: [A cancelled session is counted `cancelled`, not `completed`](voice-session-ending-migration.md#a-cancelled-session-is-counted-cancelled-not-completed).

### The `openai-realtime` health entry reports `Degraded`

**What changed.** The `openai-realtime` entry that `AddOpenAiRealtimeBridge` registers (`RealtimeHealthCheck`)
reported `Healthy` whenever an API key was configured, also while the vendor rejected every session. It now
reports `Degraded` once the last 3 consecutive sessions were rejected (an upgrade refused with a 4xx status, or a
failure close before any vendor audio reached the caller), and `Healthy` again on the first session the vendor
served. Rejections never report `Unhealthy`, so a default health endpoint keeps answering 200. The entry's data
carries `consecutiveRejections`, and after a rejection `lastRejection` and `lastRejectionAt`. With no API key it
still reports `Unhealthy`.

**Who is affected.** A readiness or liveness probe that includes this entry, or reads every entry, and treats
`Degraded` as a failure: it now fails while the vendor rejects sessions.

**What to do.** Keep the entry where a vendor outage should take the instance out of rotation. To keep it out of
a probe, filter it by name:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

static void MapProbes(WebApplication app)
{
    app.MapHealthChecks("/health/live", new HealthCheckOptions
    {
        Predicate = check => check.Name != "openai-realtime",
    });
}
```

Guide: [The `openai-realtime` health entry reports the vendor's rejections](voice-session-ending-migration.md#the-openai-realtime-health-entry-reports-the-vendors-rejections).

## Push webhooks

### A webhook receives a topic-less event

**What changed.** `Verbara.Sdk.Push.Webhooks` delivered no event whose `PushEventMetadata.TopicPath` was null or
empty. Such an event is now matched by its `EventType`, the rule the SSE stream already applies: an event type
that is a valid topic is matched against each subscription's pattern (with `{self}` resolving to the event's
user), and one that is not a valid topic reaches only `**` subscriptions. An event with a non-empty topic path is
matched by that path alone, as before. No signature changes.

**Who is affected.**

- A subscription whose pattern is `**`, or matches an event type you publish without a topic path: it receives
  events it never received.
- A publisher that relied on "no topic path, no webhook".
- A receiver that answers those new events with a 4xx: with the defaults one such event fails 6 times, which opens
  that URL's circuit (threshold 5), and every delivery to that URL, topic-bearing ones included, is skipped for
  30 s and counted on `circuit.skipped`.

**What to do.** To keep topic-less events away from a receiver, publish them with a topic path its pattern does
not match, or have the receiver accept them (answer `2xx`).

Guide: [Webhooks receive events without a topic path](push-stream-and-webhook-options-migration.md#webhooks-receive-events-without-a-topic-path-290).

## Behaviour you may notice but need not change

None of these breaks a build or a public contract. Each is in the CHANGELOG.

- **A stop is a stop, not a cancelled token.** Cancelling the token passed to the start of the ARI AudioSocket or
  WebSocket audio server, the ARI outbound listener, the FastAGI server, or to `PipelineSocketConnection.ConnectAsync`
  or `AriClient.ConnectAsync`, after it returned, no longer stops it. If you stopped one that way, call `StopAsync`,
  `CloseAsync`, `DisconnectAsync` or `DisposeAsync`.
- **A session subscriber that throws no longer silences the others.** The failure is logged at Warning and counted
  on `sessions.events.subscriber_faults`, no longer on `ami.events.observer_faults`. An alert on the old counter
  for these failures should move.
- **Audio meter series gain a tag.** Every `Verbara.Sdk.Ari.Audio` measurement carries `audio.transport`
  (`audiosocket` or `websocket`), and the sessions now record the meter's instruments. A dashboard that matches
  `audio.transport.failures` or `audio.connections.refused` by an exact label set must add the tag.
- **The latency sample of a cancelled speech turn carries `voiceai.ending`.** `stt.transcription.latency_ms` and
  `tts.synthesis.latency_ms` keep every sample; a query that keeps tags apart shows up to four more series per
  histogram. Keep the samples without the tag to read the turns that were not cut short.
- **`SendFlashAction` reaches Asterisk.** It now sends `Action: SendFlash`, so a hook flash you sent is really sent.
- **A cancelled WebSocket recognizer or synthesizer always throws `OperationCanceledException`**, also when the
  cancel lands after the vendor finished.
- **The AudioSocket server's stop sends a hangup frame** to each live session, and a far end that resets
  mid-playback counts as the far end leaving, not as `tts.syntheses.failed`.
- **The `Microsoft.Extensions.*` dependency floor is 10.0.0** (10.0.5 for `Verbara.Sdk.Sessions.Redis`). An
  application that does not pin those packages may resolve an earlier patch than with 2.8.0: pin the one you want.
