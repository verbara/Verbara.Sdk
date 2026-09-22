---
tier: MEDIANO
owner: Harol
approver: Harol
stakeholder: Operators who page on ARI health, applications that poll `AriClient.State` to decide whether to retry or fail over, and anyone reading the ARI package's exception handling
decision_ref: Sdk/ADR-0056
---

# Proposal: ari-failed-connect-and-silent-catches

## Why

### A first connect that never connects leaves the client reading as one still dialling

`AriClient.ConnectAsync` sets `AriConnectionState.Connecting` (`src/Verbara.Sdk.Ari/Client/AriClient.cs:127`),
builds the socket and a linked cancellation source, and then awaits
`_webSocket.ConnectAsync(uri, cancellationToken)` (`:141`) with no `catch` and no `finally`.
`SetState(AriConnectionState.Connected)` is the very next statement (`:143`). So for **every** way that
dial can end without a connection — a refused upgrade, a refused connection, a name that does not
resolve, or the caller's own token — the exception leaves the method and `State` stays `Connecting`
for the life of the instance.

Nothing dials again after that. The events loop is started only after a successful connect (`:155`),
and `ReconnectLoopAsync` is reached only from `EventLoopAsync` (`:205`), so `AutoReconnect` — `true`
by default (`AriClientOptions.cs`) — never applies to a first dial. The client is inert, and its
published state says an attempt is in progress.

The reconnect path says the opposite of the initial one. `ReconnectLoopAsync` already writes
`Faulted` twice: when `MaxReconnectAttempts` is exhausted (`:222`) and when a dial is refused `401`
(`:282`, added by #258). And `AriConnectionState.Faulted` is documented as "Unrecoverable error (auth
failure, DNS, max retries)" (`src/Verbara.Sdk/Enums/AriConnectionState.cs:18-19`) — the same failures
the initial dial leaves as `Connecting`.

This is not inferred from the code alone. The released 2.5.3 CHANGELOG entry *"`AriClient` kept
reconnecting after Asterisk refused its credentials"* states it outright — "An initial `ConnectAsync`
answered `401` still throws `WebSocketException` to the caller and leaves `State` at `Connecting`" —
and two bullets earlier tells consumers, for the very case where no observer notification arrives,
to "watch `State` or the health check". The documentation points at the one signal that is wrong.

What a caller can observe today:

| channel | after a failed first connect | what it should say |
|---|---|---|
| the throw from `ConnectAsync` | correct — the exception reaches the caller unchanged | unchanged |
| `State` | `Connecting`, permanently | a terminal value: the attempt is over |
| `IsConnected` | `false` — correct, but it cannot distinguish "dialling" from "dead" | unchanged |
| `AriHealthCheck` | `Unhealthy`, because `Connecting` falls into the `_` arm (`Diagnostics/AriHealthCheck.cs:19`) | still `Unhealthy`, with the message naming a terminal state |
| the event observable | nothing — no `OnError`, no `OnCompleted` | unchanged |

The health **status** is already right, which is why this never showed up on a dashboard. What is
wrong is every consumer that reads `State` itself: a supervisor waiting for the client to leave
`Connecting` waits forever, and a retry policy that treats `Connecting` as "give it time" never
retries. PR #258 left this deliberately — "a failed initial `ConnectAsync` … still leaves `State` at
`Connecting` … That is tracked separately" — as an owner decision, which this change takes.

### The whole of this repository's open code-scanning backlog sits in the same package

Twenty-two open alerts, every one of them under `src/Verbara.Sdk.Ari`, in five files:

| rule | count | files |
|---|---|---|
| `cs/empty-catch-block` | 18 | `Client/AriClient.cs`, `Outbound/AriOutboundListener.cs`, `Audio/AudioSocketServer.cs`, `Audio/AudioSocketSession.cs`, `Audio/WebSocketAudioServer.cs` |
| `cs/missed-using-statement` | 2 | `Outbound/AriOutboundListener.cs`, `Audio/AudioSocketServer.cs` |
| `cs/nested-if-statements` | 2 | `Outbound/AriOutboundListener.cs`, `Audio/AudioSocketSession.cs` |

They have been deferred, more than once, to "the ARI refactor", and they belong with the connect-state
fix because that fix edits `AriClient.ConnectAsync` — three statements above the one bare catch in
that file — and because moving these lines is what will finally settle whether the two dismissed
`cs/dispose-not-called-on-throw` alerts on the reconnect loop's per-dial socket reopen under new
numbers.

The same package already shows what the remedy looks like. `AriOutboundListener` carries five catch
blocks that swallow with a comment saying why (`:126`, `:127`, `:267`, `:268`, `:294`) and **none of
them raises an alert**, while its seven bare ones (`:112`, `:152`, `:153`, `:154`, `:198`, `:213`,
`:283`) each raise one. The query's complaint is that an empty block hides intent; stating the intent
is the fix, and the two already-merged sweeps of the test tree (#254, #255) took the same route.

But stating intent only works where there *is* a defensible intent. Two of the eighteen are not
obviously teardown: `AriOutboundListener.AcceptLoopAsync`'s `SocketException` ends the accept loop
while `IsRunning` still reports `true`, and `AudioSocketSession`'s two `IOException` catches turn a
transport error into the same ending as a clean hangup. Neither is silenced here on the strength of a
comment nobody checked.

## What Changes

1. **The dial is wrapped in a `try`/`finally`, not a `catch`.** A local flag is set immediately after
   `await _webSocket.ConnectAsync(...)`; the `finally` writes the terminal state only when that flag
   is false. Nothing is caught, so the exception, its type and its stack reach the caller exactly as
   they do today, and no new `cs/catch-of-all-exceptions` alert is created by the change that exists
   to close alerts.
2. **The terminal value is decided by who ended the attempt, read from the caller's own token.**
   `cancellationToken.IsCancellationRequested` at the moment the attempt ends: cancelled means the
   caller withdrew the attempt, which leaves `Disconnected`; anything else leaves `Faulted`. The
   decision is not taken from the exception's type, message or `CancellationToken` — ADR-0053 records
   that trap for a bridge's `ConnectAsync`, where a cancelled connect surfaces a `TaskCanceledException`
   carrying a token the caller never held.
3. **A caller's withdrawal is not a fault.** ADR-0050 E6, ADR-0052 and ADR-0053 all hold that a
   requested cancellation is never a failure, and ADR-0053 changed a routine host shutdown from a
   failed session to a completed one. Booking a shutdown-cancelled connect as `Faulted` would walk
   that back for anything alerting on `Faulted`. `Disconnected` — "cleanly disconnected", what
   `DisconnectAsync` leaves (`:405`) — is the resting state a withdrawn attempt shares.
4. **`Faulted` is a statement, not a gate.** `ConnectAsync` writes `Connecting` unconditionally on its
   first line and nothing in the type reads `_state` to decide anything (`IsConnected` at `:67` and
   `DisposeAsync` at `:411` only compare it to `Connected`), so a later attempt on the same instance
   behaves exactly as it does from `Initial`. The change adds no gate, and a test pins that.
5. **Ownership of the socket and the linked source does not change.** A failed attempt still leaves
   both to `DisposeAsync` (`:416`–`:425`). That is the reasoning the two standing
   `cs/dispose-not-called-on-throw` dismissals rest on for the reconnect loop's socket, and this
   change does not contradict it from the other end of the same file.
6. **No new log event.** `ReconnectRejected` exists because the reconnect loop runs detached and the
   log is its only channel. `ConnectAsync` throws to its caller, so an Error log would duplicate what
   the caller already holds. Rejected deliberately, and recorded as such.
7. **Each swallowed exception states which ending it absorbs, by file and member.** Where the swallow
   is correct, the block says which token or which teardown produced the exception. Where it is not —
   where the block can be reached while the component is meant to keep running — it is not silenced:
   the finding is written down for the owner and its alert stays open. Silencing is never the goal;
   an alert that stays open with a recorded reason is a better outcome than a comment that makes a
   real defect invisible.
8. **Two hand-written disposes become `using` blocks, and two nested conditions are combined.** Both
   `using` conversions are ordinary restructurings of a `finally` that already disposes, and one of
   them removes a `#pragma warning disable IDISP016` pair that only existed because the same object
   was disposed twice by hand. Both combined conditions keep their short-circuit and their
   parentheses, and both are already covered by committed tests.
9. **The two gaps PR #258 disclosed close here.** Its "Not verified" section named them: the
   per-iteration socket filter is proven only by a scratch probe, and nothing ran against a real
   Asterisk. Each becomes a committed test — the first in the unit lane, the second in the existing
   Docker-gated lane, which ADR-0051 keeps off the PR path.

## Impact

- `src/Verbara.Sdk.Ari/Client/AriClient.cs`: the `try`/`finally` around the initial dial, and one
  catch block that gains a comment. No public API change; `AriConnectionState` is untouched.
- `src/Verbara.Sdk.Ari/Outbound/AriOutboundListener.cs`, `Audio/AudioSocketServer.cs`,
  `Audio/AudioSocketSession.cs`, `Audio/WebSocketAudioServer.cs`: comments in catch blocks, two
  `using` conversions, two combined conditions. Behaviour-preserving by rule — anything that would
  change behaviour is recorded and handed to the owner instead.
- `Tests/Verbara.Sdk.Ari.Tests/Client/AriClientStateTests.cs`: the connect-outcome tests and the
  concurrent-socket reconnect test. `Tests/Verbara.Sdk.Ari.Tests/Audio/AudioSocketSessionTests.cs`:
  the `ReadFrameAsync` empty-channel case the combined condition needs.
- `Tests/Verbara.Sdk.IntegrationTests/Ari/`: one Docker-gated test against a real Asterisk.
- `docs/decisions/0056-*.md` and its `README.md` row. `CHANGELOG.md`: one `[Unreleased]` entry which
  also amends the 2.5.3 sentence that documents today's behaviour.
- **An observable change for anyone reading `State`.** After a failed first connect it reads `Faulted`
  or `Disconnected` instead of `Connecting`. A supervisor that polls for "still `Connecting`" stops
  waiting; one that alerts on `Faulted` starts alerting on a refused first dial, where before it saw
  only an exception. `AriHealthCheck` returns `Unhealthy` before and after — only its message text
  moves — and `IsConnected` is `false` before and after.
- `Disconnected` widens slightly: it has meant "`DisconnectAsync` completed" and now also means "a
  connect attempt the caller withdrew". Both are endings the caller asked for.
- Downstream (Pro, Platform): nothing to recompile. Anything that branches on `AriConnectionState`
  sees the reclassification.
- **Not in this change, found while reading and recorded so it is not lost:**
  `AudioSocketServer.HandleConnectionAsync` removes its session from `_streams` with the key-only
  `TryRemove` overload, so a connection that lost the `TryAdd` race for a channel id can unregister
  the session that won it. `WebSocketAudioServer` was fixed for exactly this in 2.5.3 (#256) by using
  the key/value overload. It is a behaviour change in a member this change restructures, it is not one
  of the 22 alerts, and it needs its own tests — so it gets its own change rather than riding along.
- **Not all 22 alerts close here.** Tasks 4.3 and 4.5 leave up to three of them alerting, each with
  a written finding instead of a commented silence, so the post-merge check in task 9.2 is read
  against that record rather than against an empty list.

## Architectural Risk

- **Level:** LOW for the connect state, LOW-to-MEDIUM for the cleanup.
- **Affected:** `AriClient`'s published connection state and every consumer that reads it, including
  `AriHealthCheck`; and the exception handling of five files in `Verbara.Sdk.Ari`, whose accept loops,
  connection handlers and read pumps are on the path of every ARI audio and outbound session.
- **Mitigation:** the connect fix catches nothing, so it cannot change what a caller receives; a
  control test pins that a successful connect is untouched, and the mutations that matter each fail a
  committed test — removing the `finally`, writing one state for both endings either way round,
  writing the state before the await, and adding a gate that refuses to dial from `Faulted`. The
  cleanup is held to behaviour-preservation by rule: a catch whose swallow cannot be justified is
  recorded and left alerting rather than commented, and the full `Verbara.Sdk.Ari.Tests` suite plus
  the Docker-gated ARI lane run over the restructured members.
- **Residual, recorded and not closed here:** (a) whether the classification reads the caller's token
  rather than the exception cannot be separated by a committed test, because the current
  `ClientWebSocket.ConnectAsync` overload gives no way to raise a cancellation the caller did not ask
  for — the design follows ADR-0053's recorded trap, and the mutation is listed as unseparable rather
  than claimed covered; (b) a caller that cancels in the same instant a refusal arrives is booked as a
  withdrawal, which is defensible and unordered either way; (c) overlapping `ConnectAsync` and
  `DisconnectAsync` calls on one instance remain outside this requirement, as they are today; (d)
  nothing stops the next bare catch block from reopening these alerts — a guard for that is not part
  of this change.
