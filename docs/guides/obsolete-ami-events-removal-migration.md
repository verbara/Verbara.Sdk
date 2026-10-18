# Migrating off the AMI event types removed in 2.9.0

Required by the release cadence: a minor that carries a breaking change ships a migration guide.

2.9.0 removes surface that had been marked `[Obsolete]` and that nothing a supported Asterisk sends
can reach:

- **26 AMI event types** in `Verbara.Sdk.Ami` (`Verbara.Sdk.Ami.Events`): seven `MeetMe*`, two
  `Monitor*`, eight pre-Asterisk-12 bridging, dialing and queue events, seven `Skype*` and two
  `ZapShowChannels*`. The table below lists every one.
- **`Verbara.Sdk.Ami.Internal.LegacyEventAdapter`.** It was public, called by nothing in the SDK, and
  only built three of the removed types. It never synthesized anything on its own: earlier versions
  of the compatibility guide said it did, and that was not true.
- **`DeepgramVoices.Helios`** in `Verbara.Sdk.VoiceAi.Tts`, an alias of `HeliosLegacy`.

`PeerEntryEvent` is **not** removed. `IAXpeerlist` and `IAXpeers` (chan_iax2) still answer with
`Event: PeerEntry` on the pinned 20, 22 and 23 images and on 24.0.0-rc2, so the type stays, and it is no
longer `[Obsolete]`.
A `#pragma warning disable CS0618` you kept around it can go.

2.9.0 also **deprecates**, without removing, 49 types and one member that only a dead Asterisk module
or a renamed wire name can reach. They compile with the `VSDK0005` warning and are removed in 2.10.0;
see [VSDK0005](#vsdk0005).

## What you have to do

1. **Move every `Verbara.Sdk.*` package to 2.9.0 together.** Do not mix versions. `Verbara.Sdk.Live`
   2.8.0 names two of the removed types in its event router, so loaded next to `Verbara.Sdk.Ami` 2.9.0
   it cannot resolve them.
2. **Recompile.** A binary built against 2.8.0 or earlier that names a removed type fails when the
   runtime loads it (`TypeLoadException`), not when it starts. Recompiling turns each use into a
   compile error: `CS0246` for a type, `CS0117` for `Helios`.
3. **Fix each compile error** with the replacement from the table, or, if your Asterisk still sends
   the event, with the `EventType` + `RawFields` recipe below.

## The removed event types

"Supported Asterisk" means 22 and 24 (the LTS releases) first, and 23. "Sent" was measured on the
pinned 20.20.1, 22.10.1 and 23.4.1 images, with one scripted workload each (two ConfBridge
participants, MixMonitor, `Monitor()`, `MeetMe()`, a `Dial`, an answered queue call, `QueuePause` on
then off, and the list actions of the removed modules). Each capture held 297 events, and none carried
a removed name. The capture was then repeated on those three images and on 24.0.0-rc2 with a wider
workload: every list action the SDK publishes that produces events, the AGI and Hangup actions on a
live AsyncAGI call, and `IAXpeerlist` and `IAXpeers`. Each of those four captures held 414 events,
none with a removed name and 4 with `PeerEntry`. The SDK's 15 versioned AMI recordings (20.20.1,
22.9.0, 23.4.1) hold 7,074 events, none with a removed name either.

Each replacement was checked against those captures, or against `manager show events` on 20, 22 and
23 where the workload does not produce the event. The last column says which.

### app_meetme (use app_confbridge)

app_meetme is still in Asterisk's source tree, but it is not built by default and it needs DAHDI.
None of the pinned images ships it. `manager show events` still documents its events on 20, 22 and
23, so an Asterisk built by hand with app_meetme can still send them.

| Removed type | SDK mapped | Asterisk sends | Sent by a supported Asterisk? | Use instead | Checked against |
|---|---|---|---|---|---|
| `MeetMeJoinEvent` | `MeetMeJoin` | `MeetmeJoin` | No (only if app_meetme is built) | `ConfbridgeJoinEvent` | capture, 2× per version |
| `MeetMeLeaveEvent` | `MeetMeLeave` | `MeetmeLeave` | No (only if app_meetme is built) | `ConfbridgeLeaveEvent` | capture, 2× per version |
| `MeetMeEndEvent` | `MeetMeEnd` | `MeetmeEnd` | No (only if app_meetme is built) | `ConfbridgeEndEvent` | capture, 1× per version |
| `MeetMeTalkingEvent` | `MeetMeTalking` | `MeetmeTalking` | No (only if app_meetme is built) | `ConfbridgeTalkingEvent` | `manager show events` 20/22/23 |
| `MeetMeStopTalkingEvent` | `MeetMeStopTalking` | nothing: talking off is `MeetmeTalking` with `Status: off` | No | `ConfbridgeTalkingEvent` with `TalkingStatus == false` | `manager show events` 20/22/23 |
| `MeetMeTalkingRequestEvent` | `MeetMeTalkingRequest` | `MeetmeTalkRequest` | No (only if app_meetme is built) | No ConfBridge equivalent | — |
| `MeetMeMuteEvent` | `MeetMeMute` | `MeetmeMute` | No (only if app_meetme is built) | `ConfbridgeMuteEvent` / `ConfbridgeUnmuteEvent` | `manager show events` 20/22/23 |

Two of these never matched what app_meetme sends, even where it sends it. Asterisk has no
`MeetMeStopTalking` event, and it spells the talk request `MeetmeTalkRequest`, which the SDK's
`MeetMeTalkingRequest` mapping never caught: that event already arrived as a plain `ManagerEvent`.

### Monitor (use MixMonitor)

res_monitor was removed in Asterisk 21. On 20 it is not built by default, and the pinned 20.20.1 image
does not ship it. `manager show events` documents `MonitorStart`/`MonitorStop` on 20 only.

| Removed type | Wire name | Sent by a supported Asterisk? | Use instead | Checked against |
|---|---|---|---|---|
| `MonitorStartEvent` | `MonitorStart` | No (only on 20 with res_monitor built) | `MixMonitorStartEvent` | capture, 1× per version |
| `MonitorStopEvent` | `MonitorStop` | No (only on 20 with res_monitor built) | `MixMonitorStopEvent` | capture, 1× per version |

### Bridging, dialing and queues before Asterisk 12

No version from 20 on sends or documents any of these names.

| Removed type | Wire name | Sent by a supported Asterisk? | Use instead | Checked against |
|---|---|---|---|---|
| `LinkEvent` | `Link` | No | `BridgeEnterEvent` | capture, 6× per version |
| `UnlinkEvent` | `Unlink` | No | `BridgeLeaveEvent` | capture, 6× per version |
| `BridgeEvent` | `Bridge` | No | `BridgeCreateEvent` / `BridgeEnterEvent` | capture, 3× / 6× per version |
| `DialEvent` | `Dial` | No | `DialBeginEvent` / `DialEndEvent` | capture, 9× each per version |
| `JoinEvent` | `Join` | No | `QueueCallerJoinEvent` | capture, 1× per version |
| `LeaveEvent` | `Leave` | No | `QueueCallerLeaveEvent` | capture, 1× per version |
| `PausedEvent` | `Paused` | No | `QueueMemberPauseEvent` with `Paused == true` | capture: `QueueMemberPause` with `Paused: 1` |
| `UnpausedEvent` | `Unpaused` | No | `QueueMemberPauseEvent` with `Paused == false` | capture: `QueueMemberPause` with `Paused: 0` |

Pause and unpause are **one** event on the wire, `QueueMemberPause`, told apart by its `Paused`
header. Use `QueueMemberPauseEvent`, not `QueueMemberPausedEvent`: no version from 20 on sends or
documents `QueueMemberPaused`, so a handler written against it never fires.

### Discontinued modules

No version from 20 on sends or documents any of these names, and their list actions (`SkypeBuddies`,
`SkypeLicenseList`, `ZapShowChannels`) answer `Invalid/unknown command` on 20, 22, 23 and 24.0.0-rc2.

| Removed types | Sent by a supported Asterisk? | Use instead |
|---|---|---|
| `SkypeAccountStatusEvent` (`SkypeAccountStatus`), `SkypeBuddyEntryEvent` (`SkypeBuddyEntry`), `SkypeBuddyListCompleteEvent` (`SkypeBuddyListComplete`), `SkypeBuddyStatusEvent` (`SkypeBuddyStatus`), `SkypeChatMessageEvent` (`SkypeChatMessage`), `SkypeLicenseEvent` (`SkypeLicense`), `SkypeLicenseListCompleteEvent` (`SkypeLicenseListComplete`) | No | Nothing: Skype for Asterisk was discontinued |
| `ZapShowChannelsEvent` (`ZapShowChannels`), `ZapShowChannelsCompleteEvent` (`ZapShowChannelsComplete`) | No | Not checked (see below) |

Zaptel was replaced by DAHDI. The SDK's DAHDI-era counterpart is `DahdiShowChannelsAction` with
`DahdiShowChannelsEvent` and `DahdiShowChannelsCompleteEvent`. No capture behind this guide exercises
it: chan_dahdi is not loaded in the pinned images, `DAHDIShowChannels` answers
`Invalid/unknown command` there, and `manager show events` does not list its response events. Treat
it as the place to look, not as a measured replacement.

## If your Asterisk still sends one of these events

Asterisk 20 has no SDK support, but upstream still patches it, and 22 or 23 can be built by hand with
app_meetme. On such a server the event still arrives. With no typed class for its name, it is
delivered as a plain `ManagerEvent` to every `OnEvent` handler, every observer and any pending
event-generating action. Nothing is dropped, and nothing is logged as a warning or an error for it.

- `EventType` holds the name exactly as Asterisk spelled it: `MeetmeJoin`, not `MeetMeJoin`.
- `RawFields` holds every distinct header name with its value. A header Asterisk repeats keeps its
  **last** value.

```csharp
using Verbara.Sdk;

static void WatchMeetMe(IAmiConnection connection)
{
    connection.OnEvent += evt =>
    {
        var fields = evt.RawFields;
        if (fields is null)
        {
            return ValueTask.CompletedTask;
        }

        switch (evt.EventType)
        {
            case "MeetmeJoin":
                fields.TryGetValue("Meetme", out var room);
                fields.TryGetValue("Usernum", out var user);
                fields.TryGetValue("Channel", out var channel);
                Console.WriteLine($"{channel} joined MeetMe room {room} as user {user}");
                break;

            case "MeetmeTalkRequest":
                fields.TryGetValue("Meetme", out var requestRoom);
                fields.TryGetValue("Status", out var status); // "on" or "off"
                Console.WriteLine($"Talk request in room {requestRoom}: {status}");
                break;
        }

        return ValueTask.CompletedTask;
    };
}
```

The two names above are what app_meetme writes. Its header names (`Meetme`, `Usernum`, `Status`, and
`Channel` from the channel snapshot) come from app_meetme's source; no capture behind this guide carries them, because none of the pinned
images builds app_meetme. Log one event's `RawFields` from your own server before you rely on a key.
`OnEvent` is the shape shown; an `IObserver<ManagerEvent>` passed to `Subscribe` receives the same
`ManagerEvent`.

## `VerbaraServer.MeetMe`

`VerbaraServer` no longer routes `MeetMeJoin` or `MeetMeLeave`. `VerbaraServer.MeetMe` is now fed by
ConfBridge only (`ConfbridgeJoin`, `ConfbridgeLeave` and `ConfbridgeEnd`). On an Asterisk that still runs app_meetme,
its rooms no longer appear there; the raw events still reach your handlers, as above.

In 2.9.0 its ConfBridge participants are keyed by channel, so every caller counts and a room stays
until its last participant leaves or the conference ends; see
[Conference participants keyed by channel](conference-participants-migration.md).

## `DeepgramVoices.Helios` → `DeepgramVoices.HeliosLegacy`

`Helios` was an alias of `HeliosLegacy`, kept since 2.5.0 under the `VSDK0001` obsolete warning.
Replace the name; the value is the same, `"aura-helios-en"`. Helios exists only in Aura 1.

<!-- skip-doc-snippet -->
```csharp
// before
options.Model = DeepgramVoices.Helios;

// after
options.Model = DeepgramVoices.HeliosLegacy;
```

`Helios` was a `const`, so its value was copied into your assembly when you compiled it. A binary
built against an earlier version keeps working; only the next compile fails, with `CS0117`.

## VSDK0005

Deprecated in 2.9.0, removed in 2.10.0.

These 49 types and one member stay in 2.9.0, marked `[Obsolete]` with the diagnostic id `VSDK0005`.
Each one reaches only an Asterisk module that is gone or deprecated upstream, a module whose publisher
discontinued it, or a wire name Asterisk renamed or removed in version 10 or 12. Naming one in your code
reports the `VSDK0005` **warning**, never an error; the warning's message says what to use instead.

**Nothing changes at run time.** Every marked action still serializes and is sent as before, every
marked response and event is still parsed when Asterisk sends it, and the SDK maps each wire name to
the same type as in 2.8.0. `UseConfBridge = false` still runs MeetMe. Only the compiler output changes.

### Silencing it

Replace each use with the type in the last column before 2.10.0. Until then, if you build with
`TreatWarningsAsErrors`, you can opt out of this deprecation alone, without silencing `CS0618` across
your project:

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);VSDK0005</NoWarn>
</PropertyGroup>
```

To keep the warning elsewhere, wrap only the line that needs the type in
`#pragma warning disable VSDK0005` / `#pragma warning restore VSDK0005`.

### chan_sip (use PJSIP)

chan_sip was removed in Asterisk 21 (deprecated since 17).

| Symbol | Module or wire-name status | Use instead |
|---|---|---|
| `SipPeersAction` | `SIPpeers`: chan_sip, removed in Asterisk 21 | `PJSipShowEndpointsAction` |
| `SipShowPeerAction` | `SIPshowpeer`: chan_sip, removed in Asterisk 21 | `PJSipShowEndpointAction` |
| `SipShowRegistryAction` | `SIPshowregistry`: chan_sip, removed in Asterisk 21 | `PJSipShowRegistrationsOutboundAction` |
| `SipNotifyAction` | `SIPnotify`: chan_sip, removed in Asterisk 21 | `PJSIPNotifyAction` |
| `SipShowPeerResponse` | the answer to `SIPshowpeer`: chan_sip, removed in Asterisk 21 | `PJSipShowEndpointAction` and its `EndpointDetail` events |
| `ChannelReloadEvent` | `ChannelReload`: sent only by chan_sip, removed in Asterisk 21 | none |

### res_monitor (use MixMonitor)

res_monitor was removed in Asterisk 21 (deprecated since 16).

| Symbol | Module or wire-name status | Use instead |
|---|---|---|
| `MonitorAction` | `Monitor`: res_monitor, removed in Asterisk 21 | `MixMonitorAction` |
| `StopMonitorAction` | `StopMonitor`: res_monitor, removed in Asterisk 21 | `StopMixMonitorAction` |
| `ChangeMonitorAction` | `ChangeMonitor`: res_monitor, removed in Asterisk 21 | none: stop the MixMonitor and start a new one |
| `PauseMonitorAction` | `PauseMonitor`: res_monitor, removed in Asterisk 21 | `MixMonitorMuteAction` |
| `UnpauseMonitorAction` | `UnpauseMonitor`: res_monitor, removed in Asterisk 21 | `MixMonitorMuteAction` |

### app_meetme (use app_confbridge)

app_meetme is **deprecated** since Asterisk 19, not removed: it is still in the source tree, but it is
not built by default and it needs DAHDI. None of the pinned images ships it.

| Symbol | Module or wire-name status | Use instead |
|---|---|---|
| `MeetMeMuteAction` | `MeetmeMute`: app_meetme, deprecated since Asterisk 19 | `ConfbridgeMuteAction` |
| `MeetMeUnmuteAction` | `MeetmeUnmute`: app_meetme, deprecated since Asterisk 19 | `ConfbridgeUnmuteAction` |
| `MeetMeEventBase` | app_meetme, deprecated since Asterisk 19; no event derives from it since 2.9.0 | `ConfbridgeEventBase` |
| `MeetmeCompatibility` | app_meetme, deprecated since Asterisk 19 | none |
| `MeetmeCommand` (`Verbara.Sdk.Agi`) | `EXEC MeetMe`: app_meetme, deprecated since Asterisk 19 | `ConfbridgeCommand` |
| `MeetmeActivity.UseConfBridge` (`Verbara.Sdk.Activities`) | `false` runs MeetMe: app_meetme, deprecated since Asterisk 19 | none: ConfBridge is the default, so remove the assignment. From 2.10.0 `MeetmeActivity` runs ConfBridge only |

`MeetmeActivity` itself, `MeetMeManager`, `MeetMeRoom`, `MeetMeUser` and `MeetMeUserState` are not
deprecated: they carry a MeetMe name, but ConfBridge feeds them.

### Skype for Asterisk (discontinued)

A commercial module, never part of Asterisk itself. Its publisher stopped selling it after 2011-07-26.
`SkypeBuddies` and `SkypeLicenseList` answer `Invalid/unknown command` on 20, 22, 23 and 24.0.0-rc2.

| Symbol | Module or wire-name status | Use instead |
|---|---|---|
| `SkypeAccountPropertyAction` | `SkypeAccountProperty`: Skype for Asterisk, discontinued in 2011 | none |
| `SkypeAddBuddyAction` | `SkypeAddBuddy`: Skype for Asterisk, discontinued in 2011 | none |
| `SkypeBuddiesAction` | `SkypeBuddies`: Skype for Asterisk, discontinued in 2011 | none |
| `SkypeBuddyAction` | `SkypeBuddy`: Skype for Asterisk, discontinued in 2011 | none |
| `SkypeChatSendAction` | `SkypeChatSend`: Skype for Asterisk, discontinued in 2011 | none |
| `SkypeLicenseListAction` | `SkypeLicenseList`: Skype for Asterisk, discontinued in 2011 | none |
| `SkypeLicenseStatusAction` | `SkypeLicenseStatus`: Skype for Asterisk, discontinued in 2011 | none |
| `SkypeRemoveBuddyAction` | `SkypeRemoveBuddy`: Skype for Asterisk, discontinued in 2011 | none |
| `SkypeBuddyResponse` | the answer to `SkypeBuddy`: Skype for Asterisk, discontinued in 2011 | none |
| `SkypeLicenseStatusResponse` | the answer to `SkypeLicenseStatus`: Skype for Asterisk, discontinued in 2011 | none |

The commercial fax module's `FaxLicenseListAction`, `FaxLicenseStatusAction` and
`FaxLicenseStatusResponse` are not deprecated: that module is still sold.

### Zaptel action names (use the DAHDI names)

Asterisk 1.6 replaced chan_zap with chan_dahdi, which registers these actions under `DAHDI*` names
only. The SDK types only `DahdiShowChannelsAction`; send the other `DAHDI*` actions by name. chan_dahdi
is not loaded in the pinned images, so none of these replacements is measured.

| Symbol | Module or wire-name status | Use instead |
|---|---|---|
| `ZapDialOffhookAction` | `ZapDialOffhook`: renamed `DAHDIDialOffhook` in Asterisk 1.6 | the `DAHDIDialOffhook` action (not typed) |
| `ZapDndOffAction` | `ZapDNDoff`: renamed `DAHDIDNDoff` in Asterisk 1.6 | the `DAHDIDNDoff` action (not typed) |
| `ZapDndOnAction` | `ZapDNDon`: renamed `DAHDIDNDon` in Asterisk 1.6 | the `DAHDIDNDon` action (not typed) |
| `ZapHangupAction` | `ZapHangup`: renamed `DAHDIHangup` in Asterisk 1.6 | the `DAHDIHangup` action (not typed) |
| `ZapRestartAction` | `ZapRestart`: renamed `DAHDIRestart` in Asterisk 1.6 | the `DAHDIRestart` action (not typed) |
| `ZapShowChannelsAction` | `ZapShowChannels`: renamed `DAHDIShowChannels` in Asterisk 1.6 | `DahdiShowChannelsAction` |
| `ZapTransferAction` | `ZapTransfer`: renamed `DAHDITransfer` in Asterisk 1.6 | the `DAHDITransfer` action (not typed) |

### chan_agent and app_setcdruserfield

| Symbol | Module or wire-name status | Use instead |
|---|---|---|
| `AgentCallbackLoginAction` | `AgentCallbackLogin`: removed in Asterisk 1.6; chan_agent removed in Asterisk 12 | none: the `AgentLogin` dialplan application |
| `AgentCallbackLoginEvent` | `Agentcallbacklogin`: not sent since Asterisk 1.6 | `AgentLoginEvent` |
| `AgentCallbackLogoffEvent` | `AgentCallbackLogoff`: not sent since Asterisk 11 | `AgentLogoffEvent` |
| `SetCdrUserFieldAction` | `SetCDRUserField`: app_setcdruserfield, removed in Asterisk 1.6 | `SetVarAction` with `CDR(userfield)` |

### Event names Asterisk 10 and 12 renamed or removed

The modules behind these events are alive; since Asterisk 10 or 12 they send the event under another
name, or no longer send it. A handler written against the old type never fires on a supported Asterisk.

| Symbol | Module or wire-name status | Use instead |
|---|---|---|
| `QueueMemberPausedEvent` | `QueueMemberPaused`: renamed `QueueMemberPause` in Asterisk 12 | `QueueMemberPauseEvent` |
| `MusicOnHoldEvent` | `MusicOnHold`: split into `MusicOnHoldStart` and `MusicOnHoldStop` in Asterisk 12 | `MusicOnHoldStartEvent`, `MusicOnHoldStopEvent` |
| `FaxReceivedEvent` | `FaxReceived`: renamed `ReceiveFAX` in Asterisk 12 | `ReceiveFaxEvent` |
| `DtmfEvent` | `DTMF`: split into `DTMFBegin` and `DTMFEnd` in Asterisk 12 | `DtmfBeginEvent`, `DtmfEndEvent` |
| `TransferEvent` | `Transfer`: replaced by `BlindTransfer` and `AttendedTransfer` in Asterisk 12 | `BlindTransferEvent`, `AttendedTransferEvent` |
| `ChannelUpdateEvent` | `ChannelUpdate`: removed in Asterisk 12 | none |
| `JabberEventEvent` | `JabberEvent`: removed in Asterisk 12 | none |
| `AgiExecEvent` | `AGIExec`: split into `AGIExecStart` and `AGIExecEnd` in Asterisk 12 | `AgiExecStartEvent`, `AgiExecEndEvent` |
| `AsyncAgiEvent` | `AsyncAGI`: split into `AsyncAGIStart`, `AsyncAGIExec` and `AsyncAGIEnd` in Asterisk 12 | `AsyncAgiStartEvent`, `AsyncAgiExecEvent`, `AsyncAgiEndEvent` |
| `SendFaxStatusEvent` | `SendFAXStatus`: consolidated into `FAXStatus` in Asterisk 10 | `FaxStatusEvent` |
| `BridgeExecEvent` | `BridgeExec`: removed in Asterisk 12; `BridgeCreate`, `BridgeEnter` and `BridgeLeave` carry what it reported | `BridgeCreateEvent`, `BridgeEnterEvent`, `BridgeLeaveEvent` |
| `ModuleLoadReportEvent` | `ModuleLoadReport`: removed in Asterisk 12 | none |

`QueueMemberPausedEvent` and `QueueMemberPauseEvent` are two types: the live `QueueMemberPause` still
parses as `QueueMemberPauseEvent`, which is not deprecated.

## Moved from the compatibility guide

These three examples used to live in
[asterisk-version-compatibility.md](asterisk-version-compatibility.md). The "before" halves name
removed types and no longer compile.

### MeetMe to ConfBridge

<!-- skip-doc-snippet -->
```csharp
// Before (app_meetme)
case MeetMeJoinEvent mmj:
    OnUserJoined(mmj.Meetme, mmj.Usernum, mmj.Channel);
    break;

// After (app_confbridge)
case ConfbridgeJoinEvent cbj:
    OnUserJoined(cbj.Conference, 0, cbj.Channel);
    break;
```

Keeping both cases for a mixed fleet is no longer possible: on a server that still runs app_meetme,
handle `MeetmeJoin` through `EventType` and `RawFields`.

### Monitor to MixMonitor

<!-- skip-doc-snippet -->
```csharp
// Before
case MonitorStartEvent ms: ...

// After
case MixMonitorStartEvent mms: ...
```

### Legacy Dial to DialBegin/DialEnd

<!-- skip-doc-snippet -->
```csharp
// Before (Asterisk < 12)
case DialEvent de when de.SubEvent == "Begin": ...
case DialEvent de when de.SubEvent == "End": ...

// After (Asterisk 12+)
case DialBeginEvent db: ...
case DialEndEvent de: ...
```
