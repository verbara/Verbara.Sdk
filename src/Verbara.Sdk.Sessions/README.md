# Verbara.Sdk.Sessions

Session Engine for the Verbara.Sdk ecosystem. Provides call session correlation, lifecycle state machines, and domain events for real-time telephony monitoring.

## Features

- **CallSession** - Models the full lifecycle of a call: Created, Dialing, Ringing, Queued, Connected, OnHold, Transferring, Conference, Completed, Failed (`TimedOut` is kept for sessions stored by earlier versions; since 2.7.0 no call ends in it)
- **CallSessionManager** - Automatic session creation and correlation by LinkedId, with 4-tier O(1) indexing
- **Reconciliation sweep** - Registered by `AddVerbaraSessions`: when a held call is older than `DialingTimeout`, it checks the held calls against Asterisk's channel snapshot and ends, as a reload ends it, a call whose channels Asterisk no longer reports. It never ends a call for its age. See [the migration guide](../../docs/guides/call-session-ending-migration.md#the-reconciliation-sweep-and-long-answered-calls)
- **Domain Events** - Observable stream of CallStarted, CallConnected, CallQueued, CallHeld, CallEnded, CallReleased and the other notifications of a call
- **Extension Points** - Abstract base classes for custom routing (CallRouterBase), agent selection (AgentSelectorBase), and persistence (SessionStoreBase)
- **SessionMetrics** - System.Diagnostics.Metrics counters for sessions created, completed and failed, and for calls released on detach (`sessions.released_on_detach`); `sessions.timed_out` and `sessions.orphaned` are still published, and no longer incremented since 2.7.0
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

## Domain Events

`ICallSessionManager.Events` delivers each notification to every subscriber in turn, on the thread that raised it — the
thread delivering the server's event, a reload, a sweep, a release or the disposal. **Every notification of a call —
its start, queueing, connection, hold, resumption, transfer, ending and release — is raised while the engine holds that
call's lock**, after checking that it still holds the call; since 2.9.0 that includes `CallStartedEvent` and
`CallQueuedEvent`, which were raised outside it before. A `CallWrapUpEvent` is not: it is the agent's after-call time,
raised when the wrap-up closes. A subscriber that blocks holds them up; keep handlers short and hand long work off.

**A subscriber that throws fails alone.** Its exception is caught at that subscriber, logged once at `Warning` with the
exception and counted on `sessions.events.subscriber_faults` (meter `Verbara.Sdk.Sessions`):

| EventId | Line |
|---|---|
| 3 | `A subscriber of the session events threw on {EventType} for session {SessionId}; the other subscribers and the session engine went on` |
| 4 | `A subscriber of the session events threw on {EventType}; the other subscribers and the session engine went on` — the stream's `completion` at disposal (or an `error`), with no session |

The other subscribers still receive the notification, and the engine's own work for the call (the store, the channel
index, the metrics), the live state's later handlers, and a reload or sweep that raised it complete as with no failing
subscriber. Nothing a subscriber throws (an `OutOfMemoryException` excepted) reaches the code that raised the notification. Up to 2.8.x it did: the later
subscribers, the SDK's own queue and agent trackers among them, missed that notification, a call could stay held with
its ending never saved, and a reload stopped part way.

- **A subscriber subscribed directly stays subscribed** and receives the next notification.
- **A subscription composed through an operator (`Where`, `Select`, `OfType`, …) is gone after its first failure.** The
  operator ends it on the first exception from its own function or from the handler behind it; the guard reports that
  one failure and cannot keep it alive. So one `Warning` can mean that subscriber receives nothing more. To stay
  subscribed, subscribe directly and filter inside the handler.
- **Only what throws on the delivering thread is caught.** A subscriber that moves its work elsewhere — `ObserveOn` or
  `SubscribeOn`, an `async void` handler, work pushed to `Task.Run` — throws on another thread, where this does not
  catch it.

`IAgentSessionTracker.StateChanges` behaves the same way, on the thread that raised the agent's change and under that
agent's lock. Its failures log at `Warning` under the tracker's own category, EventId 5 (`A subscriber of the agent
state changes threw on the change of agent {AgentId} from {PreviousState} to {NewState}; …`) and EventId 6 (its
completion or error), and count on the same `sessions.events.subscriber_faults`.

## A server that leaves for good

`DetachFromServer(serverId)` unsubscribes the server and keeps every call the engine holds for it: those calls stay
held, are listed in `ActiveSessions`, and are counted as `serverless` by a multi-server host's sweep on every tick.
That is right when another instance takes the server over, and it is still what
`DetachFromServer(serverId, DetachedCalls.Keep)` does. When the server is gone for good, detach it with
`DetachedCalls.Release`:

```csharp
IReadOnlyList<CallSession> released = sessionManager.DetachFromServer("node-3", DetachedCalls.Release);
```

The release lets go of every call held under that id that has not ended, without ending it. Each call leaves every
lookup (by session id, `linkedid`, channel id and bridge id), `ActiveSessions`, `sessions.active` and the default store.
Its state, legs, timestamps and metadata stay as they were, and no `CallEndedEvent`, completion, failure or duration is
recorded for it. Calls of that server that already ended are left to their retention. The release also works for an
id that is not attached, so a host that detached with `Keep` can release later. A second release of the same id
releases nothing. A null or empty id throws `ArgumentException`, and an undefined `DetachedCalls` value throws
`ArgumentOutOfRangeException`. `ICallSessionManager`'s default implementation of the overload releases nothing: an
implementation that does not override it only detaches.

**How you learn what was released:**

- **The return value**: the released calls, in the order their notifications were raised.
- **`CallReleasedEvent`** on `Events`, once per released call, carrying `SessionId`, `ServerId`, `LinkedId`,
  `TenantId` and the release instant as `Timestamp`. It is raised under the call's lock while `GetById` still finds the
  call, and the call leaves every lookup under that same lock, so it is the last notification for that call object. A
  subscriber that hands the work to another thread should rely on the values the event carries, not on a lookup. It is
  not a permanent end of the session id: a call registered later under the same session id, as a cluster's
  reconstruction may register one, is a new call.
- **`sessions.released_on_detach`** (meter `Verbara.Sdk.Sessions`), +1 per released call, tagged `server.id`, and one
  Information log line per release naming the server and the count (EventId 12).

A subscriber of `CallReleasedEvent` that throws fails alone, as described above: the other subscribers still receive
it, the call still leaves every lookup, the rest are still released, and the detach returns normally.

**One exception to "last notification".** A wrap-up already open when its call is released keeps running and closes
as it would have, by its timer or by the agent's next connection. Its one `CallWrapUpEvent` carries the released
session id and may follow `CallReleasedEvent`.

**What the release waits for, and its two limits.** The release first closes the server's attachment to every event
not yet handled, then waits for that attachment's handlers already running, at most a few seconds. So a hangup or an
arrival already being handled takes effect first: a call it ended is ended, not released, and a call it opened is
released with the others. The release then waits at most a second for each call's lock. A call whose lock a handler
still holds past that is skipped: it stays held under the server id, is not notified or returned, and is released
by a later release of the id. Either limit expiring writes one Error entry (EventId 13) naming the server, the
handlers still in flight and the session ids skipped. The wait covers only the attachment the release itself
closes: for an id detached earlier with `Keep`, a handler of that attachment still running may finish just after the
release notification. An attach, or a second release, of the same id asked for while a release runs waits for it to
finish.

**A call-released subscriber must not block.** The two limits bound the engine's own waits, not your subscribers.
`CallReleasedEvent` subscribers run on the releasing thread with the call's lock held, so one that blocks holds the
release, and every attach or release of that id waiting for it, until it returns. No Error entry is written for it.
Hand slow work to another thread.

**Not from inside a notification.** A release asked for from inside any notification of the engine (a
`CallReleasedEvent` included), and an attach that would have to wait for a release in progress of its id, throws
`InvalidOperationException` naming the server id, and detaches and releases nothing: it would wait for the very
thread that is delivering the notification. Handle that exception inside the subscriber and ask from another thread.
One the subscriber lets escape is reported like any subscriber failure, and the delivery continues.

**What the trackers do.** `IQueueSessionTracker` closes the released call's queue visit: a caller still waiting
stops counting in `CallsWaiting`, and the visit is counted neither answered nor abandoned. `IAgentSessionTracker`
drops the released call from an agent whose `CurrentCall` it was, and keeps the agent's state, counting nothing. Such
an agent can read `OnCall` with no current call until another event of that agent reaches this process, which, for a
server this process no longer watches, may never happen.

**If the server id comes back.** Nothing of the earlier attachment is held, so a reconciliation of the calls it left
finds none: a call whose hangup fell while the server was away is never ended in this process. A call still up on
the returning server is held again only through the paths that open a call: a new leg, a reload or an admission (each
a new session), or a reconstructed call the host registers, which is accepted again under a new session id or under
the released one.

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
