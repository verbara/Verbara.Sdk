---
tier: PEQUEÑO
owner: Harol
approver: Harol
stakeholder: Operators who stop or restart an AudioSocket host while calls are still arriving, and the far ends whose connections the server would otherwise forget
decision_ref: Sdk/ADR-0053
---

# Proposal: audiosocket-accepted-connection-is-always-closed

## Why

`AudioSocketServer.AcceptLoopAsync` hands every accepted connection to its handler through the thread
pool, gated on the server's stopping token
(`src/Verbara.Sdk.VoiceAi.AudioSocket/AudioSocketServer.cs:106-108`):

```csharp
var client = await AcceptAsync(ct).ConfigureAwait(false);
backoff = InitialAcceptBackoff;
_ = Task.Run(() => HandleConnectionAsync(client, ct), ct);
```

That second argument is not a courtesy. The overload's own contract is that the token "allows the work
to be cancelled if it has not yet started", and *started* is decided when the pool picks the work item
up, not when the loop queues it. Two orderings therefore drop the handler entirely:

| ordering | what happens |
|---|---|
| the stop lands after `AcceptAsync` returned a connection and before line 108 runs | the work item is created already cancelled and the handler never runs |
| the stop lands after the work item is queued and before the pool starts it | the same outcome, decided by scheduling rather than by the loop |

A *pending* accept is not the hazard: a token cancelled while the accept is still waiting raises
`OperationCanceledException` and produces no connection at all. The hazard is an accept that has
already succeeded.

**The handler is the only owner of an accepted connection.** All three of its dispose sites are inside
the method the token can skip:

| site | when it closes the connection |
|---|---|
| `AudioSocketServer.cs:182-189` | the identifying frame never arrived — which includes a stopping server, and stays quiet, because the warning above the dispose is suppressed while the stopping token is cancelled |
| `AudioSocketServer.cs:205-211` → `AudioSocketSession.cs:230-237` | the session limit was reached, so the session is disposed and its terminate releases the transport |
| `AudioSocketServer.cs:221-225` | anything the handler threw |

Nothing else can close it. A connection joins `_sessions` only at `AudioSocketServer.cs:205`, after the
identifying frame, so `StopAsync` — which cancels the token, stops the listener, disposes the
*registered* sessions and logs `ServerStopped` (`AudioSocketServer.cs:85-97`) — never sees it.
`ConnectionsAccepted` is incremented at `AudioSocketServer.cs:193`, also inside the handler and also
after the frame, so no counter and no gauge moves either: `ActiveSessionCount` reads zero over a
connection that is still open. The socket is then released only if a garbage collection eventually
finalizes its handle — an unbounded delay a shutdown cannot order — and until then the far end holds a
connection a stopped server will never read from or answer.

**The handler is not the defect.** A stop that lands *while the handler is waiting* for the identifying
frame already closes the connection, and closes it quietly: pinned today by
`HandleConnectionAsync_ShouldCloseWithoutWarningOrError_WhenServerStopsBeforeUuidArrives`
(`Tests/Verbara.Sdk.VoiceAi.AudioSocket.Tests/AudioSocketServerEdgeCaseTests.cs:90-122`), which asserts
nothing at Warning or above and the peer's read reaching end of stream. Only the hand-off between the
accept and that handler is unguarded, and it is unguarded in exactly the moment a shutdown makes
likely.

This is the ADR-0053 family read one layer earlier. That decision closed a session that ends without a
hangup frame and never releases its transport; here the transport belongs to a connection that never
became a session, so ADR-0053's terminate — the thing that releases it — does not yet exist.

## What Changes

1. Hand the connection over unconditionally: `_ = Task.Run(() => HandleConnectionAsync(client, ct));`.
   The handler keeps receiving `ct`, so a connection it takes over while the server is stopping is
   closed at once through the branch at `AudioSocketServer.cs:182-189`, and closed quietly, because
   that branch suppresses its warning when the stopping token is cancelled. One owner per accepted
   connection, and the stopping token reaches it as an argument rather than as a gate.
2. This is the shape this repo's other accept loop already uses:
   `src/Verbara.Sdk.Ari/Outbound/AriOutboundListener.cs:149` hands its accepted connection over with
   `CancellationToken.None` while passing the stopping token into the handler. The fix makes the two
   accept loops agree rather than introducing a new convention.
3. **Rejected: check the token in the loop and dispose there.** It closes the first ordering and leaves
   the second, because the token is re-checked when the pool starts the work item; and it creates a
   second place that disposes an accepted connection, which is the arrangement ADR-0053 exists to
   forbid. No deterministic test separates it from the fix, so the rejection rests on the overload's
   documented contract and on the sibling listener, and is recorded here rather than claimed as
   covered.
4. **Not in scope: `StopAsync` does not wait for in-flight handlers.** The sibling listener awaits its
   accept loop before returning; this server tracks no handler tasks, so a connection accepted at the
   last moment is closed shortly after `StopAsync` returns rather than before it. Closing it at all is
   this change; ordering it against the shutdown's return would mean tracking every handler task, and
   is recorded as a follow-up in `tasks.md` rather than done here.
5. Unchanged: the normal accept path, the wait between failed accepts (100 ms doubling to a 5 s cap,
   starting over after a successful accept) and the loop ending at once when the token is cancelled
   during a wait. The three tests in `AudioSocketServerAcceptBackoffTests` must pass before and after.
6. Tests, failing first, and ordered by construction rather than by a clock: an accept override that
   cancels the loop's token and then returns a real loopback connection in the same call, so the
   hand-off always sees a cancelled token; a fake clock that is never advanced, so no wait can end on
   time and the only thing that can close the connection is the server closing it; and the peer's read
   reaching end of stream as the happens-after edge on that close. One control keeps the fix from
   over-correcting into closing connections the server is still serving.
7. `Sdk/ADR-0058` records the durable rule: a token that stops a server never gates a hand-off that
   carries ownership of a resource the server has already acquired.

## Impact

- `src/Verbara.Sdk.VoiceAi.AudioSocket/AudioSocketServer.cs`: one argument and its comment. No public
  API change — `PublicAPI.Unshipped.txt` is untouched and nothing downstream recompiles.
- `Tests/Verbara.Sdk.VoiceAi.AudioSocket.Tests/AudioSocketServerEdgeCaseTests.cs`: the regression test
  and one control, on the harness that class already has (`SignalTimeout`, `ReadFromServerAsync`,
  `NextTimerAsync`, `CapturingLogger`).
- `docs/decisions/0058-*.md` plus its index row in `docs/decisions/README.md`.
- **Behaviour changes only at shutdown.** A connection accepted in the same moment the server stops is
  now closed by the server instead of being left to finalization. Each such connection runs its handler
  briefly and creates one short-lived timeout source, which the handler's `using` releases when it
  returns.
- A connection whose socket has *already* failed at that moment now reaches the handler's catch-all and
  is logged once as a connection error, at Error, where before nothing ran and nothing was logged. That
  is the handler's existing answer for a failed connection, newly reachable in one more ordering.
- No counter, gauge, activity or event changes. These connections never reached
  `ConnectionsAccepted`, `ActiveSessionCount` or `OnSessionStarted` before the fix and do not reach
  them after it.

## Architectural Risk

- **Level:** LOW.
- **Affected:** the accept path of `AudioSocketServer` in `Verbara.Sdk.VoiceAi.AudioSocket`. No public
  API and no telemetry surface, so no downstream recompile and no dashboard moves; the difference is
  visible only while the server is stopping.
- **Mitigation:** the fix removes an argument rather than adding a path — the code the connection now
  reaches is the branch that has always closed a connection for a stopping server, and is already
  covered. The mistakes each fail a committed test: restoring the token fails the regression test;
  deleting the handler's dispose fails the regression test and the two existing handler tests; passing
  the handler a token that is not the server's leaves it waiting on a clock that never moves, which the
  regression test's bound catches. The one alternative no test separates (disposing in the loop instead
  of handing over) is recorded above as rejected, with its reason.
