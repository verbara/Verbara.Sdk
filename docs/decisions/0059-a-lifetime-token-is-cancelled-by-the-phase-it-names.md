# ADR-0059: A lifetime token is cancelled by the phase it names

- **Status:** Accepted
- **Date:** 2026-09-21
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0054 (one owner for a cancellation source, and everyone else may only express
  intent — this ADR applies that rule to a token handed *across a lifetime boundary*, where the
  second owner is not a member of the same class but a component that outlives the phase),
  ADR-0053 (an ending is classified by who ended it — the classification this change finally makes
  reachable, because the ending it names could not occur), ADR-0045 (why neither host test is
  allowed a clock, which decided the mechanism one of them uses)

## Context

`SessionManagerHostedService` receives two cancellation tokens from the host and used the wrong one.

| the host's token | what `IHostedService` documents it to mean | what the service did with it |
|---|---|---|
| `StartAsync(cancellationToken)` | *"Indicates that the start process has been aborted"* | handed it to `CallSessionManager.SetShutdownToken`, and the manager kept it for the life of the process |
| `StopAsync(cancellationToken)` | *"Indicates that the shutdown process should no longer be graceful"* | nothing; `StopAsync` only unsubscribed from the server |

That stored token is not a shutdown token. `CallSessionManager` persists every session change
fire-and-forget — fourteen `_ = PersistAsync(session)` call sites — and each one saves under the same
field: `await _store.SaveAsync(session, _shutdownToken)`. Every save the SDK makes, from the first
channel event to the last, ran under a token whose documented meaning is *this process failed to
start*.

**The borrowed token is inert after a completed start — measured three ways, not argued.** On the
pinned `Microsoft.Extensions.Hosting` 10.0.10:

1. **The unfixed service on a real `IHost`.** Started normally, one save in flight under the token
   the manager stores, then a normal graceful `host.StopAsync(CancellationToken.None)`:

       token after a COMPLETED start: CanBeCanceled=True IsCancellationRequested=False registrationAccepted=True saveObserved=False
       token after a NORMAL graceful stop: IsCancellationRequested=False saveObserved=False ApplicationStopping.IsCancellationRequested=True ApplicationStopped.IsCancellationRequested=True
       token after lifetime.StopApplication(): IsCancellationRequested=False

   The shutdown really ran — both lifetime tokens are cancelled — and the manager's token still is
   not.
2. **The mechanism, decompiled.** `Host.StartAsync` wraps its combined token in a `using`:

       using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _applicationLifetime.ApplicationStopping))

   The link to `ApplicationStopping` is real, and it lives only *during* the start: the source is
   released the moment the start returns. So both plausible readings are half right — measured on
   the same package, `ApplicationStopping` fired from inside a hosted service's `StartAsync` **does**
   cancel the start token, and a linked source released before its parent cancels never cancels.
3. **A dead token is silent, not loud.** `Register` on the token of a source that was released
   without ever being cancelled is *accepted* — `IsCancellationRequested=False`,
   `CanBeCanceled=True`, no exception — and the callback is simply never invoked. The defect
   therefore threw nothing and logged nothing: a save in flight at shutdown just held a token nobody
   could cancel.

Two consequences followed, and the second is why this became a change of its own rather than a
comment. A stop could not cut a save short: whether an in-flight save survived was decided by the
process exiting, not by anything this repo wrote. And **the one filter that reads the token was
unreachable in the phase it was written for.** #261 added
`catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested)` so that a save
the shutdown cut short is not logged as `Failed to persist session {SessionId}`. The filter is
correct and its reasoning is correct; the token was wrong, so in production the clause was reachable
only inside the startup window, and the case it exists for — a graceful shutdown that ran out of
budget with a save still in flight — could not occur at all. Its three regression tests reached it by
calling the `internal` `SetShutdownToken` directly, which was the only caller that ever cancelled it.

The rule this violates is the one ADR-0054 R2 states for a different source. A token a component
keeps for its whole lifetime has to come from something that outlives the phase it was handed in,
and a token borrowed from the start phase is not that.

## Decision

**A token a component keeps past a phase comes from a source that outlives that phase, and it is
cancelled by the phase it names.** A phase token may be used *within* its phase and may not be
stored.

**R1 — the hosted service owns the source.** `private readonly CancellationTokenSource _shutdown = new()`,
created with the service and handed to the manager in `StartAsync`. The `StartAsync` parameter is
deliberately unused, with a comment saying so: this start begins nothing that can be aborted.

**R2 — a borrowed phase token is not stored, and not linked either.** Linking the start token into
the owned source would keep exactly the coupling this removes — an aborted start would still cancel
every future save — so the parameter is *replaced*, not combined.

**R3 — the stop phase cancels the source, through a registration rather than on entry.** `StopAsync`
registers the host's stop token onto the source with a static callback and the source as state, so
there is no closure and no `ExecutionContext` capture. It does **not** cancel on entry: a graceful
stop is exactly the case in which an in-flight save is still worth its budget. An
already-cancelled stop token runs the callback inline, which is how the "no longer graceful" case is
reached without a clock.

**R4 — the service unsubscribes from the server before it wires that registration.** The order of
those two statements is load-bearing, not cosmetic: the `Cancel()` the registration can run inline
must not reach a manager that is still attached.

**R5 — whoever owns a source owes an idempotent release, and cancels before releasing.** `Dispose`
cancels `_shutdown` and then disposes it, so a save that outlived the whole stop ends at teardown
instead of being left under a token nothing can ever cancel; and it is gated, because
`IDisposable` requires a second call to be ignored.

## Consequences

- **What an operator sees at shutdown changes.** A save still in flight when the host's stop budget
  expires now ends with an `OperationCanceledException` that #261's filter swallows: no new line at
  Error, and the shutdown no longer waits on a save nothing was going to cancel. A save that fails
  for any other reason, and a store that cancels a save on its own while the persistence token is
  still live, are still logged at Error exactly as #261 left them. ADR-0053's classification —
  the ending belongs to whoever ended it — finally applies to an ending that can actually happen.
- **`HostOptions.ShutdownTimeout` cannot make the stop token arrive already cancelled**, and the
  next reader will assume it can. `Host.StopAsync` turns that option into
  `CreateLinkedTokenSource(cancellationToken)` + `CancelAfter(_options.ShutdownTimeout)`, and
  `CancelAfter` fires on a timer even at zero — measured on the pinned package,
  `new CancellationTokenSource(TimeSpan.Zero).IsCancellationRequested` is `True` immediately because
  the *constructor* special-cases zero, while `CancelAfter(TimeSpan.Zero)` leaves it `False`, on a
  plain source and a linked one alike. A zero `ShutdownTimeout` would therefore be a race against a
  timer, which is the wall-clock dependency ADR-0045 forbids. The deterministic route is an
  already-cancelled token passed to `host.StopAsync`: the host links the caller's token into the one
  it hands each service, so `StopAsync` is still invoked and its token is cancelled on entry.
- **A save started *after* the service is disposed is cut short immediately, not faulted.** Measured
  against the fixed service: `Dispose` does not detach from the server, so a channel event arriving
  afterwards still reaches `PersistAsync` and saves under the released source's token —
  `CanBeCanceled=True IsCancellationRequested=True`, `Register` accepted and the callback invoked
  **inline**, `ThrowIfCancellationRequested` throwing `OperationCanceledException`, and **no
  `ObjectDisposedException` anywhere**. Releasing a source forbids `Cancel`/`CancelAfter` on the
  source; it never breaks reads of a token already handed out. So such a save ends at its first
  cancellation check and the filter swallows it. Before the fix the behaviour was the opposite in
  kind: the token could never be cancelled, so the save ran to completion or hung.
- **`Dispose` had to be *gated*, not merely ordered.** The first implementation was correctly ordered
  and still wrong: a second call reached `Cancel()` on a released source and threw
  `ObjectDisposedException`, which violates `IDisposable`'s own contract — inside a change whose
  whole subject is owning and releasing a cancellation source correctly. Closed tests-first with
  `Dispose_ShouldNotThrow_WhenCalledMoreThanOnce`, run red against the ungated version, then fixed
  with an `Interlocked.Exchange` gate. It is not academic even though the container disposes exactly
  once: both registrations are container-created singletons, so this `Dispose` really runs in
  production, and a type that throws on a second call is a trap for anyone who wraps or re-registers
  it. Generalised: **owning a source is not only a cancel-before-release obligation, it is an
  idempotency obligation.**
- **R4's ordering was unbound until this change bound it, and two artifacts wrongly claimed
  otherwise.** The mutation check moved the registration above the detach: the build stayed at
  0 warnings and the whole assembly stayed green, as did every tree-scanning guard. The control that
  had been credited with catching it passes a stop token it never cancels, so the registration never
  fires and both statements have run by the time `StopAsync` returns — the detach is in place
  whichever order they are written in. A probe then proved the order really is load-bearing: with the
  mutation, a channel event arriving inline with the registration's `Cancel()` reaches a manager that
  is still attached, creates a session and starts a new save *after* the stop — which the living spec
  forbids in so many words. A test now binds it
  (`StopAsync_ShouldNotStartASave_WhenTheStopTokenIsAlreadyCancelledAndAnEventArrivesInline`),
  proved red against the mutation before being accepted, and both artifacts were corrected.
- **One survivor is deliberately left unbound, and the distinction is the point.** Deleting the
  `_stopRegistration.Dispose()` that stops a second `StopAsync` leaking its registration leaves the
  suite entirely green, and it stays implemented and recorded as owed rather than guarded. The rule
  separating it from the case above: **a scenario the spec states must be bound by a test; an
  internal ordering or hygiene detail no requirement mentions may be recorded instead.** R4 is in
  the living spec, so an unbound mutation there is a requirement with no test; the registration
  disposal is one method's housekeeping against a path nothing in this change reaches.
- **The multi-server path has no lifetime token at all, and that is a known gap, not a bug to
  rediscover.** `AddVerbaraSessionsMultiServer` registers no hosted service, so on that path
  `_shutdownToken` stays `default`, no save is ever cut short, and nothing this ADR decides applies.
  That registration attaches and detaches manually by design; giving it a lifetime token is a
  separate change.
- **Nothing is renamed and no public surface moves.** `SetShutdownToken` and `_shutdownToken` are
  inaccurate names for what is really the manager's ambient persistence token, but `SetShutdownToken`
  is `internal` and reachable outside this repo through `InternalsVisibleTo`, so renaming it is a
  cross-repo edit. The two comments that described the old wiring were corrected instead. Both
  touched symbols are `internal` and in no `PublicAPI.Shipped.txt`, so no downstream package
  recompiles.

## Alternatives considered

**Link the start token into the owned source.** Rejected: it looks conservative and changes nothing
that matters. An aborted start would still cancel the token every future save runs under, which is
the defect, and the regression test's assertion that the save's token is not the start token would
still fail. The parameter has to be replaced, not combined.

**Cancel the source on entry to `StopAsync`.** Rejected, and it is the over-correction a reader
reaches for first: it makes the stop cut *every* in-flight save short at the first instant of
shutdown, discarding exactly the session state a graceful shutdown exists to save. Caught by the
in-budget control and by the pre-`Cancel` guard inside the withdrawn-grace test, both of which exist
for this mistake.

**Rename `SetShutdownToken` and `_shutdownToken` to say "persistence token".** Right on the merits
and rejected on scope: the member is reachable from another repo through `InternalsVisibleTo`, so the
rename is a cross-repo edit and this was one repo. Corrected prose now carries what the names do not.

**Drive the withdrawn-grace test from a zero `HostOptions.ShutdownTimeout`.** Rejected once
`CancelAfter` was measured: it fires on a timer at zero, so the test would race a clock. An
already-cancelled token passed to `host.StopAsync` reaches the same state by construction.

**Leave the wiring and document the filter's real reachability.** Rejected. It is the honest version
of doing nothing, and it concedes that the SDK has no moment at which it ends a save because the host
ran out of shutdown budget — which is the behaviour #261 was written to shape, and the behaviour an
operator reading the persistence error log at shutdown assumes exists.
