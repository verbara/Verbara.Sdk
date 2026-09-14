# Tasks: audiosocket-accepted-connection-is-always-closed

## 1. Reproduce before fixing

- [ ] 1.1 Write the regression test first, against the unfixed server, and record its verbatim failure.
      In `Tests/Verbara.Sdk.VoiceAi.AudioSocket.Tests/AudioSocketServerEdgeCaseTests.cs` add
      `AcceptLoopAsync_ShouldCloseTheAcceptedConnection_WhenTheServerStopsBeforeTheHandoffRuns`:
      a real loopback pair whose server end is what `AcceptOverride` returns, and an override that
      cancels the loop's own token and *then* returns that connection in the same call, so the
      hand-off that follows always sees a cancelled token — ordered by construction, not by timing.
      The server runs on a `FakeTimeProvider` that is never advanced and on a `ConnectionTimeout` well
      above it, so no wait can end on a clock and the only thing that can close the connection is the
      server closing it. Drive `AcceptLoopAsync` directly with the test's token, as the backoff tests
      do. Assert: the peer's read reaches end of stream (`ReadFromServerAsync`, bounded by
      `SignalTimeout`), exactly one accept was attempted, the loop ran to completion without throwing,
      and the `CapturingLogger` recorded nothing at Warning or above.
- [ ] 1.2 Add the control that keeps the fix from over-correcting into closing a connection the server
      is still serving: `AcceptLoopAsync_ShouldLeaveTheConnectionOpen_WhileTheHandlerIsServingIt`.
      Same harness with the token live; the override returns the connection on its first call and
      afterwards parks on an accept that ends only when the token does. Wait for the handler's timeout
      timer to appear on the fake clock (`NextTimerAsync`, due `ConnectionTimeout`) — the handler
      taking the connection over is what creates it — and assert the peer's read has not completed at
      that point. Then cancel the token and assert the read reaches end of stream and the loop ends.
      Green before the fix and after it.
- [ ] 1.3 Run the three existing accept-backoff tests before the fix and after it, unchanged:
      `AcceptLoopAsync_ShouldDoubleTheWaitUpToFiveSeconds_WhenAcceptsKeepFailing`,
      `AcceptLoopAsync_ShouldStartTheWaitOver_WhenAnAcceptSucceeds` and
      `AcceptLoopAsync_ShouldEndWithoutWaitingOutTheBackoff_WhenCancelledDuringIt`. They carry the
      "unchanged" half of the requirement — the 100/200/400/800/1600/3200/5000/5000 ms sequence, the
      restart after a successful accept, and the loop ending at once when the token cancels during a
      wait — and the second of them also proves a connection accepted with a live token still reaches
      the handler, because it filters that handler's timeout timer off the fake clock.

## 2. Fix

- [ ] 2.1 In `src/Verbara.Sdk.VoiceAi.AudioSocket/AudioSocketServer.cs:108`, hand the accepted
      connection over unconditionally — `_ = Task.Run(() => HandleConnectionAsync(client, ct));` —
      and replace the line's silence with a comment that says why the stopping token is an argument
      and not a gate. Confirm by reading that the handler still receives `ct`, that its
      no-identifying-frame branch (lines 182-189) is the single place that closes such a connection,
      and that the branch stays quiet while `ct` is cancelled.
- [ ] 2.2 Sweep `src/` for every other `Task.Run(…, token)` whose delegate owns a resource nothing
      else can release, and record what you find here without changing it. Known before the sweep:
      `AudioSocketServer.cs:80` (the accept loop itself, gated on the host's *start* token — it leaks
      no connection, because the listener is the server's own and `StopAsync`/`DisposeAsync` close it,
      but a cancelled start token leaves a bound server that accepts nothing) and
      `src/Verbara.Sdk.Push.Nats/NatsBridge.cs:236` (a subscribe loop per filter). The contrast is
      `src/Verbara.Sdk.Ari/Outbound/AriOutboundListener.cs:99,149`, which already uses
      `CancellationToken.None` for both. Fix nothing outside `AcceptLoopAsync`; anything that deserves
      work becomes an open change, not a line in a PR description.
      Also known: `AudioSocketServer.cs:199` unregisters with the key-only
      `_sessions.TryRemove(channelId, out _)` inside the member this change reads — the same defect
      the ARI change records for its sibling server. Record it here; do not fix it in this change.
- [ ] 2.3 Record the follow-up this change deliberately does not do: `StopAsync` does not wait for
      in-flight handlers, so a connection accepted at the last moment is closed shortly after
      `StopAsync` returns rather than before it. Ordering it would mean tracking every handler task,
      as the sibling listener does for its accept loop. Write it into an open change or leave it here
      with the reason — not only in the PR prose.

## 3. Decision record

- [ ] 3.1 Land `docs/decisions/0058-a-stopping-token-never-gates-a-handoff.md` (`Sdk/ADR-0058`) in this
      change, and add its row to `docs/decisions/README.md`. One page: the rule (a token that stops a
      server never gates a hand-off carrying ownership of a resource the server already acquired — it
      is passed *into* the handler instead), the two rejected alternatives (checking the token in the
      loop and disposing there; waiting for handlers in `StopAsync`), the consequence (one owner per
      accepted connection, matching ADR-0053's rule for a session's transport, extended to the
      connection that has not become a session yet), and the two sites that now agree
      (`AudioSocketServer`, `AriOutboundListener`). Reference `Sdk/ADR-0053`, which this change's
      `decision_ref` cites and which ADR-0058 extends.

- [ ] 3.2 Repoint this change's `decision_ref` to `Sdk/ADR-0058` once that file exists
- [ ] 3.3 At archive time, widen the `streaming-session-lifecycle` `## Purpose` so it covers
      the server accept path this requirement files under it — or, if the owner prefers, split the
      requirement into its own capability before archiving
## 4. Verification

- [ ] 4.1 `dotnet build Verbara.Sdk.slnx -c Release`: 0 warnings, 0 errors
      (`TreatWarningsAsErrors`, `WarningLevel 9999`).
- [ ] 4.2 Unit lane green under the CI filter
      (`Category!=Functional&Category!=Integration&Category!=Realtime&Category!=Spike`), with
      `Verbara.Sdk.Governance.Tests` included, and `tools/audit-test-asserts.sh` at `Violations: 0`.
      Record the per-assembly counts for `Verbara.Sdk.VoiceAi.AudioSocket.Tests` before and after.
- [ ] 4.3 Mutations, each applied alone to the fixed tree, built, run against
      `Verbara.Sdk.VoiceAi.AudioSocket.Tests`, then restored. Each must fail at least one test, and
      the verbatim failure goes in this file:
      (a) restore `, ct` on the hand-off — 1.1 fails at the peer read;
      (b) delete `client.Dispose()` from the no-identifying-frame branch — 1.1 fails, and so do the
      two existing handler tests that assert the peer read reaching end of stream;
      (c) hand over but pass the handler `CancellationToken.None` instead of `ct` — 1.1 fails, because
      the handler then waits for a deadline on a clock the test never advances.
      Record explicitly that mutation (d), disposing in the accept loop instead of handing over, fails
      no test: it is the alternative the proposal rejects for the queued-then-started ordering, which
      no deterministic test can separate, and the rejection rests on the overload's documented
      contract and on the sibling listener.
- [ ] 4.4 The two new tests pass 20 runs in a row against a Release build, with the per-run duration
      recorded — nothing here may end on `SignalTimeout`, which is a failure bound and never a pace.
- [ ] 4.5 `openspec validate --all --strict` green.
- [ ] 4.6 CI green through the merge queue.

## 5. Close-out

- [ ] 5.1 `CHANGELOG.md` `[Unreleased]` entry under `### Fixed`, stating that a connection accepted in
      the same moment the server was asked to stop was left open with no owner and is now closed, and
      that a connection whose socket had already failed can now be logged once as a connection error.
      Leave the `(#N)` citation for close-out. No `PackageVersion` bump here: publishing is the
      release train's job (`Sdk/ADR-0055`), and this change ships no public API.
- [ ] 5.2 `openspec archive audiosocket-accepted-connection-is-always-closed --yes` once the fix is on
      `main`, as its own `docs(openspec):` PR, with the feature PR's number backfilled into the
      CHANGELOG entry.
