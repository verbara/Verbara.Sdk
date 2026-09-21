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
      `SetState(AriConnectionState.Connected)` keeps its place immediately after the block. The whole
      edit under `src/` is 26 lines added and 1 removed, all inside `ConnectAsync`:

      ```csharp
      var connected = false;
      try
      {
          await _webSocket.ConnectAsync(uri, cancellationToken);
          connected = true;
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

      SetState(AriConnectionState.Connected);
      ```

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

      **5. The terminal state written on the success path too — NOT CAUGHT.** `Passed: 11, Failed: 0`.
      Removing the `if (!connected)` guard alone does not compile — `CS0219: The variable 'connected' is
      assigned but its value is never used`, an error under `TreatWarningsAsErrors` — so the mutation in
      its only building form drops the flag with the guard: `try { await …; } finally { SetState(…); }`,
      with `SetState(AriConnectionState.Connected)` still after the block. **Every committed test
      passes.** The reason is the ordering the prescribed shape fixes: a `finally` runs *before* the
      statement after its block, so the unconditional terminal write lands on the success path and is
      then overwritten by `SetState(AriConnectionState.Connected)` a statement later. The state is
      momentarily `Faulted` inside `ConnectAsync`, and no observer exists between the two writes.

      This contradicts the delta spec's Mitigation, which lists "writing it on the success path" among
      the mutations that "each fail at least one of them"; the proposal's own Mitigation list does not
      claim it. **Measured, not argued**: the claim becomes true if the success write moves inside the
      `try` (`await …; SetState(Connected);` in the `try`, the guarded terminal write in the `finally`)
      — that layout is behaviourally identical, and the same mutation applied to it fails two committed
      tests:

      ```text
        Failed ConnectAsync_ShouldLeaveStateConnected_WhenTheUpgradeSucceeds [26 ms]
         Expected sut.State to be AriConnectionState.Connected {value: 2} because the dial succeeded, but found AriConnectionState.Faulted {value: 6}.
      Expected sut.IsConnected to be True, but found False.
        Failed ConnectAsync_ShouldConnect_WhenRetriedAfterAFailedAttempt [3 ms]
         Expected sut.State to be AriConnectionState.Connected {value: 2} because the state a failed attempt leaves is a statement about that attempt, not a gate on the next one, but found AriConnectionState.Faulted {value: 6}.
      Expected sut.IsConnected to be True, but found False.
      ```

      The probe was reverted and the shipped code is the shape 3.1 prescribes — "wrap **only** that
      await" — with the gap recorded here for the owner rather than closed by moving a line nobody
      asked to move, or by writing a test, which is 2's territory and closed.

      Re-measured independently rather than taken on report, because this finding contradicts a
      shipped artifact: the mutation was re-applied from scratch, built (`0 Warning(s), 0 Error(s)`,
      exit code read separately from the test output so a stale binary could not answer for it) and
      run — `Failed: 0, Passed: 11, Total: 11`. The alternative layout was then built and run twice,
      once unmutated (`Failed: 0, Passed: 11`) and once mutated (`Failed: 2, Passed: 9`, the two tests
      quoted above). Both confirm the record. The shipped tree was restored to the prescribed shape
      afterwards and re-verified at `Failed: 0, Passed: 11`.

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

- [ ] 4.1 `AriClient.EventLoopAsync` — `cs/empty-catch-block` on its `OperationCanceledException`
      catch. The token is the client's own linked source, cancelled by `DisconnectAsync` and
      `DisposeAsync`; the auto-reconnect block below re-reads the same token, so a cancelled loop does
      not dial. State that in the block.
- [ ] 4.2 `AriOutboundListener` — `cs/empty-catch-block` in four members:
      - `StopAsync`, the `SocketException` from stopping a listener that is already down;
      - `AcceptLoopAsync`, the `OperationCanceledException` and `ObjectDisposedException` of its own
        stop path (the token cancelled, the listener disposed under a pending accept);
      - `HandleConnectionAsync`, the `OperationCanceledException` of the stop token, and the
        `ObjectDisposedException` of the second `Dispose` in its `finally`;
      - `ReadPumpAsync`, the `OperationCanceledException` of the stop token — note in the block that
        the idle timeout is already taken by the filtered catch above it.
      Match the wording of the five commented catches this file already has, which raise no alert.
- [ ] 4.3 `AriOutboundListener.AcceptLoopAsync` — its `SocketException` catch is the one the rule in
      this group exists for. It also absorbs an accept failure that is **not** the stop path, and the
      loop then ends while `IsRunning` still reports `true` and the listener serves nothing. Decide
      with evidence: log it at Error and keep the loop's exit, or leave the alert open with a written
      finding. Do not comment it silent.
- [ ] 4.4 `Audio/AudioSocketServer` — `cs/empty-catch-block` in `AcceptLoopAsync` (the stop token, and
      the listener disposed under a pending accept) and in `HandleConnectionAsync` (the stop token and
      the linked idle deadline, which share one catch — say both).
- [ ] 4.5 `Audio/AudioSocketSession` — `cs/empty-catch-block` on the `OperationCanceledException` and
      the `IOException` of both `FillPipeAsync` and `ReadPumpAsync`. The cancellation pair is the
      session's own source and is ordinary teardown. The `IOException` pair is the second case for
      4.3's rule: it ends a session on a transport error with the same observable outcome as a clean
      hangup, the type has `AudioStreamState.Error` and never uses it for this, and it carries no
      logger. Record the finding for the owner; do not add a logger, change the state machine or widen
      the constructor here.
- [ ] 4.6 `Audio/WebSocketAudioServer` — `cs/empty-catch-block` in `AcceptLoopAsync` (two) and
      `HandleConnectionAsync` (one), the same stop-path shapes as 4.4.
- [ ] 4.7 Write down, per catch, what was done and why: commented, logged, or left alerting with a
      finding. This list is what the post-merge check in 9.2 is read against.

## 5. The two hand-written disposes and the two nested conditions

- [ ] 5.1 `AriOutboundListener.HandleConnectionAsync` — `cs/missed-using-statement`. Wrap the body in
      `using (client)`, and drop the now-redundant `client.Dispose()` in both early-return paths and
      the `try`/`catch (ObjectDisposedException)` in the `finally`. Keep the ordering that exists
      today: the connection is released before the method returns.
- [ ] 5.2 `Audio/AudioSocketServer.HandleConnectionAsync` — `cs/missed-using-statement`. Wrap the
      client in `using` and the session in `await using`, nested so the session is released first and
      the client second, as the `finally` does today. Keep `_streams.TryRemove` before the session is
      released. The early-return path stops disposing by hand, which removes the
      `#pragma warning disable IDISP016` pair that existed only for that double dispose — confirm the
      build stays at zero warnings without it.
- [ ] 5.3 `AriOutboundListener.Validate` — `cs/nested-if-statements`. Combine the expectation check
      and the authorization check into one condition, parenthesised so the short-circuit is unchanged:
      a listener with neither expectation configured must still not consult the header.
      `StartAsync_ShouldAcceptHandshake_WhenAllHeadersValid`,
      `StartAsync_ShouldRejectHandshake_WhenAuthMismatch` and
      `StartAsync_ShouldAcceptAuth_WhenBasicAuthHeaderMatchesExpected` already pin both branches; run
      them and record it.
- [ ] 5.4 `Audio/AudioSocketSession.ReadFrameAsync` — `cs/nested-if-statements`. Combine the wait and
      the read into one short-circuiting condition. The true branch is covered; the false branch is
      not, so add `ReadFrameAsync_ShouldReturnEmpty_WhenTheChannelIsCompleted` to
      `AudioSocketSessionTests`, matching the equivalent test `WebSocketAudioSessionTests` already has.
- [ ] 5.5 Run the whole of `Tests/Verbara.Sdk.Ari.Tests` over the restructured members and confirm no
      behaviour moved.

## 6. Close the two gaps PR #258 disclosed

- [ ] 6.1 The per-iteration socket filter is proven only by a scratch probe. Commit
      `ConnectAsync_ShouldFaultAndStopReconnecting_WhenTheRefusedDialIsNotTheCurrentSocket`: hold a
      reconnect dial at the server, run a concurrent `ConnectAsync` so the `_webSocket` field is
      replaced, then answer the held dial `401`. The fixed loop moves to `Faulted` and stops; a filter
      reading the field instead of the socket dialled in that iteration keeps dialling. Prove it fails
      against a filter on the field, and order the race by the accept, not by a sleep.
- [ ] 6.2 Nothing has run against a real Asterisk. Add a `[Trait("Category", "Integration")]` test
      beside `Tests/Verbara.Sdk.IntegrationTests/Ari/AriHealthCheckIntegrationTests.cs` that points a
      client at the fixture's Asterisk with credentials it will refuse, and asserts the initial
      `ConnectAsync` throws and leaves `Faulted`, and that the health check reports `Unhealthy`. This
      lane is Docker-gated and stays off the PR path (ADR-0051) — run it locally and record the result.
- [ ] 6.3 Record what is still unexercised: only the `ws://` HTTP/1.1 upgrade path. `wss://` is not
      covered by any of these tests, before or after.

## 7. Decision record and changelog

- [ ] 7.1 Write `docs/decisions/0056-<kebab-title>.md` — **ADR-0056**, the decision this change rests
      on. It records: a connect attempt that ends without a connection leaves a terminal state;
      the ending is classified by who ended it, read from the caller's token and never from the
      exception; a withdrawal leaves `Disconnected` and everything else `Faulted`; the terminal value
      is a statement and not a gate; and the two rejected alternatives — `Faulted` for a withdrawal
      too (rejected: it would page on a routine shutdown, walking back ADR-0053) and `Initial` for a
      withdrawal (rejected: the client opened a socket and a linked source, so it is not as-created).
      Relate it to ADR-0010, ADR-0050 E6, ADR-0052, ADR-0053 and ADR-0054.
- [ ] 7.2 Add the ADR-0056 row to `docs/decisions/README.md` in numeric order, matching the style of
      the surrounding rows.
- [ ] 7.3 Land the ADR-count guard's other two edits **in this same PR**:
      `StatusBlockCoherenceTests.ThePublishedAdrCount_ShouldMatchTheDecisionsOnDisk` (#279) counts
      `docs/decisions/*.md` against the figure `README.md` publishes, and ADR-0042 D1 requires a
      changed figure's registry row to move with it.
      - bump the `**N ADRs**` figure in `README.md`;
      - update its row in `docs/claim-registry.md`.
      Key both edits to the **figure**, never to a line number — the same rule as task 1.2. #280 has
      just moved this claim from `README.md:74` to `:67` and re-based the registry's line pointers
      with it, so a number recorded here goes stale on the next docs PR. Adding 7.1's ADR file without these two fails the
      `Unit Tests` job that task 8.2 requires green.
- [ ] 7.4 Repoint this change's `decision_ref` to `Sdk/ADR-0056` once that file exists, so the
      proposal cites the decision it rests on rather than the closest neighbour
- [ ] 7.5 `CHANGELOG.md` `[Unreleased]`: one entry stating the observable change — after a failed
      first connect, `State` reads `Faulted`, or `Disconnected` when the caller cancelled, instead of
      `Connecting`; `AriHealthCheck` is `Unhealthy` before and after and only its message moves;
      `IsConnected` is unchanged; the exception reaches the caller unchanged. Say explicitly that this
      amends the sentence in the 2.5.3 entry *"`AriClient` kept reconnecting after Asterisk refused
      its credentials"* which documents the old behaviour. Leave the `(#N)` citation for close-out.
- [ ] 7.6 Put the release tier to the owner before merging: this changes an observable that consumers
      were told to watch, with no API change. ADR-0050's precedent calls a behavioural break minor
      rather than patch; 2.5.3 shipped one as a patch. Record the answer rather than assuming it.

## 8. Verification

- [ ] 8.1 `dotnet build Verbara.Sdk.slnx -c Release`: 0 warnings, 0 errors — including after the
      `IDISP016` pragma pair is removed.
- [ ] 8.2 Unit lane green under the CI filter, with coverage and `Verbara.Sdk.Governance.Tests`
      included. Line and branch coverage inside the band the ratchet enforces, coverage-exclusion
      markers unchanged against the baseline, and `tools/audit-test-asserts.sh` at zero violations.
- [ ] 8.3 The new `AriClientStateTests` cases pass 20 runs in a row. They open loopback sockets, so
      record the per-run time as well as the pass count.
- [ ] 8.4 The Docker-gated ARI lane green locally: `Tests/Verbara.Sdk.IntegrationTests` under
      `Category=Integration`, plus the ARI functional tests.
- [ ] 8.5 `openspec validate --all --strict` green.
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
- [ ] 9.6 After the archive, confirm `openspec/specs/client-connection-state/spec.md` exists, write
      its `## Purpose` to match the other living specs — the placeholder the CLI emits is a
      `openspec validate --all --strict` failure, not a cosmetic gap — and re-run that validation
