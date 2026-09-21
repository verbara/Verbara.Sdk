# Tasks: session-shutdown-token-is-cancelled-by-the-stop-phase

## 1. Reproduce before fixing

> Every failure and count below was produced on this branch with **`src/` untouched** — the unfixed
> service — by `dotnet test … -c Release`. In the pasted blocks the absolute machine path is replaced
> by `<repo>` (nothing under `openspec/` carries an absolute path) and the FluentAssertions frames
> between the failing assertion and the test method are elided with one marker line; every message is
> otherwise verbatim.

- [x] 1.1 Add a file-local session store to `Tests/Verbara.Sdk.Hosting.Tests/SessionManagerHostedServiceTests.cs`
      that records the token each `SaveAsync` was handed and parks the save until that token is
      cancelled, ending it with `OperationCanceledException`. Copy the delay-free shape of
      `PendingSaveStore` in `Tests/Verbara.Sdk.Sessions.Tests/CallSessionManagerTests.cs` (it
      registers on the token, so cancelling resumes the save before `Cancel` returns and the
      assertion can read the result immediately). No `Task.Delay` and no wall-clock wait anywhere in
      the new tests — the test-determinism capability forbids a fixed delay standing in for "it has
      finished reacting", and the existing #261 tests prove it is not needed here.
      **Recorded:** `PendingSaveStore` + `PendingSave` (an `IValueTaskSource`) are file-local in
      `SessionManagerHostedServiceTests.cs`, the same delay-free shape as the Sessions copy: the
      registration ends the save on the cancelling thread, so the persist has resumed, read the
      exception and finished its catch before `Cancel()` returns. Two more file-local helpers came
      with them — `RecordingLogger` (+ `LogEntry`), so a test can assert nothing was logged at Error
      or above, and `ParkedStartHostedService` for 1.6, which parks on a `TaskCompletionSource`
      completed by the host's own start-token registration. No `Task.Delay`, `Thread.Sleep`,
      `SpinWait` or `Stopwatch` loop, and no wall-clock wait of any kind:
      `Verbara.Sdk.Governance.Tests` is **129 passed / 0 failed**, so
      `SyncFenceRegressionGuardTests.Guard_ShouldNotExceedBaseline_InTestTree` still holds this file
      at the allowance of 0 it gets by being absent from `sync-fence-baseline.json`, and that
      baseline is **unchanged**.

- [x] 1.2 Write `StartAsync_ShouldLeaveAnInFlightSaveRunning_WhenTheStartTokenIsCancelled` first,
      against the unfixed service, and record its verbatim failure in this file. Start the service
      with the token of a test-owned source, drive `server.Channels.OnNewChannel(...)` so one save is
      in flight, then cancel that source. Assert the token the store was handed is not cancelled, the
      save is still in flight, and nothing was logged at Error or above.
      **Recorded failure:**

          Failed Verbara.Sdk.Hosting.Tests.SessionManagerHostedServiceTests.StartAsync_ShouldLeaveAnInFlightSaveRunning_WhenTheStartTokenIsCancelled [< 1 ms]
          Error Message:
             Expected save.Token.IsCancellationRequested to be False because the token the host passes to StartAsync means the start was aborted, and aborting a start must not cancel the token every save runs under, but found True.
          Stack Trace:
             at FluentAssertions ... (frames elided)
             at Verbara.Sdk.Hosting.Tests.SessionManagerHostedServiceTests.StartAsync_ShouldLeaveAnInFlightSaveRunning_WhenTheStartTokenIsCancelled() in <repo>/Tests/Verbara.Sdk.Hosting.Tests/SessionManagerHostedServiceTests.cs:line 140
             at Verbara.Sdk.Hosting.Tests.SessionManagerHostedServiceTests.StartAsync_ShouldLeaveAnInFlightSaveRunning_WhenTheStartTokenIsCancelled() in <repo>/Tests/Verbara.Sdk.Hosting.Tests/SessionManagerHostedServiceTests.cs:line 147

      The unfixed service hands the manager the host's `StartAsync` token, so the store's save is
      running under the very token the test cancels.

- [x] 1.3 Write `StopAsync_ShouldCutShortAnInFlightSave_WhenTheStopTokenIsCancelled` first, against
      the unfixed service, and record its verbatim failure. With a save in flight, call `StopAsync`
      with the token of a test-owned source and cancel that source; assert the save ended with an
      `OperationCanceledException`, and that nothing was logged at Error or above because the filter
      at `CallSessionManager.cs:55` swallowed it.
      **Recorded failure:**

          Failed Verbara.Sdk.Hosting.Tests.SessionManagerHostedServiceTests.StopAsync_ShouldCutShortAnInFlightSave_WhenTheStopTokenIsCancelled [< 1 ms]
          Error Message:
             Expected save.IsObserved to be True because the host withdrew its grace, which cancelled the service's source and so ended the save and resumed the persist before Cancel returned, but found False.
          Stack Trace:
             at FluentAssertions ... (frames elided)
             at Verbara.Sdk.Hosting.Tests.SessionManagerHostedServiceTests.StopAsync_ShouldCutShortAnInFlightSave_WhenTheStopTokenIsCancelled() in <repo>/Tests/Verbara.Sdk.Hosting.Tests/SessionManagerHostedServiceTests.cs:line 166
             at Verbara.Sdk.Hosting.Tests.SessionManagerHostedServiceTests.StopAsync_ShouldCutShortAnInFlightSave_WhenTheStopTokenIsCancelled() in <repo>/Tests/Verbara.Sdk.Hosting.Tests/SessionManagerHostedServiceTests.cs:line 170

      `StopAsync` does nothing with its token today, so cancelling it reaches neither the manager nor
      the store and the save parks forever. The assertion just before `stop.Cancel()` — the save is
      still unobserved once `StopAsync` has returned — passes before the fix and after it, and is the
      in-test guard against a fix that cancels the source on entry.

- [x] 1.4 Add the controls, which must pass before the fix and after it:
      `StopAsync_ShouldLeaveAnInFlightSaveRunning_WhenTheStopTokenIsNeverCancelled` (a stop inside
      its budget does not cut a save short — this is the control that keeps the fix from
      over-correcting into "cancel everything at stop"), and
      `StopAsync_ShouldNotStartASave_WhenAServerEventArrivesAfterTheStop` (the detach runs before the
      stop token is wired). Confirm the three tests #261 added remain untouched and green:
      `OnChannelAdded_ShouldNotLogPersistError_WhenShutdownCancelsInFlightSave`,
      `OnChannelAdded_ShouldNotLogPersistError_WhenSaveStartsAfterShutdownCancelled` and
      `OnChannelAdded_ShouldLogPersistError_WhenStoreCancelsSaveWithoutShutdown`
      (`Tests/Verbara.Sdk.Sessions.Tests/CallSessionManagerTests.cs:211-292`).
      **Recorded:** both controls **pass against the unfixed service** —
      `StopAsync_ShouldLeaveAnInFlightSaveRunning_WhenTheStopTokenIsNeverCancelled` [1 ms] and
      `StopAsync_ShouldNotStartASave_WhenAServerEventArrivesAfterTheStop` [1 ms]. In
      `SessionManagerHostedServiceTests` the run is **8 passed / 5 failed / 13 total** (the 6
      pre-existing tests plus these 2 controls pass; the 5 new regression tests are the failures of
      1.2, 1.3, 1.5 and 1.6), and the whole `Verbara.Sdk.Hosting.Tests` assembly is **38 passed /
      5 failed / 43 total**, so the new package reference of 1.6 disturbed nothing else.
      The three #261 tests are **untouched** — `git diff --stat -- Tests/Verbara.Sdk.Sessions.Tests/`
      is empty — and green: **5 passed / 0 failed** for their 5 cases (two of the three are
      `[Theory]` with `TaskCanceled` and `OperationCanceled`).

- [x] 1.5 Add `Dispose_ShouldCutShortAnInFlightSave_WhenTheServiceIsDisposedWithoutAStop`, covering
      the host that tears the service down without a graceful stop.
      **Recorded failure:**

          Failed Verbara.Sdk.Hosting.Tests.SessionManagerHostedServiceTests.Dispose_ShouldCutShortAnInFlightSave_WhenTheServiceIsDisposedWithoutAStop [< 1 ms]
          Error Message:
             Expected var disposable = sut to be assignable to System.IDisposable because the service owns the cancellation source it hands the manager, so it has to release it, but Verbara.Sdk.Hosting.SessionManagerHostedService is not.
          Stack Trace:
             at FluentAssertions ... (frames elided)
             at Verbara.Sdk.Hosting.Tests.SessionManagerHostedServiceTests.Dispose_ShouldCutShortAnInFlightSave_WhenTheServiceIsDisposedWithoutAStop() in <repo>/Tests/Verbara.Sdk.Hosting.Tests/SessionManagerHostedServiceTests.cs:line 231
             at Verbara.Sdk.Hosting.Tests.SessionManagerHostedServiceTests.Dispose_ShouldCutShortAnInFlightSave_WhenTheServiceIsDisposedWithoutAStop() in <repo>/Tests/Verbara.Sdk.Hosting.Tests/SessionManagerHostedServiceTests.cs:line 239

      The test reaches disposal through `sut.Should().BeAssignableTo<IDisposable>().Which` rather than
      `using var sut` or a direct `sut.Dispose()`: the service is `sealed`, so both of those are a
      compile error while it is not disposable, and a test that cannot compile cannot fail first. The
      assertion is itself part of the requirement — a service that owns a cancellation source has to
      release it — so it stays after the fix.

- [x] 1.6 Pin the wiring through a real host. Add `<PackageReference Include="Microsoft.Extensions.Hosting" />`
      to `Tests/Verbara.Sdk.Hosting.Tests/Verbara.Sdk.Hosting.Tests.csproj` (the version is already
      pinned centrally in `Directory.Packages.props`, so no version attribute), then:
      `HostStartAsync_ShouldLeaveAnInFlightSaveRunning_WhenTheStartIsAborted` — a second hosted
      service registered after this one parks until the test cancels the token it passed to
      `Host.StartAsync`, so the host's start token is cancelled with this service already started —
      and `HostStopAsync_ShouldCutShortAnInFlightSave_WhenTheShutdownIsNoLongerGraceful`, driven by a
      `HostOptions.ShutdownTimeout` that makes the stop token already cancelled when `StopAsync`
      runs. Record in this file which mechanism the pinned host version honours; neither test may
      wait on a clock.
      **Recorded failures:**

          Failed Verbara.Sdk.Hosting.Tests.SessionManagerHostedServiceTests.HostStartAsync_ShouldLeaveAnInFlightSaveRunning_WhenTheStartIsAborted [3 ms]
          Error Message:
             Expected save.Token.IsCancellationRequested to be False because cancelling the token passed to Host.StartAsync aborts the start, and the manager must not be saving under it, but found True.
          Stack Trace:
             at FluentAssertions ... (frames elided)
             at Verbara.Sdk.Hosting.Tests.SessionManagerHostedServiceTests.HostStartAsync_ShouldLeaveAnInFlightSaveRunning_WhenTheStartIsAborted() in <repo>/Tests/Verbara.Sdk.Hosting.Tests/SessionManagerHostedServiceTests.cs:line 271
             at Verbara.Sdk.Hosting.Tests.SessionManagerHostedServiceTests.HostStartAsync_ShouldLeaveAnInFlightSaveRunning_WhenTheStartIsAborted() in <repo>/Tests/Verbara.Sdk.Hosting.Tests/SessionManagerHostedServiceTests.cs:line 277

          Failed Verbara.Sdk.Hosting.Tests.SessionManagerHostedServiceTests.HostStopAsync_ShouldCutShortAnInFlightSave_WhenTheShutdownIsNoLongerGraceful [57 ms]
          Error Message:
             Expected save.IsObserved to be True because the shutdown was no longer graceful when it reached the service, so the save was cut short instead of outliving the host, but found False.
          Stack Trace:
             at FluentAssertions ... (frames elided)
             at Verbara.Sdk.Hosting.Tests.SessionManagerHostedServiceTests.HostStopAsync_ShouldCutShortAnInFlightSave_WhenTheShutdownIsNoLongerGraceful() in <repo>/Tests/Verbara.Sdk.Hosting.Tests/SessionManagerHostedServiceTests.cs:line 303
             at Verbara.Sdk.Hosting.Tests.SessionManagerHostedServiceTests.HostStopAsync_ShouldCutShortAnInFlightSave_WhenTheShutdownIsNoLongerGraceful() in <repo>/Tests/Verbara.Sdk.Hosting.Tests/SessionManagerHostedServiceTests.cs:line 307

      **Mechanism: not `HostOptions.ShutdownTimeout`.** `Host.StopAsync` in the pinned
      `Microsoft.Extensions.Hosting` **10.0.10** turns that option into
      `CreateLinkedTokenSource(cancellationToken)` + `CancelAfter(_options.ShutdownTimeout)`, and
      `CancelAfter` fires on a timer even at zero — measured on the same pinned package:
      `new CancellationTokenSource(TimeSpan.Zero).IsCancellationRequested` is `True` immediately
      (the constructor special-cases zero) but `CancelAfter(TimeSpan.Zero)` leaves it `False`
      immediately, on a plain source and on a linked one alike. A zero `ShutdownTimeout` would
      therefore be a race against a timer, which is exactly the wall-clock dependency these tests may
      not have. The deterministic route the test uses instead is an **already-cancelled token passed
      to `host.StopAsync`**: the host links the caller's token into the token it hands each service,
      so the service's `StopAsync` is still invoked and its token is already cancelled on entry
      (measured: `stop token seen by the service: IsCancellationRequested=True`, `StopAsync was
      invoked on the service at all: True`). The park in the start test is causal too — a
      `TaskCompletionSource` completed by the host start token's own registration, never a delay.

- [x] 1.7 Measure, do not assume, what the host's start token does today, because it decides how the
      defect reads in production: with the **unfixed** service on a real host, does a normal stop
      cancel the token the manager is holding (the combined start token still linked to
      `ApplicationStopping`) or not (that source released at the end of the start)? Record the
      measured answer here and carry it into ADR-0059. Under the first reading today's code cuts
      every in-flight save short at the first instant of shutdown; under the second it never cuts one
      short at all. The proposal's claim — that the filter's intended case is unreachable — holds
      either way, and 744b9d81's body asserts the second; this task turns that assertion into a
      measurement.
      **Measured answer: the second reading. A normal stop does not cancel it — nothing can, once the
      start has returned.** So today's code never cuts an in-flight save short, and the filter at
      `CallSessionManager.cs:55` is unreachable after a completed start. 744b9d81's body was right.

      *How it was measured (three independent ways, all on the pinned `Microsoft.Extensions.Hosting`
      10.0.10 from `Directory.Packages.props`):*

      1. **The real unfixed service on a real `IHost`.** A throwaway `[Fact]` in
         `Verbara.Sdk.Hosting.Tests` built a `HostBuilder` host around the unfixed
         `SessionManagerHostedService`, started it normally, drove one channel event so a save was in
         flight under the token the manager stores, then ran a **normal, graceful**
         `host.StopAsync(CancellationToken.None)`:

             MEASUREMENT 1.7
               token after a COMPLETED start: CanBeCanceled=True IsCancellationRequested=False registrationAccepted=True registrationFailure=none saveObserved=False
               token after a NORMAL graceful stop: IsCancellationRequested=False saveObserved=False ApplicationStopping.IsCancellationRequested=True ApplicationStopped.IsCancellationRequested=True
               token after lifetime.StopApplication(): IsCancellationRequested=False

         The shutdown really did run — `ApplicationStopping` and `ApplicationStopped` are both
         cancelled — and the manager's token still is not. The harness was deleted after the
         measurement; the committed equivalent is 1.6's
         `HostStopAsync_ShouldCutShortAnInFlightSave_WhenTheShutdownIsNoLongerGraceful`, whose
         pre-fix failure (`save.IsObserved … but found False`) is the same fact with a withdrawn
         grace instead of a graceful one.
      2. **Decompiled, for the mechanism.** `ilspycmd -t Microsoft.Extensions.Hosting.Internal.Host`
         over `microsoft.extensions.hosting/10.0.10/lib/net10.0/Microsoft.Extensions.Hosting.dll` in
         the NuGet cache shows the start token *is* linked to `ApplicationStopping` — and that the
         link lives in a `using`, so it is disposed the moment the start returns (abridged, `…`
         marks the elisions):

             using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _applicationLifetime.ApplicationStopping))
             {
                 if (_options.StartupTimeout != Timeout.InfiniteTimeSpan) { cts.CancelAfter(_options.StartupTimeout); }
                 cancellationToken = cts.Token;
                 …
                 await ForeachService(_hostedServices, cancellationToken, …, service.StartAsync(token) …);

         So both readings are half right: the link exists, but only *during* the start. Both
         measured, on the same package: `ApplicationStopping` fired from inside a hosted service's
         `StartAsync` **does** cancel the start token (`True`), and a linked source disposed before
         its parent cancels never cancels (`False`).
      3. **A dead token is silent, not loud.** `Register` on a token whose source was disposed
         without ever being cancelled is accepted — `Register=ok`, `UnsafeRegister=ok`,
         `IsCancellationRequested=False`, `CanBeCanceled=True` — so the callback is simply never
         invoked. Today's defect throws nothing and logs nothing: a save in flight at shutdown just
         holds a token nobody can cancel, exactly as `registrationAccepted=True` with
         `saveObserved=False` shows above.

      Carry all three into ADR-0059.

## 2. Fix

- [x] 2.1 In `src/Verbara.Sdk.Hosting/SessionManagerHostedService.cs`, own the source: a
      `private readonly CancellationTokenSource _shutdown = new()`, handed to
      `csm.SetShutdownToken(_shutdown.Token)` in `StartAsync` in place of the parameter. `StartAsync`
      keeps ignoring its own token — it starts nothing that can be aborted.
      **Recorded:** exactly that. The field is `private readonly CancellationTokenSource _shutdown = new()`
      on the primary-constructor class, `StartAsync` now calls `csm.SetShutdownToken(_shutdown.Token)`,
      and its `cancellationToken` parameter is untouched with a comment saying why. The manager's
      token is therefore from a source nothing outside this service can reach: 1.2's
      `save.Token.Should().NotBe(start.Token)` passes, which is the assertion that would still fail if
      the parameter were merely linked into the new source instead of replaced by it.
      `dotnet build Verbara.Sdk.slnx -c Release` is **0 warnings, 0 errors** — no analyzer fired on
      the new field, and `CA2016`/`MA0040` did not fire on passing a token other than the parameter to
      `SetShutdownToken` (an explicit non-default argument is not a missing forward).
- [x] 2.2 In `StopAsync`, keep `DetachFromServer("default")` first, then register the host's stop
      token onto the source: `cancellationToken.UnsafeRegister(static s => ((CancellationTokenSource)s!).Cancel(), _shutdown)`
      — a static callback with the source as state, so no closure allocation and no `ExecutionContext`
      capture, which is the shape this AOT-first repo wants. Keep the returned registration in a
      field and dispose any previous one first, so a second `StopAsync` cannot leak one. Do **not**
      cancel the source on entry: an already-cancelled stop token cancels it synchronously through
      the registration, and a graceful one leaves the save its budget.
      **Recorded:** implemented as written — detach first, then `_stopRegistration.Dispose();`
      followed by `_stopRegistration = cancellationToken.UnsafeRegister(static state => ((CancellationTokenSource)state!).Cancel(), _shutdown);`,
      and nothing cancels the source on entry. All three behaviours this splits are green:
      1.3 (a stop token cancelled later cuts the save short — the registration fires
      `_shutdown.Cancel()`, which runs the store's own registration inline, so the persist has
      finished its catch before `stop.Cancel()` returns), 1.4's in-budget control (a stop whose token
      never cancels leaves `save.Token.IsCancellationRequested` false and the save in flight), and
      1.4's ordering control (a channel event after the stop starts no save at all, because the
      detach precedes the registration). 1.6's `HostStopAsync_…` covers the already-cancelled case:
      the host hands the service a token that is cancelled on entry and `UnsafeRegister` runs the
      callback inline, so no `Cancel()` on entry is needed to reach that case.
      **One half of this task is guard-free, and this record says so rather than claiming it:** no
      committed test binds *disposing the previous registration*. A second `StopAsync` leaking its
      registration is covered by no test in this change, and task 3.3's mutation list does not claim
      it either. Measured, not assumed: with the `_stopRegistration.Dispose()` line in `StopAsync`
      deleted and the project rebuilt, `Verbara.Sdk.Hosting.Tests` is still **43 passed / 0 failed**
      at 0 warnings — the mutation is invisible to the suite. It is owed, not covered; adding the
      test was out of this task's scope, and the line is implemented anyway.
      Also measured, because four existing tests take this path: `UnsafeRegister` on the
      non-cancelable `CancellationToken.None` returns a default registration and does nothing, so
      `StopAsync_ShouldNotThrow_WhenNotConcreteCallSessionManager`,
      `StopAsync_ShouldReturnCompletedTask` and `StopAsync_ShouldDetachFromServer_WhenConcreteCallSessionManager`
      stayed green and `StopAsync` still returns an already-completed task.
- [x] 2.3 Implement `IDisposable` on the service: dispose the registration, cancel `_shutdown`, then
      dispose it. Cancelling before releasing is the load-bearing order. Record in this file what a
      save started *after* that disposal does (`CancellationToken.Register` on the token of a
      disposed-but-cancelled source), measured, and carry the answer into ADR-0059 — no new
      behaviour is scoped for it here.
      **Recorded:** the type is now `IHostedService, IDisposable` and `Dispose()` is
      `_stopRegistration.Dispose(); _shutdown.Cancel(); _shutdown.Dispose();` — in that order. 1.5
      passes, so a save alive at teardown ends with an `OperationCanceledException` the existing
      filter swallows, and `IDISP001` staying a *suggestion* in `.editorconfig` means the thirteen
      `new SessionManagerHostedService(...)` call sites — all of them in
      `SessionManagerHostedServiceTests.cs`, and only one of them disposing — still build at
      0 warnings. In production the container owns the disposal: both registrations are
      `services.AddSingleton<IHostedService, SessionManagerHostedService>()`
      (`ServiceCollectionExtensions.cs:179` and `:196`), a container-created singleton, so the
      provider disposes it exactly once at teardown.

      **Measured — a save started *after* the disposal is cut short immediately, not faulted.** Run
      against the fixed service on a real `CallSessionManager` (a throwaway `[Fact]` in
      `Verbara.Sdk.Hosting.Tests`, deleted after the measurement): start the service, dispose it, then
      raise a channel event — `Dispose` does not detach from the server, so the event still reaches
      `PersistAsync`, which saves under the disposed source's token:

          MEASUREMENT 2.3 (a save started after the service is disposed)
            token handed to SaveAsync: CanBeCanceled=True IsCancellationRequested=True
            Register on it: accepted; callback ran inline: True
            ThrowIfCancellationRequested on it: threw OperationCanceledException
            logged at Error or above: 0

      So the token of a cancelled-then-disposed source stays fully usable: `Register` is accepted and
      invokes the callback **inline** rather than throwing, `ThrowIfCancellationRequested` throws
      `OperationCanceledException`, and **no `ObjectDisposedException` is raised anywhere** — disposal
      only forbids `Cancel`/`CancelAfter` on the source, never reads of the token it already handed
      out. A save begun after teardown therefore ends at its first cancellation check, and the
      `catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested)` filter
      swallows it, so an operator sees nothing at Error. The pre-fix behaviour was the opposite in
      kind: the token could never be cancelled at all, so such a save ran to completion or hung.
      Carry into ADR-0059.

      **Residual, recorded and not changed: `Dispose()` is not idempotent.** A second call reaches
      `_shutdown.Cancel()` on an already-disposed source and throws `ObjectDisposedException`. Every
      path that exists today disposes exactly once (the DI container, and 1.5's single explicit
      call), and no test binds a second call, so this is written down rather than guarded — the same
      treatment the proposal gives its other two residuals.

      **Amended by the main session — `Dispose` was not idempotent.** Phase B implemented the task as
      written and flagged that a second call reaches `_shutdown.Cancel()` on a released source and
      throws `ObjectDisposedException`. That is a violation of `IDisposable`'s own contract, which
      requires a second call to be ignored rather than throw, and it would have shipped inside a
      change whose whole subject is owning and releasing a cancellation source correctly.

      Closed tests-first: `Dispose_ShouldNotThrow_WhenCalledMoreThanOnce` was added and **run red
      against the ungated `Dispose`** (`Failed: 1, Passed: 0`), then `Dispose` gained
      `if (Interlocked.Exchange(ref _disposed, 1) == 1) return;` and the assembly moved 43/43 → 44/44
      with the build still at 0 warnings, 0 errors. A gate rather than a reordering, because the
      hazard is the second entry, not the statement order.

      Note this is not academic even though the container disposes exactly once: both registrations
      are container-created singletons (`ServiceCollectionExtensions.cs:179`, `:196`), so the new
      `Dispose` really runs in production, and a type whose `Dispose` throws on a second call is a
      trap for anyone who wraps or re-registers it.
- [x] 2.4 Correct the two comments in `src/Verbara.Sdk.Sessions/Manager/CallSessionManager.cs` that
      describe the old wiring: the XML doc on `SetShutdownToken` (line 43) should say which phase
      cancels the token and that every save runs under it, not only the ones at shutdown; the catch
      comment (lines 57-60) should stop naming "the host's StartAsync token". Leave both member names
      alone — `SetShutdownToken` is `internal` but visible outside this repo through
      `InternalsVisibleTo`, so a rename is a cross-repo edit, not a one-PR one.
      **Recorded — both corrected, nothing renamed.** The XML doc went from
      *"Sets the cancellation token used for persistence operations during shutdown."* to a summary
      that states the scope and the owner: it sets the token **every** persistence call runs under,
      not only the ones at shutdown; with the SDK's hosted service the token comes from a source that
      service owns, and the stop phase is what cancels it — the host withdrawing its graceful
      shutdown, or that service's own teardown — while an aborted start does not cancel it. The catch
      comment's first two lines, *"The shutdown token (with the SDK's hosted service, the host's
      StartAsync token) was cancelled, for example by a stop requested before startup completed."*,
      are now *"The persistence token was cancelled — with the SDK's hosted service, by the host
      withdrawing its graceful shutdown or by that service's teardown."*; its last two lines, which
      say the save was cut short on purpose and that a cancellation while the token is still live is
      still logged below, are unchanged and still true. `SetShutdownToken` and `_shutdownToken` keep
      their names — `git diff` on this file is comments only, and `git diff --stat -- '*PublicAPI*'`
      is empty, so no downstream recompile follows.
- [x] 2.5 Sweep the other hosted services in `src/Verbara.Sdk.Hosting/` for the same shape — a token
      received in `StartAsync` and stored for use after the start has finished. Fix it here only if
      it is `SessionManagerHostedService`; record everything else in this file without changing it.
      Expected from the reading done while drafting: `AmiConnectionHostedService`,
      `AriConnectionHostedService`, `AriAudioHostedService`, `AriOutboundListenerHostedService` and
      `VerbaraServerHostedService` each pass the start token to a start call and the stop token to a
      stop call and keep neither; `SessionReconciliationService` links its loop source over the start
      token but owns that source and cancels it in its own `StopAsync`.
      **Recorded — the drafting reading holds for all six of the package's other services, and
      widening the sweep past the package is what found the one thing worth an open change.** The
      shape is a class of defect, not a package-local one, so every `IHostedService` and
      `BackgroundService` under `src/` was read, not only the ones in `src/Verbara.Sdk.Hosting/`
      (`grep -rn ': *IHostedService\|BackgroundService\|IHostedLifecycleService' --include='*.cs' src/`
      — twelve types, all twelve read). Nothing outside `SessionManagerHostedService.cs` was changed.

      | Where | What it does with the start token | What it does with the stop token | Verdict |
      |---|---|---|---|
      | `Verbara.Sdk.Hosting/AmiConnectionHostedService.cs:11-15` | forwards to `connection.ConnectAsync(ct)` | forwards to `connection.DisconnectAsync(ct)` | **clean** — stateless primary-ctor class, no field of any kind |
      | `Verbara.Sdk.Hosting/AriConnectionHostedService.cs:11-15` | forwards to `client.ConnectAsync(ct)` | forwards to `client.DisconnectAsync(ct)` | **clean** — same shape, no state |
      | `Verbara.Sdk.Hosting/AriAudioHostedService.cs:14-26` | forwards to both servers' `StartAsync(ct)` | forwards to both servers' `StopAsync(ct)`, reverse order | **clean** — no state; the two injected servers are the only fields |
      | `Verbara.Sdk.Hosting/AriOutboundListenerHostedService.cs:12-16` | forwards to `listener.StartAsync(ct)` | forwards to `listener.StopAsync(ct)` | **clean** — same shape, no state |
      | `Verbara.Sdk.Hosting/VerbaraServerHostedService.cs:11-15` | forwards to `server.StartAsync(ct)` | ignored — `StopAsync` is `Task.CompletedTask` | **clean of this shape** (see the asymmetry note below) |
      | `Verbara.Sdk.Hosting/SessionReconciliationService.cs:29-35,83-107` | links it: `_cts = CreateLinkedTokenSource(cancellationToken)` (`:31`), loop runs on `_cts.Token` | `StopAsync` awaits `_cts.CancelAsync()` (`:86`) and disposes the timer | **not the shape** — it owns the source and its own stop cancels it (two hygiene notes below) |
      | `Verbara.Sdk.Hosting/SessionManagerHostedService.cs` | — | — | **the subject of this change**, fixed here |
      | `Verbara.Sdk.Agi/Hosting/AgiHostedService.cs:10-14` | forwards to `agiServer.StartAsync(ct)` | forwards to `agiServer.StopAsync(ct)` | **clean** — no state |
      | `Verbara.Sdk.VoiceAi.AudioSocket/AudioSocketServer.cs:68-97` | **ignores it for lifetime purposes**: `_cts = new CancellationTokenSource()` (`:70`), its own source, not linked; the parameter is only `Task.Run`'s token (`:80`) | `StopAsync` awaits `_cts.CancelAsync()` (`:88`); `DisposeAsync` stops first, then releases (`:242-243`) | **clean, and already exactly what ADR-0059 prescribes** — the one service in the tree that already owns its source outright |
      | `Verbara.Sdk.VoiceAi/Pipeline/VoiceAiSessionBroker.cs:18,34,38,52` | **stores it**: `_stoppingToken = cancellationToken` (`:34`), then hands it to every `_handler.HandleSessionAsync(session, _stoppingToken)` (`:38`) for the life of the process | nothing — `StopAsync` is `Task.CompletedTask` (`:52`) | **EXACT MATCH, unfixed — the finding of this sweep** |
      | `Verbara.Sdk.Push.Nats/NatsBridge.cs:116,151` | n/a — does not override `StartAsync`; the loop runs on `ExecuteAsync`'s `stoppingToken` | overrides `StopAsync`, forwards to `base.StopAsync(cancellationToken)` | **clean by construction** — `BackgroundService`'s token is a lifetime token, not a phase token |
      | `Verbara.Sdk.Push.Webhooks/WebhookDeliveryService.cs:76` | n/a — does not override `StartAsync`; loop runs on `ExecuteAsync`'s `stoppingToken` | n/a — `base.StopAsync` | **clean by construction**, same reason |

      **The match is `VoiceAiSessionBroker`, and it is the same defect verbatim in a different
      package.** All three properties that made this change necessary hold there: the stored token is
      the `StartAsync` parameter, so it is inert after a completed start — 1.7's measurement applies
      unchanged, same host and same pinned `Microsoft.Extensions.Hosting` 10.0.10 — nothing in the
      stop phase touches it, and the field name (`_stoppingToken`) claims a phase that never reaches
      it, exactly as `_shutdownToken` did. It is also *worse* in one respect and *better* in another.
      Worse: the handler it adds to `_server.OnSessionStarted` in `StartAsync` (`:36-46`) is never
      removed, so there is no analogue of the `DetachFromServer` this change relies on — an
      AudioSocket session arriving after the stop still spawns a handler, under a token that can
      never be cancelled. Better: `HandleSessionAsync` is awaited fire-and-forget with a faulted-only
      continuation, so nothing swallows a cancellation the way `PersistAsync`'s filter did; there is
      no unreachable-filter half of the defect. The fix is the same three moves plus one — own a
      source, wire the stop token onto it, cancel-then-release under a gate, and unsubscribe the
      handler before wiring — which is a change of its own, not a line in this PR's description.
      Nothing about it was touched here.

      **One level down, the five forward-only wrappers hand their start token to a callee that links
      it into a longer-lived source.** `AmiConnection.cs:113`, `AriClient.cs:129`,
      `Ari/AudioSocketServer.cs:61`, `Ari/WebSocketAudioServer.cs:72` and
      `Outbound/AriOutboundListener.cs:92` each do `_cts = CreateLinkedTokenSource(<the caller's token>)`
      and run their read/accept/event loop on `_cts.Token`, cancelling it in their own
      `DisconnectAsync`/`StopAsync`. That is the `SessionReconciliationService` shape rather than the
      defect's: the source has one owner and its own stop cancels it. It is still a link over a phase
      token, which ADR-0059 R2 refuses in so many words ("not stored, and *not linked either*"), and
      1.7 is why it is benign in practice — the host releases the combined start-token source when
      the start returns, so after a completed start the link can never fire. Within the start window
      it can: an aborted start cancels those loops, which is arguably the wanted behaviour and is
      **not measured here**. Recorded as shape, not as a defect; `Verbara.Sdk.VoiceAi.AudioSocket`'s
      `AudioSocketServer` is the counter-example that shows the link is unnecessary.

      **Two hygiene notes on `SessionReconciliationService`, neither of them this shape, both
      recorded and unchanged.** (a) Its `Dispose` (`:103-107`) *releases* `_cts` without cancelling
      it — the inverse of R5's cancel-before-release. It survives only because the same method
      disposes `_timer`, which makes the in-flight `WaitForNextTickAsync` return `false` and ends the
      loop: a teardown with no `StopAsync` therefore ends the loop by a mechanism other than the one
      the ADR prescribes. (b) That `Dispose` *is* idempotent, unlike the first cut of this change's —
      both statements are `?.Dispose()` on types whose `Dispose` is itself idempotent, so no gate is
      needed. **The asymmetry note on `VerbaraServerHostedService`:** its `StopAsync` does nothing at
      all, so the Live server's AMI subscription and `Reconnected` handler (`VerbaraServer.cs:90-91`)
      outlive the stop. Also not this shape — no token is stored — and also out of scope.

      **Where the work goes.** `VoiceAiSessionBroker` deserves an open change of its own; the two
      hygiene notes and the `VerbaraServerHostedService` asymmetry are worth at most a sentence in
      that change's context. None of it belongs in this PR, and this record is where close-out reads
      it from.
- [x] 2.6 Record, without changing it, that `AddVerbaraSessionsMultiServer`
      (`src/Verbara.Sdk.Hosting/ServiceCollectionExtensions.cs:201-213`) registers no hosted service,
      so on that path `_shutdownToken` stays `default` and no save is ever cut short. Carry it into
      ADR-0059 as a known gap so it is not rediscovered as a bug.
      **Recorded, and the earlier reading is confirmed with two corrections.** Verified against the
      code, not carried over: `AddVerbaraSessionsMultiServer` is
      `{ return AddSessionsCore(services, configure); }` and nothing else, and `AddSessionsCore`
      (`:228-249`) registers no `IHostedService` whatsoever — `TryAddSingleton` for the manager, the
      two trackers and the store, the `SessionOptions` validator and `AddOptions(...).ValidateOnStart()`,
      and the `sessions` health check. The two `services.AddSingleton<IHostedService, …>()` pairs in
      the file that would supply one are on the single-server paths only (`:179-180` and `:196-197`).
      So on the multi-server path neither `SessionManagerHostedService` nor
      `SessionReconciliationService` is ever constructed, `CallSessionManager._shutdownToken`
      (`CallSessionManager.cs:30`, a plain field with no constructor assignment) stays `default`, and
      nothing this change decides applies there.

      **Correction 1 — the citation is off, and it is the method's own lines that matter.** The
      method is `ServiceCollectionExtensions.cs:207-212`; `201-206` is its doc comment. The doc
      comment does already say the quiet part out loud — *"Does NOT register a hosted service —
      manual server attachment via `CallSessionManager.AttachToServer` / `DetachFromServer` is
      required"* — so the gap is documented at the call site today, just not as a token gap.

      **Correction 2 — the gap covers two registrations, not one.** `proposal.md` § Impact and
      ADR-0059's known-gap bullet both name `AddVerbaraSessionsMultiServer` alone.
      `AddVerbaraSessionsMultiServerBuilder` (`:220-226`) is the fluent sibling and does the same
      thing — `AddSessionsCore(services, configure); return new SessionsBuilder(services);` — so a
      multi-server deployment wired through the builder (which is how the Redis and Postgres backends
      are meant to be attached) has the identical gap. Neither artifact was edited for this: the
      correction is recorded here, and the two named paths are the two *single-server* entry points'
      exact counterparts, so the sentence in each artifact is incomplete rather than wrong.

      **What `default` actually means for the two requirements, since `default` is not just
      "uncancelled".** `default(CancellationToken)` has `CanBeCanceled == false`, so
      `await _store.SaveAsync(session, _shutdownToken)` hands the store a token that can never be
      cancelled and no save is ever cut short — the first requirement is vacuous on this path. The
      second requirement, by contrast, is *fully* satisfied on this path and always has been:
      `_shutdownToken.IsCancellationRequested` is permanently `false`, so the filter at
      `CallSessionManager.cs:60` is never taken and **every** failed save, including a store that
      ends one with an `OperationCanceledException` of its own, is logged at Error as
      `Failed to persist session {SessionId}`. A multi-server operator sees strictly more persistence
      errors than a single-server one, never fewer — worth knowing before anyone closes this gap,
      because closing it makes shutdown-time saves go quiet there too.

      Also noted while confirming this, and outside the token question: the same missing registration
      means the multi-server paths get **no reconciliation sweep either** — `SessionReconciliationService`
      is registered next to `SessionManagerHostedService` on both single-server paths and nowhere
      else, so timed-out and orphaned sessions are never swept in a clustered deployment. That is a
      larger gap than this change's and belongs to whatever change closes the multi-server lifetime,
      which is the same place `VoiceAiSessionBroker` (2.5) will be handled.

## 3. Verification

- [x] 3.1 `dotnet build Verbara.Sdk.slnx -c Release`: 0 warnings, 0 errors (`TreatWarningsAsErrors`,
      `WarningLevel 9999`). Confirm no analyzer fired on the new disposable field.
      **Recorded — 0 warnings / 0 errors, and run `--no-incremental` as well because an incremental
      0/0 is weaker evidence than it looks: an up-to-date project is skipped and never re-emits its
      warnings.**

          dotnet build Verbara.Sdk.slnx -c Release                    → Build succeeded. 0 Warning(s) 0 Error(s)  (9.52 s)
          dotnet build Verbara.Sdk.slnx -c Release --no-incremental    → Build succeeded. 0 Warning(s) 0 Error(s)  (9.99 s)

      The second run was repeated at `-v n` and the log checked rather than trusted: **zero** lines
      match `warning |error ` anywhere in it, and it shows **91** project outputs and **271**
      `CoreCompile:` target invocations — so the whole solution really recompiled and every analyzer
      re-ran. A third independent 0/0 Release build came from the Pack Warnings Gate step in 3.2.
      No analyzer fired on the new disposable field: `IDISP001` is a *suggestion* in `.editorconfig`
      (2.3) and `CA2016`/`MA0040` do not fire on handing `SetShutdownToken` a token other than the
      parameter (2.1), which is why 0/0 survives a field that thirteen call sites mostly do not
      dispose.
- [x] 3.2 Unit lane green under the CI filter
      (`--filter "Category!=Functional&Category!=Integration&Category!=Realtime&Category!=Spike"`),
      `Verbara.Sdk.Governance.Tests` included, with coverage collected; `tools/audit-test-asserts.sh`
      reports `Violations: 0`. Record the assembly and test counts, and the coverage band.
      **Recorded — the step list was derived by reading `.github/workflows/ci.yml` on this branch
      (425 lines, nine jobs), not from memory, because this change moves files in `src/`, a test
      project, `docs/` and `openspec/` and the tree-scanning guards are tripped by a change
      anywhere.** Every command below was run directly, never inside a pipeline whose status is then
      read, and each exit code was captured off the command itself. The coverage results went to a
      scratch directory outside the tree; the commands are otherwise `ci.yml`'s verbatim.

      | `ci.yml` job → step | Result |
      |---|---|
      | **Docs-only gate** — `bash scripts/ci/classify-docs-only.sh <base> <head>` | rc=0, **`docs_only=false`** — measured on this change's *real* diff, not assumed (see the note below), so no required context is silently skipped |
      | **Unit Tests** → Build | `dotnet build Verbara.Sdk.slnx -c Release` — 0 warnings / 0 errors |
      | **Unit Tests** → run unit tests with coverage | rc=0. **30 assembly runs, 3589 passed / 0 failed / 0 skipped**, 30× `Test Run Successful.`, 0× `Test Run Failed.`; 4 assemblies matched no test under the filter (the Docker-bound ones) and 34 `coverage.cobertura.xml` files were produced |
      | — `Verbara.Sdk.Hosting.Tests`, same filter, targeted | **45 passed / 0 failed / 45 total** |
      | — `Verbara.Sdk.Governance.Tests`, same filter, targeted | **129 passed / 0 failed / 129 total** |
      | — `Verbara.Sdk.Governance.Tests --filter FullyQualifiedName~SyncFence` | **19 passed / 0 failed**, and `git diff --stat -- '*sync-fence-baseline.json'` is empty — the ratchet was not raised |
      | — `StatusBlockCoherenceTests` (4.3's three cases) | **3 passed / 0 failed** |
      | **Coverage Ratchet** → `dotnet tool restore` + `dotnet reportgenerator` | rc=0 both; merged `MultiReport (34x Cobertura)` → 25 assemblies, 389 classes, 321 files |
      | **Coverage Ratchet** → `check-coverage-floor.py` | rc=0 — **line 83.63% in band [83.0, 86.0]**, branch **68.23%** (floor 64.0, blocking), lines measured **13364** (min 12315). `Coverage band OK.` |
      | **Coverage Ratchet** → `check-patch-coverage.py` | rc=0 but **not meaningful on an uncommitted tree — flagged, not claimed** (see below) |
      | **Coverage Ratchet** → `check-exclusion-baseline.py` | rc=0 — 0 exclusion markers against a baseline of 0, 865 files scanned under `src/**/*.cs` |
      | **Coverage Script Tests** (7 steps) | all rc=0 — `python3 -m unittest discover scripts/tests` **257 tests OK**, classifier **37**, release-hygiene **39**, release-provenance **131**, perf-breach notifier **27**, package-validation **106**, CodeQL SARIF filter **448**, each `passed=N failed=0` |
      | **AOT Trim Check** — `bash tools/verify-aot.sh` | rc=0 — canary published, smoke-ran (`AOT Canary — all SDK types are trim-safe`), `AOT verification passed for RID=linux-x64 — 0 trim warnings` |
      | **Pack Warnings Gate** — build, `dotnet pack --no-build -p:TreatWarningsAsErrors=true`, 2 guards | all rc=0 — **29 `.nupkg`** produced with no warning or error line in the log; guard self-test under `PKV_MSBUILD_CASES=1` **passed=120 failed=0** (vs 106 without the MSBuild cases); `package-validation-coverage: all 29 shipped project(s) are validated against 2.5.3.` |
      | **Audit Test Asserts** — `bash tools/audit-test-asserts.sh` | rc=0 — 444 files scanned, ~2924 `[Fact]`/`[Theory]`, **`Violations: 0`** |
      | **Audit Test Asserts** → `python3 scripts/check-recording-redaction.py .` | rc=0 — 72 files across 2 `Recordings/` trees, `Recording redaction OK.` |
      | **OpenSpec Validate** | rc=0 — see 3.4 |
      | **Functional Tests (Testcontainers)** | **skipped — needs a Docker daemon and four image pulls.** `ci.yml` itself skips its two heavy steps on `pull_request` without the `ci:functional` label (ADR-0051); the merge queue runs matrix [22, 23], which is 3.6's business |
      | `codeql.yml` **Analyze (C#)** | **skipped — GitHub-hosted, not runnable locally.** Its SARIF filter's 448 unit cases did run, in Coverage Script Tests |

      **Four things came out differently from the task text or the brief, and this is the part worth
      reading.**

      1. **A naive read of the test log reports 726, and 726 is wrong.** The solution run prints a
         per-assembly summary block, thirty of them, and the last one printed is simply the largest
         assembly — not a total. `tail` of the log shows `Total tests: 726`. The thirty blocks sum to
         **3589**, with `Passed:` summing to the same 3589 and no `Failed:`/`Skipped:` block at all.
         Any figure quoted from the end of that log is one assembly's: the re-run below attributes
         the 726 to `Verbara.Sdk.Ami.Tests.dll`, with `Verbara.Sdk.FunctionalTests.dll` (487, its
         non-Functional cases) and `Verbara.Sdk.Ari.Tests.dll` (433) next.
      2. **`check-patch-coverage.py` cannot be satisfied from an uncommitted tree, and it reports
         `pass` anyway — a gate that fails open in exactly this situation.** It diffs
         `git diff <merge-base origin/main HEAD>...HEAD`, i.e. committed state only; with this whole
         change uncommitted the diff is empty and the script prints *"Patch coverage: no measurable
         cobertura lines in this diff (no instrumented line added). floor 85.0% — n/a, pass."* That
         rc=0 is **not** evidence about this change. Substituted a real measurement instead: in the
         merged report `Verbara.Sdk.Hosting.SessionManagerHostedService` is **line-rate 1.0,
         branch-rate 1.0, 17 lines, 0 uncovered**, and the only other `src/` edit in the diff is
         comments in `CallSessionManager.cs`, so every instrumented line this change adds is covered
         and the 85% patch floor is met with the whole margin. Worth re-running for real on the PR.
      3. **The brief's "at minimum" list is a strict subset of what `ci.yml` actually gates on, and
         the four extra contexts all ran green**: AOT Trim Check, Pack Warnings Gate, Coverage Script
         Tests (which carries no `needs: gate` edge and therefore always runs), and the
         recording-redaction step that rides *inside* Audit Test Asserts rather than in a job of its
         own. The Docs-only gate job is a fifth, and it is the one that decides whether any of the
         others run at all.
      4. **The gate verdict was measured, not reasoned.** `classify-docs-only.sh` is a pure function
         of `git diff BASE HEAD`, so an uncommitted tree normally leaves it unexercisable. It was fed
         this change's real diff through a dangling snapshot commit — `git stash create`, which
         writes objects only: no ref, no index change, no working-tree change, and `git status` was
         re-checked identical afterwards. Verdict **`docs_only=false`**, for two independent reasons
         in the script's own case blocks: `README.md` is in its docsnippets carve-out (first block,
         immediate `false`) and `src/Verbara.Sdk.Hosting/SessionManagerHostedService.cs` is a nested
         non-doc path. So every heavy required context runs on this PR — which matters because a
         wrong `true` here would report five required checks as `skipped` and land the change
         unbuilt.

      **Re-measured over the finished tree, after every record in this file was written**, because
      the tree-scanning guards read `openspec/` and `docs/` and a Phase C record is itself a change
      to the tree: the full unit lane under the CI filter is **30 assemblies, 3589 passed / 0 failed
      / 0 skipped** — identical to the coverage run above, every row `Passed!` — `Verbara.Sdk.Governance.Tests`
      is **129 passed / 0 failed**, and `openspec validate --all --strict` is **11 passed / 0 failed**
      (3.4). Neither figure moved, which is the point of re-running them rather than citing the
      earlier pass.
- [x] 3.3 Mutation check — for each mistake, name the committed test that fails: keeping the start
      token (1.2 and 1.3); cancelling the source on entry to `StopAsync` (1.4's in-budget control);
      dropping the stop registration (1.3 and 1.6); releasing the source without cancelling it (1.5);
      registering the stop token before the detach (1.4's ordering control).
      **Correction — the last claim was false when this task was written.** 1.4's ordering control
      does *not* fail when the stop token is registered before the detach: its stop token is never
      cancelled, so the registration never fires and both statements have run by the time `StopAsync`
      returns. The order is load-bearing all the same, and it was **unbound** until this task added
      the test that binds it —
      `StopAsync_ShouldNotStartASave_WhenTheStopTokenIsAlreadyCancelledAndAnEventArrivesInline`,
      committed here and proved red against the mutation before being accepted (both runs below).
      The claim in `proposal.md` § Architectural Risk was corrected to match.
      **Run adversarially: each mutation was applied alone to the fixed tree, built, run under the CI
      unit filter and reverted, the aim being to make it survive.** Baseline before and after:
      `dotnet build Verbara.Sdk.slnx -c Release` 0 warnings / 0 errors, `Verbara.Sdk.Hosting.Tests`
      **44 passed / 0 failed**, and the tree byte-identical (`sha256` of
      `SessionManagerHostedService.cs` back to `897f75e4…`).

      | Mutation | Caught by | Verbatim |
      |---|---|---|
      | keeping the start token (`SetShutdownToken(cancellationToken)`) | **5 tests**, not 2 | see below |
      | cancelling the source on entry to `StopAsync` | 1.4's in-budget control **and** 1.3's in-test guard | see below |
      | dropping the stop registration — deletion | **compiler**, `error CS0649` | `Field 'SessionManagerHostedService._stopRegistration' is never assigned to, and will always have its default value` |
      | dropping the stop registration — compiling form (`CancellationToken.None.UnsafeRegister`) | 1.3 and 1.6's stop test, exactly as claimed | see below |
      | releasing the source without cancelling it | 1.5 | see below |
      | **registering the stop token before the detach** | **nothing at the time — SURVIVED 44/44.** Now bound by the new test this task added, proved red against it | see below |
      | (known, 2.2) deleting `_stopRegistration.Dispose()` in `StopAsync` | **nothing — SURVIVED**, re-measured | 44/44 green |
      | (known, 2.3) removing the `Dispose` `Interlocked` gate | `Dispose_ShouldNotThrow_WhenCalledMoreThanOnce` | see below |
      | (bonus, 2.3) releasing the source *before* cancelling it | 1.5 and `Dispose_ShouldNotThrow_WhenCalledMoreThanOnce` | see below |

      **Keeping the start token** — `csm.SetShutdownToken(cancellationToken)`. Builds at 0 warnings
      (`CA2016`/`MA0040` do not fire). **5 failed / 39 passed**, so the claim of "1.2 and 1.3"
      understates it; 1.5 and both of 1.6's host tests fail too, because every one of them starts the
      service with a token nothing later cancels:

          Failed …StartAsync_ShouldLeaveAnInFlightSaveRunning_WhenTheStartTokenIsCancelled
             Expected save.Token.IsCancellationRequested to be False because the token the host passes to StartAsync means the start was aborted, and aborting a start must not cancel the token every save runs under, but found True.
          Failed …StopAsync_ShouldCutShortAnInFlightSave_WhenTheStopTokenIsCancelled
             Expected save.IsObserved to be True because the host withdrew its grace, which cancelled the service's source and so ended the save and resumed the persist before Cancel returned, but found False.
          Failed …Dispose_ShouldCutShortAnInFlightSave_WhenTheServiceIsDisposedWithoutAStop
             Expected save.IsObserved to be True because teardown cancelled the source before releasing it, so the save ended rather than being left under a token nothing can ever cancel, but found False.
          Failed …HostStartAsync_ShouldLeaveAnInFlightSaveRunning_WhenTheStartIsAborted
             Expected save.Token.IsCancellationRequested to be False because cancelling the token passed to Host.StartAsync aborts the start, and the manager must not be saving under it, but found True.
          Failed …HostStopAsync_ShouldCutShortAnInFlightSave_WhenTheShutdownIsNoLongerGraceful
             Expected save.IsObserved to be True because the shutdown was no longer graceful when it reached the service, so the save was cut short instead of outliving the host, but found False.

      **Cancelling the source on entry to `StopAsync`** — `_shutdown.Cancel();` as the first statement.
      **2 failed / 42 passed**. The in-budget control is the named catcher, but 1.3's own pre-`Cancel`
      guard fires as well, which is what that line was written for:

          Failed …StopAsync_ShouldLeaveAnInFlightSaveRunning_WhenTheStopTokenIsNeverCancelled
             Expected save.Token.IsCancellationRequested to be False because the stop is inside its budget, so nothing may cancel the persistence token, but found True.
          Failed …StopAsync_ShouldCutShortAnInFlightSave_WhenTheStopTokenIsCancelled
             Expected save.IsObserved to be False because entering StopAsync does not cut the save short by itself, but found True.

      **Dropping the stop registration.** Deleting the two statements does not compile — `_stopRegistration`
      is still read in `Dispose` but no longer assigned, so `CS0649` is an error here under
      `TreatWarningsAsErrors`. That only refuses the *deletion*, though, not the mistake: the
      compiling form of "the host's stop token is never wired" is one token on the same statement,
      `CancellationToken.None.UnsafeRegister(…)`, which builds at 0 warnings. **2 failed / 42 passed**,
      exactly the pair the claim names:

          Failed …StopAsync_ShouldCutShortAnInFlightSave_WhenTheStopTokenIsCancelled
             Expected save.IsObserved to be True because the host withdrew its grace, which cancelled the service's source and so ended the save and resumed the persist before Cancel returned, but found False.
          Failed …HostStopAsync_ShouldCutShortAnInFlightSave_WhenTheShutdownIsNoLongerGraceful
             Expected save.IsObserved to be True because the shutdown was no longer graceful when it reached the service, so the save was cut short instead of outliving the host, but found False.

      **Releasing the source without cancelling it** — `_shutdown.Cancel();` deleted from `Dispose`.
      Builds at 0 warnings (no `IDISP*`/`CA` rule notices the missing cancel). **1 failed / 43 passed**,
      the one the claim names:

          Failed …Dispose_ShouldCutShortAnInFlightSave_WhenTheServiceIsDisposedWithoutAStop
             Expected save.IsObserved to be True because teardown cancelled the source before releasing it, so the save ended rather than being left under a token nothing can ever cancel, but found False.

      **SURVIVOR — registering the stop token before the detach. The claim in this task and in
      `proposal.md` is wrong: 1.4's ordering control does not catch it.** With the
      `_stopRegistration.Dispose()` + `UnsafeRegister` pair moved above the
      `csm.DetachFromServer("default")` call, the build is 0 warnings / 0 errors and
      `Verbara.Sdk.Hosting.Tests` is **44 passed / 0 failed**; `Verbara.Sdk.Governance.Tests` is
      **129 passed / 0 failed**, so no tree-scanning guard catches it either.

      Why the ordering control cannot catch it: `StopAsync_ShouldNotStartASave_WhenAServerEventArrivesAfterTheStop`
      passes a stop token it **never cancels**, so the registration never fires and both statements
      have run by the time `StopAsync` returns — the detach is in place whichever order they are
      written in. The only committed test whose stop token is already cancelled on entry,
      `HostStopAsync_ShouldCutShortAnInFlightSave_WhenTheShutdownIsNoLongerGraceful`, raises no
      channel event after the stop, so it never looks at the window the ordering exists to close. No
      committed test combines the two conditions the order is load-bearing under.

      **And the order really is load-bearing — measured, not argued.** A throwaway `[Fact]` (deleted
      after the measurement; the tree is byte-identical) drove the case no committed test reaches: a
      store whose first `SaveAsync` registers a callback on the persistence token it is handed, and
      that callback raises a channel event. Start the service, raise one event so the callback is
      armed, then `StopAsync` with an **already-cancelled** token — the real "no longer graceful"
      shape 1.6 uses:

          PROBE 3.3 (the stop registration moved before the detach)
            fixed order (detach, then register): manager.GetByLinkedId("linked-2") is null  → PASS
            mutated order (register, then detach): a session for "linked-2" exists          → FAIL
               Expected manager.GetByLinkedId("linked-2") to be <null> …, but found

      So with the mutation the `Cancel()` runs inline from the registration while the manager is
      **still subscribed to the server**, and a channel event arriving at that instant creates a new
      session and starts a new save *after* the stop — precisely what the spec scenario "No new save
      starts from a server event after the stop" forbids, and what its stated reason ("because the
      service unsubscribes from the server before it wires the host's stop token to its source")
      claims is guaranteed. The requirement was implemented and the reason was true of the code; it
      was the *test* that did not bind it.

      **Closed in this task rather than recorded as owed, because the gap was requirement coverage:**
      the spec delta states that scenario outright, so leaving it unbound would have archived a
      scenario no test holds. The throwaway probe became
      `StopAsync_ShouldNotStartASave_WhenTheStopTokenIsAlreadyCancelledAndAnEventArrivesInline`,
      committed next to 1.4's ordering control, in the same file-local style and reusing
      `PendingSaveStore` — the event is raised from a `save.Token.Register` callback the test installs
      itself, so no new helper class was needed. Its first assertion,
      `save.Token.IsCancellationRequested.Should().BeTrue(...)`, exists to stop the test passing
      vacuously: without it, a mutation that dropped the stop registration entirely would leave the
      token uncancelled, the callback unrun and the remaining assertions trivially satisfied. Causal
      throughout — an already-cancelled stop token and a cancellation callback, no clock, no
      `Task.Delay`, and `sync-fence-baseline.json` untouched.

      **Proved red before it was accepted, not assumed.** Correct order — `dotnet build` 0 warnings /
      0 errors and:

          Passed!  - Failed:     0, Passed:    45, Skipped:     0, Total:    45 - Verbara.Sdk.Hosting.Tests.dll (net10.0)

      and the test alone: `Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1`. Then with the
      registration moved above the detach — still 0 warnings / 0 errors — it is the **only** failure
      in the assembly, which is what makes it a binding of this ordering and nothing else (the object
      dump is abridged at the marker; `<repo>` replaces the machine path):

          Failed Verbara.Sdk.Hosting.Tests.SessionManagerHostedServiceTests.StopAsync_ShouldNotStartASave_WhenTheStopTokenIsAlreadyCancelledAndAnEventArrivesInline [32 ms]
          Error Message:
             Expected store.Saves to contain a single item because the service unsubscribes from the server before it wires the host's stop token, so the cancellation cannot reach a still-subscribed manager and no second save is started, but found
          {
              Verbara.Sdk.Hosting.Tests.SessionManagerHostedServiceTests+PendingSave
              {
                  Failure = System.OperationCanceledException: The operation was canceled.
             at Verbara.Sdk.Hosting.Tests.SessionManagerHostedServiceTests.PendingSave.GetResult(Int16 token) in <repo>/Tests/Verbara.Sdk.Hosting.Tests/SessionManagerHostedServiceTests.cs:line 512
             at Verbara.Sdk.Sessions.Manager.CallSessionManager.PersistAsync(CallSession session) in <repo>/src/Verbara.Sdk.Sessions/Manager/CallSessionManager.cs:line 58,
                  IsObserved = True,
                  Token = … IsCancellationRequested = True …
              },
              … (the second PendingSave, identically shaped) …
          }.
          Failed!  - Failed:     1, Passed:    44, Skipped:     0, Total:    45 - Verbara.Sdk.Hosting.Tests.dll (net10.0)

      Two saves where one was expected: the second is the session the channel event created because it
      reached a manager that had not been detached yet. The mutation was then reverted and the
      assembly is **45 passed / 0 failed** at 0 warnings.

      **SURVIVOR — deleting `_stopRegistration.Dispose()` in `StopAsync`. Known and already recorded
      under 2.2; re-measured here at the new count**: build 0 warnings / 0 errors,
      `Verbara.Sdk.Hosting.Tests` **44 passed / 0 failed** (2.2 recorded 43/43, before
      `Dispose_ShouldNotThrow_WhenCalledMoreThanOnce` existed). Not a new finding.

      **Left as owed, deliberately, and this is why the two survivors are treated differently.** No
      spec scenario stands behind the registration-disposal line: it is an ordering detail inside one
      method, guarding a leak across a second `StopAsync` that no path in this change reaches, and
      the living spec says nothing about it — so it stays implemented and recorded, as 2.2 has it.
      The ordering survivor above is the opposite: the delta spec states its scenario in so many
      words, so an unbound mutation there is a requirement with no test, which is why it was closed
      here instead of written down.

      **Removing the `Dispose` gate is caught, as 2.3's amendment intends.** Deleting the two gate
      lines alone does not compile — `_disposed` becomes unread, `error CS0169: The field
      'SessionManagerHostedService._disposed' is never used` — so the mutation was applied in its
      faithful pre-gate form, gate *and* field removed, which builds at 0 warnings. **1 failed /
      43 passed**:

          Failed …Dispose_ShouldNotThrow_WhenCalledMoreThanOnce
             Did not expect any exception, but found System.ObjectDisposedException: The CancellationTokenSource has been disposed.
               at System.Threading.CancellationTokenSource.Cancel()
               at Verbara.Sdk.Hosting.SessionManagerHostedService.Dispose()

      **Bonus, because 2.3 calls the order load-bearing: releasing the source before cancelling it**
      (`_shutdown.Dispose(); _shutdown.Cancel();`) builds at 0 warnings and is caught twice — the
      claim holds. **2 failed / 42 passed**, both with
      `System.ObjectDisposedException : The CancellationTokenSource has been disposed.` thrown from
      `CancellationTokenSource.Cancel()` inside `Dispose`:
      `Dispose_ShouldCutShortAnInFlightSave_WhenTheServiceIsDisposedWithoutAStop` and
      `Dispose_ShouldNotThrow_WhenCalledMoreThanOnce` (the first call throws, so the gate does not
      hide it).

      **Net: of the 8 mutations, 6 were caught as the artifacts claimed — four of them by more tests
      than claimed — one claim (the ordering control) did not hold and is now corrected and closed by
      a new test proved red, and one survivor was already known and stays owed with its reason
      recorded.** The assembly goes 44 → 45 tests; `Verbara.Sdk.Governance.Tests` is 129 passed /
      0 failed and `sync-fence-baseline.json` is unchanged.
- [x] 3.4 `openspec validate --all --strict` clean.
      **Recorded:** rc=0, **`Totals: 11 passed, 0 failed (11 items)`** — five open changes and six
      living specs, with `✓ change/session-shutdown-token-is-cancelled-by-the-stop-phase` among
      them, carrying **no** diagnostic of any severity. Run *after* 4.4's `decision_ref` edit and
      after 2.5's and 2.6's records, so it validates the tree that ships. The local CLI is
      **1.13.1**, which is exactly the version `ci.yml`'s OpenSpec Validate job pins
      (`npx -y @fission-ai/openspec@1.13.1 validate --all --strict --no-interactive`), so this is
      the same gate and not a near neighbour. The only output beyond the eleven ✓ lines is
      pre-existing `[INFO] Requirement text is very long (>500 characters)` on six **living specs**
      (`ci-gating`, `claim-guards`, `docs-brand-consistency`, `provider-contract-fidelity`,
      `streaming-session-lifecycle`, `test-determinism`); INFO is not a failure under `--strict`,
      none of it is on this change, and nothing here made it worse.
- [x] 3.5 Prove the public surface did not move: `git diff --stat -- '*PublicAPI*'` is empty, so
      neither Pro nor Platform needs a recompile or a pin bump.
      **Recorded — empty, and proved three ways rather than one, because an empty diff is also what a
      mistyped pathspec prints.**
      - `git diff --stat -- '*PublicAPI*'` → no rows, rc=0; `git diff --stat --cached -- '*PublicAPI*'`
        → the same, so nothing is hiding in the index either.
      - No **new** `PublicAPI` file: `git status --porcelain` has no line matching `publicapi`, which
        an added surface file would produce as `??` and an empty diff would not catch.
      - The pathspec is live, not vacuous: **58** `PublicAPI*.txt` files exist in the tree, and a
        grep for `SessionManagerHostedService` and `SetShutdownToken` across all of them returns
        **nothing** — the service is `internal sealed` (`SessionManagerHostedService.cs:7`) and
        `SetShutdownToken` is `internal`, so neither is part of any package's declared surface to
        begin with.

      **What it means downstream.** Pro and Platform consume these packages as NuGet references
      against the declared public surface; nothing in it moved, so neither repo needs a recompile, a
      pin bump or a re-pack, and no `PublicAPI.Unshipped.txt` entry is owed. `SetShutdownToken` stays
      reachable from Pro through `InternalsVisibleTo` under the same name and signature — which is
      exactly why 2.4 corrected its prose instead of renaming it — so even the `InternalsVisibleTo`
      surface, which no `PublicAPI` file tracks, is unchanged. The behaviour behind it moves; the
      shape does not.

- [ ] 3.6 CI green through the merge queue. Enqueue it **alone**: the open changes that add a **new** ADR
      file (0046, 0047, 0056, 0057, 0058, 0059) all bump the same `**N ADRs**` figure, and the queue
      squashes. Whether git even sees the collision depends on where the two catalog rows land: rows
      inserted at the same spot conflict textually and the queue ejects the second before it builds;
      rows at different spots — the common case, since every one of those tasks adds its row *in
      numeric order* — merge cleanly and the second then fails `Unit Tests` inside the queue, a
      semantic conflict rather than a textual one. Either way the practice is the same: one
      ADR-adding change in the queue at a time, and re-count the figure after any rebase, because
      `strict:false` does not force one. Order does not otherwise matter — the guard counts files,
      not a contiguous sequence — so this change keeps ADR-0059 whenever it lands.

## 4. Decision record

- [x] 4.1 Land `docs/decisions/0059-a-lifetime-token-is-cancelled-by-the-phase-it-names.md` (0055 is
      the highest id on this branch). Status Accepted; Related: ADR-0054 (one owner for a
      cancellation source — this ADR applies the same rule to a token handed across a lifetime
      boundary), ADR-0053 (an ending is classified by who ended it). It states the rule — a component
      that outlives a phase creates its own source; a token borrowed from `StartAsync` may not be
      stored — and carries the three measurements this change makes: 1.7 (what the host's start token
      does today), 2.3 (a save started after disposal) and 2.6 (the multi-server path has no lifetime
      token at all).
      **Recorded:** landed, 190 lines, in the shape of its four nearest relatives (0053/0054/0057/0058
      run 126-163) — `# ADR-0059: A lifetime token is cancelled by the phase it names`, Status
      Accepted, Date 2026-09-21, Deciders `Harol A. Reina H.`, then Context / Decision (R1-R5) /
      Consequences / Alternatives considered. The headline rule is stated as written here: *a token a
      component keeps past a phase comes from a source that outlives that phase, and it is cancelled
      by the phase it names; a phase token may be used within its phase and may not be stored.*
      **The parenthetical above is stale after the rebase and was not acted on:** 0058 is the highest
      id on this branch, not 0055 — 0057 and 0058 landed while this change was open — and **0056 is a
      gap that stays one**, reserved by `ari-failed-connect-and-silent-catches`, which has not landed.
      Nothing was renumbered: the guard counts files, not a contiguous sequence, and
      `docs/decisions/` already holds gaps at 0046 and 0047.
      **Related** carries the two asked for — ADR-0054 (one owner for a cancellation source, read
      across a lifetime boundary) and ADR-0053 (an ending is classified by who ended it, which this
      change is what makes reachable) — plus **ADR-0045**, because "no wall-clock wait" is what
      decided the mechanism of 1.6's stop test and the ADR has to say why `HostOptions.ShutdownTimeout`
      was refused.
      All three measurements are carried: 1.7 as three numbered paragraphs (the real-host run with its
      pasted block, the `using` around `CreateLinkedTokenSource` decompiled, and "a dead token is
      silent, not loud"), 2.3 as its own consequence bullet, and 2.6 as the known-gap bullet. The five
      apply-time findings the main session asked for are each a consequence bullet: the start token's
      inertness, `CancelAfter`'s timer (only the `CancellationTokenSource(TimeSpan)` *constructor*
      special-cases zero), the `Dispose` gate closed tests-first with
      `Dispose_ShouldNotThrow_WhenCalledMoreThanOnce`, R4's ordering being unbound until a test proved
      red against the mutation bound it (with both wrong artifacts noted as corrected), and the
      deliberately-unbound `_stopRegistration.Dispose()` survivor **together with the rule that
      separates the two**: a scenario the spec states must be bound by a test; an internal ordering or
      hygiene detail no requirement mentions may be recorded instead.
      Public-MIT clean: a grep for every machine-root prefix over the new file returns nothing — no
      absolute machine path and no private-repo reference; the NuGet cache location is given as the
      package-relative `microsoft.extensions.hosting/10.0.10/...`, and no existing ADR was touched
      (`docs/decisions/` is append-only).
- [x] 4.2 Add the ADR-0059 row to `docs/decisions/README.md` in numeric order, matching the
      format of the existing rows. `TheDecisionCatalog_ShouldListEveryAdrOnDisk`, which landed with
      `audiosocket-accepted-connection-is-always-closed`, fails when a file has no row, so the
      omission is red rather than silent — but writing the row is still this task's job, and this
      change was the one of the four that did not carry the step.
      **Recorded:** the row is appended as the catalog's new last line, which *is* numeric order —
      0058 was last, and 0059 is the highest id on disk. Format matches the neighbours exactly:
      `- [ADR-0059](0059-a-lifetime-token-is-cancelled-by-the-phase-it-names.md) — <summary>. (Accepted, 2026-09-21)`.
      Counted after the edit rather than assumed: **56** files in `docs/decisions/*.md` excluding the
      catalog, **56** row link targets, **56** distinct ids — so the set equality
      `TheDecisionCatalog_ShouldListEveryAdrOnDisk` checks holds with no duplicate and no omission,
      and it passes by name (see 4.3).
- [x] 4.3 Land the ADR-count guard's other two edits **in this same PR**:
      `StatusBlockCoherenceTests.ThePublishedAdrCount_ShouldMatchTheDecisionsOnDisk` (#279) counts
      `docs/decisions/*.md` against the figure `README.md` publishes, and ADR-0042 D1 requires a
      changed figure's registry row to move with it.
      - bump the `**N ADRs**` figure in `README.md`;
      - update its row in `docs/claim-registry.md`.
      Key both edits to the **figure**, never to a line number: #280 has just moved this claim from
      `README.md:74` to `:67` and re-based the registry's line pointers with it, so a number recorded
      here goes stale on the next docs PR. Adding ADR-0059's file without these two fails the `Unit Tests` job.
      **Recorded — both edits keyed to the figure, not to a line number.** The count was re-counted on
      disk first rather than taken from the artifacts: `find docs/decisions -maxdepth 1 -name '*.md' ! -name 'README.md' | wc -l`
      is **56** after ADR-0059 (55 before), which is exactly what
      `ThePublishedAdrCount_ShouldMatchTheDecisionsOnDisk` computes.
      - `README.md`: the sole `**N ADRs**` occurrence went **`**55 ADRs**` → `**56 ADRs**`**, matched by
        the pattern and asserted unique before replacing. Edited in place, so the figure is still on
        `README.md:67` and the registry's line pointer did not move.
      - `docs/claim-registry.md` (ADR-0042 D1), the one row carrying that figure — before
        `| 67 | **55 ADRs** | ENFORCING | StatusBlockCoherenceTests — counts docs/decisions/*.md, excluding the catalog README.md | **OK** |`,
        after the same row with **56**. Class, guard and status are unchanged: **OK** before and
        **OK** after, which is what the reverse coupling is for.
      **Proved, not assumed:** `dotnet test Tests/Verbara.Sdk.OpenTelemetry.Tests/ --filter "FullyQualifiedName~StatusBlockCoherenceTests"`
      is green at all **three** cases —

          Passed Verbara.Sdk.OpenTelemetry.Tests.StatusBlockCoherenceTests.TheHeadlineVersion_ShouldMatchTheVersionThePackagesShipWith [4 ms]
          Passed Verbara.Sdk.OpenTelemetry.Tests.StatusBlockCoherenceTests.ThePublishedAdrCount_ShouldMatchTheDecisionsOnDisk [< 1 ms]
          Passed Verbara.Sdk.OpenTelemetry.Tests.StatusBlockCoherenceTests.TheDecisionCatalog_ShouldListEveryAdrOnDisk [3 ms]
          Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3, Duration: 10 ms - Verbara.Sdk.OpenTelemetry.Tests.dll (net10.0)

      — so the new file, its catalog row, the `README.md` figure and the registry row are coherent
      with each other and with the tree. 3.6's practice still applies: re-count the figure after any
      rebase, because a sibling ADR-adding change landing first makes 56 wrong without touching a line
      of this diff.

- [x] 4.4 Update this change's `proposal.md` `decision_ref` to `Sdk/ADR-0059` once that file exists,
      so the living record points at the ADR this change wrote rather than at its nearest neighbour.
      **Recorded:** `decision_ref: Sdk/ADR-0054` → `decision_ref: Sdk/ADR-0059`, asserted unique
      before replacing (one match for `^decision_ref: Sdk/ADR-0054$`). That frontmatter value is the
      whole edit — one line changed, one line added, nothing else in the file touched. The prose that
      still cites ADR-0054 is correct and stays: § Why derives the rule from ADR-0054 R2, which is
      what ADR-0059 now extends across a lifetime boundary, and ADR-0059's own **Related** carries
      the link back. `openspec validate --all --strict` accepts the new value (3.4).

## 5. Close-out

- [x] 5.1 `CHANGELOG.md` `[Unreleased]` entry under `### Fixed`, stating in operator terms what moves:
      a save in flight when the host stops now keeps the host's shutdown budget and is cut short when
      that budget expires, instead of being governed by a token that only an aborted start cancels.
      Leave the `(#N)` citation for close-out.
- [ ] 5.2 `openspec archive session-shutdown-token-is-cancelled-by-the-stop-phase --yes` once the fix
      is on `main` (the CLI, never the agent archive path). This change creates a new capability, so
      after the archive check that `openspec/specs/session-persistence-lifecycle/spec.md` exists and
      author its `## Purpose` to match the other living specs, then re-run
      `openspec validate --all --strict`.
