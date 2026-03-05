# Asterisk Version Compatibility Matrix

> SDK: Asterisk.Sdk 0.1.0-beta.1 | Supported: Asterisk 18-23

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

## Events Removed in Asterisk 21+

The following modules were removed in Asterisk 21. Their events are marked `[Obsolete]` in the SDK but **not deleted**, ensuring backwards compatibility with Asterisk 18-20.

### app_meetme (replaced by app_confbridge)

| Legacy Event | Replacement |
|-------------|-------------|
| `MeetMeJoinEvent` | `ConfbridgeJoinEvent` |
| `MeetMeLeaveEvent` | `ConfbridgeLeaveEvent` |
| `MeetMeEndEvent` | `ConfbridgeEndEvent` |
| `MeetMeTalkingEvent` | `ConfbridgeTalkingEvent` |
| `MeetMeStopTalkingEvent` | `ConfbridgeTalkingEvent` |
| `MeetMeTalkingRequestEvent` | No direct replacement |
| `MeetMeMuteEvent` | `ConfbridgeMuteEvent` |

### app_monitor (replaced by app_mixmonitor)

| Legacy Event | Replacement |
|-------------|-------------|
| `MonitorStartEvent` | `MixMonitorStartEvent` |
| `MonitorStopEvent` | `MixMonitorStopEvent` |

### Legacy bridging/dialing (replaced in Asterisk 12)

| Legacy Event | Replacement |
|-------------|-------------|
| `LinkEvent` | `BridgeEnterEvent` |
| `UnlinkEvent` | `BridgeLeaveEvent` |
| `BridgeEvent` | `BridgeCreateEvent` / `BridgeEnterEvent` |
| `DialEvent` | `DialBeginEvent` / `DialEndEvent` |
| `JoinEvent` | `QueueCallerJoinEvent` |
| `LeaveEvent` | `QueueCallerLeaveEvent` |
| `PausedEvent` | `QueueMemberPausedEvent` |
| `UnpausedEvent` | `QueueMemberPausedEvent` |

### Discontinued modules

| Legacy Events | Notes |
|--------------|-------|
| `SkypeAccountStatusEvent`, `SkypeBuddyEntryEvent`, `SkypeBuddyListCompleteEvent`, `SkypeBuddyStatusEvent`, `SkypeChatMessageEvent`, `SkypeLicenseEvent`, `SkypeLicenseListCompleteEvent` | Skype for Asterisk discontinued |
| `ZapShowChannelsEvent`, `ZapShowChannelsCompleteEvent` | Zaptel replaced by DAHDI |

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

### MeetMe to ConfBridge

If migrating from Asterisk 18-20 to 21+, replace MeetMe event handlers:

```csharp
// Before (Asterisk 18-20)
case MeetMeJoinEvent mmj:
    OnUserJoined(mmj.Meetme, mmj.Usernum, mmj.Channel);
    break;

// After (Asterisk 21+)
case ConfbridgeJoinEvent cbj:
    OnUserJoined(cbj.Conference, 0, cbj.Channel);
    break;
```

The SDK handles both simultaneously — keep both cases if supporting mixed-version environments.

### Monitor to MixMonitor

```csharp
// Before
case MonitorStartEvent ms: ...

// After
case MixMonitorStartEvent mms: ...
```

### Legacy Dial to DialBegin/DialEnd

```csharp
// Before (Asterisk < 12)
case DialEvent de when de.SubEvent == "Begin": ...
case DialEvent de when de.SubEvent == "End": ...

// After (Asterisk 12+)
case DialBeginEvent db: ...
case DialEndEvent de: ...
```

The SDK includes `LegacyEventAdapter` which automatically synthesizes legacy events from modern ones for backwards compatibility.

## Fallback Behavior

Unknown AMI events deserialize to `ManagerEvent` with all fields in `RawFields`. Unknown ARI events deserialize to `AriEvent` with the raw JSON in `RawJson`. This ensures **zero data loss** regardless of Asterisk version.
