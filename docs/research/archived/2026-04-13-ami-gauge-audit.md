# AMI ObservableGauge Audit — Reconnect Leak Verification

**Date:** 2026-04-13
**Sprint:** Asterisk.Sdk v1.6.0 Sprint 1, Task A3
**Spec referenced:** `2026-03-16 next-level-design.md` — "AMI: fix observable gauge leak on reconnect"
**Files audited:**
- `src/Asterisk.Sdk.Ami/Diagnostics/AmiMetrics.cs` (60 lines)
- `src/Asterisk.Sdk.Ami/Connection/AmiConnection.cs` (760 lines)

---

## 1. ObservableGauge Inventory

| # | Name | What it measures | Callback (file:line) | Captured state |
|---|------|------------------|----------------------|----------------|
| 1 | `ami.event_pump.pending` | Events queued in the async event pump buffer | `AmiConnection.cs:149-150` | Reads instance field `_eventPump?.PendingCount ?? 0` via `this` closure |
| 2 | `ami.pending_actions`    | AMI actions awaiting a response                | `AmiConnection.cs:151-152` | Reads instance field `_pendingActions.Count` via `this` closure |

Both gauges are registered on the **static** `AmiMetrics.Meter` (`AmiMetrics.cs:14`), inside `ConnectAsync` (`AmiConnection.cs:147-154`), guarded by the instance flag `_gaugesRegistered` (`AmiConnection.cs:90`).

`AmiMetrics` itself contains:
- 6 `Counter<long>` (events received/dropped/dispatched, actions sent, responses received, reconnections)
- 2 `Histogram<double>` (action roundtrip ms, event dispatch ms)
- 0 other gauges

No other `Meter.CreateObservableGauge` call exists in `Asterisk.Sdk.Ami`.

---

## 2. Verdict: **No reconnect-loop leak in v1.5.5 — but a latent disposal leak exists**

### 2.1 Is there a stale-state leak across reconnects of the same instance? **FALSE.**

The original bug pattern (gauges registered on every `ConnectAsync`, accumulating duplicate observers on the singleton `Meter`) **was fixed**. Evidence at `AmiConnection.cs:146-154`:

```csharp
// Register observable gauges only once (avoid accumulation on reconnect)
if (!_gaugesRegistered)
{
    AmiMetrics.Meter.CreateObservableGauge("ami.event_pump.pending",
        () => _eventPump?.PendingCount ?? 0, ...);
    AmiMetrics.Meter.CreateObservableGauge("ami.pending_actions",
        () => _pendingActions.Count, ...);
    _gaugesRegistered = true;
}
```

The flag is set on first `ConnectAsync` and never reset (not even in `CleanupAsync`/`DisconnectAsync`), so subsequent reconnects of the same `AmiConnection` instance do not re-register.

### 2.2 Do the closures read fresh state after reconnect? **YES, by design.**

- **`_pendingActions`** (`AmiConnection.cs:82`) is `readonly` — the `ConcurrentDictionary` reference is stable for the instance lifetime. `CleanupAsync` only calls `.Clear()` (`AmiConnection.cs:687`); it never reassigns. The gauge closure dereferences `this._pendingActions.Count` and always sees the live dict. ✓
- **`_eventPump`** (`AmiConnection.cs:76`) is mutable: reassigned in `ConnectAsync:136` and nulled in `CleanupAsync:666`. The closure `() => _eventPump?.PendingCount ?? 0` reads the **field** via `this`, not a captured local, so each callback observation re-reads the current value. The null-conditional handles the brief reconnect window. ✓

### 2.3 Latent leak: gauges hold a reference to `AmiConnection` forever

`AmiMetrics.Meter` is `static`. Both gauge callbacks capture `this` (via the implicit `_eventPump` / `_pendingActions` field reads). The `Meter` retains the gauge registrations for its entire lifetime (process lifetime). Therefore:

> Once `AmiConnection.ConnectAsync` succeeds, the instance can **never** be garbage-collected, even after `DisposeAsync()`.

This is **not** a per-reconnect leak (which the spec described and which is fixed). It is a **per-instance leak** that surfaces if the application creates AMI connections dynamically (e.g., per-tenant hot-swap, per-test instance, cluster node failover spawning new connections). For singleton DI usage it is harmless.

`AmiMetrics` itself has no `Dispose` path — the static `Meter` is never disposed (acceptable for a process-wide metrics source, but it means gauge unregistration must happen explicitly via the `IDisposable` returned by `CreateObservableGauge`, which the current code discards).

---

## 3. Recommended fix (latent disposal leak)

Capture the `IDisposable` returned by `CreateObservableGauge` and dispose it in `DisposeAsync` (not `CleanupAsync`, which runs on every reconnect). Also flip the closures to weak indirection so the `Meter` doesn't pin `this`.

**Proposed change** (sketch, do not apply in this task):

```csharp
private IDisposable? _eventPumpGauge;
private IDisposable? _pendingActionsGauge;

// in ConnectAsync, replace the existing gauge block:
if (!_gaugesRegistered)
{
    var weakSelf = new WeakReference<AmiConnection>(this);
    _eventPumpGauge = AmiMetrics.Meter.CreateObservableGauge<long>(
        "ami.event_pump.pending",
        () => weakSelf.TryGetTarget(out var s) ? (s._eventPump?.PendingCount ?? 0) : 0,
        description: "Events pending in the event pump buffer");
    _pendingActionsGauge = AmiMetrics.Meter.CreateObservableGauge<long>(
        "ami.pending_actions",
        () => weakSelf.TryGetTarget(out var s) ? s._pendingActions.Count : 0,
        description: "Actions awaiting response");
    _gaugesRegistered = true;
}

// in DisposeAsync (after DisconnectAsync):
_eventPumpGauge?.Dispose();
_pendingActionsGauge?.Dispose();
_eventPumpGauge = null;
_pendingActionsGauge = null;
```

Effect:
- Disposing the gauges removes them from the `Meter`'s instrument list (System.Diagnostics.Metrics supports this since .NET 8).
- `WeakReference<AmiConnection>` allows GC to reclaim the instance even if a listener holds the gauge briefly.
- `_gaugesRegistered` stays `true` per instance, so reconnects still don't double-register.

**Risk assessment for v1.6.0:** LOW. Pure additive change; only affects long-lived processes that churn `AmiConnection` instances (which today is rare — `IAmiConnection` is typically registered as singleton in DI). Defer to v1.6.x patch unless Sprint 1 explicitly wants to ship it.

---

## 4. Regression test for the **already-fixed** reconnect-stale-state bug

The current code is correct, but Sprint 1 should add a regression test so future refactors don't reintroduce the bug. Use the existing `DockerAsterisk` functional test fixture pattern.

**File:** `tests/Asterisk.Sdk.Ami.FunctionalTests/AmiReconnectGaugeTests.cs`

```csharp
using System.Diagnostics.Metrics;

[Collection(DockerAsteriskCollection.Name)]
public sealed class AmiReconnectGaugeTests(DockerAsteriskFixture fx)
{
    [Fact]
    public async Task ObservableGauges_ShouldReadLiveState_AfterMultipleReconnects()
    {
        await using var conn = fx.CreateAmiConnection(autoReconnect: true);

        // MeterListener captures gauge values on demand
        var pendingActionsObservations = new List<long>();
        var eventPumpObservations = new List<long>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (inst, l) =>
            {
                if (inst.Meter.Name == "Asterisk.Sdk.Ami" &&
                    (inst.Name == "ami.pending_actions" || inst.Name == "ami.event_pump.pending"))
                    l.EnableMeasurementEvents(inst);
            }
        };
        listener.SetMeasurementEventCallback<long>((inst, val, _, _) =>
        {
            if (inst.Name == "ami.pending_actions") pendingActionsObservations.Add(val);
            else if (inst.Name == "ami.event_pump.pending") eventPumpObservations.Add(val);
        });
        listener.Start();

        await conn.ConnectAsync();

        for (var cycle = 0; cycle < 3; cycle++)
        {
            // Force a reconnect by killing the underlying socket
            await fx.DropAmiSocketAsync(conn);
            await fx.WaitForReconnectAsync(conn, TimeSpan.FromSeconds(10));

            // Issue an in-flight action so _pendingActions briefly > 0
            var pingTask = conn.SendActionAsync(new PingAction());

            // Sample gauges — values must be observable (not stuck at 0 forever)
            pendingActionsObservations.Clear();
            eventPumpObservations.Clear();
            listener.RecordObservableInstruments();

            await pingTask;

            pendingActionsObservations.Should().NotBeEmpty(
                $"cycle {cycle}: gauge must respond to live _pendingActions dict, not a stale reference");
            eventPumpObservations.Should().NotBeEmpty(
                $"cycle {cycle}: gauge must respond to live _eventPump, not a disposed one");
        }

        // Sanity: gauges registered exactly once across 3 reconnects
        // (no way to introspect Meter directly; assert via no duplicate observations per call)
        pendingActionsObservations.Should().HaveCount(1,
            "gauge must be registered exactly once — duplicate registration would emit N observations per Record call");
    }
}
```

**Helpers required on `DockerAsteriskFixture`** (likely already present from existing reconnect tests, verify in A4):
- `CreateAmiConnection(bool autoReconnect)`
- `DropAmiSocketAsync(IAmiConnection)` — issues `iptables -A INPUT -p tcp --dport 5038 -j REJECT` inside the container, or kills the TCP session via Asterisk CLI
- `WaitForReconnectAsync(IAmiConnection, TimeSpan)` — polls `conn.State == Connected` after a `Reconnected` event

If those helpers don't exist yet, A4 (functional reconnect coverage) is the natural place to add them.

---

## 5. Summary

| Question | Answer |
|----------|--------|
| Does the gauge-leak-on-reconnect bug from `next-level-design.md` still exist in v1.5.5? | **No.** Fixed via `_gaugesRegistered` guard at `AmiConnection.cs:147`. |
| Do callbacks read stale state after reconnect? | **No.** Closures dereference instance fields each invocation, not captured locals; `_pendingActions` is `readonly`, `_eventPump` is null-checked. |
| Is there any related leak? | **Yes, latent.** Static `Meter` pins the `AmiConnection` instance via the gauge closures; the returned `IDisposable` is discarded so gauges are never unregistered. Harmless for singleton DI; problematic for multi-instance churn. |
| Action for Sprint 1? | (a) Add the regression test in §4 to lock in the fix. (b) Optionally schedule the §3 disposal fix for v1.6.x — small, additive, low risk. |

No code changes were made by this task.
