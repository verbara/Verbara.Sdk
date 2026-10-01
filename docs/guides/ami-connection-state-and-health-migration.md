# Migrating to the announced AMI connection state and the health checks that read it

Required by ADR-0028: a minor that carries a breaking change ships a migration guide.

An AMI connection now announces every state it takes, on `IAmiConnection.StateChanged`, and the
Live health checks read that state. No signature you call changes, so nothing stops compiling, but
four behaviours move: what the `live` health check answers, a new `verbara-pool` health check on a
multi-server host, the state a failed `ConnectAsync` leaves, and when an ending started from a task
an event handler left running waits.

## What you have to do

**Update the package.** Then look at the four places below where your code may depend on the old
behaviour:

1. A liveness probe pointed at an unfiltered health endpoint. The `live` check now reports a lost
   AMI connection, and a multi-server host gains a `verbara-pool` check: both can turn that
   endpoint `Unhealthy` during an outage. See [the health checks](#the-live-health-check).
2. Code that reads `State` after a `ConnectAsync` that threw, or calls `ConnectAsync` on a
   connection that is already connected or reconnecting. See
   [a failed connect](#state-after-a-failed-connectasync).
3. A loop that polls `State` to notice that the reconnect gave up. It still works; `StateChanged`
   tells you instead. See [watching the state](#watching-the-state-instead-of-polling-it).
4. An event handler that awaits the stored task of a `DisposeAsync` or `DisconnectAsync` that it
   did not call. See [an ending started from a detached task](#an-ending-started-from-a-task-a-handler-left-running).

## The `live` health check

**Before:** `LiveHealthCheck` (registered as `live` by `AddVerbara`) answered from the state Live
holds: `Healthy` with channels loaded, `Degraded` with none. It never looked at the AMI connection,
so a connection that had given up for good reported `Healthy` for as long as the table held a
channel that nothing would ever update again.

**Now:** it reads the AMI connection first.

| AMI connection `State` | `live` before | `live` now |
|---|---|---|
| `Connected` | `Healthy` / `Degraded` (from the state) | unchanged |
| `Reconnecting`, `Connecting`, `Initial` | `Healthy` / `Degraded` (from the state) | `Degraded` — "Live state is not being updated: AMI {state}" |
| `Disconnecting`, `Disconnected` | `Healthy` / `Degraded` (from the state) | `Unhealthy` — "Live state is not being updated: AMI {state}" |

The check's data gains `amiState`, the state's name as a string. A recovering connection is
`Degraded`, not `Unhealthy`, because it may come back on its own; one that has ended will not.

## The `verbara-pool` health check

**Before:** `AddVerbaraMultiServer` registered no health check.

**Now:** it registers `VerbaraServerPoolHealthCheck` under the name `verbara-pool`, with no tags,
once however often it is called. It reads every server's AMI connection when it runs:

| The pool's servers | `verbara-pool` |
|---|---|
| none, or every one `Connected` | `Healthy` |
| every one `Disconnecting` or `Disconnected` | `Unhealthy` |
| any other mix (one reconnecting, one ended and one recovering, …) | `Degraded` |

Its data maps each server id to its connection's state name.

### What this means for code you may have written

- **An unfiltered endpoint** (`app.MapHealthChecks("/health")`) aggregates every registered check,
  so it now reports these two. If a liveness probe reads it, an Asterisk outage that the
  connection does not recover from restarts your pod. Point liveness at an endpoint filtered by tag
  and keep the AMI checks on readiness, or on a diagnostics endpoint:

  ```csharp
  app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = check => check.Tags.Contains("liveness") });
  app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
  ```

  The SDK registers its health checks with no tags, so an endpoint filtered by tag includes them
  only if you tag them yourself.
- **An endpoint filtered by tag** sees no change: `verbara-pool` and `live` carry none.
- **An alert on `Degraded`** fires during every reconnect now, for as long as the reconnect takes.

## `State` after a failed `ConnectAsync`

**Before:** a `ConnectAsync` that threw (nothing listening, the login refused, the connect timed
out) left `State` at `Connecting` for good, although no attempt was running, and kept the
attempt's socket until the next ending. A `ConnectAsync` on a connection that was already
`Connected` or `Reconnecting` replaced the live session's socket without releasing it.

**Now:**

- The failed attempt releases what it opened, and `State` reads `Disconnected`.
  `StateChanged` announces `Connecting → Disconnected` with `ByCaller` set and the exception as
  `Cause` (no cause when your own cancellation token withdrew the attempt). The exception you catch
  is unchanged. Call `ConnectAsync` again to retry.
- A `ConnectAsync` on a connection that is `Connected`, `Reconnecting` or `Connecting` throws
  `InvalidOperationException` and changes nothing. A lost connection reconnects on its own; to
  connect again after a give-up, call `ConnectAsync` once the connection reads `Disconnected`.

### What this means for code you may have written

- **A check that treats `Connecting` as "an attempt is running"** is now correct after a failed
  connect, where before it stayed true for good.
- **A retry loop around `ConnectAsync`** keeps working; a failed attempt no longer leaves its
  socket open.
- **A second `ConnectAsync` "to be sure"** on a connection that is up now throws. Read `State`
  first, or drop the call.

## Watching the state instead of polling it

`IAmiConnection.StateChanged` is raised once for every change of `State`, in order, each change
starting where the one before it ended. A change carries `Previous`, `Current`, `Cause` and
`ByCaller`, and two shortcuts:

- `IsLoss`: the connection left `Connected` without you asking. Once per outage, delivered before
  `VerbaraServer.ConnectionLost`.
- `IsFinal`: the connection reached `Disconnected` without you asking. Nothing will reconnect it:
  `AutoReconnect` is off, or the reconnect gave up at `MaxReconnectAttempts`. Its `Cause` is the
  last attempt's exception (`AmiAuthenticationException` when the credentials were rejected).

```csharp
connection.StateChanged += change =>
{
    if (change.IsFinal)
        logger.LogError(change.Cause, "AMI connection ended for good: {Previous} -> {Current}", change.Previous, change.Current);
};
```

Handlers run one at a time on the thread pool, on the same queue as `ConnectionLost` and
`Reconnected`; keep them short. A handler that has not returned after 30 s is logged once at
Warning: `[AMI] A StateChanged handler has not returned after 30 s; later notifications wait for it`.
An `IAmiConnection` of your own that does not implement the event raises nothing; one that wraps
another connection should forward the inner connection's changes.

## An ending started from a task a handler left running

**Before:** a `DisposeAsync` or `DisconnectAsync` called from inside the connection's event
dispatch did not wait for that dispatch, and neither did one called from any task a handler had
started, even long after that handler had returned.

**Now:** a task a handler started counts as inside the dispatch only while that dispatch is
running. Once the handler has returned, an ending the task calls waits for the dispatch in
progress, as any other caller's does.

### What this means for code you may have written

- **A handler that awaits the stored task of an ending it did not call** waits for an ending that
  waits for that handler's dispatch: the two wait for each other. This was already true of an
  ending started outside every handler; it is now also true of one started from a task an earlier
  handler left running. A handler that needs the connection ended calls `DisconnectAsync` or
  `DisposeAsync` itself: called from a handler, the ending never waits for the dispatch it runs in.

  ```csharp
  // Before: deadlocks now if storedEnding came from a task an earlier handler left running.
  await storedEnding;

  // Now: join the ending from the handler.
  await connection.DisconnectAsync();
  ```

## Also changed, with nothing to do

- **A `Reconnected` still queued when you call `DisconnectAsync` or `DisposeAsync`** is no longer
  delivered, as `DisconnectAsync` documents.
- **A send that races your `DisconnectAsync` or `DisposeAsync`** throws `AmiNotConnectedException`,
  where it could throw `NullReferenceException` or `TaskCanceledException`. `DisposeAsync` also
  completes while a send is blocked by a peer that stopped reading.

## How to check which behaviour you are on

Point an `AmiConnectionOptions` at a port with nothing listening and read `State` in your `catch`:

```csharp
try
{
    await connection.ConnectAsync(cancellationToken);
}
catch (Exception)
{
    // 2.6.1 and earlier: Connecting
    // this release:      Disconnected
    Console.WriteLine(connection.State);
}
```
