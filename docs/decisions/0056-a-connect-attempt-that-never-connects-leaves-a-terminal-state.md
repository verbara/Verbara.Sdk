# ADR-0056: A connect attempt that never connects leaves a terminal state

- **Status:** Accepted
- **Date:** 2026-09-21
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0053 (an ending is classified by who ended it — this ADR is that rule applied to
  a connect attempt, and it inherits the trap ADR-0053 records for a bridge's own `ConnectAsync`:
  the transport's cancellation carries a token the caller never held), ADR-0052 (F1, which narrows
  ADR-0050 E6 to the half this decision may use — a requested cancellation is still not a failure —
  and the Context lesson that a test passing for a reason other than the one it names is not
  evidence, which is what forced R5), ADR-0054 (R1, a requested cancellation is a completion, the
  same rule one layer up, and the precedent for stating a telemetry break as a consequence rather
  than hiding it), ADR-0058 (a stopping token never gates a hand-off — the other decision about the
  accept loop R6 changes, and the one R6 must not be read as contradicting), ADR-0010 (the two ARI
  transports are independently lifecycle-managed, which is what makes a terminal state a statement
  about one socket rather than a gate on the client)

## Context

`AriClient.ConnectAsync` dialled the events socket with nothing guarding the dial:

```csharp
SetState(AriConnectionState.Connecting);
// ... the linked source, the socket, the Authorization header and the URI
await _webSocket.ConnectAsync(uri, cancellationToken);
SetState(AriConnectionState.Connected);
```

A throw from that `await` skips the next statement, so `Connecting` was the last state ever written,
and it stayed there for the life of the instance: the events loop starts *after* the dial and the
reconnect loop is reached only from it, so a first dial that fails starts nothing that could write
again. The state said an attempt was in progress when none was and none would be.

| ending on the first `ConnectAsync` | before | after |
|---|---|---|
| refused upgrade (`401`, `503`) | throws `WebSocketException`, `State` stays `Connecting` | throws the same, `State` is **`Faulted`** |
| refused dial (nothing listening, DNS) | throws `WebSocketException`, `State` stays `Connecting` | throws the same, `State` is **`Faulted`** |
| the caller cancels the attempt | throws `TaskCanceledException`, `State` stays `Connecting` | throws the same, `State` is **`Disconnected`** |
| the upgrade succeeds | `State` is `Connected` | unchanged |

One observable moves and one derived from it follows: `State`, and the health check's *message*,
which interpolates it. The red run showed exactly that — against the unfixed client the `State`
assertions failed and, with them, the assertion that the health description names a terminal state.
Everything else was already green before the fix: the throw, its type, `IsConnected == false`,
`HealthStatus.Unhealthy` and "nothing logged at Error". The health **status** does not move, which is
why this never showed on a dashboard; the health **text** does.

This is ADR-0053's family read at the front of a connection rather than at its end. That decision
classifies how a *session* stops; here the session never started, and the artifact that survives the
attempt is a state value with no ending written into it at all.

The same shape sat one type away. `AriOutboundListener.AcceptLoopAsync` wrapped its whole `while` in
a `try` whose last clause was `catch (SocketException) { }`, outside the loop. One transient accept
failure while the listener was meant to be running — `EMFILE`, `ENOBUFS`, a connection aborted in
the backlog — ended the loop silently and for good, with `IsRunning` still reporting `true` over a
socket still in `LISTEN`. The kernel kept completing handshakes nobody would ever read, so a
connector hung instead of being refused, and `StartAsync` could not restart the listener because
`_running` was already 1. Three of the four endings that catch chain absorbed were the ordinary
stop; the fourth was a failure, and they were indistinguishable.

## Decision

**An attempt that ends without the thing it was for leaves a terminal state, and which terminal
state is decided by who ended the attempt — read from the caller's own token, never from the
exception.**

**R1 — a connect attempt that ends without a connection leaves a terminal state.** The dial is
wrapped in a `try`/`finally` that **catches nothing**. A local flag is set after the `await`; the
`finally` writes a terminal state only when the flag is false. The exception, its type and its stack
reach the caller exactly as before — not because a `finally` cannot change an exception (a throwing
one replaces it) but because this `finally` contains one `Interlocked.Exchange` on an `int` and one
read of `CancellationToken.IsCancellationRequested`, and neither can throw. The write happens
*before* the exception is observable to the awaiting caller, so a caller's own `catch` already reads
the terminal state; there is no window in which the exception is in hand and `State` still reads
`Connecting`.

**R2 — the ending is classified by who ended it, read from the caller's token.** ADR-0053 records
the trap verbatim, and for the same construct: a cancelled `ConnectAsync` surfaces a
`TaskCanceledException` carrying a *different* token, so neither the exception's type nor its own
`CancellationToken` can say whether the caller withdrew. The caller's token is the only thing that
knows, and it is what the `finally` reads.

**R3 — a withdrawal leaves `Disconnected`; every other ending leaves `Faulted`.** A withdrawal the
caller asked for is not a failure — ADR-0054 R1 one layer up, and ADR-0050 E6 as narrowed by
ADR-0052 F1 — so it rests where `DisconnectAsync` leaves the client. `Faulted` for everything else
makes the first dial agree with the reconnect loop, which already wrote `Faulted` on a refused
credential and on giving up. One `Faulted` covers every non-withdrawal ending because that is what
the transport permits, not because the endings were judged alike: `CollectHttpResponseDetails` is
set at exactly one site, inside the reconnect loop, so the initial dial cannot read a refusal's HTTP
status at all and `401` is structurally indistinguishable from `503` there.

**R4 — the terminal value is a statement about that attempt, not a gate on the next one.**
`ConnectAsync` writes `Connecting` unconditionally before it reads anything, so a second attempt on
a faulted client proceeds exactly as the first did. The only branch in the type taken on the
published state is `DisposeAsync`'s `if (IsConnected)`, and it compares against `Connected` alone —
`Faulted` and `Disconnected` are indistinguishable there from the `Connecting` they replace, and
from `Initial`. No branch anywhere reads a terminal value. ADR-0010 is why this is a property of the
design rather than a coincidence: `AriConnectionState` describes the events socket, the REST
resource clients are built from the `HttpClient` in the constructor and consult no state, and the
two transports are independently lifecycle-managed. A `Faulted` events socket says nothing about
REST.

**R5 — the success write sits inside the `try`, and its placement is part of this decision.** A
`finally` runs before the statement that follows its block, so a terminal write left unguarded
outside would land on the success path and be overwritten one statement later — invisible to any
test, because no observer exists between the two writes. Keeping `SetState(Connected)` inside the
`try` puts the `finally` last on every path, which is what makes that mutation observable. Measured:
with the write outside, the "terminal state written on the success path too" mutation left **12 of
12 tests green**; moved one scope inward, it fails two committed tests, and mutation coverage for
this change went **5/6 to 6/6**. This is ADR-0052's Context lesson arriving in a second subsystem —
a suite that cannot fail for the reason it names is not evidence — and it is recorded here as a
decision because the alternative placement is the one the task text originally prescribed.

**R6 — the accept loop classifies its endings the same way, and a failure the listener survives does
not end it.** The four endings are separated into filtered clauses inside the `while`: the stop path
(`OperationCanceledException`, `ObjectDisposedException`, and `SocketException` filtered on
`!IsRunning`) ends the loop; an unfiltered `SocketException` is logged at Error through
`AcceptLoopFailed`, waited out on a backoff that doubles from `InitialAcceptBackoff` (100 ms) to
`MaxAcceptBackoff` (5 s) on an injected `TimeProvider`, and the loop **keeps accepting**. The
discriminator for the stop is `IsRunning`, not the token, and that is correct by construction:
`StopAsync` clears `_running` before it calls `Stop()` and before it cancels, so an accept aborted
by that `Stop()` always arrives with `IsRunning` already false.

## Consequences

- **`State` is the only observable that moves, and its move is a break for anyone who reads it.** A
  consumer that branches on `State == AriConnectionState.Connecting` after a failed first connect
  now sees `Faulted`, or `Disconnected` when it cancelled. The 2.5.3 notes told consumers to watch
  `State` or the health check because observers get no `OnError`, so this is a change to something
  callers were pointed at. Same family of telemetry break ADR-0053 and ADR-0054 both recorded, and
  stated here for the same reason.
- **`AriHealthCheck` reports the same status and a different message.** `Connecting`, `Faulted` and
  `Disconnected` all fall to the `Unhealthy` arm, so only the interpolated text moves — from
  `"ARI state: Connecting"` to `"ARI state: Faulted"` or `"ARI state: Disconnected"`. The message
  names the state at the moment it was formatted rather than the arm that matched, because the check
  reads `client.State` twice; harmless here, since nothing writes again after a terminal state.
- **`IsConnected` is unchanged, and so is its one consumer.** It was false under `Connecting` and is
  false under both terminal values, so `DisposeAsync`'s `if (IsConnected) await DisconnectAsync();`
  behaves identically before and after.
- **No counter, gauge, activity or event changes.** No member of `AriMetrics` keys on a state value,
  nothing in `src/` branches on `Disconnected`, and no observable carries the state. Anything that
  pages on this pages outside this SDK.
- **`Disconnected` widens in behaviour and not in documentation.** It had exactly one writer,
  `DisconnectAsync`'s last state write; it now has a second, for a withdrawn attempt. The enum's own
  doc comments were **not** touched — `Faulted` still reads "Unrecoverable error (auth failure, DNS,
  max retries)" while a `503` refusal and a refused dial now reach it. The behaviour widened, the
  documentation did not, and saying so is more honest than asserting a coherence that is not on
  disk.
- **A failed attempt leaves its socket and its linked source to `DisposeAsync`** — unless a later
  `ConnectAsync` on the same instance replaces both fields first, orphaning the earlier pair. That
  is pre-existing and untouched here, but the new retry test drives exactly that path, so this is
  the first artifact where the claim and the test meet.
- **The accept loop's new behaviour is a change on a normal path, not only at shutdown.** A
  transient accept failure now produces one Error line per failure and a growing wait
  (100/200/400/800/1600/3200/5000/5000 ms, reset by the next successful accept) where before it
  produced silence and a dead listener. An operator sees repeated `AcceptLoopFailed` entries instead
  of a listener that reports `IsRunning` true and accepts nothing.
- **R6 does not contradict ADR-0058 R3, and the scopes are what separate them.** ADR-0058 R3 says
  the stopping token still reaches the *handler*, as an argument, and is used there to close a
  connection quietly. R6's "`IsRunning`, never the token" is about the *loop's* catch filter. Two
  rules, two scopes, one file. The hand-off itself is untouched by this change and still carries
  ADR-0058's line character for character.
- **Three things here are correct by construction and not separable by any committed test**, and no
  coverage is claimed for them. (a) Reading the caller's token rather than the exception: the call
  site is `ClientWebSocket.ConnectAsync(Uri, CancellationToken)`, and that overload gives no seam —
  no handler, no inner token, no injectable transport — to raise a cancellation the caller did not
  ask for. The three-argument `HttpMessageInvoker` overload would give one; it is not what is
  called. (b) `IsRunning` versus `ct.IsCancellationRequested` as the stop discriminator: measured,
  the token filter passes all **19** tests, because `Stop()` aborts the accept asynchronously and
  `CancelAsync` is the very next statement, so both predicates agree by the time the filter runs.
  `IsRunning` is ordering-correct by construction; the token merely happens to win. (c) The `break`
  in the filtered stop arm: measured, removing it leaves the suite green, because `Task.Delay` with
  an already cancelled token never creates a timer, so the wait a backed-off stop would take never
  materialises. Writing any of these down as tested properties would be the exact failure ADR-0052
  was written about. Every figure above was re-measured against the suite as it stands, not carried
  from the round in which it was first taken.
- **R6's "correct by construction" covers the `SocketException` pair only, and one arm of the same
  loop still reproduces the pathology R6 exists to remove.** `StartAsync(ct)` builds `_cts` as
  `CreateLinkedTokenSource(ct)`, and `AriOutboundListenerHostedService` hands it the host's *startup*
  token. If that token is cancelled — a startup abort or timeout — the accept loop leaves through
  `catch (OperationCanceledException) { break; }` while `_running` is still 1, because only
  `StopAsync` clears it. `IsRunning` then reports `true` over a listener still in LISTEN, and
  `StartAsync` cannot restart it: its first statement returns early on `_running == 1`. That is the
  same shape as the `SocketException` ending this change fixed, reached by a different door, and it
  predates this change rather than being introduced by it. It is recorded here and left open rather
  than fixed, because closing it means deciding whether a listener should link the startup token at
  all — a lifetime question, not an accept-failure one.
- The new client fakes bind and dial an IPv4 loopback literal per ADR-0044 D2/D3. A separate
  exposure remains in the Testcontainers fixture, where `BaseUrl` interpolates the container
  `Hostname` — which commonly resolves `localhost` — and which `LoopbackSeamScanner` cannot see
  structurally. ADR-0044 does not record that exposure; it draws a scope boundary that excludes it,
  so this is a gap beside that ADR rather than one it left open. Not closed here. The integration test added here
  sits in the Docker-gated lane ADR-0051 D1 keeps off the PR path, so it is evidence run locally
  rather than evidence a PR run produces.
- **Overlapping `ConnectAsync` and `DisconnectAsync` on one instance stay outside this decision, and
  the `finally` gives that gap a new edge.** The dial is handed the *caller's* token, not `_cts.Token`,
  so a concurrent `DisconnectAsync` cannot end an in-flight first dial. It writes `Disconnected`; the
  dial then fails on its own and the `finally` overwrites that with `Faulted`. Before this change
  nothing was written on the throw, so the `Disconnected` survived. Neither ordering is defended here
  — the two calls were already unsequenced — but the losing write is new and is recorded rather than
  discovered later.
- Downstream (Pro, Platform): nothing to recompile. No public API changes — no new type, no new
  member, no changed signature, and no change to `AriConnectionState` itself.

## Alternatives considered

**Leave `Faulted` for a withdrawal too, and drop the token read entirely.** It is the simpler
`finally` by one ternary, and it would spare this ADR R2 entirely — nothing would read a token.
Rejected on meaning, and the rejection is test-pinned: the mutation that writes `Faulted` for both
endings leaves **11 of 12** tests green and is caught only by the withdrawal test. Inside this SDK
nothing would page differently — the health check maps both to `Unhealthy` and no metric keys on the
value — so the cost lands on consumers that branch on `State == AriConnectionState.Faulted`, and on
the enum's own documentation, which reserves `Faulted` for an unrecoverable error. Booking a routine
shutdown as one is what ADR-0053 and ADR-0054 R1 both spent a change removing, in the opposite
direction.

**Write `Initial` for a withdrawal.** Attractive because a withdrawn attempt did nothing the caller
asked to keep. Rejected twice over. The client is not as-created: `ConnectAsync` assigns the linked
`CancellationTokenSource` and the `ClientWebSocket` before the guarded region and clears neither on
the failure path. And `Initial` is what a fresh client already publishes, so writing it here would
make "never attempted" and "attempted and withdrawn" indistinguishable — strictly worse than the
`Connecting` this change removes, which at least said something had happened. The second reason does
not depend on ownership and is the one that decides it.

**Log the accept failure and let the loop exit, as the original task text prescribed.** The smallest
change that removes the silence, and it was genuinely on the table. Rejected by the owner because it
fixes only the silence: the listener still stops accepting on one transient failure, still reports
`IsRunning` true over a bound socket, and still cannot be restarted. The Error line makes the
failure observable; it does not make the listener correct.

**Put a `catch` in `ConnectAsync` and classify from the exception.** It reads as the conventional
shape and would let each failure mode carry its own message. Rejected on two counts. It cannot work:
R2's trap means the exception does not know who ended the attempt, and the `401`-versus-`503`
distinction the classification would want is unavailable on the first dial anyway. And it would
change what the caller receives, which is the one thing this change deliberately leaves alone — the
exception, its type and its stack reach the caller exactly as they did before.
