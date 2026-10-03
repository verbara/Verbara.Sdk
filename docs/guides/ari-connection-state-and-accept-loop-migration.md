# Migrating to the ARI connect that always ends, the `ari` health table, the per-connection event buffer and the surviving accept loop

A minor release that carries a breaking change ships a migration guide.

Eight behaviours of `Verbara.Sdk.Ari` have moved, over two releases. 2.6.0 changed two: the state a failed
first connect leaves, and the outbound listener's accept loop. This release changes six more: the caller's first
connect is bounded, a failed connect releases what it opened, the `ari` health check reads the state with the
same table as the AMI checks, a caller's ending delivers none of the events still buffered, a full buffer is
counted, and each connection has its own buffer. No signature you call changes, so nothing stops compiling.
What changes is what the SDK reports, how long a connect can take, and which events reach your observers.

## What you have to do

**Update the package.** Then look at the places below where your code may depend on the old behaviour:

1. A host, a retry loop or a `CreateAndConnectAsync` call that connects to an Asterisk which may take the
   connection and never answer it. That connect now fails after about 5 s instead of waiting for good. See
   [the bounded first connect](#the-callers-first-connect-is-bounded).
2. A probe or an alert that reads the `ari` health check, or asserts on its description. It now reports
   `Degraded`, not `Unhealthy`, while the client is connecting or was never connected, and `Unhealthy` once it
   is `Faulted`. See [the `ari` health check](#the-ari-health-check).
3. Code that ends the client with `DisconnectAsync` or `DisposeAsync` and expects its observers to see the
   events that were already on their way. They no longer do. See
   [a caller's ending](#a-callers-ending-delivers-none-of-the-buffered-events).
4. An alert on `ari.events.dropped`. It now fires, and carries a `reason` tag it must filter on. See
   [the counter](#the-dropped-events-counter-counts-a-full-buffer-and-carries-a-reason).
5. Code that read `AriClient.State` after a connect attempt that failed, or that built a timeout around it.
   It settles since 2.6.0. See [the state after a failed first connect](#ariclientstate-after-a-failed-first-connect).
6. A watchdog that restarts `AriOutboundListener` when it stops accepting. Since 2.6.0 the listener no longer
   stops. See [the accept loop](#arioutboundlistener-after-an-accept-failure).

## The caller's first connect is bounded

**Before (2.6.1 and earlier):** only the reconnect loop's dials were bounded. The caller's own
`ConnectAsync`, and so `CreateAndConnectAsync` and the hosted service `AddVerbara` registers for ARI, waited
for the WebSocket upgrade for as long as the caller's token allowed. An Asterisk that accepted the TCP
connection and never answered the upgrade (an overloaded or stalled `http` module, a proxy that holds the
request) held the call in `Connecting` for good when the token was `CancellationToken.None`, and held a host's
start-up for as long as the host's start token allowed.

**Now:** the caller's dial has the same bound as each reconnect dial: **5 seconds** for the TCP dial and the
HTTP upgrade together. When it runs out first:

- `ConnectAsync` throws `WebSocketException` (`WebSocketError.Faulted`) with the message
  `The ARI WebSocket upgrade to <host> did not complete within 5 s.` and a `TimeoutException` as its inner
  exception. That is the shape a refused upgrade already takes, so a `catch (WebSocketException)` you already
  have catches it.
- `State` reads `Faulted`, as after a refused upgrade (see
  [the state after a failed first connect](#ariclientstate-after-a-failed-first-connect)).
- Nothing reconnects the client: as after any failed first connect, no reconnect loop starts. Call
  `ConnectAsync` again to retry; a retry after the far end recovered connects.

Your own token still wins when it is cancelled first: `ConnectAsync` throws `OperationCanceledException` and
`State` reads `Disconnected`. A `DisconnectAsync` or `DisposeAsync` called while the dial is held ends the dial
at once, also with `Disconnected`. The bound is fixed; there is no option to change it.

A failed attempt, whatever ended it, now releases the socket and the cancellation source it opened, where
before they stayed with the client until the next connect or its disposal. And `CreateAndConnectAsync`
disposes the client it created when the connect throws, then rethrows the connect's own exception unchanged:
before, that client was never returned to you and never released.

### What this means for code you may have written

- **A host whose start-up connects ARI** (`AddVerbara` with an `Ari` section) now fails to start after about
  5 s against an Asterisk that never answers, instead of hanging. If you want the host to start anyway and
  connect later, connect from your own code with a retry, not from the hosted service.
- **A retry loop around `CreateAndConnectAsync`** or `ConnectAsync` now runs one attempt about every 5 s
  against a mute Asterisk, plus your own delay, where before the first attempt never returned. Space your
  retries with a delay of your own; an attempt no longer leaks a client or a socket.
- **A timeout of your own around the first connect** (a token cancelled after N seconds) keeps working. If it
  is longer than 5 s, the SDK's bound fires first and you see `WebSocketException` instead of
  `OperationCanceledException`.

## The `ari` health check

**Before (2.6.1 and earlier):** `AriHealthCheck` (registered as `ari` by `AddVerbara` when an `Ari` section is
configured) answered `Healthy` for `Connected`, `Degraded` for `Reconnecting` and `Unhealthy` for every other
state, including `Connecting` and a client nobody had connected yet. Its data was empty.

**Now:** it reads the state with the same table as the AMI connection's `ami` and `live` checks. ARI has a
state AMI does not, `Faulted`, and it is mapped by what it means:

| `AriClient.State` | `ari` before | `ari` now | why |
|---|---|---|---|
| `Connected` | `Healthy` | `Healthy` | |
| `Reconnecting` | `Degraded` | `Degraded` | the reconnect loop is still running |
| `Connecting` | `Unhealthy` | `Degraded` | only the caller's own dial writes it; the reconnect loop stays `Reconnecting` |
| `Initial` | `Unhealthy` | `Degraded` | nobody has connected the client yet, and it does not connect on its own |
| `Disconnecting`, `Disconnected` | `Unhealthy` | `Unhealthy` | ended; nothing reconnects it until a new connect |
| `Faulted` | `Unhealthy` | `Unhealthy` | every path that writes it ends the dialling: a failed first connect, a give-up, a refused reconnect, a backoff that cannot be computed |

The result's data gains `ariState`, the state's name as a string, in every state. The description starts with
`ARI ` and the state's name: `ARI Connected`, `ARI Reconnecting: the connection may still recover on its own`,
`ARI Initial: not connected yet`, `ARI Faulted: nothing reconnects it until a new connect`. Before, they were
`ARI connected`, `ARI reconnecting` and `ARI state: <State>`. **If you assert on that string, it changes;
read `ariState` instead.**

### What this means for code you may have written

- **A readiness probe on `ari`** stays ready while a connect is in progress and before the first connect:
  ASP.NET Core's health endpoint answers 200 for `Degraded` unless you map it otherwise. A host that never
  connects its ARI client reports `Degraded` for as long as it runs.
- **An alert on `Unhealthy` from `ari`** now fires only once the client has ended or faulted; alert on
  `Degraded` to see a reconnect or a connect in progress.

## A caller's ending delivers none of the buffered events

**Before (2.6.1 and earlier):** `AriClient` reads events off the socket into a buffer and a consumer hands
them one at a time to the observers. `DisposeAsync` delivered every event still in that buffer before it
returned, so it waited for all of them: measured with a 250 ms observer, a dispose with 20 events buffered took
about 5 s, and one with 200 about 50 s. `DisconnectAsync` did not touch the buffer at all: it returned at once
and the observers kept receiving the closed connection's events afterwards.

**Now:** both endings wait for the event whose observer is running, if any, and deliver none of the events
still buffered. The discarded events are counted, not lost silently: one Warning per ending, with the count,

```
[ARI] Discarded on caller ending: count=<n>
```

and one measurement of `n` on `ari.events.dropped`, tagged `reason=caller_ending`. Nothing is logged or counted
when the buffer was empty, and a `DisposeAsync` after a `DisconnectAsync` reports nothing again.

An ending called from inside an observer's `OnNext` returns without waiting for that dispatch (which is waiting
for it); the release finishes, and is reported, once the `OnNext` returns.

**A connection lost without your ending is unchanged.** When the peer closes, the events of the lost
connection that were already buffered are still delivered, in order: they are events Asterisk really sent.

### Each connection has its own buffer

**Before (2.6.1 and earlier):** one buffer served the client's whole life. A `ConnectAsync` after a
`DisconnectAsync` started a second consumer on it, so two consumers delivered the new connection's events,
and the order your observers saw them in was broken in about half the runs measured.

**Now:** each connection that connects gets its own buffer and one consumer, released by your `DisconnectAsync`
or `DisposeAsync` as above. The reconnect loop keeps the connection's buffer across its reconnects. A
`ConnectAsync` after a connection that was lost and that nothing reconnected first delivers what that connection
had buffered, in order, then the new connection's events.

### What this means for code you may have written

- **If you need the tail before you close**, for example the last `StasisEnd`s before a shutdown, wait for it
  in your own code, then end the client. Once you end it, nothing still buffered reaches an observer.
- **An observer that stopped work on a flag after `DisconnectAsync`**, because events kept arriving, no
  longer needs it.
- **If you use `AriEventPump` directly**: `DisposeAsync` now stops after the event in progress and does not
  deliver the rest. To deliver the buffer before releasing the pump, stop enqueueing, wait until
  `PendingCount` reaches 0, then dispose.

## The dropped-events counter counts a full buffer and carries a reason

**Before (2.6.1 and earlier):** the counter, `AriEventPump.DroppedEvents` and `AriEventPump.OnEventDropped`
were meant to report a full buffer, but none of them could fire: the buffer discards its oldest event to make
room for a new one, and nothing saw that discard. An alert on `ari.events.dropped > 0` could never trigger.

**Now:** each event a full buffer discards is counted in `DroppedEvents`, handed to `OnEventDropped` (the
discarded event, the oldest one buffered) and recorded once on `ari.events.dropped` with `reason=buffer_full`.
A write refused because the buffer was already released by your ending is not counted as a full buffer.

| `reason` | when | what to do |
|---|---|---|
| `buffer_full` | the buffer was full and discarded its oldest event (one measurement per event) | observers are slower than the event rate: speed up or offload them — see [high-load-tuning.md](high-load-tuning.md) |
| `caller_ending` | your `DisposeAsync` or `DisconnectAsync` found events still buffered (one measurement per ending, with the count) | expected; nothing to tune |

**An alert on `ari.events.dropped` must filter on `reason=buffer_full`**, or it fires on every close that finds
events buffered.

## `AriClient.State` after a failed first connect

*Changed in 2.6.0.*

**Before (2.5.3 and earlier):** a first `ConnectAsync` that threw left `State` at `Connecting` for the life of the
instance. The state machine wrote `Connecting`, dialled the events socket, and wrote `Connected` on
the next statement — a throw from the dial skipped that statement, and nothing else could ever write
again, because the events loop starts after the dial and the reconnect loop is only reached from it.

**Now:** the attempt leaves a terminal state, and which one is decided by **who ended the attempt**,
read from your own cancellation token rather than from the exception:

| How the attempt ended | `State` before | `State` now |
|---|---|---|
| Refused upgrade (`401`, `503`), nothing listening, name does not resolve, upgrade not answered within the bound | `Connecting` | `Faulted` |
| You cancelled the token you passed, or called `DisconnectAsync` or `DisposeAsync` during the dial | `Connecting` | `Disconnected` |

**The exception you catch is unchanged** — same type, same message, same stack. The state is written
in a `finally` that catches nothing and runs before the exception becomes observable to the awaiting
caller, so a `catch` block of yours already reads the terminal value.

### What this means for code you may have written

- **A poll or timeout waiting for `State` to leave `Connecting`** can be deleted. It never left
  before; it leaves immediately now.
- **A check that treats `Connecting` as "an attempt is still in progress"** is now correct, where
  before it was permanently wrong after a failed first connect.
- **This amends what 2.5.3 told you.** That release's entry for the credential-refusal fix said an
  initial `ConnectAsync` answered `401` *"still throws `WebSocketException` to the caller and leaves
  `State` at `Connecting`"*, and pointed you at `State` or the health check because observers receive
  no `OnError` when the loop stops. The throw is still exactly that. The state it named is not.
- **`AriHealthCheck` reports `Unhealthy` after a failed first connect**, as it did when the state stayed
  `Connecting`: in 2.6.0 every state but `Connected` and `Reconnecting` fell to its `Unhealthy` arm, and
  with this release's table `Faulted` and `Disconnected` are `Unhealthy` too. Its description and data
  changed in this release, see [the `ari` health check](#the-ari-health-check).
- **`IsConnected` is unchanged.** It was false under `Connecting` and is false under both terminal
  values.

## `AriOutboundListener` after an accept failure

*Changed in 2.6.0.*

**Before (2.5.3 and earlier):** the accept loop wrapped its whole `while` in a `try` whose last clause was
`catch (SocketException) { }`, outside the loop. One transient accept failure while the listener was
meant to be running — `EMFILE`, `ENOBUFS`, a connection aborted in the backlog — ended the loop
silently and for good. `IsRunning` still reported `true`, the socket was still in `LISTEN`, and the
kernel kept completing handshakes nobody would ever read. An outbound connector hung instead of being
refused, and `StartAsync` could not restart the listener because it was still marked running.

**Now:** such a failure is logged at Error, waited out, and the loop keeps accepting. The wait doubles
from 100 ms to a 5 s cap with each consecutive failure and resets after a successful accept.

### What this means for code you may have written

- **A watchdog that restarts the listener** when it notices connections are no longer arriving can be
  removed. It could not work anyway: `StartAsync` refused while `IsRunning` was true.
- **You will see Error lines you did not see before**, under a condition that used to be silent. At
  most twelve a minute once the wait reaches its cap:
  `[AriOutbound] Accept failed — the listener stays bound and accepts again after a backoff`.
  A persistent failure is now noisy on purpose; it was invisible before.
- **A connection that arrives during a wait** stays in the listen backlog until the wait ends, rather
  than being accepted and dropped.
- **The stop path is unchanged.** `StopAsync` still ends the loop at once; it clears the running flag
  before stopping the listener, so an accept aborted by that stop is told apart from a failure by
  `IsRunning` and never by the token.

## How to check which behaviour you are on

Force a connect that cannot succeed — point `AriClientOptions` at a port with nothing listening — and
read `State` in your `catch`:

```csharp
try
{
    await client.ConnectAsync(cancellationToken);
}
catch (Exception)
{
    // 2.5.3 and earlier: Connecting
    // 2.6.0 and later:   Faulted
    Console.WriteLine(client.State);
}
```

For the bounded connect, point it instead at a port that accepts TCP and never answers, for example a
listening socket that reads nothing. On 2.6.1 and earlier the call waits for your token; on this release it
throws `WebSocketException` after about 5 s. For the health check, run it before the first connect: `ari`
reads `Degraded` with `ariState` = `Initial` on this release, `Unhealthy` before.

For the listener, there is nothing to probe safely from a consumer — the condition needs a real
accept failure. The Error line above is the signal that the new behaviour is in play.
