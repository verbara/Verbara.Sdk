---
tier: PEQUEÑO
owner: Harol
approver: Harol
stakeholder: Operators who run the SDK as a hosted process and read its persistence error log at shutdown, and applications whose session state has to survive a stop
decision_ref: Sdk/ADR-0054
---

# Proposal: session-shutdown-token-is-cancelled-by-the-stop-phase

## Why

`SessionManagerHostedService` receives two cancellation tokens from the host and uses the wrong one.

| the host's token | what the contract says it means | what this service does with it |
|---|---|---|
| `StartAsync(cancellationToken)` | *"Indicates that the start process has been aborted"* | hands it to `CallSessionManager.SetShutdownToken` and the manager keeps it for the life of the process (`SessionManagerHostedService.cs:15`) |
| `StopAsync(cancellationToken)` | *"Indicates that the shutdown process should no longer be graceful"* | nothing; `StopAsync` only unsubscribes from the server (`SessionManagerHostedService.cs:21-26`) |

Both quotes are the parameter documentation `IHostedService` ships with, in
`Microsoft.Extensions.Hosting.Abstractions`.

The stored token is not only a shutdown token. `CallSessionManager` persists every session change
fire-and-forget — 14 `_ = PersistAsync(session)` call sites — and each one saves under that field:
`await _store.SaveAsync(session, _shutdownToken)` (`CallSessionManager.cs:53`). Every save the SDK
makes, from the first channel event to the last, runs under a token whose documented meaning is
*this process failed to start*.

Two consequences follow, and the second is why this is a change of its own rather than a comment.

**A stop cannot cut a save short.** The host cancels the start token when the start is aborted — a
stop requested while the process is still starting, or `HostOptions.StartupTimeout` elapsing. A stop
that arrives after a completed start does not touch it, so there is no moment at which the SDK ends
a save because the host has run out of shutdown budget. Whether an in-flight save survives the stop
is decided by the process exiting, not by anything this repo wrote.

**The one filter that reads the token is unreachable in the phase it was written for.** #261
(744b9d81) added `catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested)`
(`CallSessionManager.cs:55`) so that a save the shutdown cut short is not logged as
`Failed to persist session {SessionId}`. The filter is correct and the reasoning behind it is
correct. The token it filters on is the wrong one, and both the code and the history already say so
in prose: the comment inside the clause reads *"The shutdown token (with the SDK's hosted service,
the host's StartAsync token) was cancelled, for example by a stop requested before startup
completed"* (`CallSessionManager.cs:57-60`), and 744b9d81's own body records that *"a normal stop
after a completed start does not cancel it"*. So in production the clause is reachable only inside
the startup window; the case it exists for — a graceful shutdown that ran out of time with a save
still in flight — cannot occur. Its three regression tests reach it by calling the internal
`SetShutdownToken` directly (`CallSessionManagerTests.cs:211-292`), which is the only caller that
ever cancels it.

The rule this violates is the one ADR-0054 R2 states for a different source: a cancellation source
has exactly one owner, and everyone else may only express intent. A token a component keeps for its
whole lifetime has to be created by something that outlives the phase it was handed in. A token
borrowed from the start phase is not that.

## What Changes

1. `SessionManagerHostedService` creates and owns a `CancellationTokenSource`, and hands the manager
   **its** token in `StartAsync`. Aborting the start no longer cancels anything the manager holds.
2. `StopAsync` registers the host's stop token onto that source, so the source is cancelled exactly
   when the host says the shutdown is no longer graceful — its stop budget elapsed, or whoever asked
   for the stop cancelled it. It does not cancel the source on entry: the point of a graceful stop
   is that a save in flight gets to finish.
3. The existing `DetachFromServer` stays first in `StopAsync`, so no new save starts from a server
   event while that budget is running.
4. `Dispose` cancels the source before releasing it, so a save that outlived the whole stop is cut
   short at teardown rather than left holding a token that can never be cancelled. The type gains
   `IDisposable` because it now owns a disposable field.
5. Nothing is renamed. `SetShutdownToken` and `_shutdownToken` are inaccurate names for what is
   really the manager's ambient persistence token, but `SetShutdownToken` is `internal` and reachable
   through `InternalsVisibleTo` from outside this repo, so renaming it is a cross-repo edit and this
   change is one repo. The two comments that describe the old behaviour are corrected instead.
6. Tests, failing first: an aborted start that must leave an in-flight save running, and a stop whose
   token cancels and must cut that save short — both against the unfixed service, plus the same two
   facts measured end to end through a real host. Controls pin that a stop inside its budget leaves
   the save running, and that the three behaviours #261 shipped are unchanged.

## Impact

- `src/Verbara.Sdk.Hosting/SessionManagerHostedService.cs`: the source, the registration, `Dispose`.
  The type is `internal` and appears in no `PublicAPI.Shipped.txt`. **No public API change**, so
  nothing downstream recompiles.
- `src/Verbara.Sdk.Sessions/Manager/CallSessionManager.cs`: comments only — the XML doc on
  `SetShutdownToken` (line 43) and the catch comment (lines 57-60), both of which describe the token
  that is going away.
- `Tests/Verbara.Sdk.Hosting.Tests/`: the regression tests, and one centrally-pinned
  `Microsoft.Extensions.Hosting` package reference so two of them can drive a real host.
- **What an operator sees at shutdown.** A save still in flight when the stop budget expires now ends
  with an `OperationCanceledException` that the existing filter swallows: no new line at Error, and
  the shutdown no longer waits on a save nothing was going to cancel. A save that fails for any other
  reason, and a store that cancels a save on its own while the shutdown token is still live, are
  still logged at Error exactly as #261 left them.
- **What is deliberately not fixed.** `AddVerbaraSessionsMultiServer` registers no hosted service at
  all (`ServiceCollectionExtensions.cs:201-213`), so in a multi-server deployment the manager's token
  is `default` and no save is ever cut short. That path attaches and detaches manually by design;
  giving it a lifetime token is a separate change, and this one records it rather than widening.
- Downstream (Pro, Platform): nothing to recompile, no telemetry meaning moves,
  no event changes shape.

## Architectural Risk

- **Level:** LOW.
- **Affected:** the persistence path of `CallSessionManager` in every single-server host, which is
  every deployment wired by `AddVerbaraSessions` / `AddVerbaraSessionsBuilder`. The failure mode a
  mistake here produces is a save cut short too early — session state lost at shutdown — so the
  control that a stop inside its budget leaves an in-flight save running is as load-bearing as the
  regression test itself.
- **Mitigation:** the source has exactly one owner, the same object that hands it out, and it is
  cancelled in exactly two places (the stop registration and `Dispose`). Each plausible mistake fails
  a committed test: keeping the start token fails the abort test and the stop test; cancelling the
  source on entry to `StopAsync` fails the in-budget control; forgetting the registration fails the
  stop test; releasing the source without cancelling it fails the teardown test; moving the detach
  after the registration fails the ordering scenario. Two residuals are written down rather than
  guarded: `Cancel()` inside `Dispose` surfaces an `AggregateException` if a store's own cancellation
  callback throws, and what happens to a save started *after* the service is disposed is measured and
  recorded in the ADR rather than asserted here.
