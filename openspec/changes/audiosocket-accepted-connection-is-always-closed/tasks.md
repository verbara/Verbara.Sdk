# Tasks: audiosocket-accepted-connection-is-always-closed

## 1. Reproduce before fixing

- [x] 1.1 Write the regression test first, against the unfixed server, and record its verbatim failure.
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

      **Added** `AcceptLoopAsync_ShouldCloseTheAcceptedConnection_WhenTheServerStopsBeforeTheHandoffRuns`
      in `Tests/Verbara.Sdk.VoiceAi.AudioSocket.Tests/AudioSocketServerEdgeCaseTests.cs`, on the class's
      own harness (`SignalTimeout`, `ReadFromServerAsync`, `CapturingLogger`). The override increments
      the attempt count, calls `serverStopping.Cancel()` and returns the loopback pair's server end in
      the same call, so the hand-off at `AudioSocketServer.cs:108` always sees a cancelled token; the
      pair is `IPAddress.Loopback` on both ends (the IPv4 literal, never `localhost` — `Sdk/ADR-0044`),
      and `ConnectionTimeout` is one hour on a `FakeTimeProvider` the test never advances, so no wait
      here can end on a clock.

      **Red against the unfixed server, three runs out of three**, each ending on the `SignalTimeout`
      bound of the peer's read: `Task.Run(…, ct)` creates the work item already cancelled, the handler
      never runs, and nothing ever closes the connection. Verbatim, with only the machine-path prefix
      replaced by `<repo>` (nothing under `openspec/` carries an absolute path):

      ```text
      [xUnit.net 00:00:10.10]     Verbara.Sdk.VoiceAi.AudioSocket.Tests.AudioSocketServerEdgeCaseTests.AcceptLoopAsync_ShouldCloseTheAcceptedConnection_WhenTheServerStopsBeforeTheHandoffRuns [FAIL]
        Failed Verbara.Sdk.VoiceAi.AudioSocket.Tests.AudioSocketServerEdgeCaseTests.AcceptLoopAsync_ShouldCloseTheAcceptedConnection_WhenTheServerStopsBeforeTheHandoffRuns [10 s]
        Error Message:
         System.TimeoutException : The operation has timed out.
        Stack Trace:
           at Verbara.Sdk.VoiceAi.AudioSocket.Tests.AudioSocketServerEdgeCaseTests.AcceptLoopAsync_ShouldCloseTheAcceptedConnection_WhenTheServerStopsBeforeTheHandoffRuns() in <repo>/Tests/Verbara.Sdk.VoiceAi.AudioSocket.Tests/AudioSocketServerEdgeCaseTests.cs:line 159
         at Verbara.Sdk.VoiceAi.AudioSocket.Tests.AudioSocketServerEdgeCaseTests.AcceptLoopAsync_ShouldCloseTheAcceptedConnection_WhenTheServerStopsBeforeTheHandoffRuns() in <repo>/Tests/Verbara.Sdk.VoiceAi.AudioSocket.Tests/AudioSocketServerEdgeCaseTests.cs:line 165
      --- End of stack trace from previous location ---

      Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 10 s - Verbara.Sdk.VoiceAi.AudioSocket.Tests.dll (net10.0)
      ```

      Line 159 is the assertion `(await ReadFromServerAsync(peer)).Should().Be(0, …)`; line 165 is the
      async resume frame for the same statement. **The failure is the bound, not an assertion message** —
      the read never reaches end of stream at all, so there is no value to compare. That is the defect
      exactly: with no owner, the connection is closed only if a garbage collection eventually finalizes
      its handle, which no test bound can wait for.

- [x] 1.2 Add the control that keeps the fix from over-correcting into closing a connection the server
      is still serving: `AcceptLoopAsync_ShouldLeaveTheConnectionOpen_WhileTheHandlerIsServingIt`.
      Same harness with the token live; the override returns the connection on its first call and
      afterwards parks on an accept that ends only when the token does. Wait for the handler's timeout
      timer to appear on the fake clock (`NextTimerAsync`, due `ConnectionTimeout`) — the handler
      taking the connection over is what creates it — and assert the peer's read has not completed at
      that point. Then cancel the token and assert the read reaches end of stream and the loop ends.
      Green before the fix and after it.

      **Added** `AcceptLoopAsync_ShouldLeaveTheConnectionOpen_WhileTheHandlerIsServingIt` beside it.
      The override returns the connection on its first call and afterwards parks on
      `ParkUntilCancelledAsync`, a `TaskCompletionSource` completed by the loop token's own
      registration — **not** `Task.Delay(Timeout.InfiniteTimeSpan, token)`, which was the first shape
      and which `SyncFenceRegressionGuardTests.Guard_ShouldNotExceedBaseline_InTestTree` rejected
      (`baseline=4, current=5` for this file). The registration is causal rather than timed, so it
      needs no `fence-allow` marker and `sync-fence-baseline.json` stays at 4 — no baseline is raised.
      The parked accept also keeps the loop from putting a backoff wait on the fake clock, so the
      handler's `ConnectionTimeout` timer is the only timer `NextTimerAsync` can return.

      **Green before the fix: 10 runs, 10 passes, 16–19 ms each** — nothing here approaches
      `SignalTimeout`, which is a failure bound and not a pace.

- [x] 1.3 Run the three existing accept-backoff tests before the fix and after it, unchanged:
      `AcceptLoopAsync_ShouldDoubleTheWaitUpToFiveSeconds_WhenAcceptsKeepFailing`,
      `AcceptLoopAsync_ShouldStartTheWaitOver_WhenAnAcceptSucceeds` and
      `AcceptLoopAsync_ShouldEndWithoutWaitingOutTheBackoff_WhenCancelledDuringIt`. They carry the
      "unchanged" half of the requirement — the 100/200/400/800/1600/3200/5000/5000 ms sequence, the
      restart after a successful accept, and the loop ending at once when the token cancels during a
      wait — and the second of them also proves a connection accepted with a live token still reaches
      the handler, because it filters that handler's timeout timer off the fake clock.

      **All three pass unchanged against the unfixed server.** `git diff` on
      `Tests/Verbara.Sdk.VoiceAi.AudioSocket.Tests/AudioSocketServerAcceptBackoffTests.cs` is empty —
      the file was not touched. From
      `dotnet test Tests/Verbara.Sdk.VoiceAi.AudioSocket.Tests/ --filter "FullyQualifiedName~AudioSocketServerAcceptBackoffTests"`:

      ```text
        Passed …AudioSocketServerAcceptBackoffTests.AcceptLoopAsync_ShouldStartTheWaitOver_WhenAnAcceptSucceeds [19 ms]
        Passed …AudioSocketServerAcceptBackoffTests.AcceptLoopAsync_ShouldEndWithoutWaitingOutTheBackoff_WhenCancelledDuringIt [1 ms]
        Passed …AudioSocketServerAcceptBackoffTests.AcceptLoopAsync_ShouldDoubleTheWaitUpToFiveSeconds_WhenAcceptsKeepFailing [1 ms]
      Total tests: 3
      ```

      The whole project's unit lane under the CI filter is **106 tests, 105 passed, 1 failed** — and the
      one failure is 1.1. The two tests added here are the only movement (104 before), and no
      neighbour reacts to them.

## 2. Fix

- [x] 2.1 In `src/Verbara.Sdk.VoiceAi.AudioSocket/AudioSocketServer.cs:108`, hand the accepted
      connection over unconditionally — `_ = Task.Run(() => HandleConnectionAsync(client, ct));` —
      and replace the line's silence with a comment that says why the stopping token is an argument
      and not a gate. Confirm by reading that the handler still receives `ct`, that its
      no-identifying-frame branch (lines 182-189) is the single place that closes such a connection,
      and that the branch stays quiet while `ct` is cancelled.

      **Applied**, with one forced deviation from the line above: the bare
      `Task.Run(() => HandleConnectionAsync(client, ct))` does not compile in this repo.
      `Directory.Build.props` sets `AnalysisLevel=latest-recommended` with `TreatWarningsAsErrors`, so
      **CA2016** fails the build on that exact line — *"Forward the 'ct' parameter to the 'Run' method
      or pass in 'CancellationToken.None' explicitly to indicate intentionally not propagating the
      token"*. The analyzer's second remedy is the shape proposal item 2 already names as the target:
      `AriOutboundListener.cs:149` passes `CancellationToken.None`. The hand-off is therefore
      `_ = Task.Run(() => HandleConnectionAsync(client, ct), CancellationToken.None);` — the same
      unconditional hand-off this task asks for, written the one way the build accepts, and the way
      that makes the two accept loops agree rather than differ. One line replaced plus a ten-line
      comment whose subject is ownership, not mechanics; `AudioSocketServer.cs` is the only source
      file touched and `PublicAPI.Unshipped.txt` is byte-identical before and after
      (md5 `2803148d25f3575759562722d048d1f1`).

      **Confirmed by reading the fixed file** (line numbers are post-fix; the comment moved everything
      below it down by eleven):

      - **The handler still receives `ct`.** The delegate is unchanged — `HandleConnectionAsync(client, ct)`.
        Only `Task.Run`'s own token argument moved, and `ct` is still what the handler links its
        `ConnectionTimeout` source against (`:155`), so a stop still ends the wait for the frame early
        instead of waiting out the timeout.
      - **`if (!gotUuid)` (`:193-201`, was `:182-189`) is the single place that closes such a
        connection.** The file holds exactly two `client.Dispose()` calls — `:199` in this branch and
        `:235` in the catch-all — and one other release path, `session.DisposeAsync()` at `:220`,
        which is reachable only once `gotUuid` is true, i.e. once the connection has become a session.
        `StopAsync`'s `session.DisposeAsync()` (`:93`) iterates `_sessions`, which a never-served
        connection never joins. A connection handed over with `ct` already cancelled walks the same
        path as a stop landing mid-wait: the linked token is born cancelled, `reader.ReadAsync`
        throws, the `when (linked.IsCancellationRequested)` filter at `:169` swallows it, the loop
        breaks with `gotUuid` still false, and `:199` closes it. The catch-all is reached only if
        something throws past that filter — a socket that had already failed, which is the one extra
        Error the proposal's Impact section predicts.
      - **The branch stays quiet while `ct` is cancelled.** `AudioSocketLog.NoUuidFrame` is
        `LogLevel.Warning` (`Internal/AudioSocketLog.cs:19-20`) and `:196` guards it with
        `if (!ct.IsCancellationRequested)`. Nothing else on that path logs, increments a counter or
        starts an activity — `ConnectionsAccepted` (`:204`) and `StartSession` (`:205`) are both below
        the `return`. So a connection closed because the server was stopping produces no Warning and
        no telemetry at all, which is what the spec's second scenario asks for.

      **Test results, all on the fixed tree:**

      - 1.1 `AcceptLoopAsync_ShouldCloseTheAcceptedConnection_WhenTheServerStopsBeforeTheHandoffRuns`:
        `Failed: 0, Passed: 1, Skipped: 0, Total: 1` — **Passed [14 ms]**. Red before the fix on the
        `SignalTimeout` bound, green now in a fraction of it.
      - 1.2 `AcceptLoopAsync_ShouldLeaveTheConnectionOpen_WhileTheHandlerIsServingIt`: **Passed [15 ms]**
        — the fix does not over-correct into closing a connection the handler is still serving.
      - The three accept-backoff tests, unchanged: `Total tests: 3, Passed: 3` —
        `…_ShouldStartTheWaitOver_WhenAnAcceptSucceeds` [18 ms],
        `…_ShouldEndWithoutWaitingOutTheBackoff_WhenCancelledDuringIt` [1 ms],
        `…_ShouldDoubleTheWaitUpToFiveSeconds_WhenAcceptsKeepFailing` [1 ms].
      - The whole `Verbara.Sdk.VoiceAi.AudioSocket.Tests` project under the CI unit filter:
        `Failed: 0, Passed: 106, Skipped: 0, Total: 106, Duration: 2 s` — task 1.3's 105/1 with the
        one failure now green, same 106 total, and no neighbour moved in either direction.
      - `dotnet build Verbara.Sdk.slnx -c Release`: `Build succeeded. 0 Warning(s), 0 Error(s)`
        (14.87 s), once CA2016 above was answered.
- [x] 2.2 Sweep `src/` for every other `Task.Run(…, token)` whose delegate owns a resource nothing
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

      **Swept `src/` for `Task.Run`: 29 call sites.** `grep -rn "Task\.Run" src/ --include='*.cs'`
      returns 33 lines; four are prose in comments. They fall into three groups, and only the first
      can carry this change's defect at all, because only it has a token to skip the work item on:

      | group | sites | can the hand-off be skipped before it starts? |
      |---|---|---|
      | A — `Task.Run(…, <live token>)` | 10 | **yes** — the shape this change fixed |
      | B — `Task.Run(…, CancellationToken.None)` | 14 | no — already ADR-0058 R2's shape |
      | C — `Task.Run(delegate)`, the single-argument overload | 5 | no — there is no token to gate on |

      **Group A in full. Two sites were known before the sweep; eight were not, and all eight are
      safe.** "Strand" below means: the work item is created already cancelled, the delegate never
      runs, and this is what is left behind.

      | site | token passed to `Task.Run` | what a skipped delegate strands | verdict |
      |---|---|---|---|
      | `src/Verbara.Sdk.VoiceAi.AudioSocket/AudioSocketServer.cs:80` | the host's **start** token | nothing disposable | **no leak — a different defect** (below) |
      | `src/Verbara.Sdk.Push.Nats/NatsBridge.cs:236` | `stoppingToken` | nothing disposable | **no leak — same class as `:80`** (below) |
      | `src/Verbara.Sdk.VoiceAi.Stt/Speechmatics/SpeechmaticsSpeechRecognizer.cs:121` (closes `:135`) | `ct` | only the duty to complete `channel.Writer` | safe |
      | `src/Verbara.Sdk.VoiceAi.Stt/Cartesia/CartesiaSpeechRecognizer.cs:99` (closes `:117`) | `ct` | ditto | safe |
      | `src/Verbara.Sdk.VoiceAi.Stt/AssemblyAi/AssemblyAiSpeechRecognizer.cs:112` (closes `:126`) | `ct` | ditto | safe |
      | `src/Verbara.Sdk.VoiceAi.Stt/Deepgram/DeepgramSpeechRecognizer.cs:77` (closes `:91`) | `ct` | ditto | safe |
      | `src/Verbara.Sdk.VoiceAi.Tts/Lmnt/LmntSpeechSynthesizer.cs:170` (closes `:188`) | `ct` | ditto | safe |
      | `src/Verbara.Sdk.VoiceAi.Tts/Cartesia/CartesiaSpeechSynthesizer.cs:101` (closes `:121`) | `ct` | ditto | safe |
      | `src/Verbara.Sdk.VoiceAi.Tts/Deepgram/DeepgramSpeechSynthesizer.cs:102` (closes `:120`) | `ct` | ditto | safe |
      | `src/Verbara.Sdk.VoiceAi.Tts/ElevenLabs/ElevenLabsSpeechSynthesizer.cs:85` (closes `:98`) | `ct` | ditto | safe |

      **The two known sites both read as the task describes, and neither leaks a connection.**

      - **`AudioSocketServer.cs:80`** — `_ = Task.Run(() => AcceptLoopAsync(_cts.Token), cancellationToken)`.
        The token is `IHostedService.StartAsync`'s own argument, the host's **start** token, and
        deliberately not `_cts.Token`, which is what the loop receives. The delegate holds no
        resource: `_listener` is a field, already bound and started at `:73`, and both `StopAsync`
        (`:90`) and `DisposeAsync` release it. So nothing leaks. What a cancelled start token does
        leave is a server that has **bound its port and logged `ServerListening`** while its accept
        loop never runs — a listener that accepts nothing and reports no fault. That is a silent
        no-op start, not a forgotten resource; it is a different defect from the one this change
        fixes and is not fixed here.
      - **`NatsBridge.cs:236`** — `_ = Task.Run(() => ConsumeFromNatsAsync(filter, subOpts, stoppingToken), stoppingToken)`,
        once per resolved filter. The delegate holds nothing disposable either: `_subscriber` is a
        field assigned at `:224` and disposed by `StopAsync` (`:162-165`), and the subscription
        itself is created **inside** the delegate (`subscriber.SubscribeAsync(filter, …)`), so a
        work item that never starts creates nothing to forget. What a cancelled `stoppingToken`
        drops is the subscription for that filter — a bridge that is connected and consuming
        nothing. Same class as `:80`, and not fixed here.

      **Why the eight new sites are safe, stated once because the shape is identical.** Each is
      `Task.Run(async () => { … }, ct)` inside a provider's streaming iterator, and neither resource
      in scope belongs to the delegate: `ws` is a `using var ws = new ClientWebSocket()` in the
      enclosing method and `sessionCts`, where there is one, is a `using var` too, so the enclosing
      scope releases both whether the work item runs or not. The delegate's only obligation is to
      complete `channel.Writer` — and the consumer awaits `channel.Reader.ReadAllAsync(ct)` on the
      **same token**, so a delegate dropped because `ct` was already cancelled cannot strand that
      consumer: the `await foreach` raises `OperationCanceledException` at the same edge that
      skipped the producer. **That symmetry is the whole test, and it is exactly what
      `AcceptLoopAsync` lacked**: there the token gated the connection's only owner while nothing
      else could release it.

      **Group B, already correct (14):** `Ami/Connection/AmiConnection.cs:154,169,538`;
      `Ami/Transport/PipelineSocketConnection.cs:71,72,236,237`; `Ari/Client/AriClient.cs:155`;
      `Ari/Audio/WebSocketAudioSession.cs:54`; `Ari/Audio/AudioSocketSession.cs:56,57`;
      `Ari/Outbound/AriOutboundListener.cs:99,149`; and this change's own
      `VoiceAi.AudioSocket/AudioSocketServer.cs:119`. The contrast the task names holds:
      `AriOutboundListener.cs:99` and `:149` both pass `CancellationToken.None`, and `:149` is now
      character-for-character identical to `:119` (task 3.1).

      **Group C, no token to skip on (5):** `Ami/Internal/AsyncEventPump.cs:48`;
      `Ari/Internal/AriEventPump.cs:47`; `Ami/Connection/AmiConnection.cs:597`;
      `VoiceAi.AudioSocket/AudioSocketSession.cs:69`; `Push/Bus/RxPushEventBus.cs:55`.

      **Nothing was changed. Nothing in group A needs a fix for *this* defect** — the two no-op-start
      sites (`AudioSocketServer.cs:80`, `NatsBridge.cs:236`) are a real but different defect and
      belong in their own change, not in this PR's description.

      ---

      **The key-only `TryRemove`, and a correction to this task's own pointer.** The task names
      `AudioSocketServer.cs:199`. Post-fix that line is `client.Dispose()` in the no-UUID branch: the
      eleven-line comment task 2.1 added moved everything below it down, so `:199` was the **pre-fix**
      number. The construct is at **`:210`**, inside the `session.OnHangup` closure (`:208-214`) in
      `HandleConnectionAsync` — the member this change reads, as the task says. Keyed to the
      construct, not the line, for the reason task 3.2 gives.

      ```csharp
      session.OnHangup += () =>
      {
          _sessions.TryRemove(channelId, out _);   // :210 — removes whatever is under the key
      ```

      **The defect.** `_sessions` is keyed by the `channelId` from the identifying frame, which a
      later connection can present again. *Registration* is already guarded — `:216` is
      `if (_sessions.Count >= … || !_sessions.TryAdd(channelId, session))`, and the connection that
      loses that race disposes its own session and returns without removing anything. *Unregistration*
      is not: the key-only overload removes whatever is currently under `channelId`, matching no
      value. So a hangup belonging to session A that fires after A's entry has been replaced by a
      session B under the same channel id unregisters **B**. B keeps running and keeps its
      connection, but `StopAsync` — which disposes `_sessions.Values` (`:92-93`) — never sees it, and
      `ActiveSessionCount` under-reports it. That is this change's own shape, "a live resource with
      no owner", one layer up.

      **The fix is already written down in this repo**, which is why this is worth an open change and
      not an investigation: `src/Verbara.Sdk.Ari/Audio/WebSocketAudioServer.cs:178` uses the
      value-matching pair overload, with a comment that states the rule —

      ```csharp
      // Each connection cleans up its own session and no other. The pair overload removes
      // the entry only while it still maps to this session, so a connection that lost the
      // TryAdd race for a channel id leaves the session that won it registered.
      _streams.TryRemove(new KeyValuePair<string, WebSocketAudioSession>(session.ChannelId, session));
      ```

      Two siblings still use the key-only form on a **reusable** key and should move together:
      `src/Verbara.Sdk.Ari/Audio/AudioSocketServer.cs:147` (`_streams.TryRemove(session.ChannelId, out _)`
      — the site the ARI change records) and `VoiceAi.AudioSocket/AudioSocketServer.cs:210` (this one).

      **The rule is not "never use the key-only overload."** `src/Verbara.Sdk.Ari/Outbound/AriOutboundListener.cs:290`
      removes by `tracked.Id`, and `_connections` is a `ConcurrentDictionary<Guid, TrackedConnection>`
      (`:58`) whose key is minted per connection and never reused — key-only is correct there. The
      rule is: **never use it on a key a later value can reuse.** Recorded, not fixed.
- [x] 2.3 Record the follow-up this change deliberately does not do: `StopAsync` does not wait for
      in-flight handlers, so a connection accepted at the last moment is closed shortly after
      `StopAsync` returns rather than before it. Ordering it would mean tracking every handler task,
      as the sibling listener does for its accept loop. Write it into an open change or leave it here
      with the reason — not only in the PR prose.

      **Recorded here, with the reason, rather than opened as a change.**

      **The gap.** `StopAsync` (`src/Verbara.Sdk.VoiceAi.AudioSocket/AudioSocketServer.cs:85-97`)
      cancels `_cts`, stops the listener, disposes the **registered** sessions and logs
      `ServerStopped`. It tracks no handler tasks — the hand-off at `:119` is `_ = Task.Run(…)`, the
      discard being the point — so a connection accepted in the same moment the server was asked to
      stop is closed by its handler **shortly after `StopAsync` has already returned**, not before
      it. Closing it at all is this change; ordering that close against the shutdown's return is not.

      **What the gap costs today is a window, not a leak.** After this change the connection *is*
      closed, by the handler, through the no-UUID branch at `:199` — nothing is left to finalization,
      which was the whole defect. What stays unordered is only *when*: `ServerStopped` can be logged
      before the last connection's close lands. `ActiveSessionCount` is unaffected, because such a
      connection never joins `_sessions`.

      **Why it is not done here.** Ordering it means tracking every handler task and awaiting them in
      `StopAsync`, the way `AriOutboundListener` awaits its `_acceptLoop`. That is a different change
      with a different risk profile:

      - it adds a collection with its own lifetime — entries have to be removed as handlers finish,
        or the set grows for the life of the server, which is a new leak in place of the old one;
      - it needs a **bound**, because an unbounded await in `StopAsync` lets one stuck handler hold
        the host's shutdown open past its own timeout — and choosing that bound is a policy call this
        change has no evidence for;
      - it touches the **normal** path, where this change touches only the stopping one, so it cannot
        inherit this proposal's `Architectural Risk: LOW`.

      **The trigger for opening it**, if one is wanted: a host that needs shutdown to be observably
      complete — i.e. that treats `ServerStopped` as meaning every connection is already closed.
      Nothing reports that symptom today, which is why this is a recorded gap and not a backlog item.

## 3. Decision record

- [x] 3.1 Land `docs/decisions/0058-a-stopping-token-never-gates-a-handoff.md` (`Sdk/ADR-0058`) in this
      change, and add its row to `docs/decisions/README.md`. One page: the rule (a token that stops a
      server never gates a hand-off carrying ownership of a resource the server already acquired — it
      is passed *into* the handler instead), the two rejected alternatives (checking the token in the
      loop and disposing there; waiting for handlers in `StopAsync`), the consequence (one owner per
      accepted connection, matching ADR-0053's rule for a session's transport, extended to the
      connection that has not become a session yet), and the two sites that now agree
      (`AudioSocketServer`, `AriOutboundListener`). Reference `Sdk/ADR-0053`, which this change's
      `decision_ref` cites and which ADR-0058 extends.

      **Landed** as `docs/decisions/0058-a-stopping-token-never-gates-a-handoff.md`, Accepted,
      132 lines — the same page-and-a-bit as ADR-0052/0053/0054 (131/143/126). Four rules: the
      hand-off is unconditional (R1), `Task.Run`'s own token is an explicit `CancellationToken.None`
      (R2), the stopping token still reaches the handler as an argument (R3), and one owner per
      accepted connection (R4, ADR-0053's rule extended to a connection that has not become a
      session). Both rejected alternatives are recorded with their reasons, plus a third — "leave the
      token and rely on the accept's own cancellation" — which is the status quo argued from the
      wrong end. Its index row went at the foot of the `## Catalog` in `docs/decisions/README.md`,
      after ADR-0055 and in numeric order.

      **Two consequences measured during this apply, neither of which 3.1 as written knew about, are
      in the ADR** (both from 4.3):

      - **The "dispose in the accept loop" alternative is refused by the compiler, not merely
        unproven.** `IDISP016` fails the build on that shape under `TreatWarningsAsErrors`; only a
        `#pragma warning disable IDISP016` makes it compile, and with the pragma the suite is 106/106.
        So R4 already has a mechanical guard in this repo — recorded as a *consequence*, explicitly
        not as a claim that `IDisposableAnalyzers` was chosen for this rule, and bounded: the guard
        holds only for the shape it recognises. The ADR also records that the guard does **not**
        backstop the sibling mistake — handing the handler a foreign token compiles cleanly, because
        `CA2016` accepts an explicit `None` on the inner call too, so R3 is carried by the tests
        alone (mutation (c) fails 1.1 *and* 1.2). This is stronger than proposal "What Changes"
        item 3, which rejects the alternative on the overload's contract and the sibling listener only.
      - **The hand-off names `CancellationToken.None` explicitly because `CA2016` is an error here**,
        and the upshot beats the proposal's prediction: the two accept loops are now **identical
        character for character** on that line, not merely agreeing in spirit. Verified —
        `AudioSocketServer.cs` and `AriOutboundListener.cs:149` both read
        `_ = Task.Run(() => HandleConnectionAsync(client, ct), CancellationToken.None);` at the same
        16-space indent, md5 `b8ae767084393cfdb37f00805881ae1d` on both lines.

- [x] 3.2 Land the ADR-count guard's other two edits **in this same PR**:
      `StatusBlockCoherenceTests.ThePublishedAdrCount_ShouldMatchTheDecisionsOnDisk` (#279) counts
      `docs/decisions/*.md` against the figure `README.md` publishes, and ADR-0042 D1 requires a
      changed figure's registry row to move with it.
      - bump the `**N ADRs**` figure in `README.md`;
      - update its row in `docs/claim-registry.md`.
      Key both edits to the **figure**, never to a line number: #280 has just moved this claim from
      `README.md:74` to `:67` and re-based the registry's line pointers with it, so a number recorded
      here goes stale on the next docs PR. Adding ADR-0058's file without these two fails the `Unit Tests` job.

      **Both edits landed, keyed to the figure and not to a line number.** The count was re-counted
      rather than trusted: `docs/decisions/*.md` excluding the catalog `README.md` held **53** files
      and the catalog held 53 rows before this change, and **54** of each after ADR-0058.

      - `README.md`: `**53 ADRs**` → `**54 ADRs**` (still line 67 — the figure is edited in place, so
        #280's re-based pointers do not move again).
      - `docs/claim-registry.md`, the same row before and after:

        ```text
        | 67 | **53 ADRs** | ENFORCING | `StatusBlockCoherenceTests` — counts `docs/decisions/*.md`, excluding the catalog `README.md` | **OK** |
        | 67 | **54 ADRs** | ENFORCING | `StatusBlockCoherenceTests` — counts `docs/decisions/*.md`, excluding the catalog `README.md` | **OK** |
        ```

        Only the figure moved; the class, the guard and the verdict are unchanged, and the row stays
        **OK** because the guard was re-run against it. The registry's dated prose at the foot of the
        file still says "53 files" — that is a record of the 2026-09-20 verification, not the live
        figure, and that history is left alone.

      **Proven green**, `dotnet test Tests/Verbara.Sdk.OpenTelemetry.Tests/ --filter
      "FullyQualifiedName~StatusBlockCoherenceTests"`:

      ```text
      Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 10 ms - Verbara.Sdk.OpenTelemetry.Tests.dll (net10.0)
      ```

      Both cases ran: `ThePublishedAdrCount_ShouldMatchTheDecisionsOnDisk` (54 published, 54 on disk)
      and its neighbour `TheHeadlineVersion_ShouldMatchTheVersionThePackagesShipWith`, which this
      change does not touch and which stays green.

- [x] 3.3 Guard the catalog itself, which nothing does today. `StatusBlockCoherenceTests` counts the
      *files* in `docs/decisions/` against the README figure and excludes the catalog by name
      (`:44`), so an ADR that lands without `docs/decisions/README.md` gaining its row passes green —
      which is how this change's three siblings each carried that step and `session-shutdown` did
      not. Add `TheDecisionCatalog_ShouldListEveryAdrOnDisk` beside the existing cases in
      `Tests/Verbara.Sdk.OpenTelemetry.Tests/StatusBlockCoherenceTests.cs`: assert **set equality**
      between the four-digit ids parsed from `docs/decisions/*.md` (excluding `README.md`) and the
      ids linked from the catalog — never a count, because a count passes when one id is listed
      twice and another is omitted. No new test project, no new job and no new check-run name
      (ADR-0042 D3); it runs on the existing `Unit Tests` job like its neighbours. Verified in sync
      on 2026-09-21 at 53 files and 53 rows, so it must pass on its first run: a red first run means
      the catalog drifted after that date and the drift is the finding, not the test.
      If another of the four sibling changes lands before this one, move this task into that change
      instead — the guard is worth most on the first of them to reach `main`.

      **Added** `TheDecisionCatalog_ShouldListEveryAdrOnDisk` beside the two existing cases in
      `Tests/Verbara.Sdk.OpenTelemetry.Tests/StatusBlockCoherenceTests.cs`. It builds two sets of
      four-digit ids and asserts they are the same set: the filenames in `docs/decisions/*.md`,
      excluding the catalog `README.md` by name exactly as its neighbour does, against the catalog's
      link **targets** — `\]\((?<id>\d{4})-[^)]*\.md\)`, so a row's prose mention of "ADR-0053" is
      not a row for ADR-0053 and the `ADR-NNNN` in the file's own Template section is not an id.
      No new test project, no new job, no new check-run name: it rides the existing `Unit Tests`
      job (ADR-0042 D3).

      The comparison reports **both directions in one failure**, so a run never sends the reader
      hunting: each drifted id becomes a sentence naming which side it is on.

      ```csharp
      var drift = onDisk.Except(linked).Order(StringComparer.Ordinal)
              .Select(id => $"ADR-{id} is a file in docs/decisions/ that the catalog does not list")
          .Concat(linked.Except(onDisk).Order(StringComparer.Ordinal)
              .Select(id => $"ADR-{id} is listed in the catalog with no file behind it"))
          .ToList();
      ```

      Two guards keep the set comparison from passing vacuously: `files.Should().NotBeEmpty(...)`
      (a broken repo-root walk would otherwise compare nothing against nothing) and
      `files.Should().OnlyContain(name => Regex.IsMatch(name, @"^\d{4}-"), ...)` (a file named off
      the `NNNN-kebab-title.md` convention would drop out of the comparison unseen instead of
      failing it).

      **Green on the first run**, no edit to any ADR, the catalog, the README or the registry —
      53 files and 53 rows when this task was written, plus this change's own ADR-0058 from 3.1,
      is 54 and 54 today:

      ```text
        Passed …StatusBlockCoherenceTests.TheHeadlineVersion_ShouldMatchTheVersionThePackagesShipWith [8 ms]
        Passed …StatusBlockCoherenceTests.ThePublishedAdrCount_ShouldMatchTheDecisionsOnDisk [1 ms]
        Passed …StatusBlockCoherenceTests.TheDecisionCatalog_ShouldListEveryAdrOnDisk [6 ms]
      Total tests: 3
      ```

      **Seen red twice before being believed** (the standard 4.3 holds every other assertion in this
      change to). Each mutation was applied to `docs/decisions/README.md` alone, run, then restored
      from a byte-identical copy; only the machine-path prefix is replaced by `<repo>` below and the
      FluentAssertions-internal stack frames are elided.

      **(i) one row deleted** — the `- [ADR-0044]` row removed, 53 rows against 54 files. The test
      fails and **names the id**:

      ```text
      [xUnit.net 00:00:00.14]     …StatusBlockCoherenceTests.TheDecisionCatalog_ShouldListEveryAdrOnDisk [FAIL]
        Failed …StatusBlockCoherenceTests.TheDecisionCatalog_ShouldListEveryAdrOnDisk [34 ms]
        Error Message:
         Expected drift to be empty because the catalog in docs/decisions/README.md must name exactly
         the ADRs on disk — an ADR lands with its row in the same pull request, and a superseded one
         keeps both, but found at least one item {"ADR-0044 is a file in docs/decisions/ that the
         catalog does not list"}.
        Stack Trace:
           at …StatusBlockCoherenceTests.TheDecisionCatalog_ShouldListEveryAdrOnDisk() in <repo>/Tests/Verbara.Sdk.OpenTelemetry.Tests/StatusBlockCoherenceTests.cs:line 101

      Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 34 ms - Verbara.Sdk.OpenTelemetry.Tests.dll (net10.0)
      ```

      **(ii) the case a count waves through** — the `- [ADR-0044]` row deleted *and* the `- [ADR-0045]`
      row duplicated, so the catalog still holds **54 rows against 54 files** while one id is listed
      twice and another is missing. This is the whole reason the task forbids a count, and the run
      shows both halves at once: **`Passed: 2`** — the neighbour
      `ThePublishedAdrCount_ShouldMatchTheDecisionsOnDisk` is green on this catalog, exactly as
      predicted — while set equality fails and names the omitted id:

      ```text
        Error Message:
         Expected drift to be empty because the catalog in docs/decisions/README.md must name exactly
         the ADRs on disk — an ADR lands with its row in the same pull request, and a superseded one
         keeps both, but found at least one item {"ADR-0044 is a file in docs/decisions/ that the
         catalog does not list"}.
        Stack Trace:
           at …StatusBlockCoherenceTests.TheDecisionCatalog_ShouldListEveryAdrOnDisk() in <repo>/Tests/Verbara.Sdk.OpenTelemetry.Tests/StatusBlockCoherenceTests.cs:line 101

      Failed!  - Failed:     1, Passed:     2, Skipped:     0, Total:     3, Duration: 70 ms - Verbara.Sdk.OpenTelemetry.Tests.dll (net10.0)
      ```

      **Catalog restored and proven untouched.** `docs/decisions/README.md` is back at md5
      `969c6b9f1908aad892155cfe1957c15e`, the value measured before the first mutation, and
      `git diff --numstat -- docs/decisions/README.md` reads `1 0` — the single ADR-0058 row that
      task 3.1 added, and nothing else. `StatusBlockCoherenceTests` re-measures at 3/3.

      **Also green on the integrated branch:** `dotnet build Verbara.Sdk.slnx -c Release` →
      `Build succeeded. 0 Warning(s), 0 Error(s)`; the same three tests under `-c Release` →
      `Failed: 0, Passed: 3` (14 ms); `Verbara.Sdk.Governance.Tests` under the CI unit filter →
      `Failed: 0, Passed: 129` (the sync-fence ratchet included — this file gains no barrier);
      `bash tools/audit-test-asserts.sh --report` → `Files scanned: 444, [Fact]/[Theory]: ~2912,
      Violations: 0`.

- [x] 3.4 Repoint this change's `decision_ref` to `Sdk/ADR-0058` once that file exists

      **Done**, and only that frontmatter value, plus one correction written *beside* "What Changes"
      item 1 — the call it quotes is `CA2016` here, so it does not compile; the analyzer's message is
      under task 2.1. The correction sits next to the original rather than replacing it: the original
      is the intent the change was approved on, and the reason 2.1 had to deviate. Item 3's wording,
      which task 4.3 flagged as understated, stays as written — ADR-0058's catalog row already
      carries the `IDISP016` finding, and 4.3 leaves that ruling to the owner. One line, in the
      frontmatter:

      ```diff
      -decision_ref: Sdk/ADR-0053
      +decision_ref: Sdk/ADR-0058
      ```

      `docs/decisions/0058-a-stopping-token-never-gates-a-handoff.md` exists (task 3.1), so the
      reference resolves. ADR-0053 is not lost: ADR-0058 cites it as the decision it extends, and the
      proposal's Why section still reads the defect as "the ADR-0053 family one layer earlier".
- [ ] 3.5 At archive time, widen the `streaming-session-lifecycle` `## Purpose` so it covers
      the server accept path this requirement files under it — or, if the owner prefers, split the
      requirement into its own capability before archiving
## 4. Verification

- [x] 4.1 `dotnet build Verbara.Sdk.slnx -c Release`: 0 warnings, 0 errors
      (`TreatWarningsAsErrors`, `WarningLevel 9999`).

      ```text
      Build succeeded.
          0 Warning(s)
          0 Error(s)

      Time Elapsed 00:00:02.82
      ```

      Exit 0. Incremental over the tree task 4.3 restored, which is why it is 2.82 s against the
      14.87 s full build recorded in 2.1; the AOT and pack gates below both rebuild from this same
      tree and also report zero.
- [x] 4.2 Unit lane green under the CI filter
      (`Category!=Functional&Category!=Integration&Category!=Realtime&Category!=Spike`), with
      `Verbara.Sdk.Governance.Tests` included, and `tools/audit-test-asserts.sh` at `Violations: 0`.
      Record the per-assembly counts for `Verbara.Sdk.VoiceAi.AudioSocket.Tests` before and after.

      **The step list below is derived from `.github/workflows/ci.yml` on this branch, not from
      memory.** Every non-Docker step of every job in that file ran on the integrated tree. One
      required context, `Analyze (C#)`, is not in `ci.yml` at all — it lives in `codeql.yml` and needs
      the GitHub CodeQL runner, so it is out of reach locally and is not claimed below.

      | `ci.yml` job → step | result |
      |---|---|
      | **Docs-only gate** → `classify-docs-only.sh` | `docs_only=false`, exit 0 — **vacuous locally**, see caveat 2 |
      | **Unit Tests** → Build | `0 Warning(s), 0 Error(s)` (task 4.1) |
      | **Unit Tests** → unit tests + coverage | **30 assemblies · Total 3577 · Passed 3577 · Failed 0 · Skipped 0**, exit 0 |
      | **Coverage Ratchet** → `dotnet tool restore` + `reportgenerator` | exit 0, report merged |
      | **Coverage Ratchet** → `check-coverage-floor.py` | Line **83.59%** (band [83.0, 86.0]) · Branch **68.17%** (floor 64.0, blocking) · Lines measured **13342** (min 12315) → `Coverage band OK.` |
      | **Coverage Ratchet** → `check-patch-coverage.py` | `n/a, pass` — **vacuous locally**, see caveat 1 |
      | **Coverage Ratchet** → `check-exclusion-baseline.py` | markers **0** (baseline 0, 865 files under `src/**/*.cs`) → `Exclusion baseline OK.` |
      | **Coverage Script Tests** → `python3 -m unittest discover scripts/tests` | **Ran 257 tests … OK** |
      | **Coverage Script Tests** → `test_classify_docs_only.sh` | exit 0, `OK` |
      | **Coverage Script Tests** → `test_release_hygiene.sh` | exit 0, `OK` |
      | **Coverage Script Tests** → `test_release_provenance.sh` | exit 0, `OK` |
      | **Coverage Script Tests** → `test_report_perf_breach.sh` | exit 0, **27 passed, 0 failed** |
      | **Coverage Script Tests** → `test_package_validation_coverage.sh` | exit 0, `OK` |
      | **Coverage Script Tests** → `test_filter_codeql_sarif.sh` | exit 0, `OK` |
      | **AOT Trim Check** → `tools/verify-aot.sh` | `AOT Canary — all SDK types are trim-safe` · **0 trim warnings** (RID=linux-x64) |
      | **Pack Warnings Gate** → Build Release | `0 Warning(s), 0 Error(s)` |
      | **Pack Warnings Gate** → `dotnet pack … -p:TreatWarningsAsErrors=true` | **29 `.nupkg` at 2.5.3, 0 warnings**, exit 0 |
      | **Pack Warnings Gate** → `PKV_MSBUILD_CASES=1 test_package_validation_coverage.sh` | **passed=120, failed=0** |
      | **Pack Warnings Gate** → `check-package-validation-coverage.sh` | `all 29 shipped project(s) are validated against 2.5.3.` |
      | **Audit Test Asserts** → `tools/audit-test-asserts.sh` | **Files scanned 444 · [Fact]/[Theory] ~2912 · Violations: 0** |
      | **Audit Test Asserts** → `check-recording-redaction.py .` | exit 0 |
      | **OpenSpec Validate** → `openspec 1.13.1 validate --all --strict` | **13 passed, 0 failed** (task 4.5) |
      | **Functional Tests (Testcontainers)** | **SKIPPED — needs Docker** (Asterisk image build + postgres/toxiproxy/sipp containers). The only skipped job. |

      **The tree-scanning guards specifically, because "the tests of the projects I touched" would
      have missed every one of them:** `Verbara.Sdk.Governance.Tests` **129/129** (the sync-fence
      ratchet is 19 of those, re-run alone: `Passed: 19, Failed: 0` — this change adds no barrier and
      raises no baseline, confirming task 1.2's choice of `ParkUntilCancelledAsync` over
      `Task.Delay(Timeout.InfiniteTimeSpan, token)`); the assert audit at **Violations: 0**; the
      redaction scan clean; and `Verbara.Sdk.OpenTelemetry.Tests` **31/31**, which carries the three
      `StatusBlockCoherenceTests` cases that tasks 3.2/3.3 landed against the 54-ADR figure.

      **`Verbara.Sdk.VoiceAi.AudioSocket.Tests`, before and after:**

      | | tests | passed | failed |
      |---|---|---|---|
      | before this change (task 1.2's baseline) | 104 | 104 | 0 |
      | after 1.1 + 1.2, before the fix (task 1.3) | 106 | 105 | **1** — the regression test |
      | after the fix, now | **106** | **106** | **0** |

      Re-measured alone on this tree:
      `Passed!  - Failed: 0, Passed: 106, Skipped: 0, Total: 106, Duration: 2 s - Verbara.Sdk.VoiceAi.AudioSocket.Tests.dll (net10.0)`.
      The two tests this change adds are the only movement, and no neighbour reacted.

      ---

      **Two local verdicts are vacuous, and both for the same reason: the branch has zero commits
      ahead of `origin/main`** — `git log --oneline origin/main..HEAD` is empty and the whole change
      is uncommitted working tree. Both steps read *committed* history, so neither saw the change.
      Neither is a failure and neither needs a code change; both are noted so nobody reads them as
      green evidence they are not.

      1. **`check-patch-coverage.py` measured nothing.** It diffs `merge_base...HEAD` (`:260`,
         `:383`), which here is empty, so it printed
         `Patch coverage: no measurable cobertura lines in this diff (no instrumented line added). floor 85.0% — n/a, pass.`
         **Substituted directly instead:** the only *instrumented* line this change adds or alters
         under `src/` is the hand-off at `AudioSocketServer.cs:119` — the ten-line comment above it is
         not instrumented, and both test files are outside the denominator (`coverlet.runsettings`
         excludes test assemblies). The merged Cobertura report gives that line **106 hits**. So the
         gate's real input on the committed branch is one line at 100%, against a floor of 85%.
      2. **`classify-docs-only.sh` was handed `BASE == HEAD`.** It returned `docs_only=false` — it
         fails **closed** on an empty diff, which is the safe direction (heavy jobs run). On the
         committed branch it will see `src/` and `Tests/` and return `false` for the real reason.
- [x] 4.3 Mutations, each applied alone to the fixed tree, built, run against
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

      **Baseline re-measured first, on the fixed tree and before any mutation.**
      `dotnet build Verbara.Sdk.slnx -c Release` → `Build succeeded. 0 Warning(s), 0 Error(s)`;
      `dotnet test Tests/Verbara.Sdk.VoiceAi.AudioSocket.Tests/ -c Release --filter
      "Category!=Functional&Category!=Integration&Category!=Realtime&Category!=Spike"` →
      `Failed: 0, Passed: 106, Skipped: 0, Total: 106, Duration: 2 s`. Each mutation below was applied
      **alone** to that tree, built and run under that same command, then restored from a byte-identical
      copy of `AudioSocketServer.cs` (md5 `f944bb7e3b9db187631a4b31d6bb79e6`) before the next one was
      applied; the restored tree re-measures at 106/106 and the solution at 0 warnings, 0 errors. Only
      the machine-path prefix is replaced by `<repo>` in the failures below — nothing under `openspec/`
      carries an absolute path.

      **No mutation survived.** Two of the three were caught more widely than predicted, and the
      fourth did not even compile.

      | mutation | outcome | caught by |
      |---|---|---|
      | (a) `, ct` back on `Task.Run` | `Failed: 1, Passed: 105` | 1.1 only — exactly as predicted |
      | (b) `client.Dispose()` deleted from the no-UUID branch | `Failed: 4, Passed: 102` | 1.1, 1.2 **and** both existing handler tests — one wider than predicted |
      | (c) handler given `CancellationToken.None` | `Failed: 2, Passed: 104` | 1.1 **and** 1.2 — one wider than predicted |
      | (d) dispose in the loop when `ct` is cancelled | **does not compile** | `IDISP016` (IDisposableAnalyzers), as an error |

      **(a) restore `, ct` on the hand-off** — `_ = Task.Run(() => HandleConnectionAsync(client, ct), ct);`.
      Compiles (CA2016 is satisfied by forwarding `ct`, which is the analyzer's first remedy). One test
      fails, on the `SignalTimeout` bound of the peer's read, and the run takes 12 s instead of 2 s:

      ```text
      [xUnit.net 00:00:11.87]     Verbara.Sdk.VoiceAi.AudioSocket.Tests.AudioSocketServerEdgeCaseTests.AcceptLoopAsync_ShouldCloseTheAcceptedConnection_WhenTheServerStopsBeforeTheHandoffRuns [FAIL]
        Failed Verbara.Sdk.VoiceAi.AudioSocket.Tests.AudioSocketServerEdgeCaseTests.AcceptLoopAsync_ShouldCloseTheAcceptedConnection_WhenTheServerStopsBeforeTheHandoffRuns [10 s]
        Error Message:
         System.TimeoutException : The operation has timed out.
        Stack Trace:
           at …AcceptLoopAsync_ShouldCloseTheAcceptedConnection_WhenTheServerStopsBeforeTheHandoffRuns() in <repo>/Tests/Verbara.Sdk.VoiceAi.AudioSocket.Tests/AudioSocketServerEdgeCaseTests.cs:line 159
         at …AcceptLoopAsync_ShouldCloseTheAcceptedConnection_WhenTheServerStopsBeforeTheHandoffRuns() in <repo>/Tests/Verbara.Sdk.VoiceAi.AudioSocket.Tests/AudioSocketServerEdgeCaseTests.cs:line 165

      Failed!  - Failed:     1, Passed:   105, Skipped:     0, Total:   106, Duration: 12 s - Verbara.Sdk.VoiceAi.AudioSocket.Tests.dll (net10.0)
      ```

      This is byte-for-byte the 1.1 failure recorded above against the unfixed server, which is the
      point: the argument *is* the defect, and nothing else in the file is carrying the fix.

      **(b) delete `client.Dispose()` from the no-identifying-frame branch.** Compiles — `client` is a
      parameter, so no IDISP rule requires the method to dispose it. **Four** tests fail, all on the
      `SignalTimeout` bound of a peer read that never reaches end of stream, and the run takes 42 s:

      ```text
      [xUnit.net 00:00:11.83]     …AudioSocketServerEdgeCaseTests.HandleConnectionAsync_ShouldLogOnlyNoUuidFrameWarning_WhenNoUuidArrivesWithinTimeout [FAIL]
        Failed …HandleConnectionAsync_ShouldLogOnlyNoUuidFrameWarning_WhenNoUuidArrivesWithinTimeout [10 s]
        Error Message:
         System.TimeoutException : The operation has timed out.
        Stack Trace:
           at …HandleConnectionAsync_ShouldLogOnlyNoUuidFrameWarning_WhenNoUuidArrivesWithinTimeout() in <repo>/Tests/Verbara.Sdk.VoiceAi.AudioSocket.Tests/AudioSocketServerEdgeCaseTests.cs:line 86
      [xUnit.net 00:00:21.83]     …AudioSocketServerEdgeCaseTests.HandleConnectionAsync_ShouldCloseWithoutWarningOrError_WhenServerStopsBeforeUuidArrives [FAIL]
        Failed …HandleConnectionAsync_ShouldCloseWithoutWarningOrError_WhenServerStopsBeforeUuidArrives [10 s]
        Error Message:
         System.TimeoutException : The operation has timed out.
        Stack Trace:
           at …HandleConnectionAsync_ShouldCloseWithoutWarningOrError_WhenServerStopsBeforeUuidArrives() in <repo>/Tests/Verbara.Sdk.VoiceAi.AudioSocket.Tests/AudioSocketServerEdgeCaseTests.cs:line 121
      [xUnit.net 00:00:31.83]     …AudioSocketServerEdgeCaseTests.AcceptLoopAsync_ShouldCloseTheAcceptedConnection_WhenTheServerStopsBeforeTheHandoffRuns [FAIL]
        Failed …AcceptLoopAsync_ShouldCloseTheAcceptedConnection_WhenTheServerStopsBeforeTheHandoffRuns [10 s]
        Error Message:
         System.TimeoutException : The operation has timed out.
        Stack Trace:
           at …AcceptLoopAsync_ShouldCloseTheAcceptedConnection_WhenTheServerStopsBeforeTheHandoffRuns() in <repo>/Tests/Verbara.Sdk.VoiceAi.AudioSocket.Tests/AudioSocketServerEdgeCaseTests.cs:line 159
      [xUnit.net 00:00:42.44]     …AudioSocketServerEdgeCaseTests.AcceptLoopAsync_ShouldLeaveTheConnectionOpen_WhileTheHandlerIsServingIt [FAIL]
        Failed …AcceptLoopAsync_ShouldLeaveTheConnectionOpen_WhileTheHandlerIsServingIt [10 s]
        Error Message:
         System.TimeoutException : The operation has timed out.
        Stack Trace:
           at …AcceptLoopAsync_ShouldLeaveTheConnectionOpen_WhileTheHandlerIsServingIt() in <repo>/Tests/Verbara.Sdk.VoiceAi.AudioSocket.Tests/AudioSocketServerEdgeCaseTests.cs:line 211

      Failed!  - Failed:     4, Passed:   102, Skipped:     0, Total:   106, Duration: 42 s - Verbara.Sdk.VoiceAi.AudioSocket.Tests.dll (net10.0)
      ```

      The prediction named three (1.1 plus the two existing handler tests). The fourth is **1.2**, the
      control added in this change: its second half cancels the token and then asserts the peer read
      reaching end of stream, so it binds the handler's dispose as well as the hand-off. That is the
      branch's coverage read exactly — `:199` is the single close for a connection that never became a
      session, and deleting it takes every test that watches a connection die without a UUID.

      **(c) hand over but pass the handler `CancellationToken.None`** —
      `_ = Task.Run(() => HandleConnectionAsync(client, CancellationToken.None), CancellationToken.None);`.
      **It compiles**: CA2016 accepts an explicit `CancellationToken.None` on the inner call as
      "intentionally not propagating the token", the same remedy it accepts on `Task.Run` itself. So the
      build does not catch this one and the tests have to. Two do, both on the `SignalTimeout` bound:

      ```text
      [xUnit.net 00:00:11.83]     …AudioSocketServerEdgeCaseTests.AcceptLoopAsync_ShouldCloseTheAcceptedConnection_WhenTheServerStopsBeforeTheHandoffRuns [FAIL]
        Failed …AcceptLoopAsync_ShouldCloseTheAcceptedConnection_WhenTheServerStopsBeforeTheHandoffRuns [10 s]
        Error Message:
         System.TimeoutException : The operation has timed out.
        Stack Trace:
           at …AcceptLoopAsync_ShouldCloseTheAcceptedConnection_WhenTheServerStopsBeforeTheHandoffRuns() in <repo>/Tests/Verbara.Sdk.VoiceAi.AudioSocket.Tests/AudioSocketServerEdgeCaseTests.cs:line 159
      [xUnit.net 00:00:22.44]     …AudioSocketServerEdgeCaseTests.AcceptLoopAsync_ShouldLeaveTheConnectionOpen_WhileTheHandlerIsServingIt [FAIL]
        Failed …AcceptLoopAsync_ShouldLeaveTheConnectionOpen_WhileTheHandlerIsServingIt [10 s]
        Error Message:
         System.TimeoutException : The operation has timed out.
        Stack Trace:
           at …AcceptLoopAsync_ShouldLeaveTheConnectionOpen_WhileTheHandlerIsServingIt() in <repo>/Tests/Verbara.Sdk.VoiceAi.AudioSocket.Tests/AudioSocketServerEdgeCaseTests.cs:line 211

      Failed!  - Failed:     2, Passed:   104, Skipped:     0, Total:   106, Duration: 22 s - Verbara.Sdk.VoiceAi.AudioSocket.Tests.dll (net10.0)
      ```

      1.1 fails for the predicted reason — the handler's only remaining deadline is `ConnectionTimeout`
      on a clock the test never advances. 1.2 fails for a different and sharper one: the token it cancels
      to end the handler's wait no longer reaches the handler at all, so the connection it is serving is
      never closed. Together they pin `ct` as an argument, not just the hand-off as unconditional.

      **(d) dispose in the accept loop instead of handing over — caught by the compiler, not by a test.**
      Applied as the proposal's rejected alternative, `if (ct.IsCancellationRequested) { client.Dispose();
      break; }` above the hand-off, the build fails:

      ```text
      <repo>/src/Verbara.Sdk.VoiceAi.AudioSocket/AudioSocketServer.cs(121,21): error IDISP016: Don't use disposed instance (https://github.com/DotNetAnalyzers/IDisposableAnalyzers/blob/master/documentation/IDISP016.md) [<repo>/src/Verbara.Sdk.VoiceAi.AudioSocket/Verbara.Sdk.VoiceAi.AudioSocket.csproj]

      Build FAILED.
          0 Warning(s)
          1 Error(s)
      ```

      **This is new information and it is in the change's favour, so the proposal's wording should follow
      it.** "What Changes" item 3 rejects this alternative on the overload's documented contract and on
      the sibling listener, and says no deterministic test separates it. The second half holds; the first
      is now understated. IDisposableAnalyzers refuses the *shape* — a second dispose site for a
      connection whose reference also escapes to the handler is exactly what IDISP016 flags — so the
      "one owner per accepted connection" rule this change is about is already enforced mechanically in
      this repo, at build time, under `TreatWarningsAsErrors`. That belongs in ADR-0058 (task 3.1) as a
      consequence: the rule has a guard, and the guard is the analyzer.

      **And the rejection's own claim is confirmed at the test level.** With `#pragma warning disable
      IDISP016` around that dispose — the one way the shape can be made to build — the suite is
      `Failed: 0, Passed: 106, Skipped: 0, Total: 106, Duration: 2 s`: **no test separates it from the
      fix**, exactly as the proposal says. The rejection therefore stands on the overload's contract, on
      the sibling listener and now on IDISP016; it does not stand on a test, and this change does not
      claim it does.

      **One further probe, beyond the four the task names**, because 1.1's "nothing at Warning or above"
      assertion would be decorative if nothing bound it: removing the `if (!ct.IsCancellationRequested)`
      guard on `AudioSocketLog.NoUuidFrame` at `:196` fails **2** tests —
      `HandleConnectionAsync_ShouldCloseWithoutWarningOrError_WhenServerStopsBeforeUuidArrives` [33 ms]
      and `AcceptLoopAsync_ShouldCloseTheAcceptedConnection_WhenTheServerStopsBeforeTheHandoffRuns`
      [2 ms] — and unlike every mutation above it fails on an **assertion**, not on a bound:

      ```text
         Expected logger.Entries {…EventName = "NoUuidFrame", Level = LogLevel.Warning {value: 3}…} to not
         have any items matching (Convert(entry.Level, Int32) >= 3) because a connection closed because the
         server was already stopping missed no deadline and failed in no way, but found {…}.
           at …AcceptLoopAsync_ShouldCloseTheAcceptedConnection_WhenTheServerStopsBeforeTheHandoffRuns() in <repo>/Tests/Verbara.Sdk.VoiceAi.AudioSocket.Tests/AudioSocketServerEdgeCaseTests.cs:line 165

      Failed!  - Failed:     2, Passed:   104, Skipped:     0, Total:   106, Duration: 2 s
      ```

      So the "closed **quietly**" half of the spec's second scenario is bound by a real assertion, and the
      quiet close is not riding on the same timeout bound as everything else.

      **Mitigation claim (proposal → Architectural Risk).** It holds, and understates itself on two of
      three counts: restoring the token fails 1.1 (as claimed); deleting the handler's dispose fails the
      regression test and the two existing handler tests (as claimed) **and the control**; passing the
      handler a foreign token is caught by the regression test's bound (as claimed) **and by the
      control, on a different mechanism**. The one sentence to revisit is in "What Changes" item 3, not
      in Mitigation: the rejected alternative is refused by IDISP016 before any test runs.

      **Tree restored.** `AudioSocketServer.cs` is back at md5 `f944bb7e3b9db187631a4b31d6bb79e6`, both
      test files are untouched (`b3caf45d0ea79e12f2ae4a273b2fcbc9` and
      `81c4b4716434bf1db00bcb668e9b6fa2`, unchanged across the whole exercise), and the restored tree
      re-measures at `Failed: 0, Passed: 106` and `0 Warning(s), 0 Error(s)`.
- [x] 4.4 The two new tests pass 20 runs in a row against a Release build, with the per-run duration
      recorded — nothing here may end on `SignalTimeout`, which is a failure bound and never a pace.

      **20 runs, 20 passes, no flake.** Each run is
      `dotnet test Tests/Verbara.Sdk.VoiceAi.AudioSocket.Tests --no-build -c Release --filter "…ShouldCloseTheAcceptedConnection…|…ShouldLeaveTheConnectionOpen…" --logger "console;verbosity=detailed"`,
      against the Release build from 4.1, run alone so nothing else contended for the loopback
      sockets or the CPU. Every run reported `Total tests: 2, Passed: 2` and exit 0 — the filter is
      proven to match both tests on every run, not to have silently matched none.

      | run | 1.1 `…ShouldCloseTheAcceptedConnection…` | 1.2 `…ShouldLeaveTheConnectionOpen…` |
      |---|---|---|
      | 1–5 | 15, 17, 17, 16, 15 ms | 3, 4, 3, 3, 3 ms |
      | 6–10 | 17, 16, 16, 16, 15 ms | 3, 4, 3, 3, 3 ms |
      | 11–15 | 17, 15, 14, 16, 15 ms | 4, 3, 4, 3, 3 ms |
      | 16–20 | 16, 15, 16, 16, 15 ms | 3, 3, 4, 4, 3 ms |

      **Ranges: 1.1 is 14–17 ms (spread 3 ms); 1.2 is 3–4 ms (spread 1 ms).** `SignalTimeout` is
      10 s, so the slowest run of the regression test uses **0.17%** of its failure bound and the
      control **0.04%** — three orders of magnitude of headroom, on a distribution too tight to be
      waiting on anything. That is the point of the measurement: both tests are ordered by
      construction (a cancelled token returned in the same call; a `TaskCompletionSource` completed
      by the loop token's registration), so a duration anywhere near the bound would mean the
      ordering had failed and a wait had taken over. None did.

      **One number came out differently from the earlier records, and it is not a regression.**
      Tasks 1.2 and 2.1 clocked the control at 15–19 ms; here it is 3–4 ms. Both were measured inside
      a full 106-test project run, where the class's harness is shared with the other cases; run as a
      two-test filter it does less setup. 1.1's 14–17 ms matches 2.1's 14 ms exactly, so the harness,
      not the fix, is what moved.
- [x] 4.5 `openspec validate --all --strict` green.

      ```text
      Totals: 13 passed, 0 failed (13 items)
      ```

      Exit 0, on `openspec --version` **1.13.1** — the same version `ci.yml`'s `OpenSpec Validate`
      job pins via `npx -y @fission-ai/openspec@1.13.1`, so the local run is the CI gate and not an
      approximation of it. All 13 items pass, this change included; the only output besides the
      ticks is `[INFO]` notes that some requirement texts run over 500 characters, which are advisory
      and appear on the pre-existing specs too. `openspec/` carries no absolute machine path — checked
      across the whole tree, not just the files this change touched.
- [ ] 4.6 CI green through the merge queue. Enqueue it **alone**: the open changes that add a **new**
      ADR file (0046, 0047, 0056, 0057, 0058, 0059) all bump the same `**N ADRs**` figure, and the
      queue squashes. Whether git even sees the collision depends on where the two catalog rows land:
      rows inserted at the same spot conflict textually and the queue ejects the second before it
      builds; rows at different spots — the common case, since every one of those tasks adds its row
      *in numeric order* — merge cleanly and the second then fails `Unit Tests` inside the queue, a
      semantic conflict rather than a textual one. Either way the practice is the same: one
      ADR-adding change in the queue at a time, and re-count the figure after any rebase, because
      `strict:false` does not force one. Order does not otherwise matter — the guard counts files,
      not a contiguous sequence — so this change keeps ADR-0058 whenever it lands.

## 5. Close-out

- [x] 5.1 `CHANGELOG.md` `[Unreleased]` entry under `### Fixed`, stating that a connection accepted in
      the same moment the server was asked to stop was left open with no owner and is now closed, and
      that a connection whose socket had already failed can now be logged once as a connection error.
      Leave the `(#N)` citation for close-out. No `PackageVersion` bump here: publishing is the
      release train's job (`Sdk/ADR-0055`), and this change ships no public API.
- [ ] 5.2 `openspec archive audiosocket-accepted-connection-is-always-closed --yes` once the fix is on
      `main`, as its own `docs(openspec):` PR, with the feature PR's number backfilled into the
      CHANGELOG entry.
