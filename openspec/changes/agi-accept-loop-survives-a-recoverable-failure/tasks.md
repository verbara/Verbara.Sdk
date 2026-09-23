# Tasks: agi-accept-loop-survives-a-recoverable-failure

## Phase A — foundation

- [x] A1 Record the head commit this change starts from, and re-read
      `src/Verbara.Sdk.Agi/Server/FastAgiServer.cs` as it stands rather than from this proposal. Confirm
      the three facts the whole design rests on, each of which makes the transplant literal here where
      it is not in the two ARI audio servers: `StopAsync` writes `Stopping` as its **first** statement,
      before `_listener?.Stop()`; `IsRunning` is `State == AgiServerState.Listening`; and `State` is
      read through `Volatile.Read`. If any is false, stop and say so — the filter in B2 depends on all
      three.
      **Done.** Head commit `3282ed22`. Re-read `src/Verbara.Sdk.Agi/Server/FastAgiServer.cs`
      (209 lines) as it stands. **All three facts CONFIRMED**, at the exact lines the proposal cites:
      1. `StopAsync` (`:182`) writes `SetState(AgiServerState.Stopping);` at `:184` as its **first**
         statement; `_listener?.Stop();` follows at `:186`. Ordering holds.
      2. `:49` — `public bool IsRunning => State == AgiServerState.Listening;`
      3. `:48` — `public AgiServerState State => (AgiServerState)Volatile.Read(ref _state);`
      Also confirmed: `SetState` (`:51`) writes through `Interlocked.Exchange`, so the stop write is
      fenced on both sides — a stop-induced `SocketException` cannot observe a stale `Listening`.
      B2's `when (!IsRunning)` filter is therefore sound here. Loop shape also as described:
      `AcceptLoopAsync` at `:90`, `try` at `:92` **outside** the `while` at `:94`, catching only
      `OperationCanceledException` (`:106`, "Normal shutdown") and `ObjectDisposedException` (`:110`,
      "Listener stopped"). No `SocketException` arm. `Faulted` is written only inside `StartAsync`
      (`:81`), never from the loop — as the proposal states.
- [x] A2 `src/Verbara.Sdk.Agi/Verbara.Sdk.Agi.csproj` has **no** `InternalsVisibleTo`. Add one for
      `Verbara.Sdk.Agi.Tests`, matching the form `src/Verbara.Sdk.Ari/Verbara.Sdk.Ari.csproj:8` uses.
      Nothing else in that file changes.
      **Done.** Added to `src/Verbara.Sdk.Agi/Verbara.Sdk.Agi.csproj` as a new first `<ItemGroup>`
      (lines 7-9), matching the form at `src/Verbara.Sdk.Ari/Verbara.Sdk.Ari.csproj:8`:
      `<InternalsVisibleTo Include="Verbara.Sdk.Agi.Tests" />`. Only that entry was added — the Ari
      file also carries a `Verbara.Sdk.Benchmarks` entry, which was deliberately **not** copied since
      no benchmark reaches Agi internals. Nothing else in the file changed. Verified the attribute is
      generated: `Verbara.Sdk.Agi.AssemblyInfo.cs:25` now emits
      `[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Verbara.Sdk.Agi.Tests")]`.
- [x] A3 `Tests/Verbara.Sdk.Agi.Tests` has **no** `FakeTimeProvider`. Copy
      `Tests/Verbara.Sdk.Ari.Tests/FakeTimeProvider.cs`, adjusting only its namespace. This is the
      repository's **fourth** hand-written copy of a type Microsoft ships as
      `Microsoft.Extensions.TimeProvider.Testing`, which the private sibling repo already takes through
      central package management. Consolidating is out of scope here and is harvested in C4, not solved.
      **Done.** `Tests/Verbara.Sdk.Ari.Tests/FakeTimeProvider.cs` copied to
      `Tests/Verbara.Sdk.Agi.Tests/FakeTimeProvider.cs`. `diff` against the source reports **exactly
      one** changed line — line 3, the namespace (`Verbara.Sdk.Ari.Tests` -> `Verbara.Sdk.Agi.Tests`).
      Nothing else was touched, per the task's "adjusting only its namespace".
      The two test projects are structurally identical (same csproj shape, same `Usings.cs` of
      `global using Xunit;`), so the copy compiles unchanged. Confirmed the unused `internal sealed
      class` trips **no** analyzer under `TreatWarningsAsErrors` + `WarningLevel 9999` +
      `AnalysisLevel latest-recommended` (CA1812 is not enabled at `latest-recommended`): the build is
      0/0 with the type present and referenced by nothing, and `FakeTimeProvider` is confirmed present
      in the compiled `Verbara.Sdk.Agi.Tests.dll`.
      **Drift — owner ruling owed.** Following "adjusting only its namespace" literally leaves one
      now-inaccurate reference in the copy's `<remarks>` (line 14): it tells the reader the fake is how
      "the accept backoff in `AriOutboundListener.AcceptLoopAsync` is driven", which is the *sibling's*
      loop, not the one this project's copy will serve once B2 lands. It is inside `<c>` tags, not a
      `cref`, so it does not break the build or the docs. Left verbatim on purpose: byte-identical
      copies stay diffable, which is what makes C4's consolidation debt auditable. If the owner would
      rather it read `FastAgiServer.AcceptLoopAsync`, that is a one-line follow-up — flagged rather than
      decided here, since re-pointing it is a change the task text did not authorise.
- [x] A4 Note the fence budget before writing any test:
      `Tests/Verbara.Sdk.Agi.Tests/Server/FastAgiServerTests.cs` carries **2** grandfathered barriers in
      `sync-fence-baseline.json`. That number may not rise. New tests drive the clock through A3's fake
      and order themselves on an accept, never on `Task.Delay` or `Thread.Sleep`.
      **Done — note only, no edit.** Confirmed against `<repo>/sync-fence-baseline.json:5`:
      `"Tests/Verbara.Sdk.Agi.Tests/Server/FastAgiServerTests.cs": 2`. That number may not rise.
      For B1's benefit, the guard was read rather than assumed: `SyncFenceScanner` is Roslyn-based and
      carries explicit false-positive immunity for comments, XML doc and string literals
      (`Scan_ShouldIgnore_WhenTaskDelayInXmlDoc` et al. in
      `Tests/Verbara.Sdk.Governance.Tests/SyncFenceRegressionGuardTests.cs`). This matters because A3's
      copied file mentions `Task.Delay` at line 12 inside a `<see cref=...>` — that mention is **not**
      counted, which is why the Ari original needs no baseline entry either. Verified empirically:
      Governance suite **Failed: 0, Passed: 129** with the new file in the tree, and
      `sync-fence-baseline.json` unchanged.
      The sibling's own tests sit at `Tests/Verbara.Sdk.Ari.Tests/Outbound/AriOutboundListenerTests.cs`:
      **1** barrier (baseline line 13) — B1 should aim at 0 new barriers, not at parity with that 1.

## Phase B — the fix, test first

- [ ] B1 **Write the regression tests first, against the unfixed loop, and paste their verbatim
      failures here.** In `Tests/Verbara.Sdk.Agi.Tests/Server/FastAgiServerTests.cs`, following the
      shape of `Tests/Verbara.Sdk.Ari.Tests/Outbound/AriOutboundListenerTests.cs`:
      - `AcceptLoopAsync_ShouldReportItAndKeepAccepting_WhenAnAcceptFailsWhileRunning` — the accept
        seam raises `SocketException` once, then serves a real connection. Asserts one `AcceptLoopFailed`
        entry at Error **and** that the next connection is still accepted.
      - `AcceptLoopAsync_ShouldBackOffFurther_WhenAcceptsKeepFailing` and reset on success — asserted
        against the fake clock's timers, not against elapsed time.
      - `StopAsync_ShouldEndTheLoopWithoutReportingAFailure_WhenTheServerIsStopped` — a real accepted
        connection, then a stop. Asserts **no** `AcceptLoopFailed`. This is the case that catches a
        filter transplanted without checking that `StopAsync` clears the running state first, so it is
        the one that must not be weakened.
      A test that passes against the unfixed loop is a broken test; report that rather than adjusting
      it until it goes red.
      **Partially done — 2 of the 3 written and measured; the third is blocked on B2 and is owed.**

      *Production change made under B1, and the only one:* `FastAgiServer` gained an `internal`
      accept seam — `AcceptOverride`, a `Func<CancellationToken, ValueTask<TcpClient>>?` property, and
      the one-line `AcceptAsync(ct)` indirection at the accept call — copied from
      `src/Verbara.Sdk.Ari/Outbound/AriOutboundListener.cs:100,245`. Without it no test can make an
      accept fail without exhausting the process's file descriptors. `AcceptLoopAsync` itself is
      **unchanged**: `try` still outside the `while`, still two catches. Nothing else was added.

      *Written, and red against the unfixed loop:*
      `AcceptLoopAsync_ShouldReportItAndKeepAccepting_WhenAnAcceptFailsWhileRunning`. The seam raises
      `SocketException(TooManyOpenSockets)` on attempt 1, hands over a real loopback connection on
      attempt 2, and parks from attempt 3, so reaching attempt 3 is the evidence the loop survived.
      Verbatim:

      ```
        Failed Verbara.Sdk.Agi.Tests.Server.FastAgiServerTests.AcceptLoopAsync_ShouldReportItAndKeepAccepting_WhenAnAcceptFailsWhileRunning [10 s]
        Error Message:
         Expected keptAccepting to be True because an accept that failed while the server is still
         running must not end the loop — after 1 attempt(s) all the server had logged was
         [ServerStarted], but found False.
      ```

      `after 1 attempt(s)` and `[ServerStarted]` are the defect stated by the test itself: one accept,
      one throw, no second attempt, and **not one log line** about it. The later assertions
      (`ContainSingle(AcceptLoopFailed at Error)`, `Contain(ConnectionAccepted)`, `IsRunning` still
      true) are unreachable today because the run stops at the first, and they are what B2/B3 turn
      green.

      *Written, and green as predicted:*
      `StopAsync_ShouldEndTheLoopWithoutReportingAFailure_WhenTheServerIsStopped` — **Passed [14 ms]**.
      It pins behaviour that is already correct, so green before the fix is the right result, not a
      broken test. It uses the **real** accept (no override), a real connection whose script signals a
      `TaskCompletionSource` — which is what proves the loop is back on the next accept — then stops.
      Measured with a standalone probe rather than assumed: on this Linux host, `TcpListener.Stop()`
      under a pending `AcceptTcpClientAsync(ct)`, in `StopAsync`'s order (`Stop()` before
      `CancelAsync()`), raises `System.Net.Sockets.SocketException` with
      `SocketErrorCode=OperationAborted`. Two consequences worth recording:
      1. **Today every clean stop faults the accept loop task**, because there is no `SocketException`
         arm at all; `StopAsync`'s `SuppressThrowing` is the only reason nobody sees it.
      2. After B2 this test lands squarely on the filtered `catch (SocketException) when (!IsRunning)`
         arm, so it is not vacuous once the arm exists — it is exactly the case that catches that
         filter being transplanted without `StopAsync`'s ordering. Its `NotContain(AcceptLoopFailed)`
         clause *is* vacuous **today** only because no such event is declared yet; its `ServerStopped`
         and `IsRunning == false` clauses pin real behaviour now.

      *Not written — `AcceptLoopAsync_ShouldBackOffFurther_WhenAcceptsKeepFailing` (and the reset),
      and the reason is a task-order conflict, not an omission.* That test's whole subject is the wait
      the loop asks for, and the only way to observe a wait without spending it is
      `Task.Delay(backoff, _timeProvider, ct)` resolving against an injected `TimeProvider` — the
      seam, the two backoff constants and the `internal` constructor that carries it are all listed
      under **B2**, and B1 was scoped to add the accept seam *and nothing else*. Adding the
      `TimeProvider` field alone would not help either: nothing would consume it until B2 restructures
      the loop, so it would be dead production code. The alternatives were both refused on purpose —
      asserting on elapsed wall time is forbidden by the change's own fence rule, and asserting only
      "it kept accepting" would duplicate the first test while pretending to cover the backoff.
      **Owed by B2:** write the backoff/reset case against the fake clock once the `TimeProvider`
      constructor exists, and strengthen the first test the same way (read `InitialAcceptBackoff` off
      `FakeTimeProvider.TimersCreated` and assert the retry has *not* happened before the clock moves
      — `Tests/Verbara.Sdk.Ari.Tests/Outbound/AriOutboundListenerTests.cs:436-537` is the shape).
      Until then the first test tolerates a real 100 ms backoff on `TimeProvider.System`, which it
      orders on a `TaskCompletionSource`, never on a clock.

      *Fence budget held.* No `Task.Delay` and no `Thread.Sleep` was added to the test file — the two
      new tests order on a `TaskCompletionSource` and on `Task.WaitAsync`. `sync-fence-baseline.json`
      is **unchanged** (`git diff --stat` empty) and `Verbara.Sdk.Governance.Tests` is
      **Failed: 0, Passed: 129**.

      *Measured after the two tests landed:* `dotnet build Verbara.Sdk.slnx -c Release` -> **0
      Warning(s), 0 Error(s)**, exit code `0` captured separately from the test run. CI unit filter ->
      **Passed: 3610, Failed: 1** across 34 assemblies, the one failure being the intended red test
      above (baseline was Passed: 3609, Failed: 0, so both new tests are accounted for).
      `tools/audit-test-asserts.sh` -> **Violations: 0**. `git diff --stat -- '*PublicAPI*'` -> empty.
- [x] B2 Apply A′ to `AcceptLoopAsync`: `try` inside the `while`; `catch (OperationCanceledException)`
      and `catch (ObjectDisposedException)` keep their comments and `break`; a filtered
      `catch (SocketException) when (!IsRunning)` breaks; an unfiltered `catch (SocketException ex)`
      logs `AcceptLoopFailed` at Error; then `await Task.Delay(backoff, _timeProvider, ct)` inside its
      own `try` whose `catch (OperationCanceledException)` breaks; then double the backoff to the cap.
      Add `InitialAcceptBackoff` 100 ms and `MaxAcceptBackoff` 5 s as `internal static readonly`, a
      `TimeProvider` field with the public constructor delegating `TimeProvider.System` and a new
      `internal` constructor taking it, and an `internal` accept seam. Every added member is `internal`;
      `PublicAPI.*.txt` must not change.
      **Done.** `AcceptLoopAsync` (`<repo>/src/Verbara.Sdk.Agi/Server/FastAgiServer.cs:120`) is now
      option A′ in the sibling's exact shape: `var backoff = InitialAcceptBackoff;` above the `while`,
      the `try` inside it, `continue` after the connection hand-off, then, in this order,
      `catch (OperationCanceledException)` → `break`, `catch (ObjectDisposedException)` → `break`,
      `catch (SocketException) when (!IsRunning)` (`:154`) → `break`, `catch (SocketException ex)`
      (`:164`) → `FastAgiServerLog.AcceptLoopFailed` and fall through, then
      `await Task.Delay(backoff, _timeProvider, ct)` in its own `try` whose
      `catch (OperationCanceledException)` breaks, then
      `backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxAcceptBackoff.Ticks))`. The two
      original catch comments are kept and extended with why each arm is reached, so the
      `ObjectDisposedException` arm is not read as dead code on Linux.
      Added members, all `internal`: `InitialAcceptBackoff` 100 ms (`:44`), `MaxAcceptBackoff` 5 s
      (`:47`), a `readonly TimeProvider _timeProvider` field, and a four-argument constructor (`:82`)
      that the existing public three-argument one now delegates to with `TimeProvider.System`. The
      accept seam (`AcceptOverride` + `AcceptAsync`) was already in the tree from B1 and was not
      touched.
      **Verified, not assumed:** `git diff --stat -- '*PublicAPI*' sync-fence-baseline.json` is
      **empty**. `dotnet build Verbara.Sdk.slnx -c Release` -> **0 Warning(s), 0 Error(s)**, exit code
      `0` captured separately from every test run.
      **Test work B1 left owed to B2, now done.** The two cases B1 could not write before the
      `TimeProvider` seam existed are in
      `<repo>/Tests/Verbara.Sdk.Agi.Tests/Server/FastAgiServerTests.cs`, plus the
      `NextTimerAsync(FakeTimeProvider)` helper that reads a wait off the fake clock without spending
      it. This is what finally gives A3's `FakeTimeProvider` a consumer.
      - `AcceptLoopAsync_ShouldBackOffFurther_WhenAcceptsKeepFailing` — every accept fails, so the
        loop is a pure backoff generator; the eight waits it asks for are read off the fake clock and
        asserted as `100, 200, 400, 800, 1600, 3200, 5000, 5000 ms`, with the first pinned to
        `InitialAcceptBackoff` and the last to `MaxAcceptBackoff`.
      - `AcceptLoopAsync_ShouldResetTheBackoff_WhenAnAcceptSucceedsBetweenFailures` — accepts 1 and 3
        fail with a real loopback connection handed over in between; the wait after the third is
        `InitialAcceptBackoff` again rather than the doubled one.
      *Split, and reported rather than decided silently:* B1's bullet names one case,
      "`AcceptLoopAsync_ShouldBackOffFurther_WhenAcceptsKeepFailing` and reset on success". It is
      written as **two** methods because the reset needs a different arrangement (a real connection
      mid-run) and because `Tests/Verbara.Sdk.Ari.Tests/Outbound/AriOutboundListenerTests.cs` — which
      B1 names as "the model" — splits them the same way. So the suite has **four** new cases, not
      three.
      *The two B1 oracle tests were NOT edited.* B1 also owed B2 a strengthening of
      `AcceptLoopAsync_ShouldReportItAndKeepAccepting_WhenAnAcceptFailsWhileRunning` onto the fake
      clock. **Not done, deliberately:** the brief for this task says the B1 tests are the oracle and
      must not be modified. That test passes unchanged, ordered on its `TaskCompletionSource` and
      tolerating a real 100 ms backoff on `TimeProvider.System`; the fake-clock coverage it would have
      gained is what the two new cases above now carry. Flagged for the owner rather than settled
      here.
      *Self-check that the new cases are not vacuous* (one B5 mutation, run early and reverted):
      removing `backoff = InitialAcceptBackoff;` after a successful accept leaves the build at 0/0 and
      is caught by `AcceptLoopAsync_ShouldResetTheBackoff_WhenAnAcceptSucceedsBetweenFailures` —
      `Failed: 1, Passed: 11`. The file was restored and re-measured green before moving on. B5 still
      owes the other four mutations.
- [x] B3 Add `[LoggerMessage(Level = LogLevel.Error, ...)] AcceptLoopFailed(ILogger, Exception)` to
      `FastAgiServerLog`, which already declares `ConnectionError` at Error for the loss of a single
      connection. Match its `"[FastAGI] ..."` message style.
      **Done**, at `<repo>/src/Verbara.Sdk.Agi/Server/FastAgiServer.cs:33-34`, declared last in
      `FastAgiServerLog` directly under `ConnectionError`:
      `[LoggerMessage(Level = LogLevel.Error, Message = "[AGI] Accept failed — the server stays bound
      and accepts again after a backoff")]`. Source-generated, so it stays AOT-safe with no reflection,
      and the generator takes the `Exception` parameter as the entry's exception — which is what lets
      the tests assert `ExceptionType == nameof(SocketException)` and `EventName == "AcceptLoopFailed"`
      off the `EventId.Name` the generator derives from the method name.
      **Drift in this task's own wording.** It says to match a `"[FastAGI] ..."` message style, but no
      message in this file uses that prefix: all six existing entries are `"[AGI] ..."`
      (`ServerStarted`, `ServerStopped`, `ConnectionAccepted`, `ScriptExecuting`, `NoScriptMapped`,
      `ConnectionError`). `[AGI]` was used, because matching the file is what the instruction means in
      substance; `[FastAGI]` would have been the only odd one out. The body deliberately mirrors the
      sibling's (`AriOutboundListenerLog.AcceptLoopFailed`, "the listener stays bound and accepts again
      after a backoff") so an operator grepping either loop reads the same sentence.
- [x] B4 **Do not write `Faulted` from the loop**, and record why here: under A′ the listener is still
      bound and still accepting, and `Faulted` would make `StartAsync` refuse to start over a loop that
      is still working. State plainly that the health check therefore still reads `Healthy` under a
      persistent failure, and that the Error line is the whole of what an operator gains.
      **Done — a written record, no code.** The loop writes no state at all. Verified against the tree
      rather than asserted: `AgiServerState.Faulted` appears exactly twice in
      `<repo>/src/Verbara.Sdk.Agi/Server/FastAgiServer.cs`, both inside `StartAsync` — the guard at
      `:96` that refuses to start from it, and the `SetState` at `:111` on a `_listener.Start()` that
      threw. `AcceptLoopAsync` (`:120`) contains no `SetState`.

      **Why the loop must not write it.** `Faulted` is a *terminal* state in this type, not a report:
      `StartAsync` opens with `if (State == AgiServerState.Faulted) throw new InvalidOperationException
      ("Cannot start from Faulted state. Call StopAsync first.")` (`:96`). Under A′ a survived accept
      failure leaves the listener **bound and still accepting** — that is the entire point of the
      change. Writing `Faulted` from the loop would therefore make a later `StartAsync` throw over a
      server that is working, and would turn a recoverable failure into an unrecoverable one. It would
      also contradict the loop's own discriminator: `IsRunning` is `State == AgiServerState.Listening`
      (`:59`), so writing `Faulted` would make the very next `catch (SocketException) when (!IsRunning)`
      read the failure as a stop and `break` — the fix would disable itself on the second consecutive
      failure.

      **The limit this leaves, stated plainly so the next reader does not assume it was solved.**
      `AgiHealthCheck` maps `Listening => HealthCheckResult.Healthy("AGI server listening")`
      (`<repo>/src/Verbara.Sdk.Agi/Diagnostics/AgiHealthCheck.cs:17`) and reads nothing else. A server
      whose accepts fail **persistently** — descriptor exhaustion that is not transient, say — stays in
      `Listening`, so `AgiHealthCheck` goes on reporting **Healthy** while not one call is being
      served. An operator paging on that health check learns nothing. What this change gives them is
      the `AcceptLoopFailed` Error line, repeated at a backoff that doubles to 5 s — **and that log
      line is the whole of the gain.** The health signal is unchanged, and `AgiServerState.Faulted`'s
      own documentation ("Listener faulted (address in use, **fd exhaustion**)",
      `<repo>/src/Verbara.Sdk/Enums/AgiServerState.cs:14`) still names a case the loop does not write.

      **What would close it, and why it is not this change.** Making a retrying loop read as
      `Degraded` needs a non-terminal state that `AgiHealthCheck` maps and `StartAsync` does not
      refuse — a decision about what `AgiServerState` means, with a public enum member and a public
      behaviour change behind it. That is a separate proposal, and the proposal and the delta spec
      both already carry it as a recorded residual rather than an oversight.
- [x] B5 Mutations, each applied alone and reverted, with the catching test recorded verbatim: the
      backoff `await` removed; the filtered stop clause removed; `break` instead of `continue` on the
      unfiltered arm; the backoff reset removed; and the filter changed to
      `when (ct.IsCancellationRequested)`. Say which of these no test catches — do not claim coverage
      that does not exist.
      **Done. Four of the five are caught; the fifth is not, and is recorded as uncovered.**

      *Method.* Every mutation was generated from one pristine copy of
      `<repo>/src/Verbara.Sdk.Agi/Server/FastAgiServer.cs`, so each was applied **alone** and never on
      top of another, and every revert was proved by `diff` against that copy rather than assumed.
      **Each build's exit code was captured on its own, before any test ran** — the project's known
      trap is `dotnet test --no-build` after a failed build silently rerunning the previous binary, and
      mutation 3 below is exactly the case where it would have produced a false verdict.
      Baseline re-measured first rather than taken from B2: `dotnet build Verbara.Sdk.slnx -c Release`
      -> **0 Warning(s), 0 Error(s)**, exit `0`; `Verbara.Sdk.Agi.Tests` under the CI unit filter ->
      **Total tests: 200, Passed: 200, Failed: 0**.

      **1. The backoff `await` removed** (the whole `try { await Task.Delay(backoff, _timeProvider,
      ct); } catch (OperationCanceledException) { break; }` block). Build exit `0`, 0 Warning(s),
      0 Error(s). **Caught**, by
      `AcceptLoopAsync_ShouldResetTheBackoff_WhenAnAcceptSucceedsBetweenFailures`:

      ```
        Failed Verbara.Sdk.Agi.Tests.Server.FastAgiServerTests.AcceptLoopAsync_ShouldResetTheBackoff_WhenAnAcceptSucceedsBetweenFailures [10 s]
        Error Message:
         System.TimeoutException : The operation has timed out.
      ```

      **A second consequence, worth recording because it is not a red test.** The run never finished:
      191 tests had reported `Passed` and that one `Failed` when
      `AcceptLoopAsync_ShouldBackOffFurther_WhenAcceptsKeepFailing` wedged the test host at **98.4%
      CPU**, and it had to be killed after 10 minutes. The cause is structural, not a test bug: with
      the delay gone the loop's failure path contains **no yielding await at all** — the test's
      `AcceptOverride` throws synchronously, so `AcceptLoopAsync` spins in its own frame, and
      `StartAsync` calls it directly (no `Task.Run`), so `await server.StartAsync()` never returns. In
      CI this mutation reads as a **job timeout**, not as a failing assertion. The backoff `await` is
      therefore load-bearing twice over: it paces a persistent failure, and it is the only point at
      which the failure path yields.
      The file was reverted, rebuilt (exit `0`, 0/0) and re-measured **Passed: 200, Failed: 0** before
      the next mutation, because the kill left a half-run behind.

      **2. The filtered stop clause `catch (SocketException) when (!IsRunning)` removed.** Build exit
      `0`, 0 Warning(s), 0 Error(s). **Caught** — **Failed: 1, Passed: 199** — by
      `StopAsync_ShouldEndTheLoopWithoutReportingAFailure_WhenTheServerIsStopped`, which is the case
      B1 named as the one that must not be weakened:

      ```
        Failed Verbara.Sdk.Agi.Tests.Server.FastAgiServerTests.StopAsync_ShouldEndTheLoopWithoutReportingAFailure_WhenTheServerIsStopped
        Error Message:
         Expected logger.Entries {…} to not have any items matching (entry.EventName ==
         "AcceptLoopFailed") because an accept aborted by the stop is the stop, not a failure — this
         is the case that catches a filter transplanted without checking that StopAsync clears the
         running state before it touches the listener, but found
         {
             EventName = "AcceptLoopFailed",
             ExceptionType = "SocketException",
             Level = LogLevel.Error {value: 4}
         }.
      ```

      **3. `break` instead of `continue` on the unfiltered arm — DRIFT: this wording does not match
      the tree, and the literal reading does not compile.** The unfiltered arm carries **no**
      `continue`; it falls through to the backoff. The only `continue` in the loop is on the *success*
      path (`<repo>/src/Verbara.Sdk.Agi/Server/FastAgiServer.cs:136`). The sibling
      `AriOutboundListener.AcceptLoopAsync` has that same shape, so this is the transplanted shape and
      not a local deviation — the task text is what is off. Rather than pick one reading silently,
      all three were measured:

      - **3-literal** — `break;` appended to `catch (SocketException ex)` after the log, so the loop
        ends on a survivable failure. **It does not compile.** `dotnet build` exit **`1`**:
        `FastAgiServer.cs(179,13): error CS0162: Unreachable code detected` — with every arm leaving
        the loop, the backoff block is unreachable, and `TreatWarningsAsErrors` turns that into an
        error. **No test was run against it**, deliberately: the build failed, so `--no-build` would
        have reported mutation 2's binary and invented a verdict. Here the **compiler** is the oracle,
        not the suite.
      - **3b** — the success path's `continue` -> `break` (the only literal `continue`). Build exit
        `0`, 0/0. **Caught** — **Failed: 2, Passed: 198**:
        `AcceptLoopAsync_ShouldReportItAndKeepAccepting_WhenAnAcceptFailsWhileRunning` with
        *"Expected keptAccepting to be True because an accept that failed while the server is still
        running must not end the loop — after 2 attempt(s) all the server had logged was
        [ServerStarted, AcceptLoopFailed, ConnectionAccepted], but found False."*, and
        `AcceptLoopAsync_ShouldResetTheBackoff_WhenAnAcceptSucceedsBetweenFailures` with
        `System.TimeoutException : The operation has timed out.`
      - **3c** — the smallest form of 3-literal that builds: `break;` in the unfiltered arm **and**
        the now-unreachable backoff block deleted. Build exit `0`, 0/0. **Caught** — **Failed: 3,
        Passed: 197** — by all three of `AcceptLoopAsync_ShouldResetTheBackoff_…`,
        `AcceptLoopAsync_ShouldBackOffFurther_…` (both `System.TimeoutException : The operation has
        timed out.`) and `AcceptLoopAsync_ShouldReportItAndKeepAccepting_…`
        (*"…after 1 attempt(s) all the server had logged was [ServerStarted, AcceptLoopFailed], but
        found False."*). This is the semantically important one — it is the "log and let the loop
        exit" alternative ADR-0056 R6 rejected — and every new case catches it.

      **4. The backoff reset on a successful accept removed.** Build exit `0`, 0 Warning(s),
      0 Error(s). **Caught** — **Failed: 1, Passed: 199** — by
      `AcceptLoopAsync_ShouldResetTheBackoff_WhenAnAcceptSucceedsBetweenFailures`:

      ```
        Failed Verbara.Sdk.Agi.Tests.Server.FastAgiServerTests.AcceptLoopAsync_ShouldResetTheBackoff_WhenAnAcceptSucceedsBetweenFailures [30 ms]
        Error Message:
         Expected 100ms because a successful accept starts the run over, so the next failure waits the
         initial backoff again rather than the doubled one, but found 200ms.
      ```

      This re-measures from pristine the self-check B2 ran early; the verdict is the same.

      **5. The filter changed to `when (ct.IsCancellationRequested)` — NOT CAUGHT. No test catches
      this mutation.** Build exit `0`, 0 Warning(s), 0 Error(s). `Verbara.Sdk.Agi.Tests` under the CI
      unit filter -> **Total tests: 200, Passed: 200, Failed: 0**. The one covering case,
      `StopAsync_ShouldEndTheLoopWithoutReportingAFailure_WhenTheServerIsStopped`, was then run 15
      further times on its own: **PASS=15, FAIL=0** — a stable survival, not a flake that happened to
      land green. To be sure nothing elsewhere in the repository separates the two predicates, the
      whole unit lane was run over the mutated tree: full-solution build exit `0`, **0 Warning(s),
      0 Error(s)**, then `dotnet test Verbara.Sdk.slnx` under the CI filter -> **Passed: 3613,
      Failed: 0** across **30** assemblies. This is the same result the sibling change measured and
      recorded as not separable.

      *Why it survives, deduced from two of these runs rather than asserted.* Mutation 2 proves the
      stop-induced ending reaches the catch chain as a **`SocketException`**: with the filtered arm
      gone it landed on the unfiltered `catch (SocketException ex)` and logged `AcceptLoopFailed`.
      Mutation 5 then passes with **no** `AcceptLoopFailed`, so that same `SocketException` was taken
      by the mutated filter — which means `ct.IsCancellationRequested` was already **true** when the
      filter ran. `StopAsync` writes `Stopping`, calls `_listener.Stop()`, and then awaits
      `_cts.CancelAsync()`; the aborted accept's continuation is scheduled on the thread pool and is
      not observed until after that cancel. **Both predicates agree on every path this suite can
      drive.**

      *What the survival does and does not mean.* `!IsRunning` remains the correct discriminator for
      the reason A1 and B2 recorded — it is written first and read through `Volatile.Read`, so it
      cannot be late, whereas `ct` is cancelled only after `Stop()` returns and can be. The survival
      says the suite **cannot observe that window from outside the type**, not that the window is not
      there. Separating them would need a seam that suspends the loop between `Stop()` and
      `CancelAsync()` — a new test design, which is B1's territory and closed, and which this task
      explicitly forbids inventing. So it is written down as **uncovered**, not dressed up as covered.

      *Tree restored, verified rather than assumed.* `diff` against the pristine copy is empty and the
      file's md5 is back to `975dab2cb800fb2abfd1696c40b51f82`; `git diff --stat` is byte-identical to
      what it was before the first mutation (**3 files changed, 393 insertions(+), 11 deletions(-)**);
      `git diff --stat -- '*PublicAPI*' sync-fence-baseline.json` is **empty**; `dotnet build
      Verbara.Sdk.slnx -c Release` -> **0 Warning(s), 0 Error(s)**, exit `0` captured before any test
      ran; the CI unit filter over the solution -> **Passed: 3613, Failed: 0** across **30**
      assemblies; `tools/audit-test-asserts.sh` -> **Violations: 0** over 446 files.

## Phase C — integration

- [x] C1 `dotnet build Verbara.Sdk.slnx -c Release`: 0 warnings, 0 errors. Capture the exit code
      separately from any test run — `dotnet test --no-build` after a failed build reruns the previous
      binary and reports the previous verdict.

      **`Build succeeded. 0 Warning(s), 0 Error(s)`**, exit 0, captured on its own line before any test
      ran.
- [x] C2 Unit lane green under the CI filter, `Verbara.Sdk.Governance.Tests` included, and
      `tools/audit-test-asserts.sh` at zero. `sync-fence-baseline.json` and every `PublicAPI.*.txt`
      unchanged — verify with `git diff --stat`, do not assert it.

      **Failed: 0, Passed: 3613** across 30 assemblies under the CI filter — 3609 before this change,
      so the four new cases and nothing else. `Verbara.Sdk.Governance.Tests` 129/129 inside that lane.
      `tools/audit-test-asserts.sh`: 446 files, **0 violations**.
      `git diff --stat -- '*PublicAPI*' sync-fence-baseline.json` is **empty** — verified, not asserted.
      `Tests/Verbara.Sdk.Agi.Tests/Server/FastAgiServerTests.cs` stays at its grandfathered **2**
      barriers; the new cases drive A3's fake clock and order on an accept.
- [x] C3 Coverage: the two-sided band and the **patch** floor. Run the coverage collection **after
      committing**, because `diff-cover` compares committed state against `origin/main` and a run over
      an uncommitted tree silently measures the previous commit instead. Read the changed-line count and
      check it against the size of the diff before believing the percentage.

      **Measured after committing, which is the whole point of this task.** A sibling change ran this
      gate over an uncommitted tree, and because `diff-cover` compares *committed* state against
      `origin/main` it silently measured the previous commit and reported `100% (6/6 changed lines)` on
      a five-file diff. The `6` was the tell. Here the changed-line count is **25**, which matches the
      size of the diff.

      | gate | result |
      |---|---|
      | patch coverage | **88.0%** — 22/25 changed executable lines, floor 85.0% |
      | line coverage | **84.12%**, band `[83.0, 86.0]` |
      | branch coverage | **68.48%**, blocking floor 64.0% |
      | lines measured | 13420, minimum 12315 |
      | exclusion markers | **0**, baseline 0 |

      **The three uncovered lines are all the stop path, and one of them is an honest flicker rather
      than a gap.** `:145` and `:152` are the `catch (ObjectDisposedException)` arm and its `break` —
      the Windows shape of a stop, which a sibling change measured as unreachable on Linux (10 probes:
      7 × `SocketException(OperationAborted)`, 3 × `OperationCanceledException`, 0 × this type).
      `:162` is the `break` inside `catch (SocketException) when (!IsRunning)`, and that arm **is**
      exercised — B5's mutation 2 removed the clause and the stop test caught it. Which of the two stop
      shapes wins is the same 7/3 race, so this line's coverage flickers between runs. Recorded rather
      than chased: covering it deterministically needs a seam that forces the shape, which would pin the
      platform rather than the behaviour.
- [x] C4 `CHANGELOG.md` `[Unreleased]`: a `### Changed` entry — an accept failure that used to end the
      server now logs at Error and the server keeps accepting. Leave the `(#N)` for close-out. Harvest
      into this change's close-out: the fourth `FakeTimeProvider` copy (A3), and the two ARI audio
      accept loops that carry the same defect and additionally need their `StopAsync` ordering corrected
      before A′ can be transplanted.

      **`### Changed`, not `### Fixed`** — the server now *survives* a failure it used to die on, which
      is new behaviour on the normal path. `(#N)` left for close-out.

      The entry states the limit rather than leaving it to be found: the loop does **not** write
      `Faulted`, so a persistent failure still reads `Healthy` through `AgiHealthCheck`. It also records
      the structural reason the backoff is load-bearing beyond rate-limiting — this server starts its
      loop without `Task.Run` (`:117`, where the sibling listener uses `Task.Run` at `:128`), so without
      a yielding await on the failure path a persistent failure spins *inside* `StartAsync` rather than
      behind it. B5 measured that: the mutation removing the wait wedges a test host at 98% CPU instead
      of going red, which in CI reads as a job timeout rather than a failed test.

      **Harvested for close-out:**
      - The fourth hand-written `FakeTimeProvider` (A3). The private sibling repo already takes
        `Microsoft.Extensions.TimeProvider.Testing` through central package management in four test
        projects; this repo has four hand-rolled copies that have diverged. Consolidation is a decision
        about the test substrate, not part of an accept-loop fix.
      - **`Ari/Audio/AudioSocketServer` and `Ari/Audio/WebSocketAudioServer` still carry this defect**,
        and A′ cannot be transplanted to them as-is: both set their running flag *after* awaiting the
        accept loop rather than before aborting it, so `when (!IsRunning)` would never match on the stop
        path and every normal shutdown would log a spurious Error. Correcting that ordering is the
        prerequisite, and it is an observable change to a shipped property.
      - The copied `FakeTimeProvider`'s `<remarks>` still points at `AriOutboundListener.AcceptLoopAsync`
        rather than this loop. Left byte-identical on purpose so the four copies stay diffable and the
        consolidation debt stays auditable.
- [x] C5 `openspec validate --all --strict` green, and CI green on the PR.

      `openspec validate --all --strict` -> **Totals: 12 passed, 0 failed**, exit 0, INFO notes only.
      CI on the PR is recorded at close-out.