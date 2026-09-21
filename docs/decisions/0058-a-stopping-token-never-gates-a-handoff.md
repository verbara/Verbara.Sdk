# ADR-0058: A stopping token never gates a hand-off

- **Status:** Accepted
- **Date:** 2026-09-21
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0053 (a streaming session's ending is classified by who ended it — this ADR is that
  decision's ownership rule read one layer earlier, on the connection that has not become a session
  yet), ADR-0004 (`TreatWarningsAsErrors` on every project, which is what turns the two analyzer
  diagnostics below into build failures rather than advice), ADR-0024 (build-time analyzers as
  policy — the same arrangement, here found in force rather than introduced)

## Context

`AudioSocketServer.AcceptLoopAsync` handed every accepted connection to its handler through the
thread pool, gated on the server's own stopping token:

```csharp
var client = await AcceptAsync(ct).ConfigureAwait(false);
_ = Task.Run(() => HandleConnectionAsync(client, ct), ct);
```

That second argument is not a courtesy. The overload's contract is that the token cancels the work
item *if it has not yet started*, and "started" is decided when the pool picks the item up — not when
the loop queues it. Two orderings therefore drop the handler entirely:

| ordering | outcome |
|---|---|
| the stop lands after `AcceptAsync` returned and before the hand-off runs | the work item is created already cancelled; the handler never runs |
| the stop lands after the item is queued and before the pool starts it | the same outcome, decided by scheduling rather than by the loop |

A *pending* accept is not the hazard — a token cancelled while the accept is still waiting produces
no connection at all. The hazard is an accept that already succeeded.

**The handler is the only owner of an accepted connection**, and all of its release sites are inside
the method the token can skip: the no-identifying-frame branch, the session's own terminate once the
connection has become a session, and the catch-all. A connection joins the server's session registry
only after the identifying frame, so `StopAsync` — which cancels the token, stops the listener and
disposes the *registered* sessions — never sees one that was skipped. No counter moves either, so a
gauge reads zero over a connection that is still open. The socket is then released only if a garbage
collection eventually finalizes its handle, an unbounded delay a shutdown cannot order, and until
then the far end holds a connection a stopped server will never read from or answer.

This is ADR-0053's family one layer earlier. That decision closed a session that ends without a
hangup frame and never releases its transport; here the transport belongs to a connection that never
became a session, so the terminate ADR-0053 installed — the thing that releases it — does not yet
exist.

## Decision

**A token that stops a server never gates a hand-off carrying ownership of a resource the server has
already acquired.** The token is passed *into* the handler as an argument; the hand-off itself is
unconditional.

**R1 — the hand-off is unconditional.** Once an accept has produced a connection, the work item that
carries the only reference to it is queued whatever the stopping token says. A skipped hand-off is
not a fast shutdown; it is a leak.

**R2 — `Task.Run`'s own token argument is `CancellationToken.None`, written explicitly.** Not an
omission: `CA2016` is an error in this repo (`AnalysisLevel=latest-recommended` under ADR-0004), so
the bare two-argument call does not compile at all, and the analyzer's own remedy is the explicit
`CancellationToken.None` that says "intentionally not propagating the token".

**R3 — the stopping token still reaches the handler, as an argument.** It is what lets the handler
end its wait for the identifying frame early instead of waiting the connection timeout out, and what
lets it close the connection *quietly* — the branch that closes an unidentified connection suppresses
its warning while the stopping token is cancelled, because such a connection missed no deadline and
failed in no way.

**R4 — one owner per accepted connection.** ADR-0053's rule for a session's transport, extended to
the connection that has not become a session yet: exactly one place disposes it, and that place is
the handler. The accept loop never disposes what it has handed over.

## Consequences

- **The two accept loops in this repo now agree.** `AudioSocketServer.AcceptLoopAsync` and
  `AriOutboundListener.AcceptLoopAsync` hand their connection over with a line that is identical
  character for character, including indentation, rather than merely agreeing in spirit. The ARI
  listener already had the shape; this decision names the reason it is right and makes it the
  convention instead of a local habit.
- **The rule already has a mechanical guard, and the guard was not built for it.** Disposing in the
  accept loop — the first rejected alternative below — does not compile: `IDisposableAnalyzers` raises
  `IDISP016` ("Don't use disposed instance") on that shape, and ADR-0004 makes it an error. A second
  dispose site for a connection whose reference also escapes to a handler is exactly what that rule
  targets, so R4 is enforced at build time in this repo by an analyzer that predates this decision.
  That is a consequence worth writing down, not a design: nobody chose `IDISP016` as the guard for
  one-owner-per-accepted-connection, and it holds only for the shape it recognises.
- **The guard's reach stops there, and the sibling mistake is only caught by tests.** Handing the
  handler a *foreign* token — `HandleConnectionAsync(client, CancellationToken.None)` — compiles
  cleanly, because `CA2016` accepts an explicit `None` on the inner call as intentional
  non-propagation just as it does on `Task.Run`. Nothing at build time distinguishes it from R3.
  Measured: that mutation fails both regression tests — the one that pins the close (the handler's
  only remaining deadline is a connection timeout on a clock the test never advances) and the control
  that pins the open connection (the token it cancels to end the handler's wait no longer reaches the
  handler at all). R3 is a test-carried rule; R1 and R4 are carried by the build as well.
- **Behaviour changes only at shutdown.** A connection accepted in the same moment the server is
  asked to stop is now closed by the server instead of being left to finalization, and closed
  quietly. Each such connection runs its handler briefly and creates one short-lived timeout source,
  which the handler releases when it returns.
- **A connection whose socket had already failed at that moment is now logged once as a connection
  error**, at Error, where before nothing ran and nothing was logged. That is the handler's existing
  answer for a failed connection, newly reachable in one more ordering.
- **No counter, gauge, activity or event changes.** These connections never reached the accepted
  counter, the active-session gauge or the session-started event before the fix, and do not reach
  them after it.
- **`StopAsync` still does not wait for in-flight handlers.** A connection accepted at the last
  moment is closed shortly *after* `StopAsync` returns rather than before it. Closing it at all is
  what this decision buys; ordering it against the shutdown's return is a separate change, recorded
  as a follow-up rather than done here.

## Alternatives considered

**Check the token in the accept loop and dispose there.** Rejected on three counts, and the third was
measured rather than reasoned. It closes the first ordering and leaves the second, because the token
is re-checked when the pool starts the work item. It creates a second place that disposes an accepted
connection, which is the arrangement ADR-0053 exists to forbid. And it does not build: `IDISP016`
fails the compilation, so the shape cannot ship without a `#pragma warning disable` sitting over it
admitting what it is. With that pragma applied, the suite is **106 passed, 0 failed** — no test
separates this alternative from the decision, exactly as expected for two orderings a deterministic
test cannot pull apart. The rejection therefore rests on the overload's documented contract, on the
sibling listener and on the analyzer; it does not rest on a test, and is not claimed to.

**Wait for in-flight handlers in `StopAsync`.** Rejected as an answer to *this* defect, though it is
a reasonable change on its own terms. It orders the close against the shutdown's return; it does not
cause the close. A hand-off the pool skipped is still skipped, and a shutdown that waits for handlers
that never started waits for nothing. It also means tracking every handler task, where today the
server tracks only sessions — a real design change, and one that would arrive bundled with a leak
fix that does not need it.

**Leave the token and rely on the accept's own cancellation.** Rejected: it is the status quo argued
from the wrong end. Cancelling during the accept is indeed safe, and that safety says nothing about
the window after the accept returns, which is the only window at issue — and the one a shutdown makes
likely rather than rare.
