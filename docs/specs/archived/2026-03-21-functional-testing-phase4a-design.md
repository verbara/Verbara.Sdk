# Functional Testing Phase 4A: DTMF + IVR + Bridge/Transfer + Parking — Design Spec

**Goal:** Deliver ~32 functional tests covering DTMF detection, IVR navigation, bridge lifecycle, blind/attended transfers, and call parking.

**Depends on:** Phase 1 infrastructure (FunctionalTests project, Docker Compose, helpers, fixtures)

---

## 1. DTMF Detection Tests (6 tests)

### Strategy

Primary: **AMI PlayDtmfAction** — inject DTMF into active channel, verify DtmfBeginEvent/DtmfEndEvent.
Secondary: **Dialplan SendDTMF via Local channel** — test multi-digit burst.
DtmfEvent (consumed): **Read() extension** — dialplan app consumes digit, fires legacy DtmfEvent.

### Config Changes

Add to `docker/functional/asterisk-config/extensions.conf`:
```ini
; DTMF SendDTMF burst test
exten => 150,1,Answer()
 same => n,Wait(1)
 same => n,SendDTMF(123456789*#,250,200)
 same => n,Wait(2)
 same => n,Hangup()

; DTMF Read() consumption test
exten => 155,1,Answer()
 same => n,Read(result,,1,,,5)
 same => n,Wait(2)
 same => n,Hangup()
```

### Tests

**DtmfDetectionTests.cs:**

1. `PlayDtmf_ShouldGenerateBeginAndEndEvents` — originate call to ext 100 (Wait 5s), PlayDtmfAction { Digit="5", Duration=250, Receive=true }, verify DtmfBeginEvent + DtmfEndEvent with correct digit.

2. `PlayDtmf_AllDigits_ShouldBeRecognized` — loop through all 16 digits (0-9, *, #, A-D), PlayDtmf each, verify each DtmfEndEvent has correct digit.

3. `PlayDtmf_DirectionReceived_ShouldSetCorrectDirection` — PlayDtmf with Receive=true, verify Direction=="Received" in event.

4. `PlayDtmf_DirectionSent_ShouldSetCorrectDirection` — PlayDtmf with Receive=false, verify Direction=="Sent".

5. `SendDtmf_MultipleDIgits_ShouldFireSequentialEvents` — originate Local/150@test-functional/n, collect DtmfEnd events, verify digits arrive in order: 1,2,3,4,5,6,7,8,9,*,#.

6. `DtmfConsumedByRead_ShouldFireDtmfEvent` — originate call to ext 155 (Read waits for 1 digit), PlayDtmf digit "7", verify DtmfEvent (legacy consumed event) fires with Digit="7".

---

## 2. IVR Navigation Tests (6 tests)

### Strategy

Use FastAGI server with test IVR scripts. SIPp or Local channels call the AGI extension. PlayDtmfAction sends navigation digits.

### Config Changes

Add to `extensions.conf`:
```ini
; IVR test — FastAGI
exten => 160,1,Answer()
 same => n,AGI(agi://127.0.0.1:4573/ivr-test)
 same => n,Hangup()
```

### Tests

**IvrNavigationTests.cs:**

Tests require a FastAGI server started in test setup with test IVR scripts. Use `FastAgiServer` with `SimpleMappingStrategy` mapping "ivr-test" to a test script.

1. `IvrCall_ShouldConnectToAgiServer` — originate to ext 160, verify AGI session established (no DTMF, just connection).

2. `IvrGetData_ShouldCollectDtmfDigit` — AGI script calls GetData(), PlayDtmf sends "3", verify script receives digit "3".

3. `IvrMultiLevel_ShouldNavigateCorrectly` — AGI script with 2-level menu: press 1→submenu, press 2→action. PlayDtmf "1" then "2", verify correct path taken (via channel variables or events).

4. `IvrTimeout_ShouldHandleNoInput` — AGI script calls GetData with 3s timeout, don't send DTMF, verify timeout path taken.

5. `IvrInvalidDigit_ShouldRetry` — AGI script validates input (only 1-3 valid), PlayDtmf "9" (invalid), verify retry prompt.

6. `NewExtenEvent_ShouldTrackDialplanFlow` — originate to ext 160, subscribe NewExtenEvent, verify event sequence shows extension transitions.

Note: IVR tests are more complex — they need a FastAGI server running in the test process. Use the SDK's `FastAgiServer` class directly. The AGI server runs on port 4573 (already exposed in Docker Compose). Since SIPp uses `network_mode: service:asterisk`, and Asterisk connects to AGI via the extension config, the test process must listen on 4573 OR use `127.0.0.1` from Asterisk's perspective. Given Docker networking, the test process is outside the container — use the host's IP or Docker host networking. Adapt as needed.

Alternative: If FastAGI from outside Docker is complex, use `AGI(agi://host.docker.internal:4573/...)` or simplify to dialplan-only IVR using `Background()` + `WaitExten()`:

```ini
exten => 160,1,Answer()
 same => n,Background(silence/1)
 same => n,WaitExten(5)

exten => 1,1,NoOp(Option 1 selected)
 same => n,Goto(test-functional,165,1)
exten => 2,1,NoOp(Option 2 selected)
 same => n,Goto(test-functional,166,1)
exten => i,1,NoOp(Invalid)
 same => n,Goto(test-functional,160,1)
exten => t,1,NoOp(Timeout)
 same => n,Hangup()

exten => 165,1,Wait(3)
 same => n,Hangup()
exten => 166,1,Wait(3)
 same => n,Hangup()
```

This dialplan-only approach uses WaitExten() which consumes DTMF and generates NewExtenEvent — simpler than FastAGI for functional tests.

---

## 3. Bridge Lifecycle Tests (6 tests)

### Strategy

Originate 2 calls via AMI, bridge them using `BridgeAction`, verify event sequence.

### Tests

**BridgeLifecycleTests.cs:**

1. `Bridge_ShouldFireCreateAndEnterEvents` — originate 2 Local channels, BridgeAction, verify BridgeCreateEvent + BridgeEnterEvent × 2.

2. `Bridge_ShouldFireLeaveAndDestroyOnHangup` — bridge 2 channels, hangup one, verify BridgeLeaveEvent + BridgeDestroyEvent.

3. `BridgeManager_ShouldTrackActiveBridges` — use AsteriskServer, bridge 2 channels, verify Bridges.BridgeCount > 0, hangup, verify count returns to 0.

4. `MultipleBridges_ShouldBeIndependent` — create 2 bridges with 2 pairs of channels, verify each bridge has correct channel count.

5. `BridgeWithThreeChannels_ShouldTrackAll` — bridge 3 channels (originate 3, bridge all), verify BridgeEnterEvent × 3.

6. `BridgeEvents_ShouldHaveCorrectBridgeId` — verify all bridge events for same bridge share the same BridgeUniqueid.

Note: Check if `BridgeAction` exists in SDK, or if bridging is done via `OriginateAction` with context/extension that uses `Bridge()` app. Also check for `AmiAction` for creating/managing bridges. Read `src/Asterisk.Sdk.Ami/Actions/` for bridge-related actions.

---

## 4. Transfer Tests (10 tests)

### Strategy

- **Blind transfer**: `RedirectAction` moves a channel to a new extension. Fires `BlindTransferEvent`.
- **Attended transfer**: `AtxferAction` initiates consultative transfer. Fires `AttendedTransferEvent`.

### Config Changes

Add transfer target extensions to `extensions.conf`:
```ini
; Transfer targets
exten => 161,1,Answer()
 same => n,Wait(10)
 same => n,Hangup()

exten => 162,1,Answer()
 same => n,Wait(10)
 same => n,Hangup()

exten => 163,1,Answer()
 same => n,Wait(10)
 same => n,Hangup()
```

### Blind Transfer Tests (5)

**TransferTests.cs:**

1. `BlindTransfer_ShouldRedirectChannel` — originate call to ext 100, RedirectAction to ext 161, verify channel moves (NewExtenEvent with ext 161).

2. `BlindTransfer_ShouldFireBlindTransferEvent` — same as above, verify BlindTransferEvent with correct context/extension.

3. `BlindTransfer_ShouldUpdateChannelManager` — use AsteriskServer, verify channel's extension changes after redirect.

4. `BlindTransfer_ToNonExistentExtension_ShouldHandleGracefully` — redirect to ext 999 (doesn't exist), verify no crash, channel hangup or error response.

5. `BlindTransfer_DuringBridge_ShouldTransferOneParty` — bridge 2 channels, redirect one to ext 161, verify bridge breaks and redirected channel moves.

### Attended Transfer Tests (5)

6. `AttendedTransfer_ShouldInitiateConsultation` — originate 2 bridged channels (A-B), AtxferAction on B to ext 162, verify consultation channel created.

7. `AttendedTransfer_ShouldFireAttendedTransferEvent` — complete the attended transfer, verify AttendedTransferEvent fires.

8. `AttendedTransfer_ShouldBridgeOriginalCallerToTarget` — after transfer completes, A should be bridged with ext 162 channel.

9. `AttendedTransfer_Cancel_ShouldRestoreOriginalBridge` — initiate attended transfer, then cancel (hangup consultation), verify A-B bridge restored.

10. `AttendedTransfer_Events_ShouldBeChronological` — collect all events during transfer, verify ordering.

Note: Check if `AtxferAction` exists in SDK. If not, attended transfer can be tested via `RedirectAction` with specific context or via DTMF feature codes (e.g., ##). Read `src/Asterisk.Sdk.Ami/Actions/` for transfer-related actions. Also check `features.conf` for transfer feature codes.

---

## 5. Parking Tests (4 tests)

### Config Changes

Create `docker/functional/asterisk-config/res_parking.conf`:
```ini
[general]

[default]
parkext => 750
parkpos => 751-770
context => test-functional
parkingtime => 10
comebacktoorigin = yes
comebackcontext = test-functional
comebackdialtime = 30
```

Add to `extensions.conf`:
```ini
; Park extension
exten => 750,1,Park(default)
```

### Tests

**ParkingTests.cs:**

1. `Park_ShouldFireParkedCallEvent` — originate call, ParkAction (or redirect to ext 750), verify ParkedCallEvent with parking lot and position.

2. `Unpark_ShouldFireUnParkedCallEvent` — park a call, then unpark (originate to parked position 751), verify UnParkedCallEvent.

3. `ParkTimeout_ShouldFireParkedCallTimeOutEvent` — park a call, wait > 10s (parkingtime), verify ParkedCallTimeOutEvent and call returns to origin.

4. `ParkGiveUp_ShouldFireParkedCallGiveUpEvent` — park a call, caller hangs up while parked, verify ParkedCallGiveUpEvent.

Note: Check if `ParkAction` exists in SDK. If not, parking can be done via `RedirectAction` to ext 700, or via dialplan `Park()` app. Read `src/Asterisk.Sdk.Ami/Actions/` for park-related actions.

---

## Summary

| Area | Tests | Files | Config changes |
|------|-------|-------|---------------|
| DTMF | 6 | DtmfDetectionTests.cs | extensions.conf (ext 150, 155) |
| IVR | 6 | IvrNavigationTests.cs | extensions.conf (ext 160, 165, 166) |
| Bridge | 6 | BridgeLifecycleTests.cs | None |
| Transfer | 10 | TransferTests.cs | extensions.conf (ext 161-163) |
| Parking | 4 | ParkingTests.cs | res_parking.conf + extensions.conf (ext 700) |
| **Total** | **32** | **5 test files** | **1 new config + extensions updates** |

Note: Reduced from 38 to 32 after analysis showed some tests overlap. IVR simplified to dialplan-only approach (Background+WaitExten) instead of FastAGI dependency.
