# Functional Testing Phase 4A: DTMF + IVR + Bridge/Transfer + Parking — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build ~32 functional tests covering DTMF detection, IVR navigation, bridge lifecycle, blind/attended transfers, and call parking.

**Architecture:** Extends Phase 1 FunctionalTests with new test extensions in extensions.conf, a new res_parking.conf, and 5 test files using existing infrastructure (AmiConnectionFactory, FunctionalTestBase, AsteriskContainerFixture, DockerControl).

**Tech Stack:** .NET 10, xunit 2.9.3, FluentAssertions 7.1.0, Asterisk 21-alpine

**Spec:** `docs/superpowers/specs/2026-03-21-functional-testing-phase4a-design.md`

---

## File Structure

```
docker/functional/asterisk-config/
  extensions.conf                           ← MODIFY: add ext 150,155,160-166,750
  res_parking.conf                          ← CREATE

Tests/Asterisk.Sdk.FunctionalTests/
  Layer5_Integration/
    Dtmf/
      DtmfDetectionTests.cs                ← CREATE (6 tests)
    Ivr/
      IvrNavigationTests.cs                ← CREATE (6 tests)
    Bridge/
      BridgeLifecycleTests.cs              ← CREATE (6 tests)
      TransferTests.cs                     ← CREATE (10 tests)
    Parking/
      ParkingTests.cs                      ← CREATE (4 tests)
```

---

### Task 1: Config changes (extensions + parking)

**Files:**
- Modify: `docker/functional/asterisk-config/extensions.conf`
- Create: `docker/functional/asterisk-config/res_parking.conf`

- [ ] **Step 1: Add DTMF + IVR + Transfer + Parking extensions**

Append to `extensions.conf` in the `[test-functional]` context:

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

; IVR test — Background + WaitExten
exten => 160,1,Answer()
 same => n(menu),Background(silence/1)
 same => n,WaitExten(5)

exten => 161,1,Answer()
 same => n,Wait(10)
 same => n,Hangup()

exten => 162,1,Answer()
 same => n,Wait(10)
 same => n,Hangup()

exten => 163,1,Answer()
 same => n,Wait(10)
 same => n,Hangup()

; IVR menu options (WaitExten targets in test-functional context)
exten => 1,1,NoOp(IVR Option 1)
 same => n,Goto(test-functional,165,1)
exten => 2,1,NoOp(IVR Option 2)
 same => n,Goto(test-functional,166,1)
exten => i,1,NoOp(IVR Invalid)
 same => n,Goto(test-functional,160,menu)
exten => t,1,NoOp(IVR Timeout)
 same => n,Hangup()

exten => 165,1,Answer()
 same => n,Wait(3)
 same => n,Hangup()

exten => 166,1,Answer()
 same => n,Wait(3)
 same => n,Hangup()

; Parking
exten => 750,1,Park(default)
```

Note: Extensions 1, 2, i, t are IVR targets for WaitExten. They only match when a call is in WaitExten state at ext 160. Read the existing extensions.conf first to ensure no conflicts with these single-digit extensions — if there are conflicts, wrap in a sub-context.

- [ ] **Step 2: Create res_parking.conf**

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

- [ ] **Step 3: Commit**

```bash
git add docker/functional/asterisk-config/
git commit -m "feat(tests): add DTMF, IVR, transfer, and parking extensions for Phase 4A"
```

---

### Task 2: DTMF detection tests (6 tests)

**Files:**
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Dtmf/DtmfDetectionTests.cs`

- [ ] **Step 1: Write DtmfDetectionTests**

All: `[AsteriskContainerFact]`, `[Trait("Category", "Integration")]`, `FunctionalTestBase`, `IClassFixture<AsteriskContainerFixture>`.

CRITICAL: Read source code first:
- `src/Asterisk.Sdk.Ami/Actions/PlayDtmfAction.cs` — properties: Channel, Digit, Duration, Receive
- `src/Asterisk.Sdk.Ami/Events/DtmfBeginEvent.cs` — what properties?
- `src/Asterisk.Sdk.Ami/Events/DtmfEndEvent.cs` — DurationMs property?
- `src/Asterisk.Sdk.Ami/Events/DtmfEvent.cs` — Digit, Direction properties?

Test pattern for each DTMF test:
1. Originate a call to ext 100 (Answer + Wait(5)) via OriginateAction with IsAsync=true
2. Subscribe to AMI events, wait for NewChannelEvent to get channel name
3. Send PlayDtmfAction with the channel name
4. Collect DtmfBeginEvent/DtmfEndEvent
5. Assert digit, duration, direction
6. Cleanup (channel hangs up naturally after Wait expires)

6 tests:
1. `PlayDtmf_ShouldGenerateBeginAndEndEvents` — digit "5", Duration=250, Receive=true
2. `PlayDtmf_AllDigits_ShouldBeRecognized` — loop 0-9,*,#,A-D (16 digits)
3. `PlayDtmf_DirectionReceived_ShouldSetCorrectDirection` — Receive=true, check Direction
4. `PlayDtmf_DirectionSent_ShouldSetCorrectDirection` — Receive=false
5. `SendDtmf_MultipleDigits_ShouldFireSequentialEvents` — originate Local/150@test-functional/n, collect DtmfEnd events in order
6. `DtmfConsumedByRead_ShouldFireDtmfEvent` — originate to ext 155, PlayDtmf "7", verify DtmfEvent (consumed by Read)

- [ ] **Step 2: Build — 0 warnings**

- [ ] **Step 3: Commit**

```bash
git add Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Dtmf/
git commit -m "test(functional): add DTMF detection tests (6 tests)"
```

---

### Task 3: IVR navigation tests (6 tests)

**Files:**
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Ivr/IvrNavigationTests.cs`

- [ ] **Step 1: Write IvrNavigationTests**

Tests use dialplan-only IVR (Background + WaitExten at ext 160). PlayDtmfAction sends navigation digits.

Test pattern:
1. Originate call to ext 160 (IVR menu)
2. Wait for channel to be in WaitExten state (wait ~2s for Background to play silence/1)
3. PlayDtmfAction with digit
4. Verify NewExtenEvent shows navigation to target extension

6 tests:
1. `IvrCall_ShouldReachWaitExten` — originate to 160, verify channel established and Background/WaitExten in progress (via NewExtenEvent)
2. `IvrOption1_ShouldNavigateToExtension165` — PlayDtmf "1", verify NewExtenEvent with exten=165 (or exten=1 then goto 165)
3. `IvrOption2_ShouldNavigateToExtension166` — PlayDtmf "2", verify navigation to 166
4. `IvrTimeout_ShouldHangup` — don't send DTMF, wait >5s (WaitExten timeout), verify HangupEvent via timeout extension "t"
5. `IvrInvalidDigit_ShouldReturnToMenu` — PlayDtmf "9" (not 1 or 2), verify goto back to 160 menu via "i" extension
6. `NewExtenEvent_ShouldTrackDialplanFlow` — originate to 160, PlayDtmf "1", collect all NewExtenEvents, verify sequence shows 160 → 1 → 165

- [ ] **Step 2: Build — 0 warnings**

- [ ] **Step 3: Commit**

```bash
git add Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Ivr/
git commit -m "test(functional): add IVR navigation tests (6 tests)"
```

---

### Task 4: Bridge lifecycle tests (6 tests)

**Files:**
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Bridge/BridgeLifecycleTests.cs`

- [ ] **Step 1: Read bridge-related APIs**

CRITICAL: Read these files to understand the actual bridge API:
- `src/Asterisk.Sdk.Ami/Actions/BridgeAction.cs` — or search for Bridge*Action in Actions/
- `src/Asterisk.Sdk.Ami/Events/BridgeCreateEvent.cs`, `BridgeEnterEvent.cs`, `BridgeLeaveEvent.cs`, `BridgeDestroyEvent.cs`
- `src/Asterisk.Sdk.Live/Bridges/BridgeManager.cs` — public API
- `src/Asterisk.Sdk.Live/Server/AsteriskServer.cs` — `.Bridges` property

Note: In Asterisk, bridging two channels can be done via:
- `BridgeAction` (AMI action that bridges two existing channels)
- Or originating calls that enter the same `ConfBridge()` or `Bridge()` dialplan app
- Or originating to `Local/` channels that meet in a bridge

Check which approach is simplest. The `BridgeAction` in AMI takes Channel1 and Channel2 parameters.

- [ ] **Step 2: Write BridgeLifecycleTests (6 tests)**

1. `Bridge_ShouldFireCreateAndEnterEvents` — originate 2 calls to ext 100 (Wait), BridgeAction, verify BridgeCreateEvent + BridgeEnterEvent
2. `Bridge_ShouldFireLeaveAndDestroyOnHangup` — bridge, hangup one channel, verify BridgeLeaveEvent + BridgeDestroyEvent
3. `BridgeManager_ShouldTrackActiveBridges` — AsteriskServer.Bridges, verify count increases on bridge, decreases on hangup
4. `MultipleBridges_ShouldBeIndependent` — 2 pairs of channels bridged, verify 2 separate bridges
5. `BridgeWithThreeChannels_ShouldTrackAll` — 3 channels in one bridge, verify BridgeEnterEvent × 3
6. `BridgeEvents_ShouldHaveCorrectBridgeId` — all events for same bridge share BridgeUniqueid

- [ ] **Step 3: Build — 0 warnings**

- [ ] **Step 4: Commit**

```bash
git add Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Bridge/
git commit -m "test(functional): add bridge lifecycle tests (6 tests)"
```

---

### Task 5: Transfer tests (10 tests)

**Files:**
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Bridge/TransferTests.cs`

- [ ] **Step 1: Read transfer-related APIs**

CRITICAL: Read these files:
- `src/Asterisk.Sdk.Ami/Actions/RedirectAction.cs` — Channel, Context, Exten, Priority
- `src/Asterisk.Sdk.Ami/Actions/AtxferAction.cs` — or search for Atxfer*, AttendedTransfer* actions
- `src/Asterisk.Sdk.Ami/Events/BlindTransferEvent.cs` — properties
- `src/Asterisk.Sdk.Ami/Events/AttendedTransferEvent.cs` — properties

If `AtxferAction` doesn't exist, attended transfer can be initiated via DTMF feature codes in `features.conf`. Check if features.conf exists in functional config and what the atxfer feature code is (default: *2 or ##).

- [ ] **Step 2: Write Blind Transfer tests (5)**

1. `BlindTransfer_ShouldRedirectChannel` — originate to 100, RedirectAction to ext 161, verify NewExtenEvent
2. `BlindTransfer_ShouldFireBlindTransferEvent` — verify BlindTransferEvent with context/extension
3. `BlindTransfer_ShouldUpdateChannelManager` — AsteriskServer, verify channel extension changes
4. `BlindTransfer_ToNonExistentExtension_ShouldFail` — redirect to 999, verify error response or hangup
5. `BlindTransfer_DuringBridge_ShouldTransferOneParty` — bridge 2 channels, redirect one to 161, verify bridge breaks

- [ ] **Step 3: Write Attended Transfer tests (5)**

6. `AttendedTransfer_ShouldCreateConsultationChannel` — bridge A-B, AtxferAction on B to 162, verify new channel
7. `AttendedTransfer_ShouldFireAttendedTransferEvent` — verify event after transfer completes
8. `AttendedTransfer_ShouldBridgeCallerToTarget` — A bridged with 162 after transfer
9. `AttendedTransfer_Cancel_ShouldRestoreOriginalBridge` — cancel transfer, verify A-B bridge restored
10. `AttendedTransfer_Events_ShouldBeChronological` — collect all events, verify ordering

Note: If AtxferAction doesn't exist in SDK, adapt tests to use what's available (RedirectAction for blind, DTMF feature codes for attended, or skip attended and document as Phase 5).

- [ ] **Step 4: Build — 0 warnings**

- [ ] **Step 5: Commit**

```bash
git add Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Bridge/TransferTests.cs
git commit -m "test(functional): add transfer tests — blind and attended (10 tests)"
```

---

### Task 6: Parking tests (4 tests)

**Files:**
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Parking/ParkingTests.cs`

- [ ] **Step 1: Read parking-related APIs**

CRITICAL: Read these files:
- `src/Asterisk.Sdk.Ami/Actions/ParkAction.cs` — or search for Park*Action
- `src/Asterisk.Sdk.Ami/Events/ParkedCallEvent.cs`
- `src/Asterisk.Sdk.Ami/Events/UnParkedCallEvent.cs`
- `src/Asterisk.Sdk.Ami/Events/ParkedCallTimeOutEvent.cs`
- `src/Asterisk.Sdk.Ami/Events/ParkedCallGiveUpEvent.cs`

If ParkAction doesn't exist, parking can be done via RedirectAction to ext 750.

- [ ] **Step 2: Write ParkingTests (4 tests)**

1. `Park_ShouldFireParkedCallEvent` — originate call to 100, redirect to 750 (Park), verify ParkedCallEvent with lot name and position (751-770 range)
2. `Unpark_ShouldFireUnParkedCallEvent` — park a call, originate new call to parked position (e.g., 751), verify UnParkedCallEvent
3. `ParkTimeout_ShouldFireTimeOutEvent` — park a call, wait >10s (parkingtime=10), verify ParkedCallTimeOutEvent
4. `ParkGiveUp_ShouldFireGiveUpEvent` — park a call, hangup the parked channel, verify ParkedCallGiveUpEvent

- [ ] **Step 3: Build — 0 warnings**

- [ ] **Step 4: Commit**

```bash
git add Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Parking/
git commit -m "test(functional): add parking tests (4 tests)"
```

---

### Task 7: Final verification

- [ ] **Step 1: Build full solution**

```bash
dotnet build Asterisk.Sdk.slnx
```
Expected: 0 warnings.

- [ ] **Step 2: Run unit tests**

```bash
dotnet test Tests/Asterisk.Sdk.FunctionalTests/ --filter "Category=Unit" --no-restore
```
Expected: All unit tests pass (Phase 1-2).

- [ ] **Step 3: Run non-functional tests**

```bash
dotnet test Asterisk.Sdk.slnx --filter "FullyQualifiedName!~FunctionalTests&FullyQualifiedName!~IntegrationTests&FullyQualifiedName!~Spike&FullyQualifiedName!~Benchmarks" --no-restore
```
Expected: All existing tests pass. 0 regressions.
