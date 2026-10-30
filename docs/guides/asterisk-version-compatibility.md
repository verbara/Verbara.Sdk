# Asterisk Version Compatibility Matrix

> Which Asterisk versions the SDK supports, and what CI runs against each, is in the
> [version support matrix](asterisk-version-matrix.md): 22 LTS primary, 23 Standard secondary, 20 and earlier not
> supported. The notes below also name older releases, because what arrives depends on the Asterisk that sends it.

## AMI Event Coverage by Version

### Events Available in All Versions (18-23)

The SDK provides typed classes for all standard AMI events across Asterisk 18-23. Events not recognized by the SDK fall back to `ManagerEvent` with all fields preserved in `RawFields` — **no data is ever lost**.

### Events Added in Asterisk 20+

| Event | Description | SDK Class |
|-------|-------------|-----------|
| `Wink` | Wink signal on DAHDI channel | `WinkEvent` |
| `DeadlockStart` | Deadlock detected (debug builds only) | `DeadlockStartEvent` |
| `CoreShowChannelMapComplete` | Completion of CoreShowChannelMap | `CoreShowChannelMapCompleteEvent` |

These events are `null`-safe: on Asterisk 18-19 they simply never fire.

### Fields Added in Asterisk 20.17+/22.7+/23+

| Field | Events | Description |
|-------|--------|-------------|
| `TechCause` | `HangupEvent`, `HangupRequestEvent`, `SoftHangupRequestEvent` | Technology-specific cause code (e.g., SIP response code) |
| `LoginTime` | `QueueMemberStatusEvent` | Agent login duration in seconds |

These fields are nullable (`string?` / `int?`) and remain `null` on older Asterisk versions. No special handling required.

### ARI Fields Added in Asterisk 22.7+/23+

| Field | Events | Description |
|-------|--------|-------------|
| `tech_cause` | `ChannelHangupRequestEvent`, `ChannelDestroyedEvent` | Technology-specific cause code |

## Legacy AMI Events (removed in 2.9.0)

The 26 legacy event types this section used to list — app_meetme, Monitor, the bridging, dialing and queue events before Asterisk 12, Skype and Zaptel — were marked `[Obsolete]` and were **removed in 2.9.0**, together with the public adapter that built three of them. None of the pinned 20, 22 and 23 test images, nor 24.0.0-rc2, sends any of them. The Legacy → Replacement tables, and what to do on an Asterisk that still sends one of these events, are in [obsolete-ami-events-removal-migration.md](obsolete-ami-events-removal-migration.md).

An event that no longer has a typed class still arrives, as a `ManagerEvent` (see [Fallback Behavior](#fallback-behavior)).

The actions, responses and events of chan_sip, res_monitor, app_meetme, Skype for Asterisk, the Zaptel action names, chan_agent's callback login, app_setcdruserfield and the event names Asterisk 10 and 12 renamed or removed are deprecated in 2.9.0 under the `VSDK0005` warning and removed in 2.10.0: see [VSDK0005](obsolete-ami-events-removal-migration.md#vsdk0005).

## An AMI Event Held Until the Client Writes (before 20.21.0, 22.11.0 and 23.5.0)

On Asterisk before 20.21.0, 22.11.0 and 23.5.0, the manager can hold an event on a session until the client next writes to it. The session's thread marks itself to be woken, releases its lock and only then waits on the socket with no timeout; an event queued in that gap misses the wake-up. Asterisk replaced that mechanism with a per-session alert pipe in 20.21.0, 22.11.0 and 23.5.0, and 24 does not have it.

Measured on 23.4.1 and 22.10.1: 5 of about 5.4 million events and 7 of about 3.9 million were held; each arrived about a millisecond after the client's next action, and none was lost. With the SDK, the next write on a session that sends nothing else is the heartbeat `Ping`, so such an event waits at most `AmiConnectionOptions.HeartbeatInterval` (30 seconds by default, with `EnableHeartbeat` on, the default). On these versions, lower the interval if a quiet session must see every event sooner, or upgrade Asterisk.

## `StatusEvent` members no supported version sends

The `Status` reply was measured on Asterisk 18.26.4, 20.20.1, 22.9.0 and 23.4.1: six legs per version, 24 frames in all. Six `StatusEvent` members name a header that none of those frames carries, so on every supported version they are always null. Since 2.9.0 they are `[Obsolete]` under the diagnostic id `VSDK0002`, and each message names where the value is:

| Member | Always null because no frame carries | Read instead |
|--------|--------------------------------------|--------------|
| `State` | a `State` header | `ChannelStateDesc` (text, for example `Up`) or `ChannelState` (numeric, for example `6`) |
| `CallerId` | a `CallerID` header | `CallerIdNum` (number) or `CallerIdName` (name) |
| `Account` | an `Account` header | `AccountCode` |
| `BridgedChannel` | a `BridgedChannel` header | `BridgeId`: a `Status` frame names the bridge, never the peer channel |
| `Link` | a `Link` header | `BridgeId` |
| `BridgedUniqueId` | a `BridgedUniqueId` header | `BridgeId` |

`ChannelState`, `ChannelStateDesc`, `CallerIdNum` and `CallerIdName` are new in 2.9.0. They are `string?`, as on every other channel event, and bind the `ChannelState`, `ChannelStateDesc`, `CallerIDNum` and `CallerIDName` headers.

```csharp
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Events;

static async Task PrintChannelsAsync(AmiConnection connection)
{
    await foreach (var evt in connection.SendEventGeneratingActionAsync(new StatusAction()))
    {
        if (evt is StatusEvent status)
        {
            Console.WriteLine($"{status.Channel} {status.ChannelStateDesc} {status.CallerIdNum} {status.Extension}");
        }
    }
}
```

**`VSDK0002`.** A project that reads one of the six members gets warning `VSDK0002`, and with `TreatWarningsAsErrors` its build fails. Move the read to the member in the table, or suppress this one obsoletion without muting any other:

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);VSDK0002</NoWarn>
</PropertyGroup>
```

**The six members stay.** They still compile and still bind; nothing is removed, and no release is scheduled to remove them. A program compiled against an earlier release keeps running against 2.9.0 without recompiling, and reads null from them, as it always did.

**`Extension` reads `Exten`.** Every measured version sends the dialplan extension under the header `Exten`, never `Extension`, so before 2.9.0 `StatusEvent.Extension` was always null. It now reads `Exten`, also for a program compiled against an earlier release, because the binding lives in `Verbara.Sdk.Ami`. A leg whose `Exten` is empty, such as the dialled leg of a `Dial()` that is running `AppDial`, reads `""`, not null.

The `Context`, `Priority`, `ConnectedLineNum` and `ConnectedLineName` headers have no typed member; read them from `RawFields`.

## ARI Event Coverage by Version

### Events Available Since Asterisk 12+

All core ARI events (StasisStart/End, Channel*, Bridge*, Playback*, Recording*, Dial, EndpointStateChange, etc.) are supported.

### Events Added in Asterisk 16+

| Event | SDK Class |
|-------|-----------|
| `ApplicationMoveFailed` | `ApplicationMoveFailedEvent` |

### Events Added in Asterisk 21+

| Event | SDK Class |
|-------|-----------|
| `ApplicationRegistered` | `ApplicationRegisteredEvent` |
| `ApplicationUnregistered` | `ApplicationUnregisteredEvent` |
| `ChannelTransfer` | `ChannelTransferEvent` |

### Events Added in Asterisk 22+

| Event | SDK Class |
|-------|-----------|
| `ChannelToneDetected` | `ChannelToneDetectedEvent` |
| `ReferTo` | `ReferToEvent` |
| `ReferredBy` | `ReferredByEvent` |
| `RequiredDestination` | `RequiredDestinationEvent` |

## Migration Guide

The MeetMe → ConfBridge, Monitor → MixMonitor and legacy Dial → DialBegin/DialEnd examples moved to [obsolete-ami-events-removal-migration.md](obsolete-ami-events-removal-migration.md): the types they migrate from were removed in 2.9.0.

## Fallback Behavior

Unknown AMI events deserialize to `ManagerEvent` with all fields in `RawFields`. Unknown ARI events deserialize to `AriEvent` with the raw JSON in `RawJson`. This ensures **zero data loss** regardless of Asterisk version.
