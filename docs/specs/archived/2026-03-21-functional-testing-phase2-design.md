# Functional Testing Phase 2: Pre-v1.0 — Design Spec

**Goal:** Deliver ~62 functional tests covering source generator pipeline validation, queue management integration, health check edge cases, CDR/CEL event verification, and an AOT trim CI gate.

**Depends on:** Phase 1 infrastructure (FunctionalTests project, Docker Compose, helpers, fixtures)

**Spec:** This document
**Roadmap:** `docs/superpowers/plans/2026-03-20-functional-testing-roadmap.md`

---

## 1. Source Generator Tests (Layer 2 — No Docker)

### Problem

The 4 source generators (EventDeserializer, ActionSerializer, EventRegistry, ResponseDeserializer) produce code for 269 events, 115 actions, and 18 responses. Current tests only validate property instantiation (~6% coverage) — **zero tests exercise the actual deserialization/serialization pipeline**.

### Design

**32 tests across 7 files**, testing every stage of the pipeline:

#### 1.1 AmiStringPool Tests (3 tests)

Test the FNV-1a hash-based string interning pool (941 keys, 35 values):
- Pool hit for known key → returns interned string (reference equality)
- Pool miss for unknown key → allocates new string (fallback path)
- Value pool hit/miss behavior

#### 1.2 EventDeserializer Pipeline Tests (8 tests)

Test `AmiMessage → typed Event` through `GeneratedEventDeserializer.Deserialize()`:
- Each property type: string, int?, long?, bool?, double?
- 2-level inheritance (ManagerEvent → HangupEvent)
- 3-level inheritance (ManagerEvent → BridgeEventBase → BridgeCreateEvent)
- CdrEvent (18 AMI-mapped fields; excludes 3 `*AsDate` properties which are NOT auto-populated) and CelEvent (16 fields) — assert only string/int properties, not `*AsDate` variants
- `[AsteriskMapping]` custom field name mapping
- All fields null/absent → properties remain null
- Unknown event name → fallback to base ManagerEvent with RawFields
- Case insensitivity in event name lookup (FrozenDictionary)

#### 1.3 ActionSerializer Pipeline Tests (6 tests)

Test `Action → KVP list` through `GeneratedActionSerializer.Serialize()`:
- Simple action (PingAction — no fields)
- Complex action (OriginateAction — strings, bool, long)
- Null fields → not included in output
- Empty string vs null behavior
- IHasExtraFields (OriginateAction.SetVariable)
- Action name resolution: registered action vs fallback (`GetType().Name.Replace("Action", "")` — **uses reflection, potential AOT issue**)

#### 1.4 ResponseDeserializer Pipeline Tests (4 tests)

Test `AmiMessage → typed Response` through `GeneratedResponseDeserializer.Deserialize()`:
- Base ManagerResponse (Response + Message fields)
- CommandResponse with `__CommandOutput` (multiline `Response: Follows` path)
- Typed response with numeric fields (CoreSettingsResponse)
- Unknown action name → fallback to base ManagerResponse

#### 1.5 Generator Edge Case Tests (7 tests)

- Boolean parsing: "0"→false, "1"→true, "true"→true, "false"→false, "yes"→**false** (SDK only recognizes "1" and "true"), null→null
- Int malformed: "abc" → null (TryParse fails silently)
- Long overflow: very large value → null
- Double edge cases: "NaN", "Infinity", "" → null
- Whitespace in values: " 42 " → depends on TryParse behavior
- Special characters: unicode, quotes in field values
- Case insensitivity: field name lookup in AmiMessage

#### 1.6 EventGeneratingAction Tests (3 tests)

Test `IEventGeneratingAction` pipeline (actions that produce event streams):
- OriginateAction with `SendEventGeneratingActionAsync` → collects events until `*Complete` suffix
- ResponseEvent with ActionId correlation (events that carry ActionId)
- Completion detection: event name `EndsWith("Complete")`

#### 1.7 BulkRoundtrip Tests (1 paramétric test)

- `[Theory]` + `[MemberData]` auto-discovers ALL classes with `[AsteriskMapping]`
- Events: create AmiMessage with fields → deserialize → verify correct type returned
- Actions: create instance → serialize → verify fields present
- Responses: create AmiMessage → deserialize → verify correct type
- Covers 100% of the 400+ generated types

### Key API Surface

```
AmiStringPool: internal static class in Asterisk.Sdk.Ami.Internal
  GetKey(ReadOnlySpan<byte>) → string
  GetValue(ReadOnlySpan<byte>) → string

GeneratedEventDeserializer: internal static class (generated)
  Deserialize(AmiMessage msg) → ManagerEvent

GeneratedActionSerializer: internal static class (generated)
  GetActionName(ManagerAction action) → string
  Serialize(ManagerAction action) → IEnumerable<KeyValuePair<string, string>>

GeneratedResponseDeserializer: internal static class (generated)
  Deserialize(AmiMessage msg, string actionName) → ManagerResponse

GeneratedEventRegistry: internal static class (generated)
  Create(string eventName) → ManagerEvent?

AmiMessage: internal class in Asterisk.Sdk.Ami.Internal
  string? this[string key] — case-insensitive field access
  string? EventType
  string? ActionId
  string? ResponseStatus
  Dictionary<string, string> Fields
```

Note: All generated classes are `internal`. FunctionalTests project already has `InternalsVisibleTo` from Phase 1.

---

## 2. Queue Tests (Layer 5 — Docker + SIPp)

### Problem

QueueManager has 12 unit tests but 0 integration tests with real Asterisk. Critical paths untested: DeviceStateChange propagation, RawFields parsing for caller events, LiveMetrics counters, initial state load, and cross-manager (Agent+Queue) correlation.

### Design

**18 tests across 4 files**, plus SIPp infrastructure.

#### 2.1 SIPp Infrastructure

**Docker Compose addition:**
```yaml
sipp:
  image: ctaloi/sipp
  container_name: functional-sipp
  depends_on:
    asterisk:
      condition: service_healthy
  network_mode: "service:asterisk"
```

**SIPp scenarios:**
- `basic-call.xml` — INVITE → 200 OK → ACK → BYE (~3s call)
- `queue-caller.xml` — INVITE to ext 500 (queue) → wait → BYE

**pjsip.conf addition:**
```ini
[sipp-trunk]
type = endpoint
context = test-functional
disallow = all
allow = ulaw
transport = transport-udp
auth = sipp-auth
aors = sipp-aors

[sipp-auth]
type = auth
auth_type = userpass
password = sipp
username = sipp

[sipp-aors]
type = aor
max_contacts = 5
qualify_frequency = 0
```

**Note:** QueueCallFlowTests must add a queue member programmatically via `QueueAddAction` before SIPp calls — the static `queues.conf` defines the queue but no members.

#### 2.2 QueueMemberTests (5 tests)

1. `AddMember_ShouldUpdateQueueAndReverseIndex` — QueueAddAction → verify member in queue + reverse index
2. `RemoveMember_ShouldCleanupReverseIndex` — QueueRemoveAction → verify cleanup
3. `PauseMember_ShouldUpdateStateAndFireEvent` — QueuePauseAction → verify Paused flag + MemberPaused event
4. `DeviceStateChange_ShouldPropagateToAllQueues` — member in 3 queues, DeviceStateChange → all 3 updated
5. `MemberInMultipleQueues_ShouldTrackCorrectly` — add same interface to 3 queues → GetQueuesForMember returns all 3

#### 2.3 QueueCallerTests (5 tests)

1. `CallerJoin_ShouldAddEntryViaRawFields` — originate call to queue → verify RawFields parsing for Queue/Channel
2. `CallerLeave_ShouldRecordWaitTimeMetric` — join + leave → verify LiveMetrics histogram recorded
3. `CallerAbandon_ShouldFireEvent` — caller abandons → QueueCallerAbandonEvent received (document QueueManager gap)
4. `QueueStatus_ShouldRebuildFullState` — send QueueStatusAction → verify complete state reconstruction
5. `QueueSummary_ShouldReturnAccurateStats` — QueueSummaryAction → verify LoggedIn, Available, Callers

#### 2.4 QueueCallFlowTests (5 tests — SIPp)

1. `SippCallToQueue_ShouldProduceFullEventSequence` — SIPp caller → queue → agent → verify Join→AgentConnect→Complete→Leave
2. `MultipleCallersInQueue_ShouldMaintainOrder` — 3 SIPp callers → verify position ordering
3. `QueueTimeout_ShouldProduceAbandonEvent` — caller waits > timeout → QueueCallerAbandon
4. `AgentAndQueueManager_ShouldCorrelateCallFlow` — verify both managers updated during same call
5. `QueueWithNoMembers_ShouldHandleGracefully` — call to empty queue → no crash

#### 2.5 QueueMetricsTests (3 tests)

1. `CallerJoinLeave_ShouldIncrementCounters` — verify `live.queue.calls.joined` and `live.queue.calls.left`
2. `WaitTime_ShouldRecordHistogram` — verify `live.queue.wait_time` histogram
3. `QueueGauges_ShouldReflectCurrentState` — verify `live.queues.count`, agent gauges

### Key API Surface

```
QueueManager:
  Queues, QueueCount, GetByName(name), GetQueuesForMember(interface)
  Events: QueueUpdated, MemberAdded, MemberRemoved, MemberStatusChanged, CallerJoined, CallerLeft

AMI Actions:
  QueueAddAction { Queue, Interface, Penalty, Paused, MemberName }
  QueueRemoveAction { Queue, Interface }
  QueuePauseAction { Interface, Queue, Paused, Reason }
  QueueStatusAction { Queue } — IEventGeneratingAction
  QueueSummaryAction { Queue } — IEventGeneratingAction

AMI Events:
  QueueMemberAddedEvent, QueueMemberRemovedEvent, QueueMemberPausedEvent
  QueueCallerJoinEvent (Queue/Channel via RawFields), QueueCallerLeaveEvent (via RawFields)
  QueueCallerAbandonEvent (NOT consumed by QueueManager)
  DeviceStateChangeEvent { Device, State }

LiveMetrics:
  Counter: live.queue.calls.joined, live.queue.calls.left
  Histogram: live.queue.wait_time (ms)
  Gauges: live.queues.count, live.agents.total/available/on_call/paused
```

---

## 3. Health Check Edge Case Tests (Layer 5 — Docker)

### Problem

Existing tests cover basic Healthy/Degraded/Unhealthy states. Missing: state transitions during reconnect, behavior under load, composite registration.

### Design

**4 tests, 1 file.**

1. `AmiHealthCheck_ShouldReturnDegraded_DuringReconnect` — kill Asterisk → verify Healthy → Degraded → Unhealthy → Healthy after restart
2. `HealthCheck_ShouldNotHang_UnderHighLoad` — 50 concurrent health check calls during heavy AMI traffic
3. `HealthCheck_ShouldReflectActualState_AfterReconnect` — reconnect cycle → verify health matches actual state
4. `AllHealthChecks_ShouldBeRegistrable_ViaHostBuilder` — register all 3 via AddAsterisk → verify resolve and return

### Key API Surface

```
AmiHealthCheck : IHealthCheck (Asterisk.Sdk.Ami.Diagnostics)
  Connected → Healthy, Reconnecting → Degraded, else → Unhealthy

AgiHealthCheck : IHealthCheck (Asterisk.Sdk.Agi.Diagnostics)
  Listening → Healthy, Starting → Degraded, else → Unhealthy

AriHealthCheck : IHealthCheck (Asterisk.Sdk.Ari.Diagnostics)
  Connected → Healthy, Reconnecting → Degraded, else → Unhealthy
```

---

## 4. CDR/CEL Tests (Layer 5 — Docker + SIPp)

### Problem

CdrEvent (21 properties) and CelEvent (16 properties) have zero tests. CDR/CEL events require `cdr_manager.conf` and `cel_manager.conf` which are missing from functional test Docker config.

### Design

**8 tests, 2 files**, plus 2 config files.

#### 4.1 Configuration (required for CDR/CEL over AMI)

**cdr_manager.conf:**
```ini
[general]
enabled = yes
```

**cel_manager.conf:**
```ini
[general]
enable = yes
events = ALL
apps = dial,queue,confbridge,park
```

#### 4.2 CdrEventTests (4 tests)

1. `SippCall_ShouldProduceCdrEvent_WithCorrectFields` — verify Src, Destination, Channel, Duration, BillableSeconds, Disposition="ANSWERED", StartTime parseable
2. `UnansweredCall_ShouldProduceCdr_WithNoAnswerDisposition` — Disposition="NO ANSWER", BillableSeconds=0, AnswerTime null/empty
3. `CdrTimestamps_ShouldBeChronologicalStrings` — parse StartTime/AnswerTime/EndTime → verify order, Duration ≈ EndTime-StartTime
4. `CdrDisposition_ShouldMatchCallOutcome` — test ANSWERED, NO ANSWER, BUSY scenarios

#### 4.3 CelSequenceTests (4 tests)

1. `SippCall_ShouldProduceCompleteCelTimeline` — CHAN_START → APP_START → ANSWER → BRIDGE_ENTER → BRIDGE_EXIT → HANGUP → CHAN_END → LINKEDID_END
2. `CelLinkedId_ShouldCorrelateRelatedEvents` — LinkedID consistent across all CelEvents of same call
3. `CelTimestamps_ShouldBeMonotonicallyIncreasing` — EventTime with microsecond precision, each ≥ previous
4. `QueueCall_ShouldProduceQueueCelEvents` — SIPp → queue → verify QUEUE_ENTER in CEL timeline, Extra field contains queue name

### Key Details

- CdrEvent fires AFTER hangup (post-mortem, one per call)
- CelEvent fires MULTIPLE times during call (one per milestone)
- Timestamps are strings: CDR `"YYYY-MM-DD HH:MM:SS"`, CEL `"YYYY-MM-DD HH:MM:SS.ffffff"`
- `*AsDate` properties are NOT auto-populated — test must parse string properties
- Disposition and AmaFlags are raw strings, not enum-converted

---

## 5. AOT Trim CI Gate

### Problem

Zero automated AOT validation. Generated code is reflection-free, but there's no CI gate to catch regressions. The action serializer fallback `GetType().Name.Replace("Action", "")` uses reflection.

### Design

**AotCanary project** — minimal executable with `<ProjectReference>` to all 16 SDK .csproj files (builds from source, not NuGet):

```
tools/AotCanary/
  AotCanary.csproj    ← OutputType=Exe, PublishAot=true, refs all packages
  Program.cs           ← instantiates key types from each package
```

**Local script:** `tools/verify-aot.sh` — runs `dotnet publish` with AOT, fails on warnings

**GitHub Actions:** `.github/workflows/aot-trim-check.yml` — runs on push/PR, executes verify-aot.sh

---

## Summary

| Area | Tests | New files | New infra |
|------|-------|-----------|-----------|
| Source generators (L2) | 32 | 7 test files | — |
| Queue (L5 + SIPp) | 18 | 4 test files | SIPp Docker, scenarios, pjsip trunk |
| Health checks (L5) | 4 | 1 test file | — |
| CDR/CEL (L5 + SIPp) | 8 | 2 test files | cdr_manager.conf, cel_manager.conf |
| AOT trim CI | 0 | AotCanary project | verify-aot.sh, GitHub Actions workflow |
| **Total** | **62** | **14 test files** | |
