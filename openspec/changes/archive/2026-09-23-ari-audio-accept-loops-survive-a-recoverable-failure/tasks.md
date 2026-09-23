# Tasks: ari-audio-accept-loops-survive-a-recoverable-failure

## Phase A — foundation

- [x] A1 Record the head commit. Re-read both targets as they stand and confirm the four facts this
      change rests on, each measured rather than taken from the proposal:
      `IsRunning` is a plain `{ get; private set; }` in both (`AudioSocketServer.cs:51`,
      `WebSocketAudioServer.cs:62`); it is set to `false` as the **penultimate** statement of `StopAsync`
      (`:196`, `:300`), after awaiting the accept loop; `StartAsync` has **no** reentrancy guard in
      either (`:59`, `:70`); and both accept loops put their `try` outside the `while` with no
      `SocketException` arm. If any is false, stop and say so — the ordering work in B1 exists only
      because of them.

      **Result — gate PASSES. All four confirmed, every one at the exact line the task names.**
      Head commit `3282ed221df3bb283954f25dd57bbcce9dde1e73` ("docs(openspec): archive
      ari-failed-connect-and-silent-catches (#293)"), branch `Harol-Reina/ari-audio-accept-loops`.
      Both files under `<repo>/src/Verbara.Sdk.Ari/Audio/`; 207 and 311 lines.

      1. **Plain auto-property.** `AudioSocketServer.cs:51` and `WebSocketAudioServer.cs:62` are each
         verbatim `public bool IsRunning { get; private set; }`. No `Volatile`, no `Interlocked`, no
         backing field — the write at `:196` / `:300` and the read at `:202` / `:306` are unordered
         across threads.
      2. **Cleared penultimate, after the abort and after the await.** `IsRunning = false` is
         `AudioSocketServer.cs:196` and `WebSocketAudioServer.cs:300`; the only statement after it in
         each is the `ServerStopped` log (`:197`, `:301`). In both, it comes after `_listener?.Stop()`
         (`:183`, `:276`), after `await _cts.CancelAsync()` (`:186`, `:279`) and after awaiting the
         accept loop (`:189`, `:285`). This is the inversion of `AriOutboundListener`, whose
         `StopAsync` clears the flag at `:138` *before* `_listener?.Stop()` at `:144`.
      3. **No reentrancy guard.** `StartAsync` at `:59` / `:70` opens straight into
         `CreateLinkedTokenSource`, `new TcpListener(...)` and `_listener.Start()`; the flag is a plain
         `IsRunning = true` at `:64` / `:75`, three statements in. A second call rebinds and overwrites
         `_cts`, `_listener` and `_acceptLoop` with nothing to stop it.
      4. **`try` outside the `while`, no `SocketException` arm.** `AudioSocketServer.cs:74-108` and
         `WebSocketAudioServer.cs:85-119`: one `try` wrapping the whole `while`, catching exactly
         `OperationCanceledException` and `ObjectDisposedException`. Neither method has a
         `SocketException` arm — and each `ObjectDisposedException` comment already says so in prose
         ("The `SocketException` arm reaches no catch in this method: the loop task faults").
- [x] A2 Record the fence budgets before writing a test:
      `Tests/Verbara.Sdk.Ari.Tests/Audio/AudioSocketServerTests.cs` carries **4** grandfathered barriers
      in `sync-fence-baseline.json`; `WebSocketAudioServerTests.cs` carries **none** and is absent from
      that file. Neither number may rise. `Tests/Verbara.Sdk.Ari.Tests/FakeTimeProvider.cs` already
      exists and `Verbara.Sdk.Ari.csproj` already has `InternalsVisibleTo` — unlike the AGI change,
      this one needs no new test infrastructure.

      **Result — both budgets confirmed, infrastructure confirmed present.**
      `<repo>/sync-fence-baseline.json` line 10 reads
      `"Tests/Verbara.Sdk.Ari.Tests/Audio/AudioSocketServerTests.cs": 4`. A grep of the whole file for
      `WebSocketAudioServerTests` returns nothing, so its budget is **0** and the first unmarked barrier
      added to it fails the build. The file's own header states the rule: "Never RAISE a count". The
      other `Verbara.Sdk.Ari.Tests` entries are `Audio/AudioSocketSessionTests.cs` 2,
      `Audio/WebSocketAudioSessionTests.cs` 2 and `Outbound/AriOutboundListenerTests.cs` 1 — none is a
      file this change touches.

      Infrastructure, nothing to add: `Tests/Verbara.Sdk.Ari.Tests/FakeTimeProvider.cs` exists and
      already overrides `CreateTimer`, which is what `Task.Delay(TimeSpan, TimeProvider,
      CancellationToken)` waits through, and publishes every timer on `TimersCreated` so a test can read
      the delay the loop asked for and know it is parked before advancing;
      `src/Verbara.Sdk.Ari/Verbara.Sdk.Ari.csproj:8` already has
      `<InternalsVisibleTo Include="Verbara.Sdk.Ari.Tests" />`. `tools/audit-test-asserts.sh` is present
      and executable. `AudioSocketServerTests.cs` already uses a `CapturingLogger` (`:297`, `:348`)
      whose `Entries` expose `Level`, `EventName` and `ExceptionType` — that is the seam B3/B4 assert
      `AcceptLoopFailed` through, and the one B4's "a clean stop reports no `AcceptLoopFailed`" needs.
- [x] A3 Enumerate every reader of `IsRunning` on both types, inside `src/`, `Tests/` and `Examples/`,
      and say for each whether the semantic change in B1 affects it. This is the blast radius of the
      only part of this change that is observable to a consumer.

      **Result — 12 reader sites in the tree, 2 in `src/` and 10 in `Tests/`, 0 in `Examples/`.**
      Method: `grep -rn --include='*.cs' IsRunning src/ Tests/ Examples/`, then every hit attributed to
      its receiver type; `bin/` and `obj/` hits are generated XML doc files for *other* types
      (`IAriOutboundListener`, `IAgiServer`) and are not readers. **No reader reaches these properties
      through an interface**: `IAudioServer` (`src/Verbara.Sdk/IAriClient.cs:699-712`) declares only
      `OnStreamConnected`, `GetStream`, `ActiveStreams` and `ActiveStreamCount` — it does **not** declare
      `IsRunning`, so `CompositeAudioServer` cannot and does not read it. That confirms the spec's
      residual verbatim: a consumer cannot build a health check without casting to the concrete type.

      **`src/` — 1 reader per server, both the same line, both in `DisposeAsync`.**

      | Site | Code | Affected by B1? |
      |---|---|---|
      | `Audio/AudioSocketServer.cs:202` | `if (IsRunning) await StopAsync();` | **Yes, and for the better — not "indifferent".** |
      | `Audio/WebSocketAudioServer.cs:306` | `if (IsRunning) await StopAsync();` | Same. |

      Single-threaded, the outcome is unchanged on all three paths: never started → `false` → skip;
      started and running → `true` → stop; already stopped → `false` → skip. The change is in the
      fourth path, which the proposal's "which is indifferent" does not cover. **Today**, a
      `DisposeAsync` that lands while a `StopAsync` is mid-flight reads `true` (the flag is still up
      until `:196` / `:300`) and enters a **second concurrent `StopAsync`**, which re-runs
      `Stop()`/`CancelAsync()` and re-walks `_streams`. **After B1** it reads `false` and skips — and
      even if it did not, B1's `Interlocked.Exchange(ref _running, 0) == 0` guard in `StopAsync` turns
      the second entry into an immediate return. Both halves of B1 close the same race from both ends.
      This is an improvement, but it is a real behaviour change on this line and should be described as
      one rather than as a no-op.

      **`Tests/` — 10 sites, none of which fails after B1.**

      | Site | Test | Assert | Affected? |
      |---|---|---|---|
      | `Audio/AudioSocketServerTests.cs:91` | `StartAsync_ShouldSetIsRunning` | `BeFalse` before start | No — fresh server, `_running` is 0. |
      | `:95` | same | `BeTrue` after start | No — `Interlocked.Exchange(ref _running, 1)` sets it. |
      | `:105` | `StopAsync_ShouldClearIsRunning` | `BeTrue` after start | No. |
      | `:108` | same | `BeFalse` after an **awaited** `StopAsync` | No — **but see the warning below.** |
      | `:269` | `StopAsync_ShouldDisposeAllActiveSessions` | `BeFalse` after awaited stop | No. |
      | `:278` | `DisposeAsync_ShouldStopRunningServer` | `BeTrue` after start | No. |
      | `:282` | same | `BeFalse` after awaited `DisposeAsync` | No — dispose reads `true`, calls `StopAsync`, which clears first; still `false` at the end. |
      | `:330` | `HandleConnection_ShouldReportIt_WhenAnObserverOfNewStreamsThrows` | `BeTrue("one connection failing is not the server failing")` | No by B1 — no stop has begun. **Load-bearing for B2:** this is the standing precedent that a per-connection failure leaves `IsRunning` true, which is the shape proposal item 3 extends to an accept failure. B4's accept cases should assert the same way. |
      | `Audio/WebSocketAudioServerTests.cs:189` | `StopAsync_ShouldDisposeSession_WhenClientStillConnected` | `BeFalse` after awaited stop | No. |
      | `:233` | `StopAsync_ShouldReturnWithSessionDisposed_WhenTokenIsCancelledWhileSubscriberBlocks` | `BeFalse` after a **token-cancelled** stop | No — **and B1 removes a latent hazard here.** |

      Two notes the next agent needs:

      - **`:108` is not a regression guard for B1, despite its name.** `StopAsync_ShouldClearIsRunning`
        awaits the stop to completion before reading, so it passes identically whether the flag is
        cleared first or last. It has never distinguished "a stop has begun" from "the stop has
        finished", and it will not start to. B1's own test — the task's "`IsRunning` reads `false` once
        a stop has begun" — must observe the flag from **inside** an in-flight stop, and it is the only
        thing that pins the ordering. Nothing existing does.
      - **`WebSocketAudioServerTests.cs:233` is the one site where the pre-B1 ordering is already
        fragile.** That test cancels the token while a subscriber blocks; `StopAsync` survives only
        because both waits (`:285`, `:291`) are
        `WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing)`, so the
        cancelled waits return rather than throw and execution still reaches `IsRunning = false` at
        `:300`. Remove that `SuppressThrowing` — or add any throwing statement between `:276` and
        `:300` — and a cancelled stop leaves the server reporting `IsRunning == true` forever. B1 makes
        the clear unconditional by putting it first, which removes the dependency entirely. Worth one
        line in the PR body: the reordering is not only about the `catch` filter.

      **`Examples/` — 0 readers.** Seven example programs construct these types
      (`OpenAiRealtimeExample`, `VoiceAiAssemblyAiExample`, `VoiceAiCartesiaExample`,
      `VoiceAiCustomProviderExample`, `VoiceAiExample`, `VoiceAiSpeechmaticsExample`,
      `WebSocketMediaExample`) and not one reads `IsRunning`. Every `IsRunning` string under
      `Examples/` is in a generated `bin/**/*.xml` doc file for `IAriOutboundListener` or `IAgiServer`.

      **Holders that never read the flag** (so unaffected, but they are what "both servers move
      together" means): `src/Verbara.Sdk.Hosting/AriAudioHostedService.cs` (calls `StartAsync` /
      `StopAsync` on both, never reads), `src/Verbara.Sdk.Hosting/ServiceCollectionExtensions.cs`
      (registration), `src/Verbara.Sdk.Ari/Audio/CompositeAudioServer.cs` (aggregates through
      `IAudioServer`, which has no `IsRunning`), `src/Verbara.Sdk.Activities/Activities/ExternalMediaActivity.cs`.
      `AriAudioHostedService.StopAsync` gains for free: a host shutdown racing a `DisposeAsync` currently
      runs teardown twice and after B1 runs it once.

      **Outside the tree**, the blast radius is exactly two shipped getters:
      `src/Verbara.Sdk.Ari/PublicAPI.Shipped.txt:36` and `:51`. Both classes' full shipped surface is
      `:31-39` and `:47-55` — constructor, `DisposeAsync`, `GetStream`, `ActiveStreams`,
      `ActiveStreamCount`, `OnStreamConnected`, `StartAsync`, `StopAsync`, `IsRunning`. No
      `PublicAPI.Unshipped.txt` entry exists for either type, so B2's `internal` additions owe nothing
      to either file.

## Phase B — the running flag first, then the loops

- [x] B1 **The ordering, before any accept-loop work.** In both servers replace the auto-property with
      `private int _running` and `IsRunning => Volatile.Read(ref _running) == 1`.
      `StartAsync` opens with `if (Interlocked.Exchange(ref _running, 1) == 1) return;` and `StopAsync`
      with `if (Interlocked.Exchange(ref _running, 0) == 0) return;`, matching
      `src/Verbara.Sdk.Ari/Outbound/AriOutboundListener.cs`. Delete the old `IsRunning = true/false`
      assignments. Keep `DisposeAsync`'s `if (IsRunning) await StopAsync();` — it still reads correctly.

      This is a behaviour change in its own right and must be tested on its own, before A′ exists:
      a second `StartAsync` binds no second listener and leaves `BoundPort` unchanged; `IsRunning`
      reads `false` once a stop has begun. **Write those tests first and record their verbatim failures.**

      **Result — done in both servers, six tests written red first and now green.**

      **Code.** `<repo>/src/Verbara.Sdk.Ari/Audio/AudioSocketServer.cs` and
      `Audio/WebSocketAudioServer.cs`, identically: a `private int _running` beside `_acceptLoop`;
      `public bool IsRunning => Volatile.Read(ref _running) == 1;` with an XML summary saying it
      reads `false` from the moment a stop *begins*; `if (Interlocked.Exchange(ref _running, 1) == 1)
      return ValueTask.CompletedTask;` as the first statement of `StartAsync` and
      `if (Interlocked.Exchange(ref _running, 0) == 0) return;` as the first of `StopAsync`; both
      `IsRunning = true/false` assignments deleted. Shape taken from
      `src/Verbara.Sdk.Ari/Outbound/AriOutboundListener.cs:102`, `:118`, `:138`. `AcceptLoopAsync`
      was not touched in either file — that is B2's. Net: +24 / +25 lines, no other file in `src/`.

      **`DisposeAsync` verified, not assumed.** `if (IsRunning) await StopAsync();` is unchanged at
      `AudioSocketServer.cs:220` and `WebSocketAudioServer.cs:325`, and the two existing cases that
      read it are green: `DisposeAsync_ShouldStopRunningServer` (true before dispose, false after —
      so dispose still reads `true` and still runs the stop) and
      `DisposeAsync_ShouldNotThrow_WhenNotStarted` (never started → `_running` is 0 → the stop is
      skipped, as before). The new `StopAsync_ShouldTearDownOnce_WhenCalledTwice` cases additionally
      leave the server to `await using`, so the already-stopped read is exercised too.

      **Tests — three per server, all six red before the change.** In
      `<repo>/Tests/Verbara.Sdk.Ari.Tests/Audio/AudioSocketServerTests.cs` and
      `Audio/WebSocketAudioServerTests.cs`, under one new section header. Verbatim failures against
      the pre-B1 tree, from `dotnet test ... --no-build -c Release` (31 tests in the two classes,
      **Passed 25 / Failed 6**):

      | Test (both servers) | Verbatim failure before B1 |
      |---|---|
      | `StopAsync_ShouldReportNotRunning_WhenTheStopHasBegunButHasNotFinished` | `Expected runningWhileStopping to be False because IsRunning describes the server's intent, not the completion of its teardown — a stop that has begun is a stop, and the accept loop's classification of its own shutdown depends on reading it that way, but found True.` |
      | `StopAsync_ShouldTearDownOnce_WhenCalledTwice` | `Expected logger.Entries to contain a single item matching (entry.EventName == "ServerStopped") because a stop that finds the server already stopped returns instead of repeating the teardown, but 2 such items were found.` |
      | `StartAsync_ShouldBindNoSecondListener_WhenTheServerIsAlreadyRunning` | `System.Net.Sockets.SocketException : Address already in use` — raised from `TcpListener.Start()` at `AudioSocketServer.cs:63` / `WebSocketAudioServer.cs:74` on the pre-B1 line numbers |

      **How the ordering case observes a stop in flight, without a clock.** A connection handler is
      parked inside the `OnStreamConnected` emission. Both servers register the session *before*
      they publish it, so the session is still in the table and the parked handler cannot dispose it
      first; `StopAsync` therefore cannot finish — the WebSocket server waits on its tracked
      handler, the AudioSocket server awaits that session's `DisposeAsync`, which awaits a read pump
      parked on a socket read on another thread. `StopAsync` runs synchronously as far as its first
      `await`, so the moment it hands back its `ValueTask` the listener is already down and the stop
      has unambiguously begun. Both reads are taken there, before anything is released, and the test
      asserts the premise (`stop.IsCompleted` was false) alongside the flag so a stop that had
      already finished cannot pass for the old reason. No `Task.Delay`, no `Thread.Sleep`: barriers
      stay at **4** in `AudioSocketServerTests.cs` and **0** in `WebSocketAudioServerTests.cs`,
      counted directly in the files as well as by the Governance guard.

      **A second finding, from running the double-start case.** Pre-B1, a second `StartAsync` does
      not merely leak — it leaves the server *unstoppable*. `TcpListener.Start()` throws after
      `_listener` and `_cts` have already been replaced, so `StopAsync` cancels the new source and
      then awaits the **first** accept loop, which is still parked on the first listener under a
      token nothing holds. Both servers hang there forever. Measured: both double-start tests sat
      out their 10 s bounded teardown in the red run. The tests therefore bound their own dispose and
      swallow that timeout, so a regression fails on its assertion instead of hanging the lane. This
      is worth a line in the PR body; the proposal only calls it a leak.

      **Infrastructure added to the tests.** `WebSocketAudioServerTests.cs` gained a `CapturingLogger`
      / `LogEntry` pair mirroring the one `AudioSocketServerTests.cs` already had, and
      `StartServerAsync` gained an optional `ILogger<WebSocketAudioServer>` parameter. B3/B4 need
      that seam for `AcceptLoopFailed` anyway.

      **Verification after the change.** `dotnet build Verbara.Sdk.slnx -c Release` → **0 Warning(s),
      0 Error(s)**, exit code 0 captured on its own line before any test ran. CI unit filter
      (`Category!=Functional&Category!=Integration&Category!=Realtime&Category!=Spike`) →
      **Failed: 0, Passed: 3615** across **30** assemblies, exit 0 — 3609 baseline plus exactly these
      six. `Verbara.Sdk.Governance.Tests` **129/129**. `tools/audit-test-asserts.sh` → 445 files,
      ~2950 `[Fact]`/`[Theory]`, **0 violations**.
      `git diff --stat -- '*PublicAPI*' sync-fence-baseline.json` → **empty**. The six new tests were
      re-run **10 times** in isolation: 10/10 green, 54–58 ms per run.

      **DRIFT against this task's own wording.** B1 asks for "leaves `BoundPort` unchanged", but
      neither target has a `BoundPort` — it exists only on the A′ template
      (`AriOutboundListener.cs:133`) and on the unrelated namesake
      `src/Verbara.Sdk.VoiceAi.AudioSocket/AudioSocketServer.cs:38`. Phase A flagged this. The
      substitute observable is the one it proposed: `ServerStarted` logged **exactly once**, through
      the `CapturingLogger` seam, plus a connection accepted afterwards to show the first listener is
      still the one serving the port.
- [x] B2 **Only then** apply A′ to both `AcceptLoopAsync` methods: `try` inside the `while`; the two
      stop clauses keep their comments and `break`; a filtered `catch (SocketException) when (!IsRunning)`
      breaks; an unfiltered `catch (SocketException ex)` logs `AcceptLoopFailed` at Error; then
      `await Task.Delay(backoff, _timeProvider, ct)` in its own `try` whose
      `catch (OperationCanceledException)` breaks; then the backoff doubles to the cap. Add
      `InitialAcceptBackoff` 100 ms and `MaxAcceptBackoff` 5 s as `internal static readonly`, a
      `TimeProvider` field with the public constructor delegating `TimeProvider.System` and a new
      `internal` one taking it, and an `internal` accept seam. Every addition is `internal`;
      `PublicAPI.*.txt` must not change.
- [x] B3 One `[LoggerMessage(Level = LogLevel.Error, ...)] AcceptLoopFailed(ILogger, Exception)` per
      server. Both logger classes already declare `ConnectionError` at Error for the loss of a single
      connection; match their message style.
- [x] B4 Regression tests for the loops, written first and red, in both test files: an accept that fails
      while running is logged once and the next connection is still accepted; consecutive failures back
      off and a success resets; and — **the case that makes this change different from its two
      predecessors** — a clean stop reports **no** `AcceptLoopFailed`. That last one is what fails when
      A′ is transplanted without B1, so it must be written to fail against the pre-B1 ordering and pass
      after it. Say explicitly whether it does.

      **Result — B2, B3 and B4 done in both servers. Tests written first; the three accept-failure
      cases per server were red before the loops changed, and the ordering guard was measured against
      the pre-B1 ordering rather than argued.**

      **B2 — code.** `<repo>/src/Verbara.Sdk.Ari/Audio/AudioSocketServer.cs` and
      `Audio/WebSocketAudioServer.cs`, identically: `InitialAcceptBackoff` 100 ms and
      `MaxAcceptBackoff` 5 s as `internal static readonly`; a `TimeProvider _timeProvider` field with
      the public constructor delegating `TimeProvider.System` to a new `internal` three-argument one;
      an `internal Func<CancellationToken, ValueTask<TcpClient>>? AcceptOverride` seam read through
      `private ValueTask<TcpClient> AcceptAsync(CancellationToken ct) => AcceptOverride?.Invoke(ct) ??
      _listener!.AcceptTcpClientAsync(ct);`; and `AcceptLoopAsync` restructured to A′ — `try` inside
      the `while`, the two stop clauses keeping their comments and now `break`ing, then
      `catch (SocketException) when (!IsRunning)` **before** the unfiltered
      `catch (SocketException ex)` that logs `AcceptLoopFailed`, then `await Task.Delay(backoff,
      _timeProvider, ct)` in its own `try` whose `catch (OperationCanceledException)` breaks, then
      `backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxAcceptBackoff.Ticks));`. A
      successful accept resets `backoff` and `continue`s past the wait; so does the
      `MaxConcurrentStreams` drop. Every addition is `internal`. +144/-? on each file; no third file
      in `src/` touched.

      **The connection hand-off was NOT copied from the template, deliberately.** The A′ model wraps
      it in `Task.Run(..., CancellationToken.None)`; these two do not and now say why in a comment.
      `AudioSocketServer` keeps `_ = HandleConnectionAsync(client, ct);` and `WebSocketAudioServer`
      keeps `TrackConnection(HandleConnectionAsync(client, ct));` — both start the handler inline on
      the loop's own continuation, which is how they have always run and what the WebSocket server's
      `_connections` tracking depends on. There is no scheduler hand-off here, so the brief's CA2016
      constraint does not bite on either target, and copying the template literally would have
      introduced a `Task.Run` the tree never had.

      **B3 — one event per server**, beside each class's existing `ConnectionError` at Error and in
      its message style: `[AudioSocket] Accept failed — the server stays bound and accepts again after
      a backoff` and `[WebSocketAudio] Accept failed — ...`.

      **B4 — five cases per server, ten in all**, in
      `<repo>/Tests/Verbara.Sdk.Ari.Tests/Audio/AudioSocketServerTests.cs` and
      `Audio/WebSocketAudioServerTests.cs` under one new section header, driven through the
      `AcceptOverride` seam and `Tests/Verbara.Sdk.Ari.Tests/FakeTimeProvider.cs`:

      | Case | What it pins |
      |---|---|
      | `AcceptLoopAsync_ShouldLogErrorAndKeepAccepting_WhenAnAcceptFails` | one `AcceptLoopFailed` at Error with `SocketException`, `IsRunning` still true, no second attempt until the clock moves, then a second attempt |
      | `AcceptLoopAsync_ShouldDoubleTheBackoffToTheCap_WhenAcceptsKeepFailing` | the eight waits are 100/200/400/800/1600/3200 ms then 5 s, 5 s |
      | `AcceptLoopAsync_ShouldResetTheBackoff_WhenAnAcceptSucceedsBetweenFailures` | a real accepted connection between two failures returns the next wait to the initial backoff |
      | `StopAsync_ShouldReportNoAcceptFailure_WhenTheStopAbortsThePendingAccept` | **the guard** — a clean stop logs no `AcceptLoopFailed` |
      | `StopAsync_ShouldReportNoAcceptFailure_WhenARealListenerStopAbortsTheAccept` | the same ending produced by the real listener rather than fabricated |

      **Red before the change, measured.** With only the `AcceptOverride` / `TimeProvider` seam in
      place and `AcceptLoopAsync` still catchless, the two classes ran **Passed 35 / Failed 6** of 41:
      all six accept-failure cases failed as `System.TimeoutException : The operation has timed out.`
      at the `NextTimerAsync` wait — the unfixed loop faults its task on the first `SocketException`
      instead of logging and backing off, so no timer is ever created. After B2+B3: **41/41 in 1 s**.

      **The clean-stop case, and the honest answer the task asked for.** It does **not** fail against
      the unfixed loop — it passes there **vacuously**, because before B3 no `AcceptLoopFailed` event
      exists for a `NotContain` to find. What it discriminates is the pre-B1 **ordering** under the
      post-B2 filter, and that was measured, not reasoned: `StopAsync` was temporarily mutated in both
      servers back to the pre-B1 shape (the `Interlocked.Exchange` guard removed, `Volatile.Write(ref
      _running, 0)` moved to just before the `ServerStopped` log) and the four stop cases run five
      times. `…WhenTheStopAbortsThePendingAccept` failed **5 of 5 runs in both servers**, verbatim:
      `Expected logger.Entries {…} to not have any items matching (entry.EventName ==
      "AcceptLoopFailed") … but found { EventName = "AcceptLoopFailed", ExceptionType =
      "SocketException", Level = LogLevel.Error }`. The mutation was then reverted and the guard
      verified back in place. **Without B1's reordering this change would log an Error on every
      ordinary shutdown, and these two cases are what keeps that out of the tree.**

      **Why the deterministic case is the guard and the real-listener case is only evidence.** Under
      the same mutation, `…WhenARealListenerStopAbortsTheAccept` failed in only **2 of 5** runs for
      `AudioSocketServer` and **0 of 5** for `WebSocketAudioServer`: which of the two shutdown shapes
      the platform raises is not deterministic, exactly as Phase A's 7-of-10 probe said. So the guard
      fabricates the `SocketException(OperationAborted)` from the stop's own cancellation, which is
      the **later** and therefore weaker of the two moments — the real abort comes from
      `_listener.Stop()`, which `StopAsync` runs earlier still. The real-listener case stays because
      it is the evidence that the guarded path is the one production actually takes.

      **Verification.** `dotnet build Verbara.Sdk.slnx -c Release` → **0 Warning(s), 0 Error(s)**,
      exit code 0 captured on its own line before any test ran. CI unit filter
      (`Category!=Functional&Category!=Integration&Category!=Realtime&Category!=Spike`) →
      **Failed: 0, Passed: 3625** across **30** assemblies, exit 0 — the 3615 B1 left plus exactly
      these ten. `Verbara.Sdk.Governance.Tests` **129/129**. `tools/audit-test-asserts.sh` → 445
      files, ~2960 `[Fact]`/`[Theory]`, **0 violations**.
      `git diff --stat -- '*PublicAPI*' sync-fence-baseline.json` → **empty**. Barrier counts
      unchanged and counted in the files as well as by the guard: `AudioSocketServerTests.cs` **4**
      `Task.Delay` / **0** `Thread.Sleep`, `WebSocketAudioServerTests.cs` **0** / **0** — the new
      cases order on the fake clock's `TimersCreated`, on an accept, or on a `TaskCompletionSource`.
      The ten new tests re-run **10 times** in isolation: 10/10 green, 49–54 ms.

      **DRIFT, carried forward for B5 and the PR body.**

      - **The stale comment in the template is still stale, and is now one level more misleading.**
        `src/Verbara.Sdk.Ari/Outbound/AriOutboundListener.cs:188-189` still asserts that "the sibling
        accept loop (AudioSocketServer.AcceptLoopAsync) passes None for the same reason". It does not,
        and after B2 it still does not — neither target uses `Task.Run` at all. Not fixed here because
        it is a third file in `src/` that this change's Impact section does not name; the new comment
        in `AudioSocketServer.AcceptLoopAsync` says so from this side instead.
      - **`InitialAcceptBackoff` / `MaxAcceptBackoff` are now declared three times in the tree**, once
        per server and once on `AriOutboundListener`, with identical values. Three copies is the
        shape the two sibling changes established; whether they should become one shared constant is
        a separate decision and is recorded rather than taken.
      - **The unit lane came in at 3625, above the brief's "3609 or better".** All ten new tests are
        additive; no pre-existing test changed behaviour.

- [x] B5 Mutations, each alone and reverted, verbatim: B1's reordering undone (flag cleared last again);
      the filtered stop clause removed; the backoff `await` removed; the backoff reset removed; the
      filter changed to `when (ct.IsCancellationRequested)`; and the `Interlocked` guard removed from
      `StartAsync`. Record which no test catches. The token-filter mutation is expected to survive — the
      two sibling changes both measured it as not separable — so predict it, then confirm or refute it.

      **Result — five of six mutations are caught; the sixth survives, as predicted.** Each mutation was
      applied to **both** servers, alone, from a pristine copy; built with its **own exit code captured
      before any test ran**; run under the CI unit filter over the whole solution; then reverted by
      restoring the pristine copy, with the md5 of both files checked back to
      `f6ba2f432378a958b6e37fa5fbbe1302` / `a2a019b60bdb6a8971ce10b31aed9b29` before the next one.
      Every one of the six built **0 Warning(s), 0 Error(s), exit 0**, so no verdict below is a stale
      binary's.

      **Baseline before the first mutation:** build exit 0, 0 Warning(s) / 0 Error(s); unit lane
      `Failed: 0, Passed: 3625` across 30 assemblies, exit 0.

      | # | Mutation (both servers) | Build | Verdict | Tests that caught it |
      |---|---|---|---|---|
      | 1 | B1's reordering undone — `Interlocked.Exchange` guard dropped from `StopAsync`, `Volatile.Write(ref _running, 0)` moved back to just before the `ServerStopped` log | 0/0, exit 0 | **CAUGHT** | 6: `Failed: 6, Passed: 3619` |
      | 2 | `catch (SocketException) when (!IsRunning)` arm deleted | 0/0, exit 0 | **CAUGHT** | 3: `Failed: 3, Passed: 3622` |
      | 3 | the backoff `try { await Task.Delay(backoff, _timeProvider, ct); } catch (OperationCanceledException) { break; }` deleted | 0/0, exit 0 | **CAUGHT** | 4 failed + **2 hung** |
      | 4 | `backoff = InitialAcceptBackoff;` after a successful accept deleted | 0/0, exit 0 | **CAUGHT** | 2: `Failed: 2, Passed: 3623` |
      | 5 | filter changed to `when (ct.IsCancellationRequested)` | 0/0, exit 0 | **SURVIVES** | none — `Failed: 0, Passed: 3625` |
      | 6 | the `StartAsync` guard replaced by `Volatile.Write(ref _running, 1);` | 0/0, exit 0 | **CAUGHT** | 2: `Failed: 2, Passed: 3623` |

      **1 — the headline mutation. CAUGHT, and by the case written for it.** This is the shape a literal
      A′ transplant would have shipped, and it does not pass for the wrong reason: six tests fail, three
      per server. The one that matters, verbatim (`AudioSocketServerTests`, its `WebSocketAudioServerTests`
      twin identical but for the type name):

      > `Expected logger.Entries { … EventName = "ServerStarted" …, { EventName = "AcceptLoopFailed",
      > ExceptionType = "SocketException", Level = LogLevel.Error }, … EventName = "ServerStopped" … } to
      > not have any items matching (entry.EventName == "AcceptLoopFailed") because an accept aborted by
      > the stop is the stop, not a failure — reporting it would put an Error in the log on every ordinary
      > shutdown and make the report worthless exactly when a real failure needs to stand out, but found
      > { { EventName = "AcceptLoopFailed", ExceptionType = "SocketException", Level = LogLevel.Error } }.`

      The other four, both servers each:
      `StopAsync_ShouldReportNotRunning_WhenTheStopHasBegunButHasNotFinished` —
      > `Expected runningWhileStopping to be False because IsRunning describes the server's intent, not
      > the completion of its teardown — a stop that has begun is a stop, and the accept loop's
      > classification of its own shutdown depends on reading it that way, but found True.`

      and `StopAsync_ShouldTearDownOnce_WhenCalledTwice` —
      > `Expected logger.Entries to contain a single item matching (entry.EventName == "ServerStopped")
      > because a stop that finds the server already stopped returns instead of repeating the teardown,
      > but 2 such items were found.`

      `StopAsync_ShouldReportNoAcceptFailure_WhenARealListenerStopAbortsTheAccept` did **not** fire on this
      run in either server — consistent with B4's finding that which shutdown shape the platform raises is
      not deterministic, and with why the fabricated case and not that one is the guard.

      **2 — the filtered stop clause removed. CAUGHT, 3 tests.**
      `StopAsync_ShouldReportNoAcceptFailure_WhenTheStopAbortsThePendingAccept` fails in **both** servers
      with the same `not have any items matching (entry.EventName == "AcceptLoopFailed")` message as
      above, and `…WhenARealListenerStopAbortsTheAccept` fails in `WebSocketAudioServerTests` only, verbatim:

      > `… to not have any items matching (entry.EventName == "AcceptLoopFailed") because a real stop
      > aborting a real pending accept is the stop, not a failure, but found { { EventName =
      > "AcceptLoopFailed", ExceptionType = "SocketException", Level = LogLevel.Error } }.`

      That the real-listener case fired for one server and not the other, on the same mutation in the same
      run, is the non-determinism B4 measured showing itself again, and is why it is evidence rather than
      the guard.

      **3 — the backoff `await` removed. CAUGHT, but the lane does not finish.** Four tests fail
      verbatim with `System.TimeoutException : The operation has timed out.` — both servers'
      `AcceptLoopAsync_ShouldLogErrorAndKeepAccepting_WhenAnAcceptFails` and
      `AcceptLoopAsync_ShouldResetTheBackoff_WhenAnAcceptSucceedsBetweenFailures`, each at its
      `NextTimerAsync` wait, because a loop with no `Task.Delay` creates no timer on the fake clock.
      Both servers' `AcceptLoopAsync_ShouldDoubleTheBackoffToTheCap_WhenAcceptsKeepFailing` **hang
      instead of failing**: that case's `AcceptOverride` throws synchronously on every attempt, so with
      the `await` gone the loop has no suspension point at all and spins on the thread that called
      `StartAsync` — and `AcceptLoopAsync` is started synchronously from `StartAsync`, so
      `await server.StartAsync()` never returns. The whole-solution run was killed at the 300 s cap
      (`TEST_EXIT=124`, 3156 tests reported, `Verbara.Sdk.Ari.Tests.dll` never summarised), so the
      per-test verdicts above were taken from five separate `--filter` runs under `timeout 60`; the
      `…ShouldDoubleTheBackoffToTheCap` run exited 124 with no result line of any kind. The two stop
      cases pass under this mutation, correctly — the backoff is not on the stop path. **Recorded as a
      finding, not fixed here:** this mutation is caught but its shape in CI is a job that hangs to its
      timeout rather than a red test, and B4's territory is closed, so no test is added for it.

      **4 — the backoff reset removed. CAUGHT, 2 tests**, one per server,
      `AcceptLoopAsync_ShouldResetTheBackoff_WhenAnAcceptSucceedsBetweenFailures`, verbatim and identical
      in both:

      > `Expected 100ms because a successful accept starts the run over, so the next failure waits the
      > initial backoff again rather than the doubled one, but found 200ms.`

      **5 — the token filter. Predicted to survive; it survives.** Prediction recorded before the run:
      it survives, because the one deterministic case that exercises this arm drives the abort from the
      stop's own cancellation registration, so `ct.IsCancellationRequested` and `!IsRunning` are both
      true when the filter is evaluated and the two predicates agree. Measured: the full lane is green —
      `Failed: 0, Passed: 3625`, exit 0, the baseline number exactly — and the two Audio classes re-run
      **5 times in isolation were 5/5 green, `Failed: 0, Passed: 122`** each time. **CONFIRMED, not
      refuted.** The prediction's reasoning is worth one correction, though: the two predicates are
      **not** equivalent in the code, only in what the suite can reach. `StopAsync` calls
      `_listener?.Stop()` **before** `await _cts.CancelAsync()`, so a real accept aborted by that
      `Stop()` can arrive with `_running` already 0 but the token not yet cancelled — a window in which
      the mutant logs a spurious Error and the shipped filter does not. `…WhenARealListenerStopAbortsTheAccept`
      is the case that could in principle enter that window, and in 6 attempts (1 full lane + 5 repeats)
      it never did. So the residual is the same one the two sibling changes recorded: this mutation is
      **not separable by any test in this suite**, and the reason to prefer `!IsRunning` stays the
      argument written in the comment on the arm itself rather than a test.

      **6 — the `StartAsync` guard removed. CAUGHT, 2 tests**, one per server,
      `StartAsync_ShouldBindNoSecondListener_WhenTheServerIsAlreadyRunning`, verbatim and identical in
      both:

      > `System.Net.Sockets.SocketException : Address already in use`

      raised at `TcpListener.Start` inside `AudioSocketServer.StartAsync` on the **second** start —
      i.e. it fails one step earlier than the `ContainSingle(ServerStarted)` assertion the case was
      written around, because on Linux the second bind to the same port is refused outright. Both tests
      report **10 s**: that is the bounded teardown in the case's own `finally`
      (`DisposeAsync().AsTask().WaitAsync(SignalTimeout)`) timing out and swallowing the timeout, which
      means the leak the guard exists to prevent really did materialise — the second start replaced
      `_cts` before it threw, leaving the first accept loop running against a source nothing can cancel
      and `StopAsync` awaiting it forever. Without that bound this mutation would have hung the run like
      mutation 3 instead of failing two tests.

      **Tree restored, proved rather than asserted.** After the last revert: both files' md5 back to
      `f6ba2f432378a958b6e37fa5fbbe1302` and `a2a019b60bdb6a8971ce10b31aed9b29`; `git diff --stat`
      byte-identical to the pre-B5 capture — `4 files changed, 1024 insertions(+), 56 deletions(-)`;
      `git status --short` the same four `M` entries plus the untracked change folder; build
      **0 Warning(s), 0 Error(s), exit 0**; unit lane **`Failed: 0, Passed: 3625`** across **30**
      assemblies, exit 0; `git diff --stat -- '*PublicAPI*' sync-fence-baseline.json` empty.

## Phase C — integration

- [x] C1 `dotnet build Verbara.Sdk.slnx -c Release`: 0 warnings, 0 errors, exit code captured on its own
      line before any test runs.

      **`0 Warning(s), 0 Error(s)`**, exit 0, captured on its own line before any test ran. Measured
      after rebasing onto `main` at `844d257c` (#298), not before.
- [x] C2 Unit lane green under the CI filter with `Verbara.Sdk.Governance.Tests` included,
      `tools/audit-test-asserts.sh` at zero, and `git diff --stat` showing `sync-fence-baseline.json` and
      every `PublicAPI.*.txt` unchanged — verified, not asserted.

      **Failed: 0, Passed: 3629** across 30 assemblies under the CI filter. The base after #298 is
      3613, so the 16 new cases and nothing else. `Verbara.Sdk.Governance.Tests` **129/129** inside
      that lane. `tools/audit-test-asserts.sh`: **0 violations**.

      `git diff origin/main -- '*PublicAPI*' sync-fence-baseline.json` is **empty** — verified, not
      asserted. Both fence budgets held: `AudioSocketServerTests.cs` stays at its grandfathered **4**
      and `WebSocketAudioServerTests.cs` stays **absent** from the baseline, which is a budget of zero
      and the stricter of the two.
- [x] C3 Coverage after committing, never before: `diff-cover` compares committed state against
      `origin/main`, so a run over an uncommitted tree measures the previous commit. Read the
      changed-line count and sanity-check it against the size of the diff before believing the percentage.

      **Measured after committing.** Changed-line count **60**, which matches the size of the diff —
      the sanity check this task exists for. A sibling change ran this gate over an uncommitted tree
      and got `100% (6/6)` on a five-file diff, because `diff-cover` compares *committed* state.

      **The first measurement passed by one point and that was not good enough.** 86.0% against a
      floor of 85.0%, with 8 lines missing. Four of them were the same hole found in
      `AriOutboundListener` and `FastAgiServer` before it: **the public two-argument constructor**
      (`AudioSocketServer.cs:70`, `WebSocketAudioServer.cs:81`) — the one a consumer resolves from DI —
      was exercised by **nothing**, because every test in both files reaches past it for the internal
      overload that takes a clock. That is a real gap, not a coverage artefact, and the thin margin is
      what surfaced it.

      Closed with one test per server, `PublicConstructor_ShouldBindAndAccept_WhenGivenOnlyOptionsAndALogger`,
      modelled on the one #291 added for the listener. Each builds the server the way a consumer does
      and asserts a real handshake, because completing one is the whole of what the delegated
      construction has to produce.

      | gate | before | after |
      |---|---|---|
      | patch coverage | 86.0% (52/60) | **93.0% (56/60)**, floor 85.0% |
      | line coverage | 84.26% | **84.32%**, band `[83.0, 86.0]` |
      | branch coverage | 68.62% | **68.62%**, floor 64.0% |
      | exclusion markers | 0 | **0**, baseline 0 |
      | unit lane | 3629 | **3631 passed, 0 failed** |

      **The four still uncovered are the `catch (ObjectDisposedException)` arms and their `break`s**
      (`:144`/`:151` and `:154`/`:161`) — the Windows shape of a stop. A sibling change measured it
      with a 10-run probe on this host: 7 × `SocketException(OperationAborted)`, 3 ×
      `OperationCanceledException`, **0 × this type**. Reaching them on Linux means calling `Dispose()`
      by hand, which proves nothing about the path the catch exists for. Left uncovered deliberately.
- [x] C4 `CHANGELOG.md` `[Unreleased]`: a `### Changed` entry covering **both** observables — the accept
      loops surviving a recoverable failure, and `IsRunning` now reading `false` from the moment a stop
      begins on two shipped public types. Leave `(#N)` for close-out.

      **`### Changed`, covering both observables**, because this change has two and citing only the
      accept loops would leave the larger half undocumented. `(#N)` left for close-out.

      The entry leads with the loops and then spends most of its length on `IsRunning`, which is the
      part that reaches a consumer: it now reads `false` from the moment a stop **begins** rather than
      from the moment it completes, on two types in `PublicAPI.Shipped.txt`. It also records the three
      things that came with the reordering and are easy to miss — the flag was a plain auto-property
      written and read across threads with no barrier; the `Interlocked` guard also makes a second
      `StartAsync` a no-op where it used to bind a second listener and leak three fields; and a throw
      anywhere in the old teardown would have skipped the clear and left the server reporting itself
      running for good.

      It states plainly what an operator does **not** get: neither loop writes a terminal state for a
      failure it survives, neither server has a health check, and `IAudioServer` does not expose
      `IsRunning` — so a persistent accept failure is visible only in the log.

      **Harvested for close-out:** the two `catch (ObjectDisposedException)` arms left uncovered by
      C3 as the Windows shape of a stop; and the open question of whether these two servers should
      carry a health check at all, which is what would make a persistent failure visible without
      reading logs.
- [x] C5 `openspec validate --all --strict` green, and CI green on the PR.

      `openspec validate --all --strict` -> **Totals: 13 passed, 0 failed**, exit 0, INFO notes only.
      The count is 13 rather than 12 because the AGI change is still an open change on `main`: its
      close-out (#299) is in flight and not yet merged. Both changes write deltas to the same
      `server-accept-lifecycle` capability under deliberately distinct requirement names — the AGI one
      describes what a loop does with a failure it can survive, this one describes when the flag that
      classifies it is cleared — so the deltas merge rather than collide. **Archive #299 before this
      change reaches its own close-out**, or the second archive inherits a living spec that does not
      yet exist.

      CI on the PR is recorded at close-out.