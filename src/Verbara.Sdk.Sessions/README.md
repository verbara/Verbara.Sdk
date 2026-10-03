# Verbara.Sdk.Sessions

Session Engine for the Verbara.Sdk ecosystem. Provides call session correlation, lifecycle state machines, and domain events for real-time telephony monitoring.

## Features

- **CallSession** - Models the full lifecycle of a call: Created, Dialing, Ringing, Queued, Connected, OnHold, Transferring, Conference, Completed, Failed, TimedOut
- **CallSessionManager** - Automatic session creation and correlation by LinkedId, with 4-tier O(1) indexing
- **SessionReconciler** - Orphan detection and timeout handling for abandoned sessions
- **Domain Events** - Observable stream of CallStarted, CallConnected, CallQueued, CallHeld, CallEnded, CallFailed events
- **Extension Points** - Abstract base classes for custom routing (CallRouterBase), agent selection (AgentSelectorBase), and persistence (SessionStoreBase)
- **SessionMetrics** - System.Diagnostics.Metrics counters for sessions created, completed, failed, timed out
- **Resident-count gauges** - `sessions.active` (calls in progress) and `sessions.retained` (ended calls still held), published by each `CallSessionManager` under the `Verbara.Sdk.Sessions` meter name and withdrawn when it is disposed

## Quick Start

```csharp
services.AddVerbara(options => { /* AMI config */ });
services.AddVerbaraSessions(options =>
{
    options.InboundContextPatterns = ["from-external"];
    options.OutboundContextPatterns = ["from-internal"];
});

var sessionManager = app.Services.GetRequiredService<ICallSessionManager>();
sessionManager.Events.Subscribe(evt => Console.WriteLine(evt));
```

## Queue metrics

`IQueueSessionTracker` keeps one `QueueSession` per queue. Its counters cover a rolling window
(`SessionOptions.QueueMetricsWindow`, 30 minutes by default, from `WindowStart`); `CallsWaiting` is the present
moment and is not reset by a new window. The unit is the **queue visit**: a caller's join, until the queue connects it
or it leaves. A caller the queue puts back (a dialplan that loops into `Queue()` again) makes a new visit each time.
Each visit is counted when Asterisk reports what happened to it.

| Counter | What it counts |
|---|---|
| `CallsOffered` | Visits that started: one per join. |
| `CallsAnswered` | Visits the queue connected to a member (`AgentConnect`). |
| `CallsAbandoned` | Visits app_queue counts abandoned, as its own `Abandoned`: the caller hung up while waiting, or the queue let it go without a connection — its timeout, the queue emptying, a withdrawal, a redirect. Counted at app_queue's abandon report (`QueueCallerAbandon`), just before the caller's leave. Includes `CallsTimedOut`. A caller that leaves with the queue's exit key is not abandoned: Asterisk counts it neither answered nor abandoned, and so does the tracker. |
| `CallsTimedOut` | The part of `CallsAbandoned` that `Queue()`'s own timeout ended (`QUEUESTATUS` = `TIMEOUT`). Needs the `dialplan` class, below; without it, 0. |
| `CallsAbandoned − CallsTimedOut` | Callers that hung up, or that the queue let go for another reason. |
| `CallsWaiting` | Callers in the queue now. Asterisk's leave ends the wait, at every exit: a connection, a hang-up, a timeout, an emptied queue, a withdrawal, a redirect or a key. |
| `CallsWithinSla`, `TotalWaitTime`, `MinWaitTime`, `MaxWaitTime` | Answered visits only, each wait from the visit's start to its connection. |

**What changed after 2.6.1.** `CallsTimedOut` was never counted and now follows Asterisk's `EXITWITHTIMEOUT`.
`CallsWaiting` dropped only when the caller was connected, hung up or joined again; it now drops at the leave.
`CallsAbandoned` was counted when the caller hung up or joined again, and included key exits; it now moves at
app_queue's abandon report and excludes them, so `CallsAbandoned` and `AbandonRate` are lower by the key exits.
Measured 2026-10-03 on Asterisk 20.20.1, 22.9.0 and 23.4.1, 320 calls per version over every way of leaving a queue:
after each call (each burst of ten, for the bursts), `CallsAbandoned` moved by app_queue's `Abandoned` and
`CallsTimedOut` by its `EXITWITHTIMEOUT` count, and `CallsWaiting` equalled app_queue's `Calls` 700 ms after each of
the 140 leaves sampled per version.

### `CallsTimedOut` needs the `dialplan` class

app_queue reports a timeout only by setting `QUEUESTATUS` on the caller's channel, which Asterisk sends as a
`VarSet` event, and only to an AMI user whose `read` includes `dialplan` (`read = all` includes it). Without it
nothing breaks: a timed-out visit is still counted in `CallsAbandoned`, and `CallsTimedOut` stays 0. The class
carries every dialplan variable assignment and extension step, so it is not free: measured on a test dialplan, 44 %
to 51 % of all the events a `read = all` user received. Behind the `manager.conf` filter in
[Missing events](../../docs/guides/troubleshooting.md#the-cost-of-dialplan-and-a-filter-that-removes-it), which
passes only the `QUEUESTATUS` the tracker needs, it cost 1.28 % more bytes than no `dialplan` at all.

A timeout that happens while the AMI connection is down is not counted in `CallsTimedOut`; the visit is still counted
abandoned. When a metrics window ends between a visit's abandon report and its `QUEUESTATUS` (a moment apart), the
two land in different windows.

### The key exit needs an unbroken event stream

A key exit is a leave with no abandon report before it. The tracker can tell it from an abandon whose report it
missed only while it has lost no event since the visit started. After a reconnect, or after the AMI connection's
event buffer dropped an event (`ami.events.dropped`, `reason=buffer_full`), a leave with no report is counted
abandoned when the caller joins a queue again or hangs up, unless the queue connects it first. Over an
`IAmiConnection` other than `AmiConnection` only reconnects are seen.

A caller whose leave fell inside an outage stops counting as waiting when the reload's queue snapshot (`QueueStatus`)
completes without it, and is counted abandoned there; a reload whose snapshot did not complete closes nothing. An
answer that happened during the outage cannot be observed, so such a visit counts abandoned too. Visits that both
started and ended inside an outage are not seen at all.

### Rates

| Rate | Formula | When nothing was offered |
|---|---|---|
| `ServiceLevel` | `CallsWithinSla` / `CallsOffered` × 100 | 100 |
| `AbandonRate` | `CallsAbandoned` / `CallsOffered` × 100 (timeouts in, key exits out) | 0 |
| `AnswerRate` | `CallsAnswered` / `CallsOffered` × 100 | 0 |
| `AvgWaitTime` | `TotalWaitTime` / `CallsAnswered` | zero |

`ServiceLevel` is not app_queue's. app_queue's `ServiceLevelPerf` divides the calls answered within its
`servicelevel` by the answered calls, and `ServiceLevelPerf2` divides the calls answered or abandoned within it by
the answered and abandoned calls. On a queue whose callers all time out, the three read 0 %, 0 % and 100 %. Compare
`ServiceLevel` with another reading of the same formula, and set `SessionOptions.SlaThreshold` (20 s by default) to
the queue's `servicelevel` when you compare it with Asterisk's.

### Over a custom session manager

The tracker reads Asterisk's leave, abandon report and `QUEUESTATUS` through the SDK's own `CallSessionManager`.
Over another `ICallSessionManager` it reads the domain events only: a visit ends at its connection, at the caller's
next join or at its hang-up, abandoned unless connected (key exits included), and no timeout is counted.

## Custom Persistence

```csharp
public class PostgresSessionStore : SessionStoreBase
{
    public override ValueTask SaveAsync(CallSession session, CancellationToken ct) { /* ... */ }
    public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) { /* ... */ }
}

services.AddSingleton<SessionStoreBase, PostgresSessionStore>();
services.AddVerbaraSessions();
```
