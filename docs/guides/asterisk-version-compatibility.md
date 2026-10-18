# Asterisk Version Compatibility Matrix

> SDK: Verbara.Sdk 0.1.0-beta.1 | Supported: Asterisk 18-23

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
