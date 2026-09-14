# Tasks: session-shutdown-token-is-cancelled-by-the-stop-phase

## 1. Reproduce before fixing

- [ ] 1.1 Add a file-local session store to `Tests/Verbara.Sdk.Hosting.Tests/SessionManagerHostedServiceTests.cs`
      that records the token each `SaveAsync` was handed and parks the save until that token is
      cancelled, ending it with `OperationCanceledException`. Copy the delay-free shape of
      `PendingSaveStore` in `Tests/Verbara.Sdk.Sessions.Tests/CallSessionManagerTests.cs` (it
      registers on the token, so cancelling resumes the save before `Cancel` returns and the
      assertion can read the result immediately). No `Task.Delay` and no wall-clock wait anywhere in
      the new tests — the test-determinism capability forbids a fixed delay standing in for "it has
      finished reacting", and the existing #261 tests prove it is not needed here.
- [ ] 1.2 Write `StartAsync_ShouldLeaveAnInFlightSaveRunning_WhenTheStartTokenIsCancelled` first,
      against the unfixed service, and record its verbatim failure in this file. Start the service
      with the token of a test-owned source, drive `server.Channels.OnNewChannel(...)` so one save is
      in flight, then cancel that source. Assert the token the store was handed is not cancelled, the
      save is still in flight, and nothing was logged at Error or above.
- [ ] 1.3 Write `StopAsync_ShouldCutShortAnInFlightSave_WhenTheStopTokenIsCancelled` first, against
      the unfixed service, and record its verbatim failure. With a save in flight, call `StopAsync`
      with the token of a test-owned source and cancel that source; assert the save ended with an
      `OperationCanceledException`, and that nothing was logged at Error or above because the filter
      at `CallSessionManager.cs:55` swallowed it.
- [ ] 1.4 Add the controls, which must pass before the fix and after it:
      `StopAsync_ShouldLeaveAnInFlightSaveRunning_WhenTheStopTokenIsNeverCancelled` (a stop inside
      its budget does not cut a save short — this is the control that keeps the fix from
      over-correcting into "cancel everything at stop"), and
      `StopAsync_ShouldNotStartASave_WhenAServerEventArrivesAfterTheStop` (the detach runs before the
      stop token is wired). Confirm the three tests #261 added remain untouched and green:
      `OnChannelAdded_ShouldNotLogPersistError_WhenShutdownCancelsInFlightSave`,
      `OnChannelAdded_ShouldNotLogPersistError_WhenSaveStartsAfterShutdownCancelled` and
      `OnChannelAdded_ShouldLogPersistError_WhenStoreCancelsSaveWithoutShutdown`
      (`Tests/Verbara.Sdk.Sessions.Tests/CallSessionManagerTests.cs:211-292`).
- [ ] 1.5 Add `Dispose_ShouldCutShortAnInFlightSave_WhenTheServiceIsDisposedWithoutAStop`, covering
      the host that tears the service down without a graceful stop.
- [ ] 1.6 Pin the wiring through a real host. Add `<PackageReference Include="Microsoft.Extensions.Hosting" />`
      to `Tests/Verbara.Sdk.Hosting.Tests/Verbara.Sdk.Hosting.Tests.csproj` (the version is already
      pinned centrally in `Directory.Packages.props`, so no version attribute), then:
      `HostStartAsync_ShouldLeaveAnInFlightSaveRunning_WhenTheStartIsAborted` — a second hosted
      service registered after this one parks until the test cancels the token it passed to
      `Host.StartAsync`, so the host's start token is cancelled with this service already started —
      and `HostStopAsync_ShouldCutShortAnInFlightSave_WhenTheShutdownIsNoLongerGraceful`, driven by a
      `HostOptions.ShutdownTimeout` that makes the stop token already cancelled when `StopAsync`
      runs. Record in this file which mechanism the pinned host version honours; neither test may
      wait on a clock.
- [ ] 1.7 Measure, do not assume, what the host's start token does today, because it decides how the
      defect reads in production: with the **unfixed** service on a real host, does a normal stop
      cancel the token the manager is holding (the combined start token still linked to
      `ApplicationStopping`) or not (that source released at the end of the start)? Record the
      measured answer here and carry it into ADR-0059. Under the first reading today's code cuts
      every in-flight save short at the first instant of shutdown; under the second it never cuts one
      short at all. The proposal's claim — that the filter's intended case is unreachable — holds
      either way, and 744b9d81's body asserts the second; this task turns that assertion into a
      measurement.

## 2. Fix

- [ ] 2.1 In `src/Verbara.Sdk.Hosting/SessionManagerHostedService.cs`, own the source: a
      `private readonly CancellationTokenSource _shutdown = new()`, handed to
      `csm.SetShutdownToken(_shutdown.Token)` in `StartAsync` in place of the parameter. `StartAsync`
      keeps ignoring its own token — it starts nothing that can be aborted.
- [ ] 2.2 In `StopAsync`, keep `DetachFromServer("default")` first, then register the host's stop
      token onto the source: `cancellationToken.UnsafeRegister(static s => ((CancellationTokenSource)s!).Cancel(), _shutdown)`
      — a static callback with the source as state, so no closure allocation and no `ExecutionContext`
      capture, which is the shape this AOT-first repo wants. Keep the returned registration in a
      field and dispose any previous one first, so a second `StopAsync` cannot leak one. Do **not**
      cancel the source on entry: an already-cancelled stop token cancels it synchronously through
      the registration, and a graceful one leaves the save its budget.
- [ ] 2.3 Implement `IDisposable` on the service: dispose the registration, cancel `_shutdown`, then
      dispose it. Cancelling before releasing is the load-bearing order. Record in this file what a
      save started *after* that disposal does (`CancellationToken.Register` on the token of a
      disposed-but-cancelled source), measured, and carry the answer into ADR-0059 — no new
      behaviour is scoped for it here.
- [ ] 2.4 Correct the two comments in `src/Verbara.Sdk.Sessions/Manager/CallSessionManager.cs` that
      describe the old wiring: the XML doc on `SetShutdownToken` (line 43) should say which phase
      cancels the token and that every save runs under it, not only the ones at shutdown; the catch
      comment (lines 57-60) should stop naming "the host's StartAsync token". Leave both member names
      alone — `SetShutdownToken` is `internal` but visible outside this repo through
      `InternalsVisibleTo`, so a rename is a cross-repo edit, not a one-PR one.
- [ ] 2.5 Sweep the other hosted services in `src/Verbara.Sdk.Hosting/` for the same shape — a token
      received in `StartAsync` and stored for use after the start has finished. Fix it here only if
      it is `SessionManagerHostedService`; record everything else in this file without changing it.
      Expected from the reading done while drafting: `AmiConnectionHostedService`,
      `AriConnectionHostedService`, `AriAudioHostedService`, `AriOutboundListenerHostedService` and
      `VerbaraServerHostedService` each pass the start token to a start call and the stop token to a
      stop call and keep neither; `SessionReconciliationService` links its loop source over the start
      token but owns that source and cancels it in its own `StopAsync`.
- [ ] 2.6 Record, without changing it, that `AddVerbaraSessionsMultiServer`
      (`src/Verbara.Sdk.Hosting/ServiceCollectionExtensions.cs:201-213`) registers no hosted service,
      so on that path `_shutdownToken` stays `default` and no save is ever cut short. Carry it into
      ADR-0059 as a known gap so it is not rediscovered as a bug.

## 3. Verification

- [ ] 3.1 `dotnet build Verbara.Sdk.slnx -c Release`: 0 warnings, 0 errors (`TreatWarningsAsErrors`,
      `WarningLevel 9999`). Confirm no analyzer fired on the new disposable field.
- [ ] 3.2 Unit lane green under the CI filter
      (`--filter "Category!=Functional&Category!=Integration&Category!=Realtime&Category!=Spike"`),
      `Verbara.Sdk.Governance.Tests` included, with coverage collected; `tools/audit-test-asserts.sh`
      reports `Violations: 0`. Record the assembly and test counts, and the coverage band.
- [ ] 3.3 Mutation check — for each mistake, name the committed test that fails: keeping the start
      token (1.2 and 1.3); cancelling the source on entry to `StopAsync` (1.4's in-budget control);
      dropping the stop registration (1.3 and 1.6); releasing the source without cancelling it (1.5);
      registering the stop token before the detach (1.4's ordering control).
- [ ] 3.4 `openspec validate --all --strict` clean.
- [ ] 3.5 Prove the public surface did not move: `git diff --stat -- '*PublicAPI*'` is empty, so
      neither Pro nor Platform needs a recompile or a pin bump.

## 4. Decision record

- [ ] 4.1 Land `docs/decisions/0059-a-lifetime-token-is-cancelled-by-the-phase-it-names.md` (0055 is
      the highest id on this branch). Status Accepted; Related: ADR-0054 (one owner for a
      cancellation source — this ADR applies the same rule to a token handed across a lifetime
      boundary), ADR-0053 (an ending is classified by who ended it). It states the rule — a component
      that outlives a phase creates its own source; a token borrowed from `StartAsync` may not be
      stored — and carries the three measurements this change makes: 1.7 (what the host's start token
      does today), 2.3 (a save started after disposal) and 2.6 (the multi-server path has no lifetime
      token at all).
- [ ] 4.2 Update this change's `proposal.md` `decision_ref` to `Sdk/ADR-0059` once that file exists,
      so the living record points at the ADR this change wrote rather than at its nearest neighbour.

## 5. Close-out

- [ ] 5.1 `CHANGELOG.md` `[Unreleased]` entry under `### Fixed`, stating in operator terms what moves:
      a save in flight when the host stops now keeps the host's shutdown budget and is cut short when
      that budget expires, instead of being governed by a token that only an aborted start cancels.
      Leave the `(#N)` citation for close-out.
- [ ] 5.2 `openspec archive session-shutdown-token-is-cancelled-by-the-stop-phase --yes` once the fix
      is on `main` (the CLI, never the agent archive path). This change creates a new capability, so
      after the archive check that `openspec/specs/session-persistence-lifecycle/spec.md` exists and
      author its `## Purpose` to match the other living specs, then re-run
      `openspec validate --all --strict`.
