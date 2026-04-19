# Phase 5C — Soak & Metrics Testing Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add 10 tests validating memory stability under sustained load and metrics counter accuracy.

**Architecture:** Soak tests use `AsyncEventPump` and `ChannelManager` directly (no Docker needed) with synthetic events to verify no memory leaks or counter drift after 10K+ cycles. Metrics tests use `MetricsCapture` (MeterListener) with live AMI connections to verify SDK counters match actual operations. Split into Layer 2 (unit, no Docker) and Layer 5 (integration, needs Docker).

**Tech Stack:** xunit, FluentAssertions, System.Diagnostics.Metrics, MetricsCapture helper

---

## File Structure

```
Tests/Asterisk.Sdk.FunctionalTests/
  Layer2_UnitProtocol/
    Soak/
      EventPumpSoakTests.cs              ← Task 1: 3 tests (no Docker)
      ChannelManagerSoakTests.cs         ← Task 2: 3 tests (no Docker)
  Layer5_Integration/
    Metrics/
      AmiMetricsTests.cs                 ← Task 3: 4 tests (needs Docker)
```

---

## Task 1: Event Pump Soak Tests (3 tests, no Docker)

**Files:**
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer2_UnitProtocol/Soak/EventPumpSoakTests.cs`

These tests exercise `AsyncEventPump` with 10K+ synthetic events. No AMI connection needed.

- [ ] **Step 1: Write 3 soak tests**

```csharp
namespace Asterisk.Sdk.FunctionalTests.Layer2_UnitProtocol.Soak;

using Asterisk.Sdk.Ami.Internal;
using FluentAssertions;

[Trait("Category", "Soak")]
public sealed class EventPumpSoakTests : IAsyncDisposable
{
    private readonly AsyncEventPump _pump = new(capacity: 20_000);

    [Fact]
    public async Task ProcessTenThousandEvents_ShouldNotLeak()
    {
        const int eventCount = 10_000;
        long processed = 0;

        _pump.Start(_ =>
        {
            Interlocked.Increment(ref processed);
            return ValueTask.CompletedTask;
        });

        for (var i = 0; i < eventCount; i++)
        {
            var evt = new Asterisk.Sdk.ManagerEvent { EventType = $"TestEvent-{i}" };
            _pump.TryEnqueue(evt);
        }

        // Wait for all events to be processed
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Interlocked.Read(ref processed) < eventCount && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        _pump.ProcessedEvents.Should().BeGreaterThanOrEqualTo(eventCount,
            "all 10K events must be processed without loss");
        _pump.DroppedEvents.Should().Be(0,
            "no events should be dropped at normal throughput");
        _pump.PendingCount.Should().Be(0,
            "pump must drain completely");
    }

    [Fact]
    public async Task RepeatedEnqueueDrain_ShouldMaintainStableMemory()
    {
        const int batchSize = 1_000;
        const int batches = 10;
        long processed = 0;

        _pump.Start(_ =>
        {
            Interlocked.Increment(ref processed);
            return ValueTask.CompletedTask;
        });

        // Force GC baseline
        GC.Collect(2, GCCollectionMode.Forced, true);
        GC.WaitForPendingFinalizers();
        var baselineMemory = GC.GetTotalMemory(true);

        for (var batch = 0; batch < batches; batch++)
        {
            for (var i = 0; i < batchSize; i++)
            {
                _pump.TryEnqueue(new Asterisk.Sdk.ManagerEvent { EventType = "SoakEvent" });
            }

            // Wait for batch to drain
            var target = (batch + 1) * batchSize;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Interlocked.Read(ref processed) < target && DateTime.UtcNow < deadline)
                await Task.Delay(20);
        }

        GC.Collect(2, GCCollectionMode.Forced, true);
        GC.WaitForPendingFinalizers();
        var finalMemory = GC.GetTotalMemory(true);

        var growth = finalMemory - baselineMemory;
        growth.Should().BeLessThan(10 * 1024 * 1024,
            "memory growth after 10K events in 10 batches must stay under 10MB");
    }

    [Fact]
    public async Task DroppedEventsCounter_ShouldBeAccurateUnderPressure()
    {
        // Small capacity pump
        using var smallPump = new AsyncEventPump(capacity: 100);
        var slowProcessing = new SemaphoreSlim(0); // Block consumer

        smallPump.Start(async _ =>
        {
            await slowProcessing.WaitAsync(); // Consumer blocks until released
        });

        // Fill the pump beyond capacity
        var enqueued = 0;
        var dropped = 0;
        for (var i = 0; i < 200; i++)
        {
            if (smallPump.TryEnqueue(new Asterisk.Sdk.ManagerEvent { EventType = "Pressure" }))
                enqueued++;
            else
                dropped++;
        }

        // Some should have been dropped
        dropped.Should().BeGreaterThan(0, "overflowing a small pump must drop events");
        smallPump.DroppedEvents.Should().Be(dropped,
            "DroppedEvents counter must match actual drops");

        // Release all blocked consumers to allow cleanup
        slowProcessing.Release(enqueued);
        await Task.Delay(500);
    }

    public async ValueTask DisposeAsync()
    {
        await _pump.DisposeAsync();
    }
}
```

- [ ] **Step 2: Build and run**

Run: `dotnet build Tests/Asterisk.Sdk.FunctionalTests/ && dotnet test Tests/Asterisk.Sdk.FunctionalTests/ --filter "FullyQualifiedName~EventPumpSoakTests" -v q`
Expected: 3 tests pass (no Docker needed)

- [ ] **Step 3: Commit**

```bash
git add Tests/Asterisk.Sdk.FunctionalTests/Layer2_UnitProtocol/Soak/EventPumpSoakTests.cs
git commit -m "test(soak): add AsyncEventPump soak tests — 10K events, memory stability, drop accuracy (3 tests)"
```

---

## Task 2: Channel Manager Soak Tests (3 tests, no Docker)

**Files:**
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer2_UnitProtocol/Soak/ChannelManagerSoakTests.cs`

These tests exercise `ChannelManager` with synthetic channel events. The manager has public `OnNewChannel`/`OnHangup` handlers.

- [ ] **Step 1: Write 3 tests**

Tests:
1. `CreateAndDestroyThousandChannels_ShouldReturnToZero` — Create 1000 channels, hangup all, assert ChannelCount == 0
2. `ConcurrentChannelOperations_ShouldMaintainConsistency` — 10 parallel tasks each creating/destroying 100 channels, assert final count matches
3. `RepeatedClearCycles_ShouldNotLeak` — 100 cycles of create-10-channels → Clear() → verify count=0, check memory stable

The subagent must read `ChannelManager.cs` to understand the exact `OnNewChannel` signature and required parameters.

- [ ] **Step 2: Build and run**

Run: `dotnet test Tests/Asterisk.Sdk.FunctionalTests/ --filter "FullyQualifiedName~ChannelManagerSoakTests" -v q`
Expected: 3 tests pass

- [ ] **Step 3: Commit**

```bash
git add Tests/Asterisk.Sdk.FunctionalTests/Layer2_UnitProtocol/Soak/ChannelManagerSoakTests.cs
git commit -m "test(soak): add ChannelManager soak tests — 1K channels, concurrency, clear cycles (3 tests)"
```

---

## Task 3: AMI Metrics Integration Tests (4 tests, needs Docker)

**Files:**
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Metrics/AmiMetricsTests.cs`

These need a live AMI connection (functional Asterisk container). Use `MetricsCapture("Asterisk.Sdk.Ami")` from `FunctionalTestBase`.

- [ ] **Step 1: Write 4 tests**

Tests:
1. `ActionsSent_ShouldIncrementOnSendAction` — Send 5 actions, assert `ami.actions.sent` >= 5
2. `EventsReceived_ShouldIncrementOnIncomingEvents` — Originate a call, assert `ami.events.received` > 0
3. `EventsDispatched_ShouldIncrementWhenObserverPresent` — Subscribe + originate, assert `ami.events.dispatched` > 0
4. `ActionRoundtrip_ShouldRecordHistogram` — Send a PingAction, assert `ami.action.roundtrip` > 0

Pattern: `[AsteriskContainerFact]`, inherit `FunctionalTestBase`, use `MetricsCapture`.

- [ ] **Step 2: Build and run**

Tests pass if Docker Asterisk is running (port 5038), skip otherwise.

- [ ] **Step 3: Commit**

```bash
git add Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Metrics/AmiMetricsTests.cs
git commit -m "test(metrics): add AMI metrics integration tests — counters and histogram (4 tests)"
```

---

## Task 4: Final Verification + Roadmap

- [ ] **Step 1:** Full build: `dotnet build Asterisk.Sdk.slnx` — 0 warnings
- [ ] **Step 2:** Run soak tests: `dotnet test Tests/Asterisk.Sdk.FunctionalTests/ --filter "Category=Soak" -v q`
- [ ] **Step 3:** Update roadmap — mark Phase 5C complete

---

## Summary

| Task | Tests | Layer | Docker? |
|------|-------|-------|---------|
| 1. EventPump soak | 3 | Layer 2 (unit) | No |
| 2. ChannelManager soak | 3 | Layer 2 (unit) | No |
| 3. AMI metrics | 4 | Layer 5 (integration) | Yes |
| 4. Verification | 0 | — | — |
| **Total** | **10** | | |
