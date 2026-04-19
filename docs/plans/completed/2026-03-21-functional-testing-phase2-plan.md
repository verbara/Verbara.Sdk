# Functional Testing Phase 2: Pre-v1.0 — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build ~62 functional tests covering source generator pipeline, queue integration with SIPp, health check edge cases, CDR/CEL events, and an AOT trim CI gate.

**Architecture:** Extends the Phase 1 `Asterisk.Sdk.FunctionalTests` project with Layer 2 (no Docker) source generator tests and Layer 5 (Docker + SIPp) integration tests. Adds SIPp Docker service, CDR/CEL Asterisk configs, and an AOT canary project with local script + GitHub Actions workflow.

**Tech Stack:** .NET 10, xunit 2.9.3, FluentAssertions 7.1.0, NSubstitute 5.3.0, SIPp (ctaloi/sipp Docker image), Asterisk 21-alpine

**Spec:** `docs/superpowers/specs/2026-03-21-functional-testing-phase2-design.md`

---

## File Structure

```
Tests/Asterisk.Sdk.FunctionalTests/
  Layer2_UnitProtocol/
    SourceGenerators/
      AmiStringPoolTests.cs                    ← 3 tests
      EventDeserializerPipelineTests.cs        ← 8 tests
      ActionSerializerPipelineTests.cs         ← 6 tests
      ResponseDeserializerPipelineTests.cs     ← 4 tests
      GeneratorEdgeCaseTests.cs                ← 7 tests
      EventGeneratingActionTests.cs            ← 3 tests
      BulkRoundtripTests.cs                    ← 1 paramétric test
  Layer5_Integration/
    Queues/
      QueueMemberTests.cs                      ← 5 tests
      QueueCallerTests.cs                      ← 5 tests
      QueueCallFlowTests.cs                    ← 5 tests
      QueueMetricsTests.cs                     ← 3 tests
    HealthChecks/
      HealthCheckEdgeCaseTests.cs              ← 4 tests
    Cdr/
      CdrEventTests.cs                         ← 4 tests
      CelSequenceTests.cs                      ← 4 tests

docker/functional/
  docker-compose.functional.yml                ← MODIFY: add sipp service
  asterisk-config/
    pjsip.conf                                 ← MODIFY: add sipp-trunk endpoint
    cdr_manager.conf                           ← CREATE
    cel_manager.conf                           ← CREATE
  sipp-scenarios/
    basic-call.xml                             ← CREATE
    queue-caller.xml                           ← CREATE

tools/
  AotCanary/
    AotCanary.csproj                           ← CREATE
    Program.cs                                 ← CREATE
  verify-aot.sh                                ← CREATE

.github/workflows/
  aot-trim-check.yml                           ← CREATE
```

---

### Task 1: SIPp infrastructure + CDR/CEL config

**Files:**
- Modify: `docker/functional/docker-compose.functional.yml`
- Modify: `docker/functional/asterisk-config/pjsip.conf`
- Create: `docker/functional/asterisk-config/cdr_manager.conf`
- Create: `docker/functional/asterisk-config/cel_manager.conf`
- Create: `docker/functional/sipp-scenarios/basic-call.xml`
- Create: `docker/functional/sipp-scenarios/queue-caller.xml`

- [ ] **Step 1: Add cdr_manager.conf**

```ini
[general]
enabled = yes
```

- [ ] **Step 2: Add cel_manager.conf**

```ini
[general]
enable = yes
events = ALL
apps = dial,queue,confbridge,park
```

- [ ] **Step 3: Add SIPp trunk to pjsip.conf**

Append to existing pjsip.conf:
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

- [ ] **Step 4: Add SIPp service to docker-compose**

Add to `docker/functional/docker-compose.functional.yml`:
```yaml
  sipp:
    image: ctaloi/sipp
    container_name: functional-sipp
    depends_on:
      asterisk:
        condition: service_healthy
    network_mode: "service:asterisk"
    volumes:
      - ./sipp-scenarios:/sipp-scenarios
    entrypoint: ["sleep", "infinity"]
```

Note: SIPp runs as a sleeping container that tests invoke via `docker exec` with specific scenario parameters. This avoids SIPp exiting immediately.

- [ ] **Step 5: Create basic-call.xml SIPp scenario**

```xml
<?xml version="1.0" encoding="UTF-8"?>
<scenario name="Basic Call">
  <send retrans="500">
    <![CDATA[
      INVITE sip:[service]@[remote_ip]:[remote_port] SIP/2.0
      Via: SIP/2.0/[transport] [local_ip]:[local_port];branch=[branch]
      From: "SIPp" <sip:sipp@[local_ip]:[local_port]>;tag=[call_number]
      To: <sip:[service]@[remote_ip]:[remote_port]>
      Call-ID: [call_id]
      CSeq: 1 INVITE
      Contact: <sip:sipp@[local_ip]:[local_port]>
      Max-Forwards: 70
      Content-Type: application/sdp
      Content-Length: [len]

      v=0
      o=user1 53655765 2353687637 IN IP[local_ip_type] [local_ip]
      s=-
      c=IN IP[media_ip_type] [media_ip]
      t=0 0
      m=audio [media_port] RTP/AVP 0
      a=rtpmap:0 PCMU/8000
    ]]>
  </send>

  <recv response="100" optional="true" />
  <recv response="180" optional="true" />
  <recv response="183" optional="true" />
  <recv response="200" rtd="true" />

  <send>
    <![CDATA[
      ACK sip:[service]@[remote_ip]:[remote_port] SIP/2.0
      Via: SIP/2.0/[transport] [local_ip]:[local_port];branch=[branch]
      From: "SIPp" <sip:sipp@[local_ip]:[local_port]>;tag=[call_number]
      To: <sip:[service]@[remote_ip]:[remote_port]>[peer_tag_param]
      Call-ID: [call_id]
      CSeq: 1 ACK
      Contact: <sip:sipp@[local_ip]:[local_port]>
      Max-Forwards: 70
      Content-Length: 0
    ]]>
  </send>

  <pause milliseconds="3000"/>

  <send retrans="500">
    <![CDATA[
      BYE sip:[service]@[remote_ip]:[remote_port] SIP/2.0
      Via: SIP/2.0/[transport] [local_ip]:[local_port];branch=[branch]
      From: "SIPp" <sip:sipp@[local_ip]:[local_port]>;tag=[call_number]
      To: <sip:[service]@[remote_ip]:[remote_port]>[peer_tag_param]
      Call-ID: [call_id]
      CSeq: 2 BYE
      Contact: <sip:sipp@[local_ip]:[local_port]>
      Max-Forwards: 70
      Content-Length: 0
    ]]>
  </send>

  <recv response="200" crlf="true" />
</scenario>
```

- [ ] **Step 6: Create queue-caller.xml SIPp scenario**

Similar to basic-call.xml but calls extension 500 (Queue) and waits longer (15s) before BYE.

- [ ] **Step 7: Verify docker-compose is valid**

Run: `docker compose -f docker/functional/docker-compose.functional.yml config`

- [ ] **Step 8: Commit**

```bash
git add docker/functional/
git commit -m "feat(tests): add SIPp infrastructure and CDR/CEL config for Phase 2"
```

---

### Task 2: Source generator tests — AmiStringPool + EventDeserializer (Layer 2)

**Files:**
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer2_UnitProtocol/SourceGenerators/AmiStringPoolTests.cs`
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer2_UnitProtocol/SourceGenerators/EventDeserializerPipelineTests.cs`

- [ ] **Step 1: Write AmiStringPoolTests (3 tests)**

Tests:
1. `GetKey_ShouldReturnInternedString_ForKnownKey` — pass UTF8 bytes of a known AMI key (e.g., "Channel"), verify same reference returned on two calls
2. `GetKey_ShouldAllocateNewString_ForUnknownKey` — pass UTF8 bytes of a non-standard key, verify string returned but not reference-equal on second call
3. `GetValue_ShouldReturnInternedString_ForCommonValue` — pass UTF8 bytes of common value (e.g., "Success"), verify interning

Note: `AmiStringPool` is internal in `Asterisk.Sdk.Ami.Internal`. InternalsVisibleTo already set.
Note: Check the actual `AmiStringPool` API — methods may take `ReadOnlySpan<byte>` or `ReadOnlySequence<byte>`. Adapt tests to actual signature.

- [ ] **Step 2: Write EventDeserializerPipelineTests (8 tests)**

Tests:
1. `Deserialize_ShouldMapStringProperty` — create AmiMessage with string field, deserialize, verify property set
2. `Deserialize_ShouldMapNullableIntProperty` — field with integer value
3. `Deserialize_ShouldMapNullableLongProperty` — field with long value
4. `Deserialize_ShouldMapNullableBoolProperty` — field with "1" or "true"
5. `Deserialize_ShouldMapNullableDoubleProperty` — field with double value (e.g., Timestamp)
6. `Deserialize_ShouldHandleTwoLevelInheritance` — HangupEvent inherits from ChannelEventBase
7. `Deserialize_ShouldHandleThreeLevelInheritance` — BridgeCreateEvent → BridgeEventBase → ManagerEvent
8. `Deserialize_CdrEvent_ShouldMapAll18Fields` — CdrEvent with all AMI-mapped fields (NOT *AsDate)

Note: Must construct `AmiMessage` instances. Check how AmiMessage is created — it may be internal with no public constructor. If so, check if there's a factory or parse from raw bytes via `AmiProtocolReader`. Adapt approach accordingly.

- [ ] **Step 3: Build and run tests**

Run: `dotnet test Tests/Asterisk.Sdk.FunctionalTests/ --filter "FullyQualifiedName~SourceGenerators" --no-restore`

- [ ] **Step 4: Commit**

```bash
git add Tests/Asterisk.Sdk.FunctionalTests/Layer2_UnitProtocol/SourceGenerators/
git commit -m "test(functional): add AmiStringPool and EventDeserializer pipeline tests (11 tests)"
```

---

### Task 3: Source generator tests — ActionSerializer + ResponseDeserializer (Layer 2)

**Files:**
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer2_UnitProtocol/SourceGenerators/ActionSerializerPipelineTests.cs`
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer2_UnitProtocol/SourceGenerators/ResponseDeserializerPipelineTests.cs`

- [ ] **Step 1: Write ActionSerializerPipelineTests (6 tests)**

Tests:
1. `Serialize_PingAction_ShouldProduceNoFields` — PingAction has no properties to serialize
2. `Serialize_OriginateAction_ShouldIncludeAllSetProperties` — verify Channel, Application, Data, IsAsync serialized
3. `Serialize_ShouldOmitNullFields` — set only Channel, verify other fields absent
4. `Serialize_ShouldDistinguishEmptyStringFromNull` — empty string should serialize, null should not
5. `Serialize_ShouldIncludeExtraFields_ForIHasExtraFields` — OriginateAction.SetVariable("key","val") → Variable field
6. `GetActionName_ShouldReturnRegisteredName` — PingAction → "Ping", OriginateAction → "Originate"

Note: `GeneratedActionSerializer` is internal. Check actual method signatures. The `Serialize` method returns `IEnumerable<KeyValuePair<string, string>>`.

- [ ] **Step 2: Write ResponseDeserializerPipelineTests (4 tests)**

Tests:
1. `Deserialize_ShouldMapBaseResponseFields` — Response, Message, ActionId
2. `Deserialize_CommandResponse_ShouldExtractOutput` — AmiMessage with `__CommandOutput` field → CommandResponse.Output
3. `Deserialize_TypedResponse_ShouldParseNumericFields` — CoreSettingsResponse or similar with int/double fields
4. `Deserialize_UnknownAction_ShouldReturnBaseManagerResponse` — unknown action name → ManagerResponse

- [ ] **Step 3: Build and run tests**

Run: `dotnet test Tests/Asterisk.Sdk.FunctionalTests/ --filter "FullyQualifiedName~SourceGenerators" --no-restore`

- [ ] **Step 4: Commit**

```bash
git add Tests/Asterisk.Sdk.FunctionalTests/Layer2_UnitProtocol/SourceGenerators/
git commit -m "test(functional): add ActionSerializer and ResponseDeserializer pipeline tests (10 tests)"
```

---

### Task 4: Source generator tests — Edge cases + EventGeneratingAction + BulkRoundtrip (Layer 2)

**Files:**
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer2_UnitProtocol/SourceGenerators/GeneratorEdgeCaseTests.cs`
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer2_UnitProtocol/SourceGenerators/EventGeneratingActionTests.cs`
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer2_UnitProtocol/SourceGenerators/BulkRoundtripTests.cs`

- [ ] **Step 1: Write GeneratorEdgeCaseTests (7 tests)**

Tests:
1. `BooleanParsing_ShouldOnlyRecognize1AndTrue` — "0"→false, "1"→true, "true"→true, "false"→false, "yes"→false, null→null
2. `IntParsing_ShouldReturnNull_ForMalformedInput` — "abc" for int field → null
3. `LongParsing_ShouldReturnNull_ForOverflow` — very large number → null (or parsed if fits)
4. `DoubleParsing_ShouldHandleEdgeCases` — "NaN", "Infinity", "" → behavior documented
5. `WhitespaceInValues_ShouldBeParsedByTryParse` — " 42 " → 42 (TryParse trims) or null
6. `SpecialCharactersInValues_ShouldBePreserved` — unicode, quotes in string fields
7. `FieldNameLookup_ShouldBeCaseInsensitive` — "channel" vs "Channel" vs "CHANNEL"

- [ ] **Step 2: Write EventGeneratingActionTests (3 tests)**

Tests:
1. `SendEventGeneratingAction_ShouldCollectEvents` — mock or construct scenario where events are generated until *Complete
2. `ResponseEvent_ShouldCarryActionId` — events inheriting from ResponseEvent have ActionId correlated
3. `CompletionDetection_ShouldMatchEndsWith` — verify Complete suffix detection logic

Note: These may need to test via `AmiConnection.SendEventGeneratingActionAsync` or test the underlying `ResponseEventCollector`. Check if `ResponseEventCollector` is accessible. If testing requires a live connection, mark as integration tests. If it can be unit tested with mocked internals, keep in Layer 2.

- [ ] **Step 3: Write BulkRoundtripTests (1 paramétric test)**

```csharp
[Trait("Category", "Unit")]
public sealed class BulkRoundtripTests
{
    public static IEnumerable<object[]> AllEventMappings()
    {
        // Use reflection (OK in test code, not runtime) to discover all
        // classes with [AsteriskMapping] in Asterisk.Sdk.Ami.Events namespace
    }

    [Theory]
    [MemberData(nameof(AllEventMappings))]
    public void AllEvents_ShouldDeserializeToCorrectType(string eventName, Type expectedType)
    {
        // Create AmiMessage with Event: eventName
        // Deserialize via GeneratedEventDeserializer
        // Assert result is expectedType
    }

    // Similar for actions and responses
}
```

Note: Reflection is OK in test code. The test verifies that every [AsteriskMapping] class has a working roundtrip. This covers 100% of the 400+ types.

- [ ] **Step 4: Build and run all source generator tests**

Run: `dotnet test Tests/Asterisk.Sdk.FunctionalTests/ --filter "FullyQualifiedName~SourceGenerators" --no-restore`
Expected: All 32 tests pass.

- [ ] **Step 5: Commit**

```bash
git add Tests/Asterisk.Sdk.FunctionalTests/Layer2_UnitProtocol/SourceGenerators/
git commit -m "test(functional): add generator edge cases, event-generating action, and bulk roundtrip tests (11 tests)"
```

---

### Task 5: Queue member + caller tests (Layer 5)

**Files:**
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Queues/QueueMemberTests.cs`
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Queues/QueueCallerTests.cs`

- [ ] **Step 1: Write QueueMemberTests (5 tests)**

Tests:
1. `AddMember_ShouldUpdateQueueAndReverseIndex` — QueueAddAction → QueueManager.GetByName has member + GetQueuesForMember returns queue
2. `RemoveMember_ShouldCleanupReverseIndex` — QueueRemoveAction → member gone + reverse index cleaned
3. `PauseMember_ShouldUpdateStateAndFireEvent` — QueuePauseAction → member.Paused=true + MemberPaused event
4. `DeviceStateChange_ShouldPropagateToAllQueues` — add member to 3 queues via AMI, trigger DeviceStateChange, verify all 3 queues update member status
5. `MemberInMultipleQueues_ShouldTrackCorrectly` — add same interface to 3 queues → GetQueuesForMember returns all 3

Each test: create AmiConnection + AsteriskServer, StartAsync, send actions, verify QueueManager state.

Note: The DeviceStateChange test (#4) may need to originate a call to trigger a device state change, or use a different mechanism. Check how device state changes are generated in Asterisk.

Note: For queue operations, you'll need multiple queues. The functional `queues.conf` only has `test-queue`. You can either:
- Add more queues to queues.conf in this task
- Create queues dynamically (Asterisk doesn't support dynamic queue creation via AMI — queues must be defined in config)
For tests needing multiple queues, add `test-queue-2` and `test-queue-3` to `docker/functional/asterisk-config/queues.conf`.

- [ ] **Step 2: Write QueueCallerTests (5 tests)**

Tests:
1. `CallerJoin_ShouldAddEntryViaRawFields` — OriginateAction to ext 500 → QueueCallerJoinEvent → verify entry in QueueManager (Queue/Channel from RawFields)
2. `CallerLeave_ShouldRecordWaitTimeMetric` — join + hangup → MetricsCapture.Get("live.queue.wait_time") > 0
3. `CallerAbandon_ShouldFireEvent` — originate to queue with no members, wait for timeout → QueueCallerAbandonEvent observed (not consumed by QueueManager — document gap)
4. `QueueStatus_ShouldRebuildFullState` — add members, originate callers, send QueueStatusAction → verify complete state matches
5. `QueueSummary_ShouldReturnAccurateStats` — QueueSummaryAction → verify LoggedIn, Available, Callers counts

- [ ] **Step 3: Build**

Run: `dotnet build Tests/Asterisk.Sdk.FunctionalTests/ --no-restore`
Expected: 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Queues/ docker/functional/asterisk-config/queues.conf
git commit -m "test(functional): add queue member and caller tests (10 tests)"
```

---

### Task 6: Queue call flow + metrics tests (Layer 5 + SIPp)

**Files:**
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Queues/QueueCallFlowTests.cs`
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Queues/QueueMetricsTests.cs`
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Infrastructure/Helpers/SippControl.cs`

- [ ] **Step 1: Create SippControl helper**

Static helper similar to DockerControl for running SIPp scenarios:
```csharp
public static class SippControl
{
    public static async Task<SippResult> RunScenarioAsync(
        string scenarioFile,
        string targetExtension,
        int calls = 1,
        TimeSpan? timeout = null)
    {
        // docker exec functional-sipp sipp -sf /sipp-scenarios/{scenarioFile}
        //   -s {targetExtension} 127.0.0.1:5060 -m {calls} ...
    }
}
```

- [ ] **Step 2: Write QueueCallFlowTests (5 tests)**

Tests:
1. `SippCallToQueue_ShouldProduceFullEventSequence` — add member via QueueAddAction, run SIPp queue-caller → collect events → verify Join→AgentConnect→Complete→Leave sequence
2. `MultipleCallersInQueue_ShouldMaintainOrder` — 3 SIPp calls → verify position 1, 2, 3
3. `QueueTimeout_ShouldProduceAbandonEvent` — queue with no members, SIPp call → timeout → QueueCallerAbandon
4. `AgentAndQueueManager_ShouldCorrelateCallFlow` — verify both AsteriskServer.Queues and AsteriskServer.Agents updated during call
5. `QueueWithNoMembers_ShouldHandleGracefully` — SIPp call to empty queue → no crash, caller eventually leaves

Note: SIPp call flow tests are complex. If SIPp integration proves problematic, the tests can be adapted to use OriginateAction with Local channels as fallback.

- [ ] **Step 3: Write QueueMetricsTests (3 tests)**

Tests:
1. `CallerJoinLeave_ShouldIncrementCounters` — MetricsCapture for "Asterisk.Sdk.Live" → verify joined/left counters
2. `WaitTime_ShouldRecordHistogram` — caller waits in queue → histogram value > 0
3. `QueueGauges_ShouldReflectCurrentState` — verify live.queues.count gauge after QueueStatus

- [ ] **Step 4: Build**

Run: `dotnet build Tests/Asterisk.Sdk.FunctionalTests/ --no-restore`

- [ ] **Step 5: Commit**

```bash
git add Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Queues/ Tests/Asterisk.Sdk.FunctionalTests/Infrastructure/Helpers/SippControl.cs
git commit -m "test(functional): add queue call flow, metrics tests, and SIPp helper (8 tests)"
```

---

### Task 7: Health check edge case tests (Layer 5)

**Files:**
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/HealthChecks/HealthCheckEdgeCaseTests.cs`

- [ ] **Step 1: Write HealthCheckEdgeCaseTests (4 tests)**

Tests:
1. `AmiHealthCheck_ShouldReturnDegraded_DuringReconnect` — connect, kill Asterisk, poll health → transitions through Degraded/Unhealthy → restart → Healthy
2. `HealthCheck_ShouldNotHang_UnderHighLoad` — 50 concurrent health check invocations while 100 PingActions fire
3. `HealthCheck_ShouldReflectActualState_AfterReconnect` — full reconnect cycle → health check matches connection.State
4. `AllHealthChecks_ShouldBeRegistrable_ViaHostBuilder` — create IHost with AddAsterisk, resolve IHealthCheck services, verify 3 registered

Note: Health checks implement `IHealthCheck` from `Microsoft.Extensions.Diagnostics.HealthChecks`. To invoke them, create the health check instance and call `CheckHealthAsync()`. For test #4, use `IHost.Services.GetServices<IHealthCheck>()`.

Check the actual health check classes:
- `AmiHealthCheck` in `Asterisk.Sdk.Ami.Diagnostics`
- `AgiHealthCheck` in `Asterisk.Sdk.Agi.Diagnostics`
- `AriHealthCheck` in `Asterisk.Sdk.Ari.Diagnostics`

Verify how they're registered — they may need constructor dependencies (IAmiConnection, etc.).

- [ ] **Step 2: Build**

Run: `dotnet build Tests/Asterisk.Sdk.FunctionalTests/ --no-restore`

- [ ] **Step 3: Commit**

```bash
git add Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/HealthChecks/
git commit -m "test(functional): add health check edge case tests (4 tests)"
```

---

### Task 8: CDR/CEL tests (Layer 5 + SIPp)

**Files:**
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Cdr/CdrEventTests.cs`
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Cdr/CelSequenceTests.cs`

- [ ] **Step 1: Write CdrEventTests (4 tests)**

Tests:
1. `SippCall_ShouldProduceCdrEvent_WithCorrectFields` — SIPp basic call → subscribe for CdrEvent → verify Src, Destination, Channel, Duration>0, BillableSeconds>0, Disposition="ANSWERED"
2. `UnansweredCall_ShouldProduceCdr_WithNoAnswerDisposition` — originate to non-existent extension or SIPp with no answer → Disposition="NO ANSWER" or "FAILED", BillableSeconds=0
3. `CdrTimestamps_ShouldBeChronologicalStrings` — parse StartTime, AnswerTime, EndTime strings with DateTimeOffset.TryParse → verify StartTime ≤ AnswerTime ≤ EndTime
4. `CdrDisposition_ShouldMatchCallOutcome` — multiple scenarios (answered, unanswered) → verify correct disposition string

Note: CdrEvent fires AFTER hangup (post-mortem). Subscribe before making the call, then wait with timeout for the event. CdrEvent.StartTime format: "YYYY-MM-DD HH:MM:SS".

Note: Requires `cdr_manager.conf` in Asterisk config (created in Task 1).

- [ ] **Step 2: Write CelSequenceTests (4 tests)**

Tests:
1. `SippCall_ShouldProduceCompleteCelTimeline` — collect all CelEvents during a SIPp call → verify EventName sequence includes CHAN_START, ANSWER, HANGUP, CHAN_END, LINKEDID_END
2. `CelLinkedId_ShouldCorrelateRelatedEvents` — all CelEvents for same call share LinkedID
3. `CelTimestamps_ShouldBeMonotonicallyIncreasing` — EventTime with microseconds, each event's timestamp ≥ previous
4. `QueueCall_ShouldProduceQueueCelEvents` — SIPp call to queue → verify CEL sequence includes queue-related events

Note: CelEvent fires multiple times per call. Collect via Subscribe. CEL EventTime format: "YYYY-MM-DD HH:MM:SS.ffffff".

Note: Requires `cel_manager.conf` with `events = ALL` and `apps = dial,queue` (created in Task 1).

- [ ] **Step 3: Build**

Run: `dotnet build Tests/Asterisk.Sdk.FunctionalTests/ --no-restore`

- [ ] **Step 4: Commit**

```bash
git add Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Cdr/
git commit -m "test(functional): add CDR and CEL event tests (8 tests)"
```

---

### Task 9: AOT Canary project + scripts + CI

**Files:**
- Create: `tools/AotCanary/AotCanary.csproj`
- Create: `tools/AotCanary/Program.cs`
- Create: `tools/verify-aot.sh`
- Create: `.github/workflows/aot-trim-check.yml`

- [ ] **Step 1: Create AotCanary.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <PublishAot>true</PublishAot>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Asterisk.Sdk\Asterisk.Sdk.csproj" />
    <ProjectReference Include="..\..\src\Asterisk.Sdk.Ami\Asterisk.Sdk.Ami.csproj" />
    <ProjectReference Include="..\..\src\Asterisk.Sdk.Agi\Asterisk.Sdk.Agi.csproj" />
    <ProjectReference Include="..\..\src\Asterisk.Sdk.Ari\Asterisk.Sdk.Ari.csproj" />
    <ProjectReference Include="..\..\src\Asterisk.Sdk.Live\Asterisk.Sdk.Live.csproj" />
    <ProjectReference Include="..\..\src\Asterisk.Sdk.Activities\Asterisk.Sdk.Activities.csproj" />
    <ProjectReference Include="..\..\src\Asterisk.Sdk.Config\Asterisk.Sdk.Config.csproj" />
    <ProjectReference Include="..\..\src\Asterisk.Sdk.Hosting\Asterisk.Sdk.Hosting.csproj" />
    <ProjectReference Include="..\..\src\Asterisk.Sdk.Sessions\Asterisk.Sdk.Sessions.csproj" />
    <ProjectReference Include="..\..\src\Asterisk.Sdk.Audio\Asterisk.Sdk.Audio.csproj" />
    <ProjectReference Include="..\..\src\Asterisk.Sdk.VoiceAi\Asterisk.Sdk.VoiceAi.csproj" />
    <ProjectReference Include="..\..\src\Asterisk.Sdk.VoiceAi.AudioSocket\Asterisk.Sdk.VoiceAi.AudioSocket.csproj" />
    <ProjectReference Include="..\..\src\Asterisk.Sdk.VoiceAi.Stt\Asterisk.Sdk.VoiceAi.Stt.csproj" />
    <ProjectReference Include="..\..\src\Asterisk.Sdk.VoiceAi.Tts\Asterisk.Sdk.VoiceAi.Tts.csproj" />
    <ProjectReference Include="..\..\src\Asterisk.Sdk.VoiceAi.Testing\Asterisk.Sdk.VoiceAi.Testing.csproj" />
    <ProjectReference Include="..\..\src\Asterisk.Sdk.VoiceAi.OpenAiRealtime\Asterisk.Sdk.VoiceAi.OpenAiRealtime.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create Program.cs**

Minimal program that instantiates key types from each package to ensure the linker sees them:
```csharp
// This program exists solely to verify AOT trim safety.
// It references types from all SDK packages so the linker processes them.
using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Connection;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Events;
using Asterisk.Sdk.Agi.Server;
// ... etc for all packages

Console.WriteLine("AOT Canary - all SDK types are AOT-safe");
// Reference key types to prevent them from being trimmed
_ = typeof(AmiConnectionOptions);
_ = typeof(PingAction);
_ = typeof(NewChannelEvent);
_ = typeof(FastAgiServer);
// ... key types from each package
```

- [ ] **Step 3: Create verify-aot.sh**

```bash
#!/usr/bin/env bash
set -euo pipefail
echo "Verifying AOT trim safety..."
dotnet publish tools/AotCanary/AotCanary.csproj \
  -c Release \
  --nologo \
  -v quiet \
  /p:PublishAot=true \
  /warnaserror 2>&1
echo "AOT verification passed — 0 trim warnings"
```

- [ ] **Step 4: Create aot-trim-check.yml**

```yaml
name: AOT Trim Check

on:
  push:
    branches: [main]
  pull_request:
    branches: [main]

jobs:
  aot-check:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - name: Verify AOT trim safety
        run: bash tools/verify-aot.sh
```

- [ ] **Step 5: Verify AOT locally**

Run: `bash tools/verify-aot.sh`
Expected: "AOT verification passed — 0 trim warnings"

Note: This requires .NET 10 AOT publishing support on the machine. If it fails due to missing AOT prerequisites (like clang/llvm), document the requirement and ensure the GitHub Actions runner has them.

- [ ] **Step 6: Commit**

```bash
git add tools/AotCanary/ tools/verify-aot.sh .github/workflows/aot-trim-check.yml
git commit -m "feat(ci): add AOT trim verification canary project and CI gate"
```

---

### Task 10: Final verification

- [ ] **Step 1: Run all Layer 2 (unit) tests**

```bash
dotnet test Tests/Asterisk.Sdk.FunctionalTests/ --filter "Category=Unit" --no-restore
```
Expected: ~36 tests pass (4 from Phase 1 + 32 from Phase 2)

- [ ] **Step 2: Verify full solution builds**

```bash
dotnet build Asterisk.Sdk.slnx --no-restore
```
Expected: 0 warnings, 0 errors.

- [ ] **Step 3: Run all existing tests (non-functional)**

```bash
dotnet test Asterisk.Sdk.slnx --no-restore --filter "FullyQualifiedName!~FunctionalTests&FullyQualifiedName!~IntegrationTests&FullyQualifiedName!~Spike&FullyQualifiedName!~Benchmarks"
```
Expected: All existing unit tests pass (974+). 0 regressions.

- [ ] **Step 4: Commit any fixes**

If any adjustments were needed, commit them.
