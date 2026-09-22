# Tasks: ari-failed-connect-and-silent-catches

## 1. Start from `main`, not from the old branch

- [x] 1.1 Branch from current `main`. Do **not** rebase, cherry-pick or resurrect `fix/codeql-src`:
      it is eleven commits behind `main`, and its versions of `Audio/WebSocketAudioServer.cs` and
      `Client/AriClient.cs` predate the per-connection session cleanup (#256) and the reconnect
      rejection (#258) that `main` has since taken by another route. Re-derive every edit from the
      file as it stands on `main`. Record the head commit this change starts from.

      **Head commit this change starts from: `b86f8906`** (`fix(voiceai): a caller hanging up
      mid-playback is not a synthesis failure (#284)`). Nothing was cherry-picked or resurrected from
      `fix/codeql-src`; every edit is re-derived from the file as it stands on `main`. The branch
      carried no commits of its own, so the rebase onto `origin/main` was a fast-forward.

      State of the tree at that commit, recorded because task 7.3 is keyed to it: **55 ADR files** in
      `docs/decisions/`, and the `**N ADRs**` figure `README.md` publishes also reads 55, so
      `StatusBlockCoherenceTests.ThePublishedAdrCount_ShouldMatchTheDecisionsOnDisk` is satisfied
      before this change touches anything. **ADR-0056 is a genuine gap** — 0055, 0057 and 0058 all
      exist on disk — so 7.1 fills a hole rather than renumbering anything, and the guard counts
      files rather than a contiguous sequence.
- [x] 1.2 Re-read the open alerts live before editing, and key every task below to **rule + file +
      member** — never to an alert number or a line number, both of which a merge renumbers. Confirm
      the set is still 18 `cs/empty-catch-block`, 2 `cs/missed-using-statement` and 2
      `cs/nested-if-statements`, all under `src/Verbara.Sdk.Ari`, and note any drift from that.

      **Re-read live against the API rather than a cached list, and the set is unchanged: 22 open
      alerts, zero drift** — 18 `cs/empty-catch-block`, 2 `cs/missed-using-statement`, 2
      `cs/nested-if-statements`, every one of them under `src/Verbara.Sdk.Ari`. Keyed to rule + file +
      member, with the alert number recorded only as a parenthesis so a renumbering cannot invalidate
      the key:

      | rule | file · member | alerts |
      |---|---|---|
      | `cs/empty-catch-block` | `Client/AriClient.cs` · `EventLoopAsync` | 873 |
      | `cs/empty-catch-block` | `Outbound/AriOutboundListener.cs` · `StopAsync` | 874 |
      | `cs/empty-catch-block` | `Outbound/AriOutboundListener.cs` · `AcceptLoopAsync` | 875, 876, 877 |
      | `cs/empty-catch-block` | `Outbound/AriOutboundListener.cs` · `HandleConnectionAsync` | 878, 879 |
      | `cs/empty-catch-block` | `Outbound/AriOutboundListener.cs` · `ReadPumpAsync` | 880 |
      | `cs/empty-catch-block` | `Audio/AudioSocketServer.cs` · `AcceptLoopAsync` | 863, 864 |
      | `cs/empty-catch-block` | `Audio/AudioSocketServer.cs` · `HandleConnectionAsync` | 865 |
      | `cs/empty-catch-block` | `Audio/AudioSocketSession.cs` · `FillPipeAsync` | 866, 867 |
      | `cs/empty-catch-block` | `Audio/AudioSocketSession.cs` · `ReadPumpAsync` | 868, 869 |
      | `cs/empty-catch-block` | `Audio/WebSocketAudioServer.cs` · `AcceptLoopAsync` | 1259, 1260 |
      | `cs/empty-catch-block` | `Audio/WebSocketAudioServer.cs` · `HandleConnectionAsync` | 1258 |
      | `cs/missed-using-statement` | `Audio/AudioSocketServer.cs` · `HandleConnectionAsync` | 363 |
      | `cs/missed-using-statement` | `Outbound/AriOutboundListener.cs` · `HandleConnectionAsync` | 365 |
      | `cs/nested-if-statements` | `Audio/AudioSocketSession.cs` · `ReadFrameAsync` | 11 |
      | `cs/nested-if-statements` | `Outbound/AriOutboundListener.cs` · `Validate` | 13 |

      Two claims the proposal rests on were re-checked against the file and both hold.
      `AriOutboundListener` has **seven** bare catches, not six — the count #282 corrected — and all
      seven alert. It also carries **five** catches that swallow with a `/* Best effort */` comment,
      and **none of the five alerts**. That pairing, inside one file, is the evidence that stating the
      intent is what the query distinguishes, so §4's remedy is not a guess.

      The split §4.2 and §4.3 assume inside `AcceptLoopAsync` is real and is the reason its three
      alerts are not interchangeable: the `OperationCanceledException` and `ObjectDisposedException`
      catches are the stop path §4.2 comments, while the third is the `SocketException` catch §4.3
      holds open for the owner.

## 2. Reproduce the connect-state defect before fixing it

- [x] 2.1 Write the regression tests first, against the unfixed client, and record their verbatim
      failures. In `AriClientStateTests`, following the file's existing pattern — a `TcpListener` on
      `IPAddress.Loopback` with port 0, a `127.0.0.1` literal in `BaseUrl` (ADR-0044), the
      `RefuseUpgradeAsync` helper and `RecordingLogger`:
      - `ConnectAsync_ShouldLeaveStateFaulted_WhenTheUpgradeIsRefused`, a `[Theory]` over
        `401 Unauthorized` and `503 Service Unavailable`. Asserts `ConnectAsync` throws, `State` is
        `Faulted`, `IsConnected` is false, and `AriHealthCheck` reports `Unhealthy` with a message
        naming `Faulted`.
      - `ConnectAsync_ShouldLeaveStateFaulted_WhenTheDialIsRefused`: bind a listener on port 0, keep
        the port, stop the listener, then connect — a deterministic refusal on loopback, no timing
        window. Asserts the throw and `Faulted`.
      - `ConnectAsync_ShouldLeaveStateDisconnected_WhenTheCallerCancelsTheAttempt`: a listener that
        accepts the connection and never answers the upgrade; the test waits for the accept, then
        cancels the token it handed `ConnectAsync`. Asserts `OperationCanceledException`, `State`
        `Disconnected`, and that nothing was logged at Error.

      **Added** all three in `Tests/Verbara.Sdk.Ari.Tests/Client/AriClientStateTests.cs`, on the
      file's own harness: `TcpListener(IPAddress.Loopback, 0)`, the `127.0.0.1` literal in `BaseUrl`
      (never `localhost` — `Sdk/ADR-0044`), `RefuseUpgradeAsync`, `RecordingLogger` and `WaitLimit`.
      The refused-dial case takes a port by binding on 0 and gives it up by disposing the listener in
      its own `using` block, so the refusal comes from the loopback stack with nothing listening. The
      cancellation case is ordered by a `TaskCompletionSource` the server-side accept completes — the
      test waits on the accept, not on a clock — and the accepted connection is held open by a second
      `TaskCompletionSource` so nothing but the caller's token can end the attempt. No new wall-clock
      barrier was added: the file still scans at zero unmarked fences and its `sync-fence-baseline.json`
      entry stays absent.

      **Red against the unfixed client, five runs out of five** (`Failed: 4, Passed: 7, Skipped: 0`,
      ~4 s per run; the `[Theory]` is two cases). Every failure is the same statement of the defect —
      the attempt ended, and `State` still reads `Connecting`. Verbatim, with only the machine-path
      prefix replaced by `<repo>` (nothing under `openspec/` carries an absolute path):

      ```text
        Failed Verbara.Sdk.Ari.Tests.Client.AriClientStateTests.ConnectAsync_ShouldLeaveStateFaulted_WhenTheUpgradeIsRefused(status: "401 Unauthorized") [8 ms]
        Error Message:
         Expected sut.State to be AriConnectionState.Faulted {value: 6} because an attempt that ended without a connection is over, and nothing dials again, but found AriConnectionState.Connecting {value: 1}.
      Expected health.Description "ARI state: Connecting" to contain "Faulted" because the health message names the terminal state.

        Stack Trace:
           at FluentAssertions.Execution.LateBoundTestFramework.Throw(String message)
         at FluentAssertions.Execution.TestFrameworkProvider.Throw(String message)
         at FluentAssertions.Execution.CollectingAssertionStrategy.ThrowIfAny(IDictionary`2 context)
         at FluentAssertions.Execution.AssertionScope.Dispose()
         at Verbara.Sdk.Ari.Tests.Client.AriClientStateTests.ConnectAsync_ShouldLeaveStateFaulted_WhenTheUpgradeIsRefused(String status) in <repo>/Tests/Verbara.Sdk.Ari.Tests/Client/AriClientStateTests.cs:line 200
         at Verbara.Sdk.Ari.Tests.Client.AriClientStateTests.ConnectAsync_ShouldLeaveStateFaulted_WhenTheUpgradeIsRefused(String status)
         at Verbara.Sdk.Ari.Tests.Client.AriClientStateTests.ConnectAsync_ShouldLeaveStateFaulted_WhenTheUpgradeIsRefused(String status) in <repo>/Tests/Verbara.Sdk.Ari.Tests/Client/AriClientStateTests.cs:line 200
      --- End of stack trace from previous location ---

        Failed Verbara.Sdk.Ari.Tests.Client.AriClientStateTests.ConnectAsync_ShouldLeaveStateFaulted_WhenTheUpgradeIsRefused(status: "503 Service Unavailable") [1 ms]
        Error Message:
         Expected sut.State to be AriConnectionState.Faulted {value: 6} because an attempt that ended without a connection is over, and nothing dials again, but found AriConnectionState.Connecting {value: 1}.
      Expected health.Description "ARI state: Connecting" to contain "Faulted" because the health message names the terminal state.

        Failed Verbara.Sdk.Ari.Tests.Client.AriClientStateTests.ConnectAsync_ShouldLeaveStateFaulted_WhenTheDialIsRefused [29 ms]
        Error Message:
         Expected sut.State to be AriConnectionState.Faulted {value: 6} because a connection the stack refused is an ending, not an attempt in progress, but found AriConnectionState.Connecting {value: 1}.

        Stack Trace:
           at FluentAssertions.Execution.LateBoundTestFramework.Throw(String message)
         at FluentAssertions.Execution.TestFrameworkProvider.Throw(String message)
         at FluentAssertions.Execution.CollectingAssertionStrategy.ThrowIfAny(IDictionary`2 context)
         at FluentAssertions.Execution.AssertionScope.Dispose()
         at Verbara.Sdk.Ari.Tests.Client.AriClientStateTests.ConnectAsync_ShouldLeaveStateFaulted_WhenTheDialIsRefused() in <repo>/Tests/Verbara.Sdk.Ari.Tests/Client/AriClientStateTests.cs:line 231
         at Verbara.Sdk.Ari.Tests.Client.AriClientStateTests.ConnectAsync_ShouldLeaveStateFaulted_WhenTheDialIsRefused()
         at Verbara.Sdk.Ari.Tests.Client.AriClientStateTests.ConnectAsync_ShouldLeaveStateFaulted_WhenTheDialIsRefused() in <repo>/Tests/Verbara.Sdk.Ari.Tests/Client/AriClientStateTests.cs:line 231
      --- End of stack trace from previous location ---

        Failed Verbara.Sdk.Ari.Tests.Client.AriClientStateTests.ConnectAsync_ShouldLeaveStateDisconnected_WhenTheCallerCancelsTheAttempt [4 ms]
        Error Message:
         Expected sut.State to be AriConnectionState.Disconnected {value: 5} because an ending the caller asked for is not a fault, but found AriConnectionState.Connecting {value: 1}.

        Stack Trace:
           at FluentAssertions.Execution.LateBoundTestFramework.Throw(String message)
         at FluentAssertions.Execution.TestFrameworkProvider.Throw(String message)
         at FluentAssertions.Execution.CollectingAssertionStrategy.ThrowIfAny(IDictionary`2 context)
         at FluentAssertions.Execution.AssertionScope.Dispose()
         at Verbara.Sdk.Ari.Tests.Client.AriClientStateTests.ConnectAsync_ShouldLeaveStateDisconnected_WhenTheCallerCancelsTheAttempt() in <repo>/Tests/Verbara.Sdk.Ari.Tests/Client/AriClientStateTests.cs:line 278
         at Verbara.Sdk.Ari.Tests.Client.AriClientStateTests.ConnectAsync_ShouldLeaveStateDisconnected_WhenTheCallerCancelsTheAttempt()
         at Verbara.Sdk.Ari.Tests.Client.AriClientStateTests.ConnectAsync_ShouldLeaveStateDisconnected_WhenTheCallerCancelsTheAttempt() in <repo>/Tests/Verbara.Sdk.Ari.Tests/Client/AriClientStateTests.cs:line 283
         at Verbara.Sdk.Ari.Tests.Client.AriClientStateTests.ConnectAsync_ShouldLeaveStateDisconnected_WhenTheCallerCancelsTheAttempt() in <repo>/Tests/Verbara.Sdk.Ari.Tests/Client/AriClientStateTests.cs:line 283
      --- End of stack trace from previous location ---

      Failed!  - Failed:     4, Passed:     7, Skipped:     0, Total:    11, Duration: 4 s - Verbara.Sdk.Ari.Tests.dll (net10.0)
      ```

      Only the `State` assertions (and, for the refused upgrade, the health message that reads it)
      fail. Every other assertion in the three tests is already green before the fix: the throw, its
      type, `IsConnected` false, `HealthStatus.Unhealthy`, and nothing logged at Error. That is the
      defect isolated to exactly one observable, which is what the proposal's table claims.

      **The task's prescribed `OperationCanceledException` is the right assertion** — the suspected
      divergence does not occur. Measured with a throwaway probe on the cancellation test (added,
      run, reverted): the exception `ClientWebSocket.ConnectAsync` surfaces when the caller's token is
      cancelled mid-upgrade is `System.Threading.Tasks.TaskCanceledException`, which derives from
      `OperationCanceledException`, so the assertion holds on the real type without being written
      against the derived one. This is a fact about today's runtime and not something 3.1 may rely on:
      the classification still reads `cancellationToken.IsCancellationRequested`, never the exception.
- [x] 2.2 Add the controls, which must pass before the fix and after it:
      - `ConnectAsync_ShouldLeaveStateConnected_WhenTheUpgradeSucceeds` — a listener answering `101`;
        `State` is `Connected` and `EventLoop` is running. A fix that wrote a terminal state on the
        success path fails here.
      - `State_ShouldBeInitial_WhenNewClientCreated` and `IsConnected_ShouldBeFalse_WhenNotConnected`
        already exist; confirm they stay green.

      **Added** `ConnectAsync_ShouldLeaveStateConnected_WhenTheUpgradeSucceeds`, which answers the
      upgrade with `101` through `WebSocketAudioServer.SendUpgradeResponseAsync` and then **holds the
      accepted socket open** on a `TaskCompletionSource` for the length of the assertions, so the
      events loop stays in its receive instead of racing them into the reconnect backoff. It asserts
      `Connected`, `IsConnected` true, `EventLoop` not null and `EventLoop.IsCompleted` false — the
      last two are what "running" means, and together they are the assertion a fix that wrote a
      terminal state on the success path would fail. The teardown releases the server and awaits it
      **before** disposing the client, which is deliberate: disposing a still-`Connected` client whose
      peer is still reading would take `DisconnectAsync` into `CloseAsync`, and a close handshake no
      raw `TcpListener` will ever answer has no bound on it.

      **Green before the fix, five runs out of five**, together with the two pre-existing controls:
      `ConnectAsync_ShouldLeaveStateConnected_WhenTheUpgradeSucceeds [3 ms]`,
      `State_ShouldBeInitial_WhenNewClientCreated [< 1 ms]` and
      `IsConnected_ShouldBeFalse_WhenNotConnected [< 1 ms]` all pass in every run above.
- [x] 2.3 Pin that a failed first connect starts nothing: `ConnectAsync_ShouldNotDialAgain_WhenTheFirstAttemptFailed`,
      with `AutoReconnect` left at its default `true` and a short `ReconnectInitialDelay`. After the
      refused dial, no further connection arrives within a bounded window, and `EventLoop` is null.
      This must pass before the fix too — it pins existing behaviour the fix must not change.

      **Added**, with `AutoReconnect` left at its default `true` and both reconnect delays at 20 ms,
      so a client that did start a reconnect loop would be back roughly a hundred times inside the
      window. Two choices make it non-vacuous rather than a test of a dead port: the listener **stays
      up** for the whole window, so a second dial would be accepted and observed; and the refusal is
      `503 Service Unavailable`, which `ReconnectLoopAsync` retries — a `401` would be refused by the
      loop's own filter and would prove nothing about whether the loop ran. The bounded "nothing
      arrived" window is `AcceptTcpClientAsync` under a two-second `CancellationTokenSource` expecting
      `OperationCanceledException`, the shape
      `DisposeAsync_ShouldStopReconnecting_WhenDisposedWhileReconnecting` already uses; a CTS timeout
      is not a wall-clock fence. `EventLoop` is asserted null in the same test.

      **Green before the fix, five runs out of five** (`ConnectAsync_ShouldNotDialAgain_WhenTheFirstAttemptFailed [2 s]`
      — the two seconds are the window itself, spent proving the absence).
- [x] 2.4 Pin that the terminal state is not a gate:
      `ConnectAsync_ShouldConnect_WhenRetriedAfterAFailedAttempt`. One listener refuses the first dial
      and answers `101` on the second; the same client instance connects and reaches `Connected`.

      **Added**: one listener, one server task, two dials — the first answered `503 Service
      Unavailable`, the second answered `101` and then held open. The same `AriClient` instance is
      dialled twice and reaches `Connected`. Teardown is the same release-then-dispose order as 2.2.

      **Green before the fix, five runs out of five** (`[2 ms]`), which is the expected state and not
      an accident: `ConnectAsync` writes `Connecting` unconditionally on its first line and nothing in
      the type reads `_state` to decide anything, so a second attempt on the same instance behaves
      exactly as the first. That is precisely why this test is worth committing — it is green now, and
      it stays green only as long as 3.1 adds no gate. It is the committed test for the last mutation
      in 3.4 ("a gate added that returns early from `ConnectAsync` when `State` is `Faulted`"), which
      has nothing else to fail against.
- [x] 2.5 Leave `AriClientExtendedTests.ConnectAsync_ShouldThrow_WhenServerUnreachable` alone and say
      why in this task: it dials a blackhole address under a 500 ms token, so whether the attempt ends
      by the caller's token or by a transport error is a race. Asserting a state there would be flaky.

      **Left untouched, and the reason is the fix's own discriminator turned on this test:** it dials
      `http://192.0.2.1:1` (TEST-NET-1, a blackhole) under a 500 ms `CancellationTokenSource`, so the
      attempt ends either by the caller's token or by a transport error depending on which the network
      reaches first — and those are exactly the two endings 3.1 classifies differently, so any state
      assertion added here would be a coin flip between `Disconnected` and `Faulted`. Its existing
      assertion is `ThrowAsync<Exception>()`, which is true of both endings and stays true after the
      fix; the three tests added in 2.1 cover each ending separately, on a loopback listener, with no
      timing window.

## 3. Fix the connect outcome

- [x] 3.1 In `AriClient.ConnectAsync`, wrap only `await _webSocket.ConnectAsync(uri, cancellationToken)`
      in a `try` that sets a local flag immediately after the await, with a `finally` that — when the
      flag is false — writes `Disconnected` if `cancellationToken.IsCancellationRequested` and
      `Faulted` otherwise. Catch nothing: the exception, its type and its stack must reach the caller
      unchanged, and a `catch (Exception)` here would open a new `cs/catch-of-all-exceptions` alert in
      the change that exists to close alerts.

      **Done as prescribed** in `src/Verbara.Sdk.Ari/Client/AriClient.cs`: the only statements inside
      the `try` are the dial and the flag, and the `finally` writes a state and nothing else. Nothing
      is caught, so the exception, its type and its stack reach the caller exactly as before and no
      `cs/catch-of-all-exceptions` alert is created by the change that exists to close alerts.
      **Amended after 3.4.** As first written, this task wrapped *only* the dial and left
      `SetState(AriConnectionState.Connected)` immediately after the block — the literal reading of
      "wrap only that await". 3.4 then measured that this shape makes the "terminal state written on
      the success path too" mutation **invisible to every committed test**, because a `finally` runs
      before the statement that follows its block. The owner chose to move the success write one scope
      inward rather than strike the delta spec's coverage claim. What ships is below; the shape this
      task originally prescribed is recorded in 3.4 together with the measurement that retired it.

      ```csharp
      var connected = false;
      try
      {
          await _webSocket.ConnectAsync(uri, cancellationToken);
          connected = true;

          // the placement comment: written inside the try so the finally is last on every path
          SetState(AriConnectionState.Connected);
      }
      finally
      {
          if (!connected)
          {
              // the classification comment 3.2 records
              SetState(cancellationToken.IsCancellationRequested
                  ? AriConnectionState.Disconnected
                  : AriConnectionState.Faulted);
          }
      }
      ```

      The `try` still guards only the dial: the two statements that joined it neither throw nor are
      awaited, and nothing is caught. The edit under `src/` is **33 lines added and 3 removed**, all
      inside `ConnectAsync` — counted from the hunk header `@@ -141,3 +141,33 @@` against
      `origin/main`, not estimated. (An earlier revision of this line said 31 and 2, taken before the
      success write moved.)

      **Green.** `dotnet build Verbara.Sdk.slnx -c Release` → `0 Warning(s)`, `0 Error(s)`. The
      `AriClientStateTests` filter → `Total tests: 11, Passed: 11` in 4.9 s: the four cases 2.1 recorded
      red are green, and the seven controls — the success path, the no-redial window, the retry after a
      failed attempt, the two pre-existing state cases and the two reconnect cases — stayed green.
- [x] 3.2 Comment the classification with its reason: the caller's token is the discriminator because a
      cancellation raised inside the transport carries a token the caller never held (ADR-0053 records
      that trap for a bridge's `ConnectAsync`), so neither the exception's type nor its
      `CancellationToken` may decide.

      **Written into the `finally`**, in the file's own comment style (`// …`, and ADRs cited bare as
      `ADR-0053`, matching `AssemblyAiSpeechRecognizer.cs` and `DeepgramSpeechRecognizer.cs`):

      > Which terminal state is decided by who ended the attempt, read from the caller's own token —
      > never from the exception. A cancellation raised inside the transport carries a token the caller
      > never held (ADR-0053 records that trap for a bridge's ConnectAsync), so neither the exception's
      > type nor its own CancellationToken can say whether the caller withdrew. A withdrawal the caller
      > asked for is not a failure, and rests where DisconnectAsync leaves the client; anything else
      > faulted.

      A second comment above the `try` says what the flag is for and why nothing is caught. 2.1's
      measured fact — that on this runtime a caller-cancelled `ClientWebSocket.ConnectAsync` surfaces
      `TaskCanceledException` unwrapped while other endings arrive wrapped in `WebSocketException` — is
      deliberately **not** in the comment: it is a fact about today's runtime, and writing it beside
      the classification would invite the next reader to switch to the exception's type. The comment
      states the contract the code depends on instead, which is the token.
- [x] 3.3 Confirm by reading that nothing in the type gates on `_state`: `IsConnected` and
      `DisposeAsync` compare it only to `Connected`, and `ConnectAsync` writes `Connecting`
      unconditionally on its first line. Record that a failed attempt still leaves its `ClientWebSocket`
      and its linked source to `DisposeAsync` — ownership is deliberately unchanged, which is the same
      reasoning the two standing `cs/dispose-not-called-on-throw` dismissals rest on for the reconnect
      loop's socket.

      **Confirmed by reading every occurrence of `_state` and `State` in the type**, not by trusting
      the figure. `_state` is written only through `SetState` and read only through the `State`
      property; `State` is read in exactly two places inside the type — `IsConnected`, which is
      `State == AriConnectionState.Connected`, and `DisposeAsync`, whose first line is
      `if (IsConnected) await DisconnectAsync();`. Both compare to `Connected` alone, so `Faulted` and
      `Disconnected` are indistinguishable from `Initial` to every branch in the file. `ConnectAsync`
      writes `Connecting` unconditionally on its first line, before it reads anything, so a second
      attempt on an instance whose first attempt failed proceeds exactly as the first did — which is
      what `ConnectAsync_ShouldConnect_WhenRetriedAfterAFailedAttempt` pins and what the last mutation
      in 3.4 breaks. The only other reader of the value is `AriHealthCheck`, outside the type, and it
      maps the state to a message rather than gating on it.

      **Ownership is unchanged.** A failed attempt leaves its `ClientWebSocket` and the linked
      `CancellationTokenSource` assigned to `_webSocket` and `_cts`, and `DisposeAsync` releases both at
      its end (`_webSocket?.Dispose()` and `_cts?.Dispose()`). The `finally` added by 3.1 writes a state
      and disposes nothing — deliberately: disposing there would be a second dispose site for a
      reference that escapes to the field, which is what `IDISP016` refuses, and it would contradict
      from the initial dial the very reasoning the two standing `cs/dispose-not-called-on-throw`
      dismissals rest on for the reconnect loop's per-dial socket.

      **Two line figures in the artifacts have drifted** (both keyed to member here, per 1.2, and
      recorded rather than smoothed): the proposal's `:141`/`:143` for the dial and the `Connected`
      write were right before this edit and are now `:149`/`:168`; `ConnectAsync` is declared at `:125`
      and not `:126`, and `DisposeAsync` is declared at `:434` — `:411` was its `if (IsConnected)` line
      before this edit, now `:436`. `IsConnected` at `:67` and `SetState` at `:76` are unmoved.
- [x] 3.4 Mutations. Each must fail at least one committed test, applied alone and then reverted:
      the `finally` removed; `Faulted` written for both endings; `Disconnected` written for both;
      the state written before the await instead of after; the terminal state written on the success
      path too; and a gate added that returns early from `ConnectAsync` when `State` is `Faulted`.
      Record which test catches which, verbatim.

      Each mutation was applied alone to the fixed file, built (`Tests/Verbara.Sdk.Ari.Tests`, always
      `0 Warning(s), 0 Error(s)` unless noted), run under the `AriClientStateTests` filter, and reverted
      before the next. **Five of the six are caught; the fifth is not, and that is a finding, not a
      gap to paper over.** The failure text below is verbatim from the run; only the FluentAssertions
      `AssertionScope` stack frames are elided, and no path appears in it — nothing in this record
      carries a machine prefix, so 2.1's `<repo>` substitution was not needed again.

      **1. The `finally` removed** — `Passed: 7, Failed: 4, Total: 11`. The `try` and the flag go with
      it: a `try` cannot stand without a `catch` or `finally`, and a flag nothing reads is `CS0219`,
      which is an error here. So this mutation is exactly the pre-fix code, and it reproduces 2.1's red
      run case for case.

      ```text
        Failed ConnectAsync_ShouldLeaveStateFaulted_WhenTheDialIsRefused [30 ms]
         Expected sut.State to be AriConnectionState.Faulted {value: 6} because a connection the stack refused is an ending, not an attempt in progress, but found AriConnectionState.Connecting {value: 1}.
        Failed ConnectAsync_ShouldLeaveStateDisconnected_WhenTheCallerCancelsTheAttempt [4 ms]
         Expected sut.State to be AriConnectionState.Disconnected {value: 5} because an ending the caller asked for is not a fault, but found AriConnectionState.Connecting {value: 1}.
        Failed ConnectAsync_ShouldLeaveStateFaulted_WhenTheUpgradeIsRefused(status: "401 Unauthorized") [11 ms]
        Failed ConnectAsync_ShouldLeaveStateFaulted_WhenTheUpgradeIsRefused(status: "503 Service Unavailable") [1 ms]
         Expected sut.State to be AriConnectionState.Faulted {value: 6} because an attempt that ended without a connection is over, and nothing dials again, but found AriConnectionState.Connecting {value: 1}.
      Expected health.Description "ARI state: Connecting" to contain "Faulted" because the health message names the terminal state.
      ```

      **2. `Faulted` written for both endings** — `Passed: 10, Failed: 1`. Only the withdrawal case can
      see it, which is the point of classifying at all.

      ```text
        Failed ConnectAsync_ShouldLeaveStateDisconnected_WhenTheCallerCancelsTheAttempt [23 ms]
         Expected sut.State to be AriConnectionState.Disconnected {value: 5} because an ending the caller asked for is not a fault, but found AriConnectionState.Faulted {value: 6}.
      ```

      **3. `Disconnected` written for both endings** — `Passed: 8, Failed: 3`. The health assertion
      fails with it, which is the second channel the requirement names.

      ```text
        Failed ConnectAsync_ShouldLeaveStateFaulted_WhenTheDialIsRefused [26 ms]
         Expected sut.State to be AriConnectionState.Faulted {value: 6} because a connection the stack refused is an ending, not an attempt in progress, but found AriConnectionState.Disconnected {value: 5}.
        Failed ConnectAsync_ShouldLeaveStateFaulted_WhenTheUpgradeIsRefused(status: "401 Unauthorized") [3 ms]
        Failed ConnectAsync_ShouldLeaveStateFaulted_WhenTheUpgradeIsRefused(status: "503 Service Unavailable") [6 ms]
         Expected sut.State to be AriConnectionState.Faulted {value: 6} because an attempt that ended without a connection is over, and nothing dials again, but found AriConnectionState.Disconnected {value: 5}.
      Expected health.Description "ARI state: Disconnected" to contain "Faulted" because the health message names the terminal state.
      ```

      **4. The flag written before the await instead of after** — `Passed: 7, Failed: 4`, the same four
      as mutation 1 and with the same text: the `finally` sees a flag that is already true, so it writes
      nothing and the state stays `Connecting`. (This is the reading of "the state written before the
      await" that the prescribed shape supports: the flag is what the dial writes after the await. The
      other reading — moving `SetState(Connected)` above the dial — is **not** separable; it is recorded
      under 3.5 with the other one.)

      ```text
        Failed ConnectAsync_ShouldLeaveStateFaulted_WhenTheDialIsRefused [28 ms]
        Failed ConnectAsync_ShouldLeaveStateDisconnected_WhenTheCallerCancelsTheAttempt [4 ms]
        Failed ConnectAsync_ShouldLeaveStateFaulted_WhenTheUpgradeIsRefused(status: "401 Unauthorized") [7 ms]
        Failed ConnectAsync_ShouldLeaveStateFaulted_WhenTheUpgradeIsRefused(status: "503 Service Unavailable") [1 ms]
      ```

      **5. The terminal state written on the success path too — CAUGHT, after a one-line change the
      owner approved.** `Failed: 2, Passed: 9`, caught by `ConnectAsync_ShouldLeaveStateConnected_WhenTheUpgradeSucceeds`
      and `ConnectAsync_ShouldConnect_WhenRetriedAfterAFailedAttempt`:

      ```text
        Failed ConnectAsync_ShouldLeaveStateConnected_WhenTheUpgradeSucceeds [25 ms]
         Expected sut.State to be AriConnectionState.Connected {value: 2} because the dial succeeded, but found AriConnectionState.Faulted {value: 6}.
      Expected sut.IsConnected to be True, but found False.
        Failed ConnectAsync_ShouldConnect_WhenRetriedAfterAFailedAttempt [3 ms]
         Expected sut.State to be AriConnectionState.Connected {value: 2} because the state a failed attempt leaves is a statement about that attempt, not a gate on the next one, but found AriConnectionState.Faulted {value: 6}.
      Expected sut.IsConnected to be True, but found False.
      ```

      **It was not caught by the shape 3.1 prescribes, and that is why the shape changed.** With
      `SetState(AriConnectionState.Connected)` *after* the `try`/`finally` — "wrap **only** that
      await", as 3.1 words it — this mutation passed every committed test (`Failed: 0, Passed: 11`).
      A `finally` runs BEFORE the statement that follows its block, so the unguarded terminal write
      landed on the success path and was overwritten by `SetState(Connected)` one statement later.
      The state was momentarily `Faulted` inside `ConnectAsync` and no observer existed between the
      two writes, so no test could ever see it. Removing the `if (!connected)` guard alone does not
      even compile — `CS0219: The variable 'connected' is assigned but its value is never used`, an
      error under `TreatWarningsAsErrors` — so the mutation's only building form drops the flag with
      the guard.

      That made the delta spec's Mitigation false where it lists "writing it on the success path"
      among the mutations that "each fail at least one of them". Two repairs were possible: move the
      success write inside the `try`, which makes the claim true, or keep the prescribed shape and
      strike the claim. Both were measured before the choice was put up — the prescribed shape at
      `Failed: 0, Passed: 11` under the mutation, the moved-line shape at `Failed: 0, Passed: 11`
      unmutated and `Failed: 2, Passed: 9` mutated. **The owner chose to move the line**, so the
      spec's claim now holds as written and needed no edit, and mutation coverage is 6 of 6 rather
      than 5 of 6. The deviation from 3.1's literal "wrap only that await" is deliberate and is
      recorded here rather than left for a reader to notice: the success write moved one scope
      inward, the dial is still the only thing the `try` guards, and the behaviour is unchanged.

      **6. A gate that returns early from `ConnectAsync` when `State` is `Faulted`** —
      `Passed: 10, Failed: 1`. Caught by 2.4's test, which exists for this mutation and has nothing else
      to fail against. The 10 s is the `WaitLimit` the teardown spends on a second dial that never
      arrives, not the assertion.

      ```text
        Failed ConnectAsync_ShouldConnect_WhenRetriedAfterAFailedAttempt [10 s]
         Expected sut.State to be AriConnectionState.Connected {value: 2} because the state a failed attempt leaves is a statement about that attempt, not a gate on the next one, but found AriConnectionState.Faulted {value: 6}.
      Expected sut.IsConnected to be True, but found False.
      ```

      **After the last revert** the file is byte-identical to the fix (`diff` against the copy taken
      before the first mutation is empty), `git diff -- src/` shows only the 26/1 hunk of 3.1, the
      solution builds at `0 Warning(s), 0 Error(s)`, and the filter is back to
      `Total tests: 11, Passed: 11`.
- [x] 3.5 Record the one mutation that is **not** separable and why: replacing
      `cancellationToken.IsCancellationRequested` with a test on the exception's type or its
      `CancellationToken`. The current `ClientWebSocket.ConnectAsync` overload offers no way to raise a
      cancellation the caller did not ask for, so no committed test can tell the two apart. Do not
      claim coverage for it.

      **Recorded, not tested, and no coverage is claimed for it.** Every ending a committed test can
      produce through this overload is either the caller's own token or a transport failure, and the
      two agree on both sides of the discriminator: when the caller's token is the cause,
      `IsCancellationRequested` is true *and* the exception is an `OperationCanceledException`; when it
      is not, both are false. A test could only separate them by raising a cancellation the caller did
      not ask for, and `ClientWebSocket.ConnectAsync(Uri, CancellationToken)` gives no seam to do it
      (no handler, no inner token, no injectable transport). The design follows ADR-0053's recorded
      trap rather than a test: a bridge's `ConnectAsync` surfaced a `TaskCanceledException` carrying a
      token the caller never held, and reading the exception there booked a live failure as a
      withdrawal. 2.1's measurement — that today this overload rethrows unwrapped on the caller's token
      and wraps otherwise — is what makes the two indistinguishable here; it is a runtime detail, not a
      contract, and it is the reason to keep reading the token rather than a reason to stop.

      **A second unseparable mutation, found while running 3.4 and recorded here with it:** moving
      `SetState(AriConnectionState.Connected)` from after the `try`/`finally` to before it passes all
      11 committed tests. The position of the *success* write relative to the dial is not pinned by
      anything — only the position of the flag is (mutation 4) — because no test observes the state
      while a dial is in flight. Nothing in this change depends on it, and no test is added for it
      (that is 2's territory, closed), but it belongs in the same family as the mutation above: what
      the committed tests pin is the state an attempt *ends* in, not the order of writes inside the
      attempt.

## 4. Say what each swallowed exception absorbs

The rule for this whole group: a catch that swallows states, inside the block, which ending it is
absorbing — which token, which teardown, which close path. A catch that can be reached while the
component is still meant to be running does **not** get a comment; it gets a log, a fix, or a written
finding for the owner with its alert left open. Silencing is not the goal.

- [x] 4.1 `AriClient.EventLoopAsync` — `cs/empty-catch-block` on its `OperationCanceledException`
      catch. The token is the client's own linked source, cancelled by `DisconnectAsync` and
      `DisposeAsync`; the auto-reconnect block below re-reads the same token, so a cancelled loop does
      not dial. State that in the block.

      **Commented**, in the `/* Best effort — … */` idiom 4.2 established, and the alert closes on the
      comment mechanism alone — nothing was added to the block, so it stays an empty block
      structurally. The block names three cancellers, not two: `_cts` is built by `ConnectAsync` as
      `CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)`, so **the caller's own token
      cancels it too**, alongside `DisconnectAsync` and `DisposeAsync`. That is a completion of the
      task text rather than a contradiction of it, recorded per 1.2.

      The second half of the task text checks out verbatim on the tree: the `try` ends before the
      auto-reconnect block, so swallowing here does not end the method — control falls through to
      `if (_options.AutoReconnect && !ct.IsCancellationRequested)`, which re-reads the same `ct`, and a
      cancelled loop therefore leaves `EventLoopAsync` without dialling. The block says so, because
      that is the only reason swallowing is safe here.

      **`AriClient` has a second already-commented catch and it raises no alert**, which is the same
      pairing 1.2 measured on `AriOutboundListener`: `DisconnectAsync`'s `catch { /* Best effort */ }`
      around the WebSocket close is untouched and is not in this change's 22.
- [x] 4.2 `AriOutboundListener` — `cs/empty-catch-block` in four members:
      - `StopAsync`, the `SocketException` from stopping a listener that is already down;
      - `AcceptLoopAsync`, the `OperationCanceledException` and `ObjectDisposedException` of its own
        stop path (the token cancelled, the listener disposed under a pending accept);
      - `HandleConnectionAsync`, the `OperationCanceledException` of the stop token, and the
        `ObjectDisposedException` of the second `Dispose` in its `finally`;
      - `ReadPumpAsync`, the `OperationCanceledException` of the stop token — note in the block that
        the idle timeout is already taken by the filtered catch above it.
      Match the wording of the five commented catches this file already has, which raise no alert.

      **All six commented**, each naming the ending it absorbs, in the idiom the file already uses —
      the `/* Best effort — … */` marker inside the block where one line carries it, and a `//` block
      above the `try` where the reason needs more room. The five commented catches already in this
      file raise no alert, so this is the idiom the query is *measured* to accept here rather than a
      guess about it (1.2 recorded that pairing: seven bare catches all alert, five commented ones
      alert zero).

      | member | catch | what the block now says |
      |---|---|---|
      | `StopAsync` | `SocketException` | `Stop()` on a listener already down — the platform closed the socket, or a concurrent `DisposeAsync` got there first; "not accepting" is the state the method exists to reach, so there is nothing to report |
      | `AcceptLoopAsync` | `OperationCanceledException` | the stop path: `ct` is `_cts.Token`, cancelled by `StopAsync` and so by `DisposeAsync`, and the pending accept ended with it |
      | `AcceptLoopAsync` | `ObjectDisposedException` | the **Windows** shape of that same stop — `Stop()` disposes the socket under a pending accept — and explicitly **not reached on Linux**, where `Stop()` raises `SocketException(OperationAborted)` and the filtered catch takes it |
      | `HandleConnectionAsync` | `OperationCanceledException` | the stop token; every await above it is the handshake or the read pump under that token, and the `finally` still closes the connection |
      | `HandleConnectionAsync` (`finally`) | `ObjectDisposedException` | the second `Dispose`: the two early-return paths already released the client, so this catch absorbs a double release and nothing else. **Superseded by 5.1**, which deleted this block along with both early-return `Dispose()` calls — there is no second release left to absorb. Alert 879 closes by removal, not by this comment |
      | `ReadPumpAsync` | `OperationCanceledException` | the stop token **and only that** — the idle timeout is the other ending a cancellation can mean here and is already taken by the filtered `when (!ct.IsCancellationRequested)` catch inside the loop, which logs it and breaks |

      **The Windows/Linux note is not a hedge — it was measured on this tree.** A throwaway probe
      (added, run, reverted) parked `AcceptTcpClientAsync(ct)` on a real loopback `TcpListener` and
      called `Stop()` under it. The result on this platform, verbatim:

      ```text
      TYPE=System.Net.Sockets.SocketException
      SocketErrorCode=OperationAborted ErrorCode=125
      MSG=Operation canceled
      ```

      So `ObjectDisposedException` is genuinely unreachable here, and the comment says so in the
      block. Leaving that implied is how the next reader concludes the catch is dead code and deletes
      it; saying "Windows, not dead" is what keeps it.

      **Two of the six are no longer empty blocks at all**, and that is worth recording before 9.2
      reads the alert list: 4.3 restructures `AcceptLoopAsync` so its `OperationCanceledException` and
      `ObjectDisposedException` catches now hold `break;`. A block with a statement in it is outside
      `cs/empty-catch-block`'s reach whatever the comment says, so those two close on structure and
      the comment is there for the reader rather than for the query. The other four close on the
      comment alone, which is the mechanism 1.2 measured.
- [x] 4.3 `AriOutboundListener.AcceptLoopAsync` — its `SocketException` catch is the one the rule in
      this group exists for. It also absorbs an accept failure that is **not** the stop path, and the
      loop then ends while `IsRunning` still reports `true` and the listener serves nothing. Decide
      with evidence: log it at Error and keep the loop's exit, or leave the alert open with a written
      finding. Do not comment it silent.

      **The owner ruled A′ — log at Error, back off, keep accepting — and explicitly rejected A,
      "log it and keep the loop's exit".** The alert closes; nothing is left open here.

      **Why A′ and not A.** This catch absorbs failures that are not the stop: EMFILE/ENFILE, a
      connection aborted in the backlog, ENOBUFS. Under A the loop still ends, and it ends while
      `Interlocked.Exchange(ref _running, 0)` has *not* run — so `IsRunning` keeps reporting `true`
      over a socket still in LISTEN. The kernel goes on completing handshakes nobody ever accepts, so
      Asterisk's outbound connectors hang rather than being refused, and `StartAsync` returns without
      doing anything because `_running` is already 1. That contradicts `IAriOutboundListener`'s own
      doc for the property, verbatim on disk: *"Whether the listener is currently bound and accepting
      connections."* A logs the failure and leaves that contradiction standing; A′ removes it.

      **The discriminator is `IsRunning`, never the token.** `StopAsync` orders its teardown
      `Interlocked.Exchange(ref _running, 0)` → `_listener?.Stop()` → `await _cts.CancelAsync()`, so a
      stop-induced `SocketException` is raised with `_running` already 0 while the token may not be
      cancelled yet. The filtered catch is therefore `catch (SocketException) when (!IsRunning)`, and
      nothing inside any catch writes `_running`.

      **The template is the sibling, not an invention.** `AudioSocketServer` solved this exact failure
      class, and A′ reuses its shape and its numbers: `InitialAcceptBackoff` 100 ms and
      `MaxAcceptBackoff` 5 s as `internal static readonly`, a `TimeProvider` seam (the existing public
      constructor delegating with `TimeProvider.System`, a new **`internal`** one taking the provider),
      `await Task.Delay(backoff, _timeProvider, ct)` then `continue`, the wait doubling to the cap per
      consecutive failure and resetting after a successful accept. Two accept loops that agree is the
      argument that settled the `audiosocket` change (#281).

      **What landed.** The `try` moved *inside* the `while` so the loop can continue; the stop paths
      `break` rather than back off; the filtered `SocketException` catch precedes the unfiltered one,
      which would otherwise be unreachable. The new log event, verbatim:

      ```csharp
      [LoggerMessage(Level = LogLevel.Error, Message = "[AriOutbound] Accept failed — the listener stays bound and accepts again after a backoff")]
      public static partial void AcceptLoopFailed(ILogger logger, Exception exception);
      ```

      Error is consistent rather than an escalation: `AriOutboundListenerLog` already declares
      `ConnectionError` at `LogLevel.Error` for the loss of a **single** connection, and this is the
      loss of every one of them.

      **Two additions beyond the four the task lists, both recorded rather than slipped in.** Neither
      is public, and both are copied from the sibling instead of designed here.
      - `internal Func<CancellationToken, ValueTask<TcpClient>>? AcceptOverride` on the listener, with
        the sibling's own justification ("so a test can make accepts fail on demand instead of
        exhausting file descriptors to get a failure"). Without it there is no way to raise a
        `SocketException` from an accept while `IsRunning` is true, so A′ would have shipped with the
        loop restructured and no test over the branch that motivated it.
      - `Tests/Verbara.Sdk.Ari.Tests/FakeTimeProvider.cs`, a copy of the AudioSocket test project's
        hand-rolled one (each test project keeps its own; there is no `TimeProvider.Testing` package in
        `Directory.Packages.props` and none was added). It is what drives the backoff without a wait.

      **Four tests added to `AriOutboundListenerTests`, all on the fake clock — no real backoff is ever
      waited out, and `sync-fence-baseline.json` is untouched** (that file's entry stays at 1, the
      pre-existing `Task.Delay(25)` inside `WaitForAsync`; no `fence-allow:` marker was needed or used).
      - `AcceptLoopAsync_ShouldLogErrorAndKeepAccepting_WhenAnAcceptFails` — one failed accept is
        logged once at Error as `AcceptLoopFailed` carrying a `SocketException`, `IsRunning` stays
        true, the loop is parked on a 100 ms wait rather than spinning, and moving the clock is what
        produces the second accept.
      - `AcceptLoopAsync_ShouldDoubleTheBackoffToTheCap_WhenAcceptsKeepFailing` — the eight waits the
        loop asks for are 100/200/400/800/1600/3200 ms then 5 s, 5 s.
      - `AcceptLoopAsync_ShouldResetTheBackoff_WhenAnAcceptSucceedsBetweenFailures` — a real loopback
        connection satisfies the middle accept, and the wait after the next failure is the initial
        backoff again, not the doubled one.
      - `StopAsync_ShouldEndTheAcceptLoopWithoutBackingOff_WhenTheListenerIsStopped` — the real
        listener, a real accepted connection so the loop is known to be parked on its next accept, then
        a stop: no `AcceptLoopFailed` entry, and the stop runs to `ListenerStopped` rather than being
        left behind by a loop still waiting.

        **Corrected after an adversarial pass.** This entry first claimed the test also proved "no
        timer created on the fake clock at all". It did carry that assertion, and the assertion could
        not fail — removing the `break;` from the `catch (SocketException) when (!IsRunning)` arm,
        which is exactly "a stopping listener that backed off", left the test green. The reason is
        mechanical: `StopAsync` cancels `_cts` before the loop could reach
        `Task.Delay(backoff, _timeProvider, ct)`, and a `Task.Delay` handed an already-cancelled token
        returns without ever calling `CreateTimer`, so no timer appears either way. The assertion has
        been removed and replaced by a comment in the test saying why, rather than left in place
        looking like a guarantee.

        **So the `break` in that filtered arm joins the unseparable list**, beside 3.5's mutation and
        the `IsRunning`-versus-token discriminator below: correct by construction, not provable by any
        test reachable from outside the type. No coverage is claimed for it.

      **Mutations, each applied alone to the finished file and reverted.**

      1. **A itself — log and let the loop exit — CAUGHT, 3 of the 4 new tests.** Its half-form does
         not even compile: adding `break;` to the unfiltered catch while leaving the backoff in place
         is `error CS0162: Unreachable code detected`, an error here, so the compiler itself rules out
         "A with a backoff still attached" and the only building form of A drops the backoff with it.
         That full form builds at `0 Warning(s), 0 Error(s)` and then fails:

         ```text
           Failed AcceptLoopAsync_ShouldLogErrorAndKeepAccepting_WhenAnAcceptFails [10 s]
           Failed AcceptLoopAsync_ShouldResetTheBackoff_WhenAnAcceptSucceedsBetweenFailures [10 s]
           Failed AcceptLoopAsync_ShouldDoubleTheBackoffToTheCap_WhenAcceptsKeepFailing [10 s]
         ```

         (The 10 s is `SignalTimeout` spent proving the loop never asks for a wait, because it is gone.)

      2. **The filtered stop catch removed entirely — CAUGHT, 3 runs out of 3**, by the stop test, in
         ~54 ms each. The stop's `SocketException` then falls into the unfiltered catch and is booked
         as a failure:

         ```text
         Expected logger.Entries … to not have any items matching (entry.EventName == "AcceptLoopFailed")
         because an accept aborted by the stop is the stop, not a failure, so it is not reported as one,
         but found { EventName = "AcceptLoopFailed", ExceptionType = "SocketException", Level = LogLevel.Error }
         ```

      3. **`when (!IsRunning)` replaced by `when (ct.IsCancellationRequested)` — NOT caught, 3 runs out
         of 3 green. This is a finding, and no coverage is claimed for it.** The task text says a token
         filter "races that ordering"; the ordering argument is right, but the measured consequence is
         the opposite of what a test can exploit — the race resolves the *other* way here. `Stop()`
         aborts the accept asynchronously, `await _cts.CancelAsync()` is the very next statement, and
         the cancel wins every time, so by the time the exception filter runs the token is already
         cancelled and both predicates agree. Producing the disagreement would mean interleaving
         between two adjacent statements inside `StopAsync`, which nothing outside the type can do.
         `IsRunning` is kept because it is correct *by construction* — `_running` is cleared before
         `Stop()`, so it cannot be late, whereas the token can — and the cost of the token filter when
         it does lose the race is a spurious Error and a backoff on a listener that is stopping. This
         belongs to the same family as 3.5's unseparable mutation: what the committed tests pin is that
         the stop is discriminated at all (mutation 2), not which predicate does it.

      **Verification, on the integrated branch.**
      - `dotnet build Verbara.Sdk.slnx -c Release` → `0 Warning(s)`, `0 Error(s)`.
      - The full CI unit filter → **`Failed: 0, Passed: 3600`** (baseline before this task was 3596;
        the four are exactly the four added above).
      - `AriOutboundListenerTests` alone → `Total tests: 18, Passed: 18`, **five runs out of five**,
        with the four new cases at 8–10 ms, 1–2 ms, 3–4 ms and 2–3 ms. The 14 pre-existing cases stayed
        green in every run.
      - `Verbara.Sdk.Governance.Tests` → `Total tests: 129, Passed: 129`, so the sync-fence guard, the
        assert audit and the loopback-seam scan all pass unchanged.
      - `bash tools/audit-test-asserts.sh` → `Files scanned: 445`, `[Fact]/[Theory]: ~2934`,
        `Violations: 0`.
      - `git diff --stat -- '*PublicAPI*'` → **empty**. The new constructor and `AcceptOverride` are
        `internal`, which the analyzer does not track, and the whole `AriOutboundListener` surface is
        in `PublicAPI.Unshipped.txt` rather than Shipped — so the proposal's "no public API change"
        still holds.
      - `openspec validate ari-failed-connect-and-silent-catches --strict` → valid.

      **One line figure that had drifted, recorded per 1.2 rather than smoothed:** the proposal counts
      `AriOutboundListener`'s commented catches at `:126`, `:127`, `:267`, `:268`, `:294` and its bare
      ones at `:112`, `:152`, `:153`, `:154`, `:198`, `:213`, `:283`. Those were right on `main` and
      are stale after this edit. The keys in the table above are rule + file + member, which a
      renumbering cannot invalidate.

      **A pre-existing slow test, measured and not caused here:**
      `DisconnectAsync_ShouldRemoveConnectionFromActiveSet` runs in **30 s**, which is the file's
      `ConnectionIdleTimeout`. It reads the same before this change and after — measured by restoring
      the `HEAD` versions of both files, rebuilding and re-running, which reported the identical
      `[30 s]`. It is not in scope here; it is noted so 8.2's lane timing is not blamed on 4.3.
- [x] 4.4 `Audio/AudioSocketServer` — `cs/empty-catch-block` in `AcceptLoopAsync` (the stop token, and
      the listener disposed under a pending accept) and in `HandleConnectionAsync` (the stop token and
      the linked idle deadline, which share one catch — say both).

      **All three commented**, none made structurally non-empty — no `break;`, no log call was added
      to any of them — so all three close on the comment mechanism 1.2 measured.

      | member | catch | what the block now says |
      |---|---|---|
      | `AcceptLoopAsync` | `OperationCanceledException` | the stop path: `ct` is `_cts.Token`, cancelled by `StopAsync` and so by `DisposeAsync`, and the pending accept ended with it |
      | `AcceptLoopAsync` | `ObjectDisposedException` | the **Windows** shape of that same stop, and explicitly not reached on Linux — with the measurement below in the block, including that the Linux `SocketException` arm reaches no catch here |
      | `HandleConnectionAsync` | `OperationCanceledException` | **both** endings named: the stop token ending `await tcs.Task.WaitAsync(ct)`, and the idle deadline this method schedules itself (`timeoutCts.CancelAfter(_options.IdleTimeout)`) ending the in-flight `Task.Delay(10, timeoutCts.Token)` while it waits for the UUID frame |

      **The task text's "the listener disposed under a pending accept" is a Windows-only shape here,
      and that was measured rather than inferred.** A throwaway console probe outside the repo tree
      (written, run, deleted) reproduced `StopAsync`'s exact ordering — `_listener.Stop()` and then
      `await _cts.CancelAsync()` — against a real `127.0.0.1` `TcpListener` parked on
      `AcceptTcpClientAsync(ct)`, ten times:

      ```text
      run 0: System.Net.Sockets.SocketException SocketErrorCode=OperationAborted
      run 1: System.OperationCanceledException
      run 2: System.Net.Sockets.SocketException SocketErrorCode=OperationAborted
      run 3: System.Net.Sockets.SocketException SocketErrorCode=OperationAborted
      run 4: System.Net.Sockets.SocketException SocketErrorCode=OperationAborted
      run 5: System.OperationCanceledException
      run 6: System.Net.Sockets.SocketException SocketErrorCode=OperationAborted
      run 7: System.Net.Sockets.SocketException SocketErrorCode=OperationAborted
      run 8: System.Net.Sockets.SocketException SocketErrorCode=OperationAborted
      run 9: System.OperationCanceledException
      ```

      So the stop is **racy, 7 `SocketException` to 3 `OperationCanceledException`, and
      `ObjectDisposedException` never** — `Stop()` aborts the accept with `OperationAborted`, and
      whether that surfaces as a cancellation depends on whether `CancelAsync()` has landed by the time
      the aborted accept's result is read. This refines 4.3's probe rather than contradicting it: that
      one called `Stop()` with no cancel behind it and saw `SocketException` every time, which is the
      `CancelAsync`-loses end of the same race.

      **Finding for the owner, no alert attached — the accept loop has no `SocketException` catch at
      all.** `Audio/AudioSocketServer.AcceptLoopAsync` catches only `OperationCanceledException` and
      `ObjectDisposedException`, so:
      - on the 7-in-10 stop arm above the loop task **faults**; `StopAsync` awaits it with
        `ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing)`, which absorbs it, and `IsRunning` is
        set false a few lines later, so the ending is the right one by an unintended door;
      - an accept that fails while the server is **still meant to be running** — EMFILE/ENFILE, a
        connection aborted in the backlog, ENOBUFS — faults it the same way, and then nothing calls
        `StopAsync`. `IsRunning` keeps reporting `true` over a socket still in LISTEN that accepts
        nothing, and not one line is logged. That is exactly the defect 4.3 removed from
        `AriOutboundListener` under the owner's A′ ruling, standing unfixed in this file and in
        `WebSocketAudioServer` (4.6) — and here it is a degree worse, because there the failure at
        least reached a catch.
      This raises **no `cs/empty-catch-block` alert** (there is no catch to be empty), so it is not in
      this change's 22 and does not move its arithmetic. It is recorded here as a candidate for its own
      change: A′'s shape — a filtered stop catch, an Error log, and the `TimeProvider`-driven backoff
      the VoiceAi `AudioSocketServer` and now `AriOutboundListener` both carry — transplants to these
      two loops almost unchanged.

      **A second, smaller observation on `HandleConnectionAsync`.** The idle deadline has two routes
      out and *neither* is reported: the one this catch absorbs (`Task.Delay` cancelled mid-wait) and
      the one that skips it (the deadline expiring between two delays, so the `while` condition goes
      false and the `if (string.IsNullOrEmpty(session.ChannelId))` early return closes the connection
      silently). So the catch adds no silence that the non-throwing path does not already have, which
      is why it is commented rather than left open — the ending is one this method scheduled for
      itself, three lines into its own `try`. If the owner wants "a connection that never identified
      itself" to be visible, that is a change to both routes at once, not an empty-catch remedy.
- [x] 4.5 `Audio/AudioSocketSession` — `cs/empty-catch-block` on the `OperationCanceledException` and
      the `IOException` of both `FillPipeAsync` and `ReadPumpAsync`. The cancellation pair is the
      session's own source and is ordinary teardown. The `IOException` pair is the second case for
      4.3's rule: it ends a session on a transport error with the same observable outcome as a clean
      hangup, the type has `AudioStreamState.Error` and never uses it for this, and it carries no
      logger. Record the finding for the owner; do not add a logger, change the state machine or widen
      the constructor here.

      **The cancellation pair is commented; the `IOException` pair is left byte-identical and its two
      alerts stay open.** Two of four closed, two of four deliberately not — this is the only task in
      section 4 that does not close every alert it touches.

      | member | catch | disposition |
      |---|---|---|
      | `FillPipeAsync` | `OperationCanceledException` | **commented** — closes on the comment mechanism; block still structurally empty. `ct` is `_cts.Token`, a source this type creates and that only `DisposeAsync` cancels (directly, or via the server's `StopAsync` walking its sessions), so it is the session being disposed and not a read that failed; the `finally` still completes the pipe writer, which is what lets the read pump finish |
      | `ReadPumpAsync` | `OperationCanceledException` | **commented**, same teardown and same mechanism; the block notes that the `finally` completes the reader, closes the audio channel and moves a still-connected session to `Disconnected`, which is the ending a `StateChanges` consumer is watching for |
      | `FillPipeAsync` | `IOException` | **LEFT ALERTING** — finding below |
      | `ReadPumpAsync` | `IOException` | **LEFT ALERTING** — finding below |

      **No comment was placed in, above or beside the two `IOException` blocks, and that is deliberate.**
      An inside-the-block comment is precisely the mechanism that retires `cs/empty-catch-block` — 1.2
      measured that pairing — and no measurement exists here for whether a comment merely *adjacent* to
      the block is also picked up by the query. Writing one would risk retiring the alert without
      retiring the defect, which is the one outcome this task exists to prevent. The open alert is the
      marker; the finding below is its text.

      ---

      **FINDING FOR THE OWNER — the two `IOException` catches in `Audio/AudioSocketSession`.**

      *Where.* `src/Verbara.Sdk.Ari/Audio/AudioSocketSession.cs`, one `catch (IOException) { }` in
      `FillPipeAsync` and one in `ReadPumpAsync`. Both are `cs/empty-catch-block` alerts and both
      remain open after this change.

      *What the first one absorbs.* In `FillPipeAsync` the only await that can raise `IOException` is
      `_stream.ReadAsync(memory, ct)` on the `NetworkStream` handed over by
      `AudioSocketServer.HandleConnectionAsync` via `client.GetStream()`. So it absorbs the transport
      dying under a live call: a connection reset by Asterisk or by the network (ECONNRESET), a broken
      pipe, the socket closed beneath the read. This is not teardown — nothing in the process asked for
      it, and the session was meant to still be running.

      *Why that is a defect and not a tidy-up.* The three ways a session can end all arrive at the same
      observable state, and a consumer cannot tell them apart:
      1. **Clean hangup** — Asterisk sends an `AudioFrameType.Hangup` frame; `ReadPumpAsync` publishes
         `AudioStreamState.Disconnected` and returns.
      2. **Clean half-close** — `_stream.ReadAsync` returns 0, `FillPipeAsync` breaks, its `finally`
         completes the writer, the read pump sees `result.IsCompleted`, breaks, and its `finally`
         moves a still-`Connected` session to `Disconnected`.
      3. **Transport failure** — `IOException`, swallowed here, then *the identical `finally`* and
         therefore the identical `Disconnected`.
      `AudioSocketServer.HandleConnectionAsync` is a live consumer of exactly this signal: it completes
      its `TaskCompletionSource` on `AudioStreamState.Disconnected or AudioStreamState.Error`. A
      dropped call and a hung-up call are indistinguishable to it, and to any external subscriber of
      the public `IAudioStream.StateChanges`.

      *The vocabulary exists and is not used.* `AudioStreamState.Error` is declared on the public enum
      in `src/Verbara.Sdk/IAriClient.cs` and is published by exactly **one** site in this type — the
      `case AudioFrameType.Error:` arm, i.e. an in-band protocol Error frame that Asterisk chose to
      send. A transport failure never reaches it. So the state machine already has the state this
      ending deserves and spends it only on the ending that announces itself.

      *Why it was not simply fixed here.* The type carries no diagnostics of any kind. Its only
      constructor is `internal AudioSocketSession(Stream stream, string format)`: no `ILogger`, no
      `Meter`, no counter (the VoiceAi `AudioSocketServer` sibling owns an instance `Meter`; this one
      does not). Reporting the failure at all therefore means widening that constructor and threading a
      logger through `AudioSocketServer`'s construction site — which this task explicitly forbids, and
      which is a design call for the owner rather than a mechanical remedy.

      *The second catch is, on the tree as it stands, dormant — record it before anyone "fixes" it.*
      `ReadPumpAsync`'s try can only receive an `IOException` through `_inputPipe.Reader.ReadAsync`,
      which rethrows whatever the **writer** completed with. `FillPipeAsync`'s `finally` always calls
      `writer.CompleteAsync()` with no argument, so the reader is never handed an exception. The only
      other route is an `IObservable` subscriber throwing `IOException` back out of `_state.OnNext`.
      Neither is a path this type produces today, so that alert is open over a catch nothing currently
      reaches. It is still the right place for the remedy, because remedy (b) below is what wakes it.

      *Remedies, for the owner to choose between — none applied here.*
      - **(a) Publish the state the enum already has.** `_state.OnNext(AudioStreamState.Error)` inside
        `FillPipeAsync`'s `IOException` catch. Cheapest, and it needs no new dependency: the read
        pump's `finally` guard is `if (_state.Value == AudioStreamState.Connected)`, so an `Error`
        already published is *not* overwritten by `Disconnected` and survives to the consumer. Cost:
        `AudioSocketServer` is unaffected (it already treats `Error` and `Disconnected` alike for its
        wait), but an external `StateChanges` subscriber starts seeing `Error` where it saw
        `Disconnected` — an observable change, so the same release-tier question 7.6 raises.
      - **(b) Complete the pipe with the exception** — `writer.CompleteAsync(ex)` — so the failure
        propagates to the reader instead of looking like a clean completion. This is what makes the
        dormant second catch live. To be combined with (a), not substituted for it.
      - **(c) Add diagnostics** by widening the `internal` constructor. No public API change (the type
        is `internal`; only `IAudioStream` is public) but it touches the construction site and wants a
        test of its own.

      *Recommendation.* (a), optionally with (b), as its own change carrying a regression test that
      resets a real `127.0.0.1` connection under a live session and asserts `Error` is observed. Out of
      scope here because it changes an observable this change's proposal says it does not touch.

      *Until then both alerts stay open, which is the intended end state of this task* — the post-merge
      check in 9.2 should expect to still see them and should not read them as work left undone.

      ---
- [x] 4.6 `Audio/WebSocketAudioServer` — `cs/empty-catch-block` in `AcceptLoopAsync` (two) and
      `HandleConnectionAsync` (one), the same stop-path shapes as 4.4.

      **All three commented**, none made structurally non-empty, so all three close on the comment
      mechanism. `AcceptLoopAsync`'s two blocks are word-for-word the ones 4.4 landed — the two accept
      loops are the same shape over the same `StopAsync` ordering (`_listener?.Stop()` then
      `await _cts.CancelAsync()`), so the measured 7-to-3 `SocketException`/`OperationCanceledException`
      race and the never-observed `ObjectDisposedException` apply here unchanged, and 4.4's finding
      about the missing `SocketException` catch applies to this loop too.

      **Where the task text does not match the tree: `HandleConnectionAsync` is *not* "the same
      stop-path shape as 4.4".** 4.4's counterpart shares its catch between two endings because it
      builds a linked source and arms it — `timeoutCts.CancelAfter(_options.IdleTimeout)`. This method
      builds **no** linked source and arms **no** deadline: the three awaits in its `try` are
      `ReadUpgradeRequestAsync(stream, ct)`, `SendUpgradeResponseAsync(stream, wsKey, ct)` and
      `await tcs.Task.WaitAsync(ct)`, every one of them under `ct` and nothing else. So the block says
      "the stop token, and only that", and says why — a reader arriving from the AudioSocket server
      would otherwise go looking for the second ending. The pairing is right for `AcceptLoopAsync` and
      wrong for `HandleConnectionAsync`, and the divergence is in the tree, not in the comment.

      Two things in this file were already done and are left alone: the `finally` deregisters and
      disposes the session, and the body is already wrapped in `using (client)` — so unlike 4.4's
      counterpart there is no hand-written `client.Dispose()` here for 5.2 to replace.
- [x] 4.7 Write down, per catch, what was done and why: commented, logged, or left alerting with a
      finding. This list is what the post-merge check in 9.2 is read against.

      **20 of the 22 close; 2 stay open by design.** Keyed to rule + file + member, never to an alert
      number or a line, both of which this change's own edits have already moved. The alert numbers
      are carried in parentheses only as a convenience for reading `9.2` against a fresh listing.

      | file · member | exception | what was done | mechanism |
      |---|---|---|---|
      | `Client/AriClient.cs` · `EventLoopAsync` | `OperationCanceledException` (873) | commented | comment |
      | `Outbound/AriOutboundListener.cs` · `StopAsync` | `SocketException` (874) | commented | comment |
      | `Outbound/AriOutboundListener.cs` · `AcceptLoopAsync` | `OperationCanceledException` (875) | commented, now holds `break;` | **structural** |
      | `Outbound/AriOutboundListener.cs` · `AcceptLoopAsync` | `ObjectDisposedException` (876) | commented, now holds `break;` | **structural** |
      | `Outbound/AriOutboundListener.cs` · `AcceptLoopAsync` | `SocketException` (877) | **logged at Error** (A′) | **structural** |
      | `Outbound/AriOutboundListener.cs` · `HandleConnectionAsync` | `OperationCanceledException` (878) | commented | comment |
      | `Outbound/AriOutboundListener.cs` · `HandleConnectionAsync` | `ObjectDisposedException` (879) | commented by 4.2, then **deleted by 5.1** | **structural (removal)** |
      | `Outbound/AriOutboundListener.cs` · `ReadPumpAsync` | `OperationCanceledException` (880) | commented | comment |
      | `Audio/AudioSocketServer.cs` · `AcceptLoopAsync` | `OperationCanceledException` (863) | commented | comment |
      | `Audio/AudioSocketServer.cs` · `AcceptLoopAsync` | `ObjectDisposedException` (864) | commented | comment |
      | `Audio/AudioSocketServer.cs` · `HandleConnectionAsync` | `OperationCanceledException` (865) | commented | comment |
      | `Audio/AudioSocketSession.cs` · `FillPipeAsync` | `OperationCanceledException` (866) | commented | comment |
      | `Audio/AudioSocketSession.cs` · `FillPipeAsync` | `IOException` (867) | **LEFT ALERTING** — finding in 4.5 | — |
      | `Audio/AudioSocketSession.cs` · `ReadPumpAsync` | `OperationCanceledException` (868) | commented | comment |
      | `Audio/AudioSocketSession.cs` · `ReadPumpAsync` | `IOException` (869) | **LEFT ALERTING** — finding in 4.5 | — |
      | `Audio/WebSocketAudioServer.cs` · `AcceptLoopAsync` | `OperationCanceledException` (1259) | commented | comment |
      | `Audio/WebSocketAudioServer.cs` · `AcceptLoopAsync` | `ObjectDisposedException` (1260) | commented | comment |
      | `Audio/WebSocketAudioServer.cs` · `HandleConnectionAsync` | `OperationCanceledException` (1258) | commented | comment |

      **Read 9.2 against this, not against an empty list.** Sixteen of the eighteen should be gone;
      **867 and 869 should still be there**, and their continued presence is the intended end state of
      task 4.5 rather than work left undone. The other four alerts in the 22 belong to section 5
      (`cs/missed-using-statement` ×2, `cs/nested-if-statements` ×2) and close by restructuring.

      **Two closure mechanisms, and the distinction decides what to do if 9.2 finds a survivor.**
      Three blocks (875, 876, 877) are no longer empty at all — two hold `break;` and one holds a log
      call — so they are outside the query's reach whatever a comment says. **One more (879) is gone
      outright**: 5.1 deleted the `finally` that held it when it converted the member to `using (client)`,
      so that alert closes by removal and cannot reopen while the `using` stands. That leaves **twelve**
      resting entirely on the comment mechanism, which 1.2 measured rather than assumed: in
      `AriOutboundListener` on `main`, seven bare catches each raised an alert and five that swallow
      *with* a comment raised none. If 9.2 finds a commented block still alerting, all twelve are
      suspect together and the remedy has to change for all of them — record it rather than dismissing
      it, exactly as 9.2 says.

      This split was wrong when first written here and is corrected above: 5.1 had not yet run, and
      879 was recorded as closing by comment. The headline is unaffected — still 20 of 22 — but the
      mechanism split is what 9.2 falsifies against, so a stale one would have sent the post-merge
      check looking for the wrong thing.

      **Nothing was silenced to make a number.** The one catch the group rule exists for — 877 — was
      not commented; it was fixed, and the owner chose the larger of the two fixes. The two that could
      not be honestly commented were left alerting with a written finding. The change closes 20 and
      claims 20.

## 5. The two hand-written disposes and the two nested conditions

- [x] 5.1 `AriOutboundListener.HandleConnectionAsync` — `cs/missed-using-statement`. Wrap the body in
      `using (client)`, and drop the now-redundant `client.Dispose()` in both early-return paths and
      the `try`/`catch (ObjectDisposedException)` in the `finally`. Keep the ordering that exists
      today: the connection is released before the method returns.

      **Done as written: three dispose sites became one.** The body is now a single `using (client)`
      block; both early-return `client.Dispose()` calls are gone, and the whole `finally` — comment,
      `try { client.Dispose(); }` and its `catch (ObjectDisposedException)` — is deleted. The ordering
      is unchanged because a statement-form `using` releases at exactly the position the `finally`
      occupied: every ending leaves the block, and `ReadPumpAsync`'s own `finally` has already
      disposed the connection wrapping this client's stream by the time control gets there.

      **This is a section-4 edit as well as a section-5 one, and two written records now contradict
      the code.** The deleted `catch (ObjectDisposedException)` is the block 4.2 commented and 4.7
      lists as closing **by comment**. After this task it closes **by removal** — the block does not
      exist. Three corrections are owed, and are deliberately *not* made here because they sit in
      another agent's finished section:
      - 4.7's table row `Outbound/AriOutboundListener.cs · HandleConnectionAsync | ObjectDisposedException (879)`
        reads `commented | comment`; it should read removed by 5.1, mechanism **structural (removal)**.
      - 4.7's "two closure mechanisms" paragraph says three blocks (875, 876, 877) are structurally
        non-empty and "the other thirteen rest entirely on the comment mechanism". Post-5.1 the honest
        split is three structurally non-empty, **one removed outright**, twelve on the comment
        mechanism. The headline is untouched: still 20 of 22 closing, 867 and 869 left alerting.
      - 4.2's table row for the same catch, and its prose "the two early-return paths already released
        the client, so this catch absorbs a double release and nothing else", describe code this task
        deletes — there are no early-return dispose calls left at all.

      **One in-code comment was corrected for the same reason**, and it was not in the task text: the
      `catch (OperationCanceledException)` block (alert 878, which stays closed by comment) ended with
      "The finally below still closes it." That `finally` is gone, so the sentence now names the new
      mechanism — "The `using (client)` this method opens with still closes it." The reasoning the
      deleted `finally` carried is preserved in a comment above the `using`, minus the double-dispose
      paragraph that no longer applies, and it names ADR-0058 R4 ("exactly one place disposes it, and
      that place is the handler"), which this shape now satisfies literally rather than approximately.

      **The analyzer fight the brief predicted did not happen.** `IDISP016`, `IDISP013`, `IDE0063` and
      `IDE0059` all stayed silent: `dotnet build Verbara.Sdk.slnx -c Release` → **0 Warning(s),
      0 Error(s)** on the first attempt. `IDISP007` ("don't dispose injected"), the one rule that
      would normally object to a `using` on a parameter, is `none` in `.editorconfig` line 56 and was
      left alone. `NetworkStream? stream = null;` was not touched.

      **The path this task edits blind had no test at all, so one was added.** The
      `upgradeRequest is null` / "malformed HTTP upgrade" early return was reachable by nothing in
      `Tests/`, so 5.1's edit to it — dropping the hand-written `Dispose()` — could not be confirmed
      by 5.5's "no behaviour moved". `StartAsync_ShouldCloseTheConnectionUnanswered_WhenTheUpgradeRequestIsMalformed`
      now covers it with a raw `TcpClient` (the path is unreachable through `ClientWebSocket` — it is
      the one rejection the listener never answers). It is ordered on the close itself, not on a
      clock: the client's read returns 0 only once the listener has released the socket, so **no
      `Task.Delay` was added and `sync-fence-baseline.json` is untouched.** Proven to pin the dispose
      rather than just the log — with `using (client)` mutated to `if (true)` the test fails with
      `System.TimeoutException` after 10 s; restored, it passes in 15 ms.
- [x] 5.2 `Audio/AudioSocketServer.HandleConnectionAsync` — `cs/missed-using-statement`. Wrap the
      client in `using` and the session in `await using`, nested so the session is released first and
      the client second, as the `finally` does today. Keep `_streams.TryRemove` before the session is
      released. The early-return path stops disposing by hand, which removes the
      `#pragma warning disable IDISP016` pair that existed only for that double dispose — confirm the
      build stays at zero warnings without it.

      **Done as written.** The body is now one `using (client)` block with
      `await using var session = new AudioSocketSession(client.GetStream(), _options.DefaultFormat);`
      declared inside it, so reverse declaration order releases the session first and the client
      second — the order the old `finally` produced. The `finally` survives, reduced to its one
      remaining job, `_streams.TryRemove(session.ChannelId, out _)`, which still runs before either
      release and on every path including `return` and both catches. The early return is now a bare
      `if (string.IsNullOrEmpty(session.ChannelId)) return;` and both pragma lines are gone.
      `await using` rather than `using` is forced, not stylistic: `AudioSocketSession` implements
      `IAudioStream`, which derives from `IAsyncDisposable` only. Note that only the *ordering* of
      the two releases moved into the `using` nesting; `TryRemove` stayed behind, so a reviewer
      should not look for a three-way move.

      **Zero warnings without the pragma — measured, not predicted.**
      `dotnet build Verbara.Sdk.slnx -c Release` → **0 Warning(s), 0 Error(s)**, and a forced
      `-t:Rebuild` of `Verbara.Sdk.Ari` alone gives the same, confirming the analyzer really did
      re-run over the edited file rather than skipping it as up to date.

      **The drift check this task asked for, settled by mutation instead of by reading — and BOTH
      claims are wrong, in opposite directions.** The pragma's in-file comment said "False positive —
      session was just created, this is the first dispose"; the task says the pair "existed only for
      that double dispose". Neither survives.
      - **The comment was false; the diagnostic was real.** Re-adding `await session.DisposeAsync();`
        to the early-return path fails the build with `error IDISP016: Don't use disposed instance`
        reported **at that call**. IDISP016 reports at the *first* dispose precisely because the
        instance is used after it, so the comment mistook the report's location for its reason. The
        provenance agrees: `git log --follow -S` finds the comment added by exactly one commit,
        `17dc6f40 feat(quality): add code quality analyzers and tooling`, whose diff for this file
        (then at `src/Asterisk.Sdk.Ari/…`, before the repo rename — `git show` needs the historical
        path) adds **only** the two pragma lines over a `finally` that already disposed a second
        time. The identical sentence appears character-for-character in a different package on a
        different class, `src/Verbara.Sdk.VoiceAi.AudioSocket/AudioSocketServer.cs`, from that same
        commit: it is boilerplate, not analysis.
      - **But the task's premise is also imprecise: a double dispose alone does not raise IDISP016
        here.** Second mutation — keep the re-added `await session.DisposeAsync();` on the early
        return, so the `await using` still disposes a second time, but replace the `finally`'s
        `session.ChannelId` read with a constant so no explicit later *use* of `session` remains:
        the build returns to **0 Warning(s), 0 Error(s)**. The compiler-generated `await using`
        disposal is not a "use" the analyzer counts. What actually raised IDISP016 in the old code
        was the `_streams.TryRemove(session.ChannelId, out _)` read in the `finally` — a genuine
        use-after-dispose on the no-UUID path — not the duplicated `DisposeAsync`.

      The conclusion is unchanged and the remedy is right either way: with no explicit `DisposeAsync`
      invocation on `session` left in the method, IDISP016 has nothing to report, and the pragma must
      not be reinstated. But "the double dispose" is the wrong name for what it was suppressing, and
      anyone re-deriving this from the task text alone would reach the right answer for the wrong
      reason.

      **One sentence of section 4.4's comment was corrected, because 5.2 makes it false.** The
      `catch (OperationCanceledException)` block ended "The `finally` deregisters and disposes the
      session and closes the client on every path." After this task the `finally` only deregisters,
      so it now reads "The `finally` deregisters the session on every path, and the enclosing
      `await using` and `using (client)` release it and then close the connection." That is the
      phrasing `Audio/WebSocketAudioServer.cs` already carries for the identical shape. Nothing else
      in 4.4's reasoning was touched, and `AcceptLoopAsync`'s two catch blocks were not touched at
      all. Flagged rather than decided: if the owner wants 4.4's text frozen, the alternative is
      shipping a comment that contradicts the code three lines below it.

      **No test was added or changed, and the existing coverage is weaker than its names suggest.**
      `HandleConnection_ShouldRemoveStream_WhenHangupReceived` is the real guard on this unit — it
      drives a hangup frame and polls until `GetStream(uuid)` is null, pinning that the reduced
      `finally` still deregisters. **It does not pin the ordering, and an adversarial pass corrected
      this sentence** — it originally claimed the test also showed deregistration happens *before* the
      session is released. It cannot see that: it polls for the end state, and both orderings reach the
      same end state. So the one genuinely new constraint 5.2 introduces — `_streams.TryRemove` before
      the session's release — is **unguarded**, and is recorded in 9.5 rather than claimed as covered.
      `HandleConnection_ShouldRegisterStream_WhenUuidReceived`,
      `HandleConnection_ShouldEmitOnStreamConnected_WhenUuidReceived` and
      `StopAsync_ShouldDisposeAllActiveSessions` pin the happy path and the stop interleaving; the
      last of those passes only because `AudioSocketSession.DisposeAsync` is idempotent, which is now
      load-bearing for this shape and must not be removed. **But
      `HandleConnection_ShouldDisposeSession_WhenNoUuidReceived` — the only test named for the path
      this task rewrites — asserts nothing beyond `ActiveStreamCount == 0`, which was already 0
      before the connection was opened and holds whether or not the session is disposed.** It never
      observes a disposal, so the early-return path has no behavioural pin; 5.2 should not be read as
      "covered by an existing test". `sync-fence-baseline.json` is untouched because no test file
      moved.

      **Two one-line temptations inside the edit window were deliberately left alone**, both because
      9.5 already holds them open: the key-only `_streams.TryRemove` overload (9.5 item 1 — the
      `WebSocketAudioServer` twin uses the `KeyValuePair` overload and this one still does not), and
      the no-UUID early return staying silent (9.5 item 7 — the idle deadline is unreported on both
      its routes). Tidying either here would have quietly consumed a harvested finding.

      **Verification, each number captured from its own run.** Build **0 Warning(s), 0 Error(s)**;
      unit lane under the CI filter **3601 passed, 0 failed, 0 skipped** across **30** assemblies
      (one above the 3600 baseline, and the extra is 5.1's
      `StartAsync_ShouldCloseTheConnectionUnanswered_WhenTheUpgradeRequestIsMalformed`, already in
      the tree when this unit started — not a test added here); `Verbara.Sdk.Governance.Tests`
      **129 / 129**; `Verbara.Sdk.Ari.Tests` alone **445 / 445** (its 30 s is the known
      `DisconnectAsync_ShouldRemoveConnectionFromActiveSet` idle wait);
      `bash tools/audit-test-asserts.sh` 445 files, **0 violations**;
      `git diff --stat -- '*PublicAPI*'` empty.
- [x] 5.3 `AriOutboundListener.Validate` — `cs/nested-if-statements`. Combine the expectation check
      and the authorization check into one condition, parenthesised so the short-circuit is unchanged:
      a listener with neither expectation configured must still not consult the header.
      `StartAsync_ShouldAcceptHandshake_WhenAllHeadersValid`,
      `StartAsync_ShouldRejectHandshake_WhenAuthMismatch` and
      `StartAsync_ShouldAcceptAuth_WhenBasicAuthHeaderMatchesExpected` already pin both branches; run
      them and record it.

      **Merged, in the braced multi-line form the `AllowedApplications` block ten lines below already
      uses**, with a comment saying the parentheses are load-bearing rather than stylistic.

      **All three named tests exist, are committed at `e9fbdb0e` (they predate this change), and
      pass — 3 passed, 0 failed.** They pin three distinct outcomes, not two: `WhenAllHeadersValid`
      is the outer-false branch (no expectation configured and `ConnectClientAsync` sends no
      `Authorization` header), `WhenBasicAuthHeaderMatchesExpected` is outer-true/authorized, and
      `WhenAuthMismatch` is outer-true/unauthorized. Nothing unit-tests `Validate` directly — it is
      `internal static` and reached only end-to-end through a real loopback handshake; the three
      `Validate(` hits elsewhere in that test file are `AriOutboundListenerOptionsValidator.Validate`,
      a different type.

      **The parentheses were proven load-bearing by mutation, not argued.** Dropping them was applied,
      built and run: `A || B && !C` parses as `A || (B && !C)`, and the suite caught it — but **not
      via the test that was expected to catch it**. `WhenAllHeadersValid` still *passed*, because with
      no expectation configured both `A` and `B` are false and `&&` short-circuits before
      `IsAuthorized` is reached. The test that failed was
      `StartAsync_ShouldAcceptAuth_WhenBasicAuthHeaderMatchesExpected`: with `ExpectedUsername` set,
      `A` alone makes the `||` true and a *correctly* authorized handshake is rejected. Recorded
      because the obvious guess about which test guards this rewrite is wrong.

      **Honest limit, which the tests cannot close.** They assert `Validate`'s outcome, not its
      evaluation order. `IsAuthorized` is pure (its only `catch` is a `FormatException` on
      `Convert.FromBase64String`, and it has no side effects), so a rewrite that puts `!IsAuthorized(…)`
      first would consult the `Authorization` header when no expectation is configured and still
      produce identical results everywhere. No test in the repo can distinguish that. The requirement
      "must still not consult the header" is carried by the code shape and by this note — not by a
      test, and 5.3 should not be read as claiming otherwise.
- [x] 5.4 `Audio/AudioSocketSession.ReadFrameAsync` — `cs/nested-if-statements`. Combine the wait and
      the read into one short-circuiting condition. The true branch is covered; the false branch is
      not, so add `ReadFrameAsync_ShouldReturnEmpty_WhenTheChannelIsCompleted` to
      `AudioSocketSessionTests`, matching the equivalent test `WebSocketAudioSessionTests` already has.

      **Merged — and this one was a paste, not a design decision.** The sibling
      `WebSocketAudioSession.ReadFrameAsync` (`<repo>/src/Verbara.Sdk.Ari/Audio/WebSocketAudioSession.cs`,
      same directory, same `IAudioStream`, same `Channel<ReadOnlyMemory<byte>>`) **already carries the
      target form verbatim** — line break before `&&`, braces around the single `return frame;` — and
      carries **no** `cs/nested-if-statements` alert: 1.2's table lists that rule only for
      `Audio/AudioSocketSession.cs · ReadFrameAsync` (11) and `Outbound/AriOutboundListener.cs ·
      Validate` (13). So the shape adopted here is one the query has already declined to flag on an
      adjacent file, rather than a guess that CodeQL will agree. Neither this task nor the proposal
      says the template existed; recording it because it changes 5.4's risk profile entirely.

      **The new test exists and is the only thing guarding the false branch.** Before it,
      `grep -rn ReadFrameAsync Tests/ --include='*.cs'` reached `AudioSocketSession.ReadFrameAsync`
      from exactly one site — `Session_ShouldParseUuidAndReadAudio`, the true branch. Proven by
      mutation, not asserted: replacing the false path's `return ReadOnlyMemory<byte>.Empty;` with
      `return new byte[] { 0x7F };` was applied, built (0 warnings, 0 errors) and run — **Failed: 1,
      Passed: 445**, the single failure being
      `AudioSocketSessionTests.ReadFrameAsync_ShouldReturnEmpty_WhenTheChannelIsCompleted`. Reverted.

      **"Matching the equivalent test" had to be read as intent, not as mechanism.** The WS sibling's
      shape *is* a wall-clock sleep — `WebSocketAudioSessionTests.cs` reads
      `// Give the read pump time to process the close frame` / `await Task.Delay(50);`. Copying that
      literally was impossible: `sync-fence-baseline.json` grants
      `Tests/Verbara.Sdk.Ari.Tests/Audio/AudioSocketSessionTests.cs` exactly **2** unmarked calls and
      the file already spends both (`Task.Delay(TimeSpan.FromSeconds(2))` and `await Task.Delay(10);`
      in `Session_ShouldParseUuidAndReadAudio`), so a third would fail
      `SyncFenceRegressionGuardTests.Guard_ShouldNotExceedBaseline_InTestTree`. The baseline was not
      raised and **no `// fence-allow:` marker was used.** Instead the test reaches channel completion
      causally: a lone `Hangup` frame drives the read pump to `TryComplete()` the writer, and
      `WaitToReadAsync` returning `false` *is* the completion signal, so no settling time exists to
      wait for. The `TimeSpan.FromSeconds(5)` bound is `Task.WaitAsync`, which is **not** in
      `SyncFenceScanner.BannedCalls` (only `Task.Delay`, `Thread.Sleep`, `Thread.SpinWait`,
      `SpinWait.SpinUntil`), and it is a hang guard never reached on the happy path. The AudioSocket
      version is therefore strictly stronger than the sibling it mirrors: the WS test never
      establishes that the channel completed, only that 50 ms elapsed, so on a loaded runner it can
      assert against a still-open channel.

      **Honest limit, which no test can close.** The short-circuit itself is unobservable here.
      Mutating `&&` to `&` was also applied, built and run: **Failed: 0, Passed: 446** — the whole
      suite green. `ChannelReader.TryRead` on a completed channel returns `false` without throwing and
      without side effects, so evaluating it eagerly produces identical results everywhere. The
      short-circuit is carried by the code shape and by this note, not by a test, and 5.4 should not
      be read as claiming otherwise. (Same failure mode 5.3 recorded for its parentheses, except that
      there the mutation *was* caught.)

      **The two `catch (IOException) { }` blocks in this file were not touched** — `FillPipeAsync` and
      `ReadPumpAsync` still read `catch (IOException) { }` exactly as 4.5 left them, and their
      `OperationCanceledException` siblings stay commented. The edit window was the nine lines of
      `ReadFrameAsync`.
- [x] 5.5 Run the whole of `Tests/Verbara.Sdk.Ari.Tests` over the restructured members and confirm no
      behaviour moved.

      **No behaviour moved. Measured, not asserted.** `Tests/Verbara.Sdk.Ari.Tests` has **no**
      `[Trait("Category", …)]` anywhere, so the CI unit filter is a no-op on this assembly and the
      filtered run *is* the whole assembly.

      - Pre-edit (`e9fbdb0e` + sections 1–4, before touching either file): **Failed: 0, Passed: 445**
      - Post-edit: **Failed: 0, Passed: 446** — the one added test, nothing else moved.

      That per-assembly figure did not exist anywhere before this task: the change's measured baseline
      records only "3600 passed across 30 assemblies", which cannot be diffed per assembly, so the
      445 above was captured deliberately *before* the edit rather than reconstructed after it.

      **Retracted: the "3600" baseline was correct, and this paragraph originally said it was not.**
      The reading of 3601 that prompted the claim was taken after 5.1 had already landed its own new
      test, so the +1 was 5.1's, not a stale figure. The chain, each step measured: **3596** at `HEAD`
      → **3600** after 4.3's four accept-loop tests → **3601** after 5.1 → **3602** after 5.4 →
      **3603** after 6.1. Assembly count is 30 throughout and failures stayed at 0.

      **The solution-wide gate is `≥ 3603`, not `≥ 3602`.** The retracted figure would have accepted a
      lane that had silently lost 6.1's test — the hardest test in the change — which is exactly the
      failure a gate exists to catch. Section 8.2 reads 3603.

      Full verification on the restored tree, each exit code captured in its own statement (never read
      off a pipeline, and `dotnet test --no-build` never run after an unchecked build):

      - `dotnet build Verbara.Sdk.slnx -c Release` → **0 Warning(s), 0 Error(s)**, exit 0
      - CI unit filter across the solution → **Failed: 0, Passed: 3602**, 30 assemblies green, exit 0
      - `Verbara.Sdk.Governance.Tests` → **129 / 129**, so the sync-fence ratchet did not move
      - `bash tools/audit-test-asserts.sh` → **445 files, ~2936 `[Fact]`/`[Theory]`, 0 violations**
        (445 *files*, unchanged — this task adds a test method, not a test file)
      - `git diff --stat -- '*PublicAPI*'` → empty. `ReadFrameAsync` is declared on the public
        `IAudioStream` (`<repo>/src/Verbara.Sdk/IAriClient.cs`) but the change is body-only, so
        `RS0016` has nothing to say.
      - `sync-fence-baseline.json` → untouched (`git diff` empty)
      - `openspec validate ari-failed-connect-and-silent-catches --strict` → passed

## 6. Close the two gaps PR #258 disclosed

- [x] 6.1 The per-iteration socket filter is proven only by a scratch probe. Commit
      `ConnectAsync_ShouldFaultAndStopReconnecting_WhenTheRefusedDialIsNotTheCurrentSocket`: hold a
      reconnect dial at the server, run a concurrent `ConnectAsync` so the `_webSocket` field is
      replaced, then answer the held dial `401`. The fixed loop moves to `Faulted` and stops; a filter
      reading the field instead of the socket dialled in that iteration keeps dialling. Prove it fails
      against a filter on the field, and order the race by the accept, not by a sleep.

      **Committed, and the race is ordered causally — there is no clock anywhere in it.** One
      `TcpListener` and a single server task that accepts three dials in a fixed order and releases
      every step itself:

      1. **Dial 1** — the initial `ConnectAsync`: upgraded with `101` and then *held*, dropped only
         when the test sets `dropFirst`, which it does **after** `ConnectAsync` has returned and
         `sut.EventLoop` is captured. This is strictly tighter than
         `...WhenReconnectIsAnsweredUnauthorized`, which drops the first socket the moment its lambda
         returns and so has a window, however wide, in which the drop can race the connect.
      2. **Dial 2** — the reconnect loop's first dial: its upgrade request is read, `reconnectDialed`
         is signalled, and it is then left unanswered. The loop is parked inside
         `ClientWebSocket.ConnectAsync` and *cannot* reach the listener again, which is what makes
         accept #3 unambiguous.
      3. **Dial 3** — the concurrent `ConnectAsync`, never answered. Accepting it is the
         happens-before edge the `401` waits on: `ConnectAsync` assigns `_webSocket = new
         ClientWebSocket()` **synchronously, before its only `await`**, so a dial that reached this
         listener proves the field no longer holds the socket dial 2 opened. Holding it unanswered is
         also what keeps `SetState(Connected)` from ever running, so `Faulted` is a deterministic read.

      `MaxReconnectAttempts = 3` is a pure backstop. The fixed path uses attempt 1 only; a mutated
      filter ends through `ReconnectGaveUp` in about 130 ms and fails on the counts, instead of
      hanging out the 10-second `WaitLimit`.

      **Mutation proof.** `catch (WebSocketException ex) when (socket?.HttpStatusCode == ...)` was
      changed to read `_webSocket?.HttpStatusCode`, the solution rebuilt (`0 Warning(s), 0 Error(s)`)
      and **only the new test** run. Verbatim, with the `AssertionScope`'s full `logger.Entries` dump
      elided — nothing quoted here carries a machine prefix:

      ```text
        Failed ConnectAsync_ShouldFaultAndStopReconnecting_WhenTheRefusedDialIsNotTheCurrentSocket [131 ms]
         Expected logger.Entries.Where(e => e.EventId.Name == "Reconnecting").Select(e => e.Properties.Single(p => p.Key == "Attempt").Value) to be equal to {1} because the loop stops at the 401 and dials no second time, but {1, 2, 3} contains 2 item(s) too many.
         ... to not have any items matching (e.EventId.Name == "ReconnectGaveUp") because the loop stops at the 401, not at MaxReconnectAttempts, but found
             EventId = ReconnectGaveUp, Level = LogLevel.Error, Properties = {[MaxAttempts, 3], ...}
        Failed: 1
      ```

      **Two findings from that run, both worth keeping.**

      **(a) `State` is not the discriminator here — the log is.** Under the mutation the client still
      ends at `AriConnectionState.Faulted`, because `ReconnectGaveUp` writes the same terminal state
      the `401` branch writes. That assertion stayed green and only the two log-shaped ones failed. A
      version of this test that asserted state alone would have been silently vacuous, which is why
      the attempt sequence and the absence of `ReconnectGaveUp` are the load-bearing assertions.

      **(b) The pre-existing test cannot see this mutation at all.** Run under the same mutated
      binary: `Passed ConnectAsync_ShouldFaultAndStopReconnecting_WhenReconnectIsAnsweredUnauthorized
      [102 ms]`, `Passed: 1`. With no concurrent connect, `_webSocket` *is* `socket`, so the two
      filters are indistinguishable. That is precisely the hole this task exists to close, and it is
      also why the mutation run had to be filtered to the new test alone — a whole-file run shows one
      green and one red and invites the wrong conclusion.

      The single line was reverted **by hand**, not with `git checkout --`, because
      `src/Verbara.Sdk.Ari/Client/AriClient.cs` also carries sections 1-3's uncommitted work; the file
      hashes identical to its pre-mutation state (`md5 c58a27e8e27fbc45affbbc2e03b68a22`,
      `git diff --stat` unchanged at `17 insertions(+), 3 deletions(-)`).

      **20 consecutive runs, 20 passed, 0 failed**, per-run **62–68 ms** (mean 64 ms), each a separate
      `dotnet test --no-build` invocation against the reverted binary. This is also the evidence 8.3
      asks for, for this case.

      **One helper extraction, byte-for-byte neutral.** The new test must write the `401` to a dial
      whose upgrade request it has *already* consumed, so it cannot call `RefuseUpgradeAsync` (which
      reads first). The status-line literal moved into `RefusalBytes(string)` and `RefuseUpgradeAsync`
      now calls it; the four committed tests that use `RefuseUpgradeAsync` are unaffected, and the
      bytes written are identical.

      **Drift worth recording: 6.1 is a regression guard over code already on `main`, not over this
      change's work.** `git log -S "ClientWebSocket? socket = null" --follow` on that file returns one
      commit, `c0eceaa9 fix(ari): stop reconnecting when Asterisk refuses the credentials (#258)`, and
      this branch's diff of `AriClient.cs` touches only the `SetState(Connected)` placement and the
      `OperationCanceledException` comment — the filter itself is byte-identical to `HEAD`. The task's
      wording reads as though the filter were new here; `proposal.md` §9 has it right.
- [x] 6.2 Nothing has run against a real Asterisk. Add a `[Trait("Category", "Integration")]` test
      beside `Tests/Verbara.Sdk.IntegrationTests/Ari/AriHealthCheckIntegrationTests.cs` that points a
      client at the fixture's Asterisk with credentials it will refuse, and asserts the initial
      `ConnectAsync` throws and leaves `Faulted`, and that the health check reports `Unhealthy`. This
      lane is Docker-gated and stays off the PR path (ADR-0051) — run it locally and record the result.

      **Added as a method on the existing `AriHealthCheckIntegrationTests` class**, not as a new
      sibling file: the class already carries `[Collection("Integration")]`, the fixture constructor
      and `IAsyncLifetime`, and a new file would re-declare all three. `ConnectAsync_ShouldLeaveState`
      `Faulted_WhenAsteriskRefusesTheCredentials` builds its `BaseUrl` exactly the way
      `AsteriskFixture.CreateAriClient` does, takes `AsteriskFixture.AriUsername` and `AriApp`
      unchanged, and appends `-refused` to the password — one controlled variable, against the real
      `res_ari` on its real ARI port. The sibling `AriHealthCheck_ShouldReturnHealthy_WhenConnected`
      is what makes that single variable meaningful: it proves the unmodified pair connects.

      **Run locally, Docker 29.8.1.** `Category=Integration` on that class, `--no-build`:

      ```text
        Passed AriHealthCheck_ShouldReturnHealthy_WhenConnected [3 ms]
        Passed AriHealthCheck_ShouldReturnUnhealthy_WhenUnreachable [< 1 ms]
        Passed ConnectAsync_ShouldLeaveStateFaulted_WhenAsteriskRefusesTheCredentials [8 ms]
        Passed: 3
      ```

      **14–17 s wall for the whole invocation** across two runs, of which the three assertions are
      ~11 ms: the rest is
      `IntegrationFixture.InitializeAsync` building the network and starting Postgres + Asterisk 22.
      The image build was a cache hit (`verbara/asterisk-local:22` already on this host); a cold run
      pays the `docker/Dockerfile.asterisk` build, including the Digium Opus codec, on top.

      **What a real Asterisk actually answers — measured, not assumed.** The exception type was the
      open question, so it was settled with a throwaway probe assertion that was applied, run and
      reverted: the message is `The server returned status code '401' when status code '101' was
      expected.` So Asterisk 22 refuses a bad ARI password with **HTTP 401**, and
      `ClientWebSocket.ConnectAsync` surfaces it as `WebSocketException` — the fake server's shape is
      the real one. The probe is **not** committed: the committed test asserts the type and the state,
      not the framework's message wording, and it **cannot** assert a status code, because
      `ConnectAsync` never sets `CollectHttpResponseDetails` (only the reconnect path does), so a
      first-dial 401 carries no status anywhere a test can read it.

      **Said plainly, because the tick would otherwise oversell it: this is mostly duplicate
      coverage, deliberately.** `AriClientStateTests.ConnectAsync_ShouldLeaveStateFaulted_WhenThe`
      `UpgradeIsRefused` with `[InlineData("401 Unauthorized")]` already asserts the whole quartet —
      throw, `Faulted`, `IsConnected` false, `Unhealthy`, `"Faulted"` in the description — against a
      hand-written fake. The only new information here is that a real Asterisk answers the way the
      fake does. That is worth having exactly once; it is the class of assumption that rots silently.

      Two smaller notes. The `[Trait("Category", "Integration")]` the task asks for is already on the
      class; it is repeated at method level to match what the file's other explicit test does.
      And the `BaseUrl` interpolates `_fixture.Asterisk.Host`, which is Testcontainers' `Hostname` and
      commonly resolves to `localhost` — a live ADR-0044 exposure that `LoopbackSeamScanner` is
      structurally blind to, inherited unchanged from `AsteriskFixture.CreateAriClient`. It is
      recorded here rather than fixed, because fixing it belongs to the fixture, not to this change.
- [x] 6.3 Record what is still unexercised: only the `ws://` HTTP/1.1 upgrade path. `wss://` is not
      covered by any of these tests, before or after.

      **The `wss://` claim is true, and it is not the whole list.** Every item below was verified by
      grep against this worktree after the two new tests landed, not carried over from the task text.

      1. **`wss://` — genuinely uncovered.** The only `wss://` occurrences anywhere under `Tests/` are
         Deepgram TTS fixtures and `ProviderEndpointScanner`'s own string-literal test data. Nothing
         in `Verbara.Sdk.Ari.Tests`, `Verbara.Sdk.IntegrationTests` or `Verbara.Sdk.FunctionalTests`
         names it, and no ARI test anywhere sets an `https://` `BaseUrl`. So it is not only the TLS
         handshake that is untested — the `https://` → `wss://` **rewrite itself** never runs, and
         that rewrite is **duplicated at two call sites** (`ConnectAsync` and `ReconnectLoopAsync`),
         so the gap is two places that can drift apart, not one.
      2. **A *successful* reconnect is untested.** `ReconnectedSuccess` has **zero** occurrences under
         `Tests/`. Every committed reconnect test ends in `Faulted`, gave-up or disposal, so the
         success arm of `ReconnectLoopAsync` — and with it the `await EventLoopAsync(ct)` recursion
         that restarts the receive loop — has no cover at all.
      3. **The give-up branch is asserted only negatively.** `ReconnectGaveUp` appears under `Tests/`
         only in the two `NotContain` assertions (the pre-existing 401 test and 6.1's new one) and two
         comments. No test asserts that it ever *fires*, or what state it leaves.
      4. **`AutoReconnect = false` on an `AriClient` has no behavioural test.** It is pinned as an
         options round-trip in `AriClientOptionsTests`, and the functional ARI lane sets it five times
         as *setup* — but those tests cancel the token in teardown, so the branch that ends the loop
         there is `ct.IsCancellationRequested`, not the flag. Nothing asserts that a dropped socket
         with `AutoReconnect = false` starts no reconnect loop.
      5. **The Docker fixture cannot close the `wss://` gap as it stands.** `docker/functional/`
         `asterisk-config/http.conf` is four lines — `enabled`, `bindaddr`, `bindport` — with no
         `tlsenable`, no `tlsbindaddr` and no certificate; grep for `tls` across that whole config
         directory returns nothing. Closing `wss://` needs a fixture change first, not just a test.

## 7. Decision record and changelog

- [x] 7.1 Write `docs/decisions/0056-<kebab-title>.md` — **ADR-0056**, the decision this change rests
      on. It records: a connect attempt that ends without a connection leaves a terminal state;
      the ending is classified by who ended it, read from the caller's token and never from the
      exception; a withdrawal leaves `Disconnected` and everything else `Faulted`; the terminal value
      is a statement and not a gate; and the two rejected alternatives — `Faulted` for a withdrawal
      too (rejected: it would page on a routine shutdown, walking back ADR-0053) and `Initial` for a
      withdrawal (rejected: the client opened a socket and a linked source, so it is not as-created).
      Relate it to ADR-0010, ADR-0050 E6, ADR-0052, ADR-0053 and ADR-0054.

      **Written as `docs/decisions/0056-a-connect-attempt-that-never-connects-leaves-a-terminal-state.md`
      — ADR-0056: *A connect attempt that never connects leaves a terminal state*.** 16.1 KB, four H2s
      (`Context` / `Decision` / `Consequences` / `Alternatives considered`), six lettered rules R1-R6,
      prose wrapped at 100 columns, British spelling. The title is verbatim the subject of commit
      `e9fbdb0e`, so the ADR title, the commit subject and 7.5's CHANGELOG heading agree without anyone
      reconciling them later. `dotnet build Verbara.Sdk.slnx -c Release` before and after: **0 Warning(s),
      0 Error(s)** (exit code captured separately, `0`).

      **The six rules, and which of them the task text did not ask for.** R1 the terminal state and why
      the exception reaches the caller unchanged; R2 the classification read from the caller's token;
      R3 `Disconnected` for a withdrawal and `Faulted` for everything else; R4 statement-not-gate.
      Then the two the task text named as additions: **R5**, the placement of `SetState(Connected)`
      inside the `try`, recorded as a decision with its measurement (11/11 green with the write
      outside; two committed tests red with it inside; mutation coverage 5/6 -> 6/6), and **R6**, the
      accept loop's option A'.

      **Judgement call on A', and the reason.** It is recorded here as R6 rather than left to the change
      record. Three reasons. It shares this ADR's spine exactly — the loop's ending is classified by who
      ended it, and the discriminator is not the exception. It is a behaviour change on a *normal* path
      (a transient accept failure now backs off and keeps accepting where it used to end the loop
      silently and leave `IsRunning` true over a bound socket), which is the kind of thing that needs a
      decision record and not only a task log. And `AriOutboundListener.AcceptLoopAsync` already has an
      ADR — ADR-0058 names that method explicitly in its Consequences — so changing its failure handling
      without relating the two would leave two decisions about one method unlinked. R6 gets its own
      Consequences bullet stating the operator-visible delta and its own rejected alternative.

      **The three unprovable things are written down as unprovable**, in one Consequences bullet, with
      no coverage claimed: the token-not-exception discriminator (scoped to the two-argument
      `ClientWebSocket.ConnectAsync(Uri, CancellationToken)` overload, per 3.5's wording — the
      three-argument `HttpMessageInvoker` overload *would* give a seam and is not what is called), the
      `IsRunning` stop discriminator (stated as ordering-correct **by construction**, with the measured
      18/18 pass of the token filter recorded, and *not* as "the token filter is wrong" — the shipped
      source comment overstates that race), and the `break` in the filtered stop arm.

      **Four citation rulings, recorded because they depart from this task's own list.**

      1. **ADR-0050 E6 is not cited bare, anywhere.** E6 is two clauses — "cancellation is never a
         failure" *and* "does not throw" — and ADR-0052 F1 narrowed the second away, with ADR-0050's own
         addendum recording the resolution. `ConnectAsync` catches nothing and the
         `OperationCanceledException` reaches the caller, so a bare E6 citation would quote a retracted
         clause and contradict the shipped code in one sentence. It appears only as "ADR-0050 E6 as
         narrowed by ADR-0052 F1", inside R3 and inside the ADR-0052 `Related` parenthetical, and it is
         listed *after* ADR-0054 R1 because it is the weakest of the three (E6's "failure" means a
         provider failure under ADR-0050 E4/E9, not an enum value — the principle transfers, the ruling
         does not).
      2. **ADR-0058 is added to the `Related` block**, which this task's list omits. It governs the very
         method section 4 changes. The ADR also carries a Consequences bullet pre-empting the apparent
         contradiction a reader will trip on: ADR-0058 R3 says the stopping token *does* reach the
         handler, R6 here says the stop discriminator is `IsRunning` and never the token. Two scopes —
         the handler versus the loop's catch filter — one file, no conflict.
      3. **ADR-0052 is cited for F1, F3 and the Context lesson, never for F2.** F2's discriminator is
         "does this `catch` end a sequence the caller is iterating?"; `ConnectAsync` is not an iterator
         and contains no `catch` at all. The load-bearing use is the Context lesson at that ADR's "the
         suite could not have caught this" section, which is the named precedent for R5.
      4. **ADR-0010 is cited in one narrowed reading only**, inside R4: the two ARI transports are
         independently lifecycle-managed, so `AriConnectionState` scopes to the events socket alone and
         a terminal value says nothing about REST. Verified in the tree — no `State ==` or `IsConnected`
         gate exists on the REST side. Its method names (`StartAsync`/`StopAsync`), its `NgHttpClient`
         reference and its pre-rebrand line-number citations are all stale, and none of them is quoted.

      **Five `Related` entries rather than the house's usual three**, which is the one deliberate
      stylistic deviation. ADR-0053, 0055, 0057, 0058 and 0059 each cite exactly three; 0038/0039 cite
      four. This task asks for five and each of the five earns a parenthetical that says what is
      borrowed, so five it is, with ADR-0050 folded into ADR-0052's entry rather than given a sixth.

      **Every warning from the research was honoured, and each is checkable in the file.** The ADR does
      *not* say nothing reads `_state` (R4 states the narrow claim: the only branch taken on the
      published state is `DisposeAsync`'s `if (IsConnected)`, which compares to `Connected` alone); does
      *not* claim `Faulted`-for-a-withdrawal would move the health check (both map to `Unhealthy`, only
      the interpolated message differs — scoped to consumers outside this SDK); does *not* claim the
      socket and linked source always reach `DisposeAsync` (the second-`ConnectAsync` orphan is stated,
      and named as pre-existing); does *not* claim the enum documentation widened (it says in as many
      words that the behaviour widened and the documentation did not); does *not* claim the first dial
      could have distinguished `401` from `503` (`CollectHttpResponseDetails` is set at exactly one
      site, inside `ReconnectLoopAsync` — verified by grep across `src/`); does *not* rest "the exception
      reaches the caller unchanged" on a false general rule about `finally` (it states the mechanism:
      one `Interlocked.Exchange` on an `int` and one `IsCancellationRequested` read, neither of which
      can throw); does *not* assert the health message names the arm that matched; and does *not* claim
      ADR-0058 R4 is unconditionally satisfied. **The ADR keys to member names throughout and carries
      no line-number citation.**

      **The guard run, verbatim, and it fails as 7.3 predicted — but as TWO tests, not one.**
      `dotnet test Tests/Verbara.Sdk.OpenTelemetry.Tests/... -c Release --no-build`:

      ```text
        Failed Verbara.Sdk.OpenTelemetry.Tests.StatusBlockCoherenceTests.ThePublishedAdrCount_ShouldMatchTheDecisionsOnDisk [35 ms]
         Expected int.Parse(published.Groups["n"].Value, CultureInfo.InvariantCulture) to be 57 because the ADR count is a count of this repository's own contents (ADR-0042 D1); an ADR that lands without the README moving is exactly the drift this catches, but found 56 (difference of -1).
        Failed Verbara.Sdk.OpenTelemetry.Tests.StatusBlockCoherenceTests.TheDecisionCatalog_ShouldListEveryAdrOnDisk [3 ms]
         Expected drift to be empty because the catalog in docs/decisions/README.md must name exactly the ADRs on disk — an ADR lands with its row in the same pull request, and a superseded one keeps both, but found at least one item {"ADR-0056 is a file in docs/decisions/ that the catalog does not list"}.
        Total tests: 31
             Passed: 29
             Failed: 2
      ```

      Both are expected and neither is fixed here. The count failure is 7.3's (`README.md` 56 -> 57
      plus the `docs/claim-registry.md` row); the catalog failure is **7.2's**, and it is worth naming
      because 7.3's own text calls the count test the thing that fails — in fact
      `TheDecisionCatalog_ShouldListEveryAdrOnDisk` asserts set equality between the 4-digit prefixes on
      disk and the catalog's link targets in both directions, so **7.2 is a build gate too, not a style
      step**. Three edits are mandatory before the `Unit Tests` job of 8.2 can go green, not two.

      **Drift recorded, per the reporting rule.**

      - **7.1's own citation list is incomplete and one entry of it is unsafe as written.** "ADR-0050
        E6" bare would make the ADR wrong (ruling 1 above), and the list omits ADR-0058, the ADR that
        governs the method section 4 rewrites (ruling 2). Both departures are deliberate and recorded.
      - **7.3's text understates the gate.** It names only
        `ThePublishedAdrCount_ShouldMatchTheDecisionsOnDisk` (#279); `StatusBlockCoherenceTests` holds a
        third test, `TheDecisionCatalog_ShouldListEveryAdrOnDisk`, which fails independently on a
        missing catalog row. Measured above: 2 failed, not 1.
      - **HEAD's commit message is stale about the shape that now ships.** `e9fbdb0e` says "The
        prescribed shape is what ships, since task 3.1 says to wrap only the await, and the gap is
        written down for the owner instead of closed by moving a line nobody asked to move." The
        `SetState(Connected)`-inside-the-`try` amendment is uncommitted, so anyone writing the PR body
        or the CHANGELOG from `git log` rather than from the working tree records the wrong shape and
        5/6 instead of 6/6. The ADR is written from the tree. The message needs an amend, or the
        section 4-6 commit needs to say it supersedes it.
      - **9.1's coupling list is stale.** It names "(0046, 0047, 0056, 0057, 0058, 0059)" as open
        changes adding an ADR file; 0057, 0058 and 0059 all landed on `main` (#284, #281, #286). Only
        0046, 0047 and 0056 remain, and 7.2's row inserts between the ADR-0055 and ADR-0057 rows, which
        is a spot neither remaining change claims.
      - **`[Unreleased]` holds three entries, not one.** 7.5's framing (and this brief's) says it holds
        #281's; it holds #281, #284 and #286. The new entry appends after #286's, newest last.
      - **`StatusBlockCoherenceTests`' class docstring cites "rows 61 and 74".** The ADR-count claim
        moved to `README.md:67` in #280, so the pointer is stale (61 and 67 today). Not load-bearing and
        deliberately **not** fixed here — it is another change's scope.
      - **The ADR is 16.1 KB against the 9-14 KB band the recent neighbours occupy** (0058 9.2 KB, 0053
        10.1 KB, 0057 11.8 KB, 0059 14.3 KB). It carries two subsystems rather than one, and the
        overflow is R6 plus the unprovable-things bullet. Trimmed where nothing load-bearing was lost;
        not trimmed further.
- [x] 7.2 Add the ADR-0056 row to `docs/decisions/README.md` in numeric order, matching the style of
      the surrounding rows.

      Inserted at line 104, between the ADR-0055 and ADR-0057 rows — the spot 7.1's notes predicted,
      and one neither of the two remaining ADR-adding changes (0046, 0047) claims. Keyed to the
      ADR-0057 row's link target, never to a line number. Style matched against its neighbours:
      `- [ADR-NNNN](NNNN-<kebab>.md) — <summary>. (Accepted, YYYY-MM-DD)`, one line, British
      spelling, back-ticked member names, no line-number citation. The summary is written from the
      tree rather than from the proposal: it carries R1/R2/R3 (the `try`/`finally` that catches
      nothing, the caller's token as the discriminator, `Disconnected` for a withdrawal and `Faulted`
      for everything else), R5 (`SetState(Connected)` inside the `try`, with the 11-of-11 measurement
      that forced it) and R6 (accept-loop option A' — Error line, 100 ms → 5 s backoff, the loop keeps
      accepting, `IsRunning` as the stop discriminator). Date `2026-09-21` taken from the ADR's own
      header, not from the clock.

      **This was a build gate, as 7.1's notes recorded and 7.2's own text does not say.**
      `TheDecisionCatalog_ShouldListEveryAdrOnDisk` asserts set equality between the four-digit
      prefixes on disk and the catalog's link *targets*, in both directions. Re-measured here rather
      than taken from 7.1: before the edit it failed with
      `{"ADR-0056 is a file in docs/decisions/ that the catalog does not list"}`; after, it passes.

      **The row runs long, and deliberately.** 1,105 characters against a catalog whose longest other
      row is ADR-0059's 927 (then 0057 at 880, 0055 at 853, 0058 at 775). Same overflow 7.1 recorded
      for the ADR itself and for the same reason — it carries two subsystems, the connect fix and the
      accept loop, where every neighbour carries one. A first draft ran to 1,400 and was cut back;
      what remains is R1/R2/R3, R5 with the measurement that forced it, R6, and the one observable
      that moves. Trimming further would drop a rule.
- [x] 7.3 Land the ADR-count guard's other two edits **in this same PR**:
      `StatusBlockCoherenceTests.ThePublishedAdrCount_ShouldMatchTheDecisionsOnDisk` (#279) counts
      `docs/decisions/*.md` against the figure `README.md` publishes, and ADR-0042 D1 requires a
      changed figure's registry row to move with it.
      - bump the `**N ADRs**` figure in `README.md`;
      - update its row in `docs/claim-registry.md`.
      Key both edits to the **figure**, never to a line number — the same rule as task 1.2. #280 has
      just moved this claim from `README.md:74` to `:67` and re-based the registry's line pointers
      with it, so a number recorded here goes stale on the next docs PR. Adding 7.1's ADR file without these two fails the
      `Unit Tests` job that task 8.2 requires green.

      **The figure is 57, counted rather than taken on trust.**
      `ls docs/decisions/*.md | grep -v README | wc -l` → **57**, which is the same set
      `StatusBlockCoherenceTests` enumerates (`*.md` in that directory, `README.md` excluded by name).
      Both edits were made by matching the *figure* — `**56 ADRs**` in `README.md`, and the
      `| **56 ADRs** |` cell in `docs/claim-registry.md` — each verified unique before the write, and
      no line number was used as an anchor. Grep confirms the repo now holds no `**56 ADRs**` outside
      `openspec/changes/archive/`, which is period-correct and out of scope by the registry's own
      Scope section.

      The registry row's own `| 67 |` pointer is left alone and is still true: this change adds no
      line to `README.md` above the figure, so the claim stays at `README.md:67`.

      **Measured, before and after, with the build's exit code captured separately from the test run.**
      `dotnet build Verbara.Sdk.slnx -c Release` → **0 Warning(s), 0 Error(s)**. Then
      `dotnet test Tests/Verbara.Sdk.OpenTelemetry.Tests/... -c Release --no-build`:

      - **before** — `Total tests: 31, Passed: 29, Failed: 2`.
        `ThePublishedAdrCount_ShouldMatchTheDecisionsOnDisk`: *expected … to be 57 … but found 56
        (difference of -1)*. `TheDecisionCatalog_ShouldListEveryAdrOnDisk`: *ADR-0056 is a file in
        docs/decisions/ that the catalog does not list*.
      - **after** — `Total tests: 31, Passed: 31`, exit code 0. All three `StatusBlockCoherenceTests`
        cases pass, including `TheHeadlineVersion_ShouldMatchTheVersionThePackagesShipWith`, which was
        green throughout.

      Full CI unit filter on the same binaries
      (`--filter "Category!=Functional&Category!=Integration&Category!=Realtime&Category!=Spike"`,
      with coverage and `coverlet.runsettings`, exactly as `ci.yml:89-94` runs it):
      **30 assemblies, Total 3603, Passed 3603, Failed 0**, exit code 0 — the section 1-6 baseline
      held, with the two previously-red cases now among the passes.
- [x] 7.4 Repoint this change's `decision_ref` to `Sdk/ADR-0056` once that file exists, so the
      proposal cites the decision it rests on rather than the closest neighbour

      `proposal.md` frontmatter: `decision_ref: Sdk/ADR-0053` → `decision_ref: Sdk/ADR-0056`. The
      file it now names is on disk
      (`docs/decisions/0056-a-connect-attempt-that-never-connects-leaves-a-terminal-state.md`), and
      ADR-0053 keeps its place inside ADR-0056's own **Related** line, which is where the inheritance
      belongs. The `Sdk/` prefix is kept as the frontmatter already wrote it — this field is read
      cross-repo, where a bare `ADR-NNNN` would be ambiguous (ADR-0037). No other frontmatter key
      moved. `openspec validate ari-failed-connect-and-silent-catches --strict` → **valid**, exit
      code 0.
- [x] 7.5 `CHANGELOG.md` `[Unreleased]`: one entry stating the observable change — after a failed
      first connect, `State` reads `Faulted`, or `Disconnected` when the caller cancelled, instead of
      `Connecting`; `AriHealthCheck` is `Unhealthy` before and after and only its message moves;
      `IsConnected` is unchanged; the exception reaches the caller unchanged. Say explicitly that this
      amends the sentence in the 2.5.3 entry *"`AriClient` kept reconnecting after Asterisk refused
      its credentials"* which documents the old behaviour. Leave the `(#N)` citation for close-out.

      **Written as TWO entries, not one, and the split is the point.** The change has two observable
      parts and they are not the same kind. `### Fixed — a first ConnectAsync that never connected
      left AriClient reporting Connecting for good` carries the connect-state fix.
      `### Changed — AriOutboundListener keeps accepting after an accept fails` carries option A':
      the loop now *survives* a recoverable accept failure where it used to end, which is a behaviour
      change on a normal path and not the repair of a defect. Both go at the end of `[Unreleased]`,
      after #286 and before `## [2.5.3]`; the section interleaves `### Fixed` and `### Changed` in
      landing order rather than grouping them, exactly as 2.5.3 does, so no reordering was needed.

      **The 2.5.3 amendment is stated against the sentence, not the heading.** The heading the task
      text quotes — *"`AriClient` kept reconnecting after Asterisk refused its credentials"* — is
      about the *reconnect* loop, which this change does not touch; the sentence in that entry which
      documents the behaviour this change amends is its last bullet: *"An initial `ConnectAsync`
      answered `401` still throws `WebSocketException` to the caller and leaves `State` at
      `Connecting`."* The new entry names the 2.5.3 entry by its heading **and** quotes that bullet,
      so both readings are served and the quotation is accurate. Verified by whitespace-normalised
      string match against `CHANGELOG.md`: that sentence now occurs twice (the 2.5.3 original and the
      quotation), and the `AcceptLoopFailed` message quoted in the `### Changed` entry matches
      `AriOutboundListener.cs` character for character.

      **The CodeQL note is carried, as a bullet.** House style does carry that kind of note — 2.5.2
      has a standalone `### Changed — CodeQL triage: behaviour-preserving cleanups, and a functional
      test that now proves its name` — but a *mention* is what was asked, so it sits as the third
      bullet of the `### Changed` entry: 22 alerts open under `src/Verbara.Sdk.Ari`, 20 close, and
      the two `catch (IOException) { }` blocks in `AudioSocketSession` are left open with a written
      finding rather than dismissed.

      **No version number and no release tier appear in either entry**, and the CHANGELOG's structure
      did not force a choice: `[Unreleased]` is a heading of its own and neither entry has to name
      what it will ship as. 7.6 stays open and untouched. The `(#N)` citation is left off both
      headings for 9.4 — note that 9.4 says *"the `[Unreleased]` entry"*, singular, and there are now
      **two** headings to backfill.

      **Verified.** `dotnet build Verbara.Sdk.slnx -c Release` → **0 Warning(s), 0 Error(s)**, exit 0.
      CI unit filter (`Category!=Functional&Category!=Integration&Category!=Realtime&Category!=Spike`)
      → **Failed: 0, Passed: 3603** across **30** assemblies, exit 0 — identical to the baseline.
      A CHANGELOG-shape guard does exist: `scripts/ci/check-publish-liveness.sh`, run by
      `release-hygiene.yml` on `push:[main]` and weekly rather than on the PR path. It passes —
      *"[Unreleased] CHANGELOG section: 10826 bytes (cap 125000, warn 112500). Within budget"*,
      exit 0. No test in the repo parses `CHANGELOG.md`; the only other reader is `publish.yml`,
      which looks for a `## [<version>]` heading and never reads `[Unreleased]`. Every new line wraps
      at 115 columns or fewer, inside the section's existing 118-column maximum.
- [x] 7.6 Put the release tier to the owner before merging: this changes an observable that consumers
      were told to watch, with no API change. ADR-0050's precedent calls a behavioural break minor
      rather than patch; 2.5.3 shipped one as a patch. Record the answer rather than assuming it.

      **The owner ruled: MINOR.** Recorded rather than assumed, with the evidence that produced it —
      which turned out to be richer, and more against the ruling, than the task text said.

      The task cites one precedent. There are **three**, and two point the other way:
      - **ADR-0050** and **ADR-0052 F4** each record, independently, that a behavioural break takes a
        `BREAKING` entry and a **minor** bump. `docs/decisions/README.md` tags both rows that way.
      - **But this repo shipped that exact class as a PATCH twice, on the same day** — `2.5.2` and
        `2.5.3`, both dated 2026-09-13, both *after* ADR-0050 (2026-08-17) and ADR-0052 (2026-08-19).
        And `2.5.2`'s is not a distant cousin: `Fixed — BREAKING: `AmiConnection.ConnectAsync`
        completed as Connected when cancelled` is the **AMI sibling of this change's ARI fix** — same
        defect shape, same remedy shape, shipped as a patch eight days ago.

      What broke the tie is a pattern neither the task nor the ADRs state, found by reading every
      `BREAKING` heading in `CHANGELOG.md`: **a `Fixed — BREAKING` has shipped in a patch; a
      `Changed — BREAKING` never has.** All three that exist are in `2.5.0`, a minor. This change
      carries both kinds — the connect-state fix is a `Fixed`, and A′ is a `Changed`, because the
      accept loop now *survives* a failure it used to end on. Taking the patch would have been the
      first `Changed — BREAKING` in a patch in this repo's history.

      **Applied:** both `[Unreleased]` headings now carry the `BREAKING` label per ADR-0052 F4 —
      neither did before, which was a gap in 7.5 rather than a decision. `Directory.Build.props` is
      **not** touched by this change and still reads `2.5.3`: the version is cut at release time
      (ADR-0055), so this ruling binds the release, not the diff.

      **Follow-up, deliberately not bundled here.** The written rule has now been contradicted twice
      and followed once. An amendment recording what the history actually does — `Fixed — BREAKING`
      may ship in a patch, `Changed — BREAKING` takes a minor — would turn `2.5.2` and `2.5.3` from
      contradictions into applications. It adds an ADR file, so it must be its own PR (task 9.1: one
      ADR-adding change in the queue at a time). Harvested into 9.5.
## 8. Verification

- [x] 8.1 `dotnet build Verbara.Sdk.slnx -c Release`: 0 warnings, 0 errors — including after the
      `IDISP016` pragma pair is removed.

      **`Build succeeded. 0 Warning(s), 0 Error(s)`**, exit 0, with both `#pragma warning disable/restore
      IDISP016` lines gone from `Audio/AudioSocketServer.cs` (`grep -c IDISP016` -> 0). The pragma's
      removal is what 5.2 was uncertain about and it costs nothing: the analyzer does not re-raise.

- [x] 8.2 Unit lane green under the CI filter, with coverage and `Verbara.Sdk.Governance.Tests`
      included. Line and branch coverage inside the band the ratchet enforces, coverage-exclusion
      markers unchanged against the baseline, and `tools/audit-test-asserts.sh` at zero violations.

      Run with the CI filter verbatim (`Category!=Functional&Category!=Integration&Category!=Realtime&Category!=Spike`),
      `--collect:"XPlat Code Coverage" --settings coverlet.runsettings`, 34 coverage files merged with
      `reportgenerator`. Each exit code captured in its own statement.

      | gate | result |
      |---|---|
      | unit lane | **Failed: 0, Passed: 3603**, 30 assemblies green |
      | `Verbara.Sdk.Governance.Tests` | **129 / 129** (included in the lane above) |
      | line coverage | **83.9%**, band `[83.0, 86.0]` — inside, two-sided |
      | branch coverage | **68.46%**, blocking floor 64.0% |
      | lines measured | **13400**, minimum 12315 |
      | patch coverage (diff-cover vs `origin/main`) | **100.0%** — 6/6 changed executable lines, floor 85.0% |
      | coverage-exclusion markers | **0**, baseline 0, 865 files scanned |
      | `tools/audit-test-asserts.sh` | 445 files, ~2934 facts, **0 violations** |

      The two-sided band matters here and passed on the low side with room: 83.9% against a floor of
      83.0%. A change that had added code without tests would have pushed it under.

      **Re-run after the last two commits: the patch-coverage gate failed, and closing it took six
      tests.** The figure above (100%, 6/6 changed lines) predates `1b8781ee` and `be6433ca`. On PR
      #291 the `Coverage Ratchet` job reported **81.0% (102/125 changed lines, floor 85.0%)**, and
      the same measurement locally reported **83.0% (104/125)**. Both are under the floor.

      **Why 125 changed lines and not 6.** Section 5 converted both `HandleConnectionAsync` methods
      to `using (client)`, which re-indented every line of both method bodies. `diff-cover` reads a
      re-indented line as a changed line, so the whole of both methods entered the patch — including
      `catch` arms that are not new code and that had never had a test. The gate is right to count
      them: that is precisely how a restructuring smuggles untested code past review. So the 21
      uncovered lines were treated as what they are — error paths with no test — and the floor,
      `coverage-exclusion-baseline.json` and `[ExcludeFromCodeCoverage]` were all left alone.

      **Six tests added, all under `<repo>/Tests/Verbara.Sdk.Ari.Tests/`.** In
      `Outbound/AriOutboundListenerTests.cs`:
      - `HandleConnectionAsync_ShouldReportItOnce_WhenTheConnectionDiesUnderTheHandshake` — a real
        loopback peer aborts with a linger-zero close, so the handshake read meets an RST and fails
        with `IOException`. Pins one `ConnectionError` at Error and no `AcceptLoopFailed`. Covers the
        `catch (IOException)` arm.
      - `HandleConnectionAsync_ShouldReportItAndKeepListening_WhenAnObserverOfAcceptedConnectionsThrows`
        — a consumer's `OnConnectionAccepted` subscription throws on the first connection only; the
        second connection is the evidence the listener survived. Covers the `catch (Exception)` arm.
      - `HandleConnectionAsync_ShouldCloseItSilently_WhenTheListenerStopsMidHandshake` — the stop
        lands while a connection is still handshaking; the peer's read returning 0 is the causal
        wait. Covers the `catch (OperationCanceledException)` arm, which other tests had been
        reaching only incidentally and not on every run.
      - `PublicConstructor_ShouldBindAndAccept_WhenGivenOnlyOptionsAndALogger` — the two-argument
        constructor consumers resolve from DI. Every other test in the file reaches past it for the
        internal three-argument overload, so the delegation to `TimeProvider.System` was exercised by
        nothing.

      In `Audio/AudioSocketServerTests.cs` (which gained a `CapturingLogger`, and a `logger:`
      parameter on its `CreateServer` helper):
      - `HandleConnection_ShouldReportIt_WhenAnObserverOfNewStreamsThrows` — a consumer's
        `OnStreamConnected` subscription throws. Covers the `catch (Exception)` arm and pins that the
        `finally` still deregisters the session.
      - `HandleConnection_ShouldWindUpAtOnce_WhenThePeerHangsUpAsSoonAsItIdentifies` — a peer that
        sends its UUID frame and half-closes in the same breath, with the idle deadline set to 30 s
        so a handler that needed the deadline would blow the 10 s wait.

      No test uses `Task.Delay` or `Thread.Sleep`: every wait is either the existing `WaitForAsync`
      loop driver or a causal signal — a read returning 0 once the server released the connection, or
      a `TaskCompletionSource` completed by the accept seam. `sync-fence-baseline.json` is unchanged.

      | figure | before | after |
      |---|---|---|
      | patch coverage (local) | **83.0%** — 104/125 | **92.0%** — 115/125, floor 85.0% |
      | patch coverage (CI, PR #291) | **81.0%** — 102/125 | not yet re-run |
      | unit lane | Failed: 0, Passed: 3603 | **Failed: 0, Passed: 3609**, 30 assemblies |
      | build | 0 Warning(s), 0 Error(s) | **0 Warning(s), 0 Error(s)** |
      | line coverage | 83.9% | **84.01%**, band `[83.0, 86.0]` |
      | branch coverage | 68.46% | **68.41%**, floor 64.0% |
      | `Verbara.Sdk.Governance.Tests` | 129 / 129 | **129 / 129** |
      | coverage-exclusion markers | 0, baseline 0 | **0**, baseline 0, 865 files |
      | `tools/audit-test-asserts.sh` | 0 violations | **0 violations**, 445 files, ~2944 facts |

      `diff-cover` missing lines, before → after:
      - `Outbound/AriOutboundListener.cs` 82.4% → **92.6%**: `78, 80, 145, 304, 306-308, 310-312,
        314-315` → `145, 214, 304, 306-307`
      - `Audio/AudioSocketServer.cs` 78.8% → **90.9%**: `97, 108, 131, 149, 170, 172-173` →
        `97, 108, 131`
      - `Audio/WebSocketAudioServer.cs` 66.7% → **66.7%**: `108, 119` → `108, 119`
      - `Audio/AudioSocketSession.cs` and `Client/AriClient.cs`: 100% before and after

      **What was left uncovered, and why — none of it faked.**
      - `AudioSocketServer.cs:97,108` and `WebSocketAudioServer.cs:108,119` are the
        `catch (ObjectDisposedException)` teardown arms. Task 4.2 already measured that this is the
        **Windows** shape of a stop and is not reached on Linux — a 10-run probe against a real
        loopback listener produced `SocketException(OperationAborted)` 7 times,
        `OperationCanceledException` 3 times and this type zero times. Reaching them from a Linux test
        would mean calling `Dispose()` by hand, which proves nothing about the path the catch exists
        for.
      - `AriOutboundListener.cs:145` is `catch (SocketException)` around `_listener?.Stop()` in
        `StopAsync`. `StopAsync` is guarded by an `Interlocked.Exchange` so it runs its body once, and
        `TcpListener.Stop()` on an already-stopped listener does not throw. No honest route in.
      - `AudioSocketServer.cs:131` is the `return;` taken when the UUID frame never arrived. The idle
        deadline's normal route out is `Task.Delay(10, timeoutCts.Token)` raising
        `OperationCanceledException`, which lands in the `catch` below; line 131 is reached only if
        the deadline fires in the gap between a delay completing and the `while` condition being
        re-read. That is a race, not a behaviour, and a test that won it would win it by luck.
      - `AriOutboundListener.cs:304,306-307` is `catch (WebSocketException)` in
        `HandleConnectionAsync`, and it appears **unreachable by construction** rather than merely
        untested: `ReadPumpAsync` has its own `catch (WebSocketException)`, its `finally`'s
        `DisposeAsync` swallows that type inside `AriOutboundConnection.DisconnectAsync`, the three
        handshake helpers are on a `NetworkStream` and raise `IOException`, and
        `WebSocket.CreateFromStream` does not raise it. The only way in is a consumer throwing that
        exact type from an `OnConnectionAccepted` subscription, which would be a test faking its way
        into the arm. Left uncovered and recorded rather than staged.

      **Two things the re-run found that are not coverage.** Neither was fixed here; both are
      reported for a ruling.
      1. `AriOutboundListener.HandleConnectionAsync` adds the connection to `_connections` and logs
         `ConnectionAccepted` **before** `_connectionSubject.OnNext(connection)`. When a consumer's
         subscription throws, the `catch (Exception)` arm logs and the `using (client)` closes the
         socket, but the entry stays in `_connections` and the `AriOutboundConnection` is never
         disposed — `ActiveConnectionCount` then reports a connection that is already closed, until
         `StopAsync` sweeps it. The sibling `WebSocketAudioServer` does not have this: its own
         subscriber-throws test asserts the session is removed and disposed. The new test asserts
         only the log and the listener's survival, so it does not bless the leak.
      2. Two of the changed lines are covered only on some runs, which is what the 2-line gap between
         CI's 102/125 and the local 104/125 is: `:296,303` (the handler's `OperationCanceledException`
         arm) was uncovered on one local run and covered on another, and `:214` (the
         `catch (SocketException) when (!IsRunning)` break) flipped the other way on the final run.
         Both are the Linux stop racing between its two shapes — the same 7/3 split 4.2 measured. The
         handler arm now has a deterministic test of its own; `:214` still does not, and it is the
         reason the margin was taken to 92.0% rather than to just over the floor.

- [x] 8.3 The new `AriClientStateTests` cases pass 20 runs in a row. They open loopback sockets, so
      record the per-run time as well as the pass count.

      **20 runs, 20 green, 0 red.** Every run `Failed: 0, Passed: 12, Skipped: 0, Total: 12`.
      Wall-clock per run, measured around each invocation rather than taken from the runner's own
      figure: **4852 ms fastest, 5607 ms slowest, ~4886 ms median**. The first run is the outlier
      (5607 ms) and every run from the third onwards sits within 4852-4940 ms — a cold-start effect,
      not variance in the tests. No ordering effect, no flake, no run needing a retry.

      These are the cases 2.1 and 6.1 added, and 6.1's concurrent-socket test — the hardest in the
      change — is among them. It opens loopback sockets and orders a genuine race by an accept rather
      than a clock, which is why this task exists; 20 clean runs at a 750 ms spread is the evidence
      that the ordering is causal rather than lucky.

- [x] 8.4 The Docker-gated ARI lane green locally: `Tests/Verbara.Sdk.IntegrationTests` under
      `Category=Integration`, plus the ARI functional tests.

      **Both green, against a real Asterisk in Docker**, run with `RunConfiguration.MaxCpuCount=1` as
      `ci.yml` does:
      - `Verbara.Sdk.IntegrationTests`, `Category=Integration` + `~Ari` -> **Total 10, Passed 10**, 14.3 s.
        Testcontainers started the Asterisk container, passed its `asterisk -rx core show uptime`
        readiness check and the AMI port probe, and tore down cleanly.
      - `Verbara.Sdk.FunctionalTests`, `Category=Functional` + `~Ari` -> **Total 6, Passed 6**.

      **Task 6.2's test ran and passed against the real thing**:
      `AriHealthCheckIntegrationTests.ConnectAsync_ShouldLeaveStateFaulted_WhenAsteriskRefusesTheCredentials`
      [6 ms]. That closes the second of the two gaps PR #258 disclosed — "nothing has run against a
      real Asterisk" — with evidence rather than with a unit-lane fake. This lane is off the PR path
      by ADR-0051 D1, so it is evidence produced locally and recorded here; CI will not reproduce it.

- [x] 8.5 `openspec validate --all --strict` green.

      **`Totals: 11 passed, 0 failed (11 items)`**, exit 0. Only `[INFO]` notes remain, all of the
      "requirement text is very long" kind, which this repo carries throughout. The item count is 11
      rather than the 12 seen earlier in this change because a sibling change archived in the interval.
- [ ] 8.6 CI green, including the code-scanning run on the PR.

## 9. Close-out and the post-merge alert check

- [ ] 9.1 Land the change; record the PR number. Enqueue it **alone**: the open changes that add a **new** ADR
      file (0046, 0047, 0056, 0057, 0058, 0059) all bump the same `**N ADRs**` figure, and the queue
      squashes. Whether git even sees the collision depends on where the two catalog rows land: rows
      inserted at the same spot conflict textually and the queue ejects the second before it builds;
      rows at different spots — the common case, since every one of those tasks adds its row *in
      numeric order* — merge cleanly and the second then fails `Unit Tests` inside the queue, a
      semantic conflict rather than a textual one. Either way the practice is the same: one
      ADR-adding change in the queue at a time, and re-count the figure after any rebase, because
      `strict:false` does not force one. Order does not otherwise matter — the guard counts files,
      not a contiguous sequence — so this change keeps ADR-0056 whenever it lands.
- [ ] 9.2 **After the merge**, once code scanning has analysed `main`, list the open alerts again and
      check them off against the record from 4.7 by rule + file + member. Every alert whose block was
      commented, converted to a `using`, or combined should be gone; the ones deliberately left open
      should still be there with their finding. If a commented block still alerts, the comment is not
      what the query distinguishes and that block's remedy has to change — record it rather than
      dismissing it.
- [ ] 9.3 **Also after the merge**, check whether the two `cs/dispose-not-called-on-throw` alerts on
      `Client/AriClient.cs` — both on the reconnect loop's per-dial `ClientWebSocket`, both dismissed
      as *won't fix* on the reasoning that the socket is owned by the field and released by
      `DisposeAsync` — reopened under new numbers because the lines moved. Do **not** re-dismiss them:
      take the reopened numbers and the standing reasoning to the owner and let the owner decide,
      exactly as with the earlier renumbering.
- [ ] 9.4 Backfill the `(#N)` citation into the `CHANGELOG.md` `[Unreleased]` entry.
- [ ] 9.5 `openspec archive ari-failed-connect-and-silent-catches --yes` once the fix is on `main`,
      as its own `docs(openspec):` PR. Before archiving, harvest into an open change or an ADR
      addendum: the `AudioSocketServer` key-only `TryRemove` finding from the proposal's Impact, every
      catch left alerting from 4.3/4.5, and the `wss://` gap from 6.3.

      **The 4.3 half of that sentence is now stale and the list has grown.** A′ closes 877, so *no*
      catch is left alerting from 4.3; the only ones are 4.5's pair. Harvest, in full:

      1. **The `AudioSocketServer` key-only `TryRemove`** — as the proposal's Impact records it.
      2. **4.5's `IOException` pair** (867, 869) with its written finding and its three costed
         remedies. This is the one that changes an observable, so it carries its own tier question.
      3. **6.3's five unexercised surfaces, not just the `wss://` one.** The recording widened once
         it was checked against the tree: `wss://` (and with it the `https://` → `wss://` rewrite,
         which is duplicated at two call sites and never runs); a **successful** reconnect, including
         the `await EventLoopAsync(ct)` recursion, with `ReconnectedSuccess` at zero occurrences under
         `Tests/`; the `ReconnectGaveUp` branch, asserted only negatively; and `AutoReconnect = false`
         on an `AriClient`, which has an options round-trip but no behavioural test. The `wss://` one
         additionally needs a **fixture** change before any test can reach it — the Docker Asterisk's
         `http.conf` has no TLS at all.
      4. **The same accept-loop defect A′ just fixed, standing unfixed in two more files.** Neither
         `Audio/AudioSocketServer.AcceptLoopAsync` nor `Audio/WebSocketAudioServer.AcceptLoopAsync`
         catches `SocketException` **at all** — verified by reading both methods. So an accept that
         fails while the server is still meant to be running (EMFILE/ENFILE, ECONNABORTED, ENOBUFS)
         faults the loop task, which `StopAsync`'s `SuppressThrowing` await absorbs, leaving
         `IsRunning` reporting `true` over a bound socket that accepts nothing and not one line
         logged. That is 4.3's defect, a degree worse — there it at least reached a catch. It raises
         **no `cs/empty-catch-block`** (there is no catch to be empty), so it does not move this
         change's arithmetic, and A′'s shape transplants almost unchanged.
      5. **Four hand-written `FakeTimeProvider` copies in one repo, and they have diverged.**
         `Cluster.Primitives.Tests` (33 lines), `Resilience.Tests` (136), `VoiceAi.AudioSocket.Tests`
         (148) and now `Verbara.Sdk.Ari.Tests` — four different implementations of one type, not four
         copies of one file. The private sibling repo already takes
         `Microsoft.Extensions.TimeProvider.Testing` (10.10.0) through central package management in
         four of its own test projects, while this repo has no such entry. Consolidating — to the
         package, or to one shared test helper — is a decision about this repo's test substrate and
         deliberately not bundled into an accept-loop fix.
      6. **No guard enforces "no absolute machine path under `openspec/`".** The invariant holds at
         zero across the tree (checked with a broad pattern, not just this machine's home), and
         `config.yaml`'s public-repo content rule forbids those paths — but nothing in
         `Verbara.Sdk.Governance.Tests`, `scripts/`, `tools/` or `.github/workflows/` checks it. Every
         comparable rule in this repo has a guard: sync-fence, the ADR count, recording redaction, the
         loopback seam. This one is held by discipline alone.
      8. **Four residues from the adversarial pass over sections 5 and 6**, none of them regressions,
         each measured rather than argued:
         - `_streams.TryRemove` before the session's release is the one new constraint 5.2 adds and
           **no test pins it**; `HandleConnection_ShouldRemoveStream_WhenHangupReceived` sees only the
           end state, which both orderings reach.
         - `ReadFrameAsync_ShouldReturnEmpty_WhenTheChannelIsCompleted` (5.4) pins the value returned
           on the false branch but not that `ReadFrameAsync` **blocks at all** — turning it into a
           non-blocking poll leaves the whole assembly green. That is the most damaging plausible
           regression of 5.4's combined condition and it is uncovered.
         - `internal Func<CancellationToken, ValueTask<TcpClient>>? AcceptOverride` puts a settable
           test seam on the production accept path of a shipped `public sealed class`, and three of
           the four accept-loop tests never exercise a real socket accept. It is `internal`, so no
           public API is owed, but the seam is a design choice worth revisiting rather than inheriting.
         - The restructured accept loop has a narrow window where the accepted `TcpClient` is owned by
           nothing, between the accept returning and `Task.Run` taking it. Under A' a throw there leaks
           one descriptor per iteration and the loop continues, where before it exited after one. Same
           defect class section 5 just fixed one level down.

      11. **A connection whose observer throws stays in `_connections` after its socket closes.**
          `AriOutboundListener.HandleConnectionAsync` does `_connections.TryAdd` and logs
          `ConnectionAccepted` **before** `_connectionSubject.OnNext`. If a subscriber throws, the
          `catch (Exception)` arm logs it and `using (client)` closes the socket — but the entry stays
          in `_connections` and its `AriOutboundConnection` is never disposed, so
          `ActiveConnectionCount` reports a connection that is already closed until `StopAsync` sweeps
          it. The sibling `WebSocketAudioServer` does **not** have this hole and has a committed test
          asserting removal *and* disposal
          (`HandleConnectionAsync_ShouldRemoveAndDisposeSession_WhenStreamConnectedSubscriberThrows`).
          Found while covering that catch arm for the patch-coverage gate; pre-existing, not introduced
          here. The test added for coverage deliberately asserts only the log and the listener's
          survival, so it does not bless the leak.

      12. **`AriOutboundListener.HandleConnectionAsync`'s `catch (WebSocketException)` looks
          unreachable by construction**, not merely untested: `ReadPumpAsync` carries its own
          `catch (WebSocketException)`; its `finally`'s `DisposeAsync` swallows that type inside
          `AriOutboundConnection.DisconnectAsync`; the three handshake helpers run on a `NetworkStream`
          and raise `IOException`; and `WebSocket.CreateFromStream` does not raise it. The only route in
          is a consumer throwing that exact type from a subscription. Pre-existing defensive
          duplication — worth deleting or proving, but not in a change about accept failures.

            10. **The written release-tier rule has been contradicted twice and followed once — record what
          the history actually does.** ADR-0050 and ADR-0052 F4 both say a behavioural break takes a
          minor. But `2.5.2` and `2.5.3`, both 2026-09-13 and both *after* those ADRs, shipped
          `Fixed — BREAKING` entries as **patches** — one of them `AmiConnection.ConnectAsync`
          completed as Connected when cancelled, the AMI sibling of this change's own ARI fix. The
          pattern that actually holds across every `BREAKING` heading in `CHANGELOG.md` is narrower
          than either ADR states: **a `Fixed — BREAKING` has shipped in a patch; a `Changed — BREAKING`
          never has** (all three live in `2.5.0`). An amendment recording that would turn two
          contradictions into two applications. It adds an ADR file, so it is its own PR — one
          ADR-adding change in the queue at a time (9.1).

            9. **Two disposal behaviours changed in the safe direction and were recorded the other way
         round.** On `AudioSocketServer`'s no-UUID early return the order really did move —
         `_streams.TryRemove` from last to first — so 5.1/5.2's blanket "the ordering is unchanged" is
         not exact. And in both converted members an exception raised *by disposal itself* used to be
         caught and swallowed on the early-return paths and now escapes the method. Benign in both
         cases, but the records say ordering was untouched, and it was not.

            7. **The idle deadline in `Audio/AudioSocketServer.HandleConnectionAsync` is unreported on both
         its routes** — the one its catch absorbs and the one that skips the catch entirely (the
         deadline expiring between two delays, so the `while` condition goes false and the
         no-channel-id early return closes the connection silently). Making "a connection that never
         identified itself" visible is a change to both routes at once, not an empty-catch remedy.
- [ ] 9.6 After the archive, confirm `openspec/specs/client-connection-state/spec.md` exists, write
      its `## Purpose` to match the other living specs — the placeholder the CLI emits is a
      `openspec validate --all --strict` failure, not a cosmetic gap — and re-run that validation
