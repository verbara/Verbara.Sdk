# Tasks: ari-failed-connect-and-silent-catches

## 1. Start from `main`, not from the old branch

- [ ] 1.1 Branch from current `main`. Do **not** rebase, cherry-pick or resurrect `fix/codeql-src`:
      it is eleven commits behind `main`, and its versions of `Audio/WebSocketAudioServer.cs` and
      `Client/AriClient.cs` predate the per-connection session cleanup (#256) and the reconnect
      rejection (#258) that `main` has since taken by another route. Re-derive every edit from the
      file as it stands on `main`. Record the head commit this change starts from.
- [ ] 1.2 Re-read the open alerts live before editing, and key every task below to **rule + file +
      member** — never to an alert number or a line number, both of which a merge renumbers. Confirm
      the set is still 18 `cs/empty-catch-block`, 2 `cs/missed-using-statement` and 2
      `cs/nested-if-statements`, all under `src/Verbara.Sdk.Ari`, and note any drift from that.

## 2. Reproduce the connect-state defect before fixing it

- [ ] 2.1 Write the regression tests first, against the unfixed client, and record their verbatim
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
- [ ] 2.2 Add the controls, which must pass before the fix and after it:
      - `ConnectAsync_ShouldLeaveStateConnected_WhenTheUpgradeSucceeds` — a listener answering `101`;
        `State` is `Connected` and `EventLoop` is running. A fix that wrote a terminal state on the
        success path fails here.
      - `State_ShouldBeInitial_WhenNewClientCreated` and `IsConnected_ShouldBeFalse_WhenNotConnected`
        already exist; confirm they stay green.
- [ ] 2.3 Pin that a failed first connect starts nothing: `ConnectAsync_ShouldNotDialAgain_WhenTheFirstAttemptFailed`,
      with `AutoReconnect` left at its default `true` and a short `ReconnectInitialDelay`. After the
      refused dial, no further connection arrives within a bounded window, and `EventLoop` is null.
      This must pass before the fix too — it pins existing behaviour the fix must not change.
- [ ] 2.4 Pin that the terminal state is not a gate:
      `ConnectAsync_ShouldConnect_WhenRetriedAfterAFailedAttempt`. One listener refuses the first dial
      and answers `101` on the second; the same client instance connects and reaches `Connected`.
- [ ] 2.5 Leave `AriClientExtendedTests.ConnectAsync_ShouldThrow_WhenServerUnreachable` alone and say
      why in this task: it dials a blackhole address under a 500 ms token, so whether the attempt ends
      by the caller's token or by a transport error is a race. Asserting a state there would be flaky.

## 3. Fix the connect outcome

- [ ] 3.1 In `AriClient.ConnectAsync`, wrap only `await _webSocket.ConnectAsync(uri, cancellationToken)`
      in a `try` that sets a local flag immediately after the await, with a `finally` that — when the
      flag is false — writes `Disconnected` if `cancellationToken.IsCancellationRequested` and
      `Faulted` otherwise. Catch nothing: the exception, its type and its stack must reach the caller
      unchanged, and a `catch (Exception)` here would open a new `cs/catch-of-all-exceptions` alert in
      the change that exists to close alerts.
- [ ] 3.2 Comment the classification with its reason: the caller's token is the discriminator because a
      cancellation raised inside the transport carries a token the caller never held (ADR-0053 records
      that trap for a bridge's `ConnectAsync`), so neither the exception's type nor its
      `CancellationToken` may decide.
- [ ] 3.3 Confirm by reading that nothing in the type gates on `_state`: `IsConnected` and
      `DisposeAsync` compare it only to `Connected`, and `ConnectAsync` writes `Connecting`
      unconditionally on its first line. Record that a failed attempt still leaves its `ClientWebSocket`
      and its linked source to `DisposeAsync` — ownership is deliberately unchanged, which is the same
      reasoning the two standing `cs/dispose-not-called-on-throw` dismissals rest on for the reconnect
      loop's socket.
- [ ] 3.4 Mutations. Each must fail at least one committed test, applied alone and then reverted:
      the `finally` removed; `Faulted` written for both endings; `Disconnected` written for both;
      the state written before the await instead of after; the terminal state written on the success
      path too; and a gate added that returns early from `ConnectAsync` when `State` is `Faulted`.
      Record which test catches which, verbatim.
- [ ] 3.5 Record the one mutation that is **not** separable and why: replacing
      `cancellationToken.IsCancellationRequested` with a test on the exception's type or its
      `CancellationToken`. The current `ClientWebSocket.ConnectAsync` overload offers no way to raise a
      cancellation the caller did not ask for, so no committed test can tell the two apart. Do not
      claim coverage for it.

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

- [ ] 7.1 Write `docs/decisions/0057-<kebab-title>.md` — **ADR-0057**, the `decision_ref` of this
      proposal. It records: a connect attempt that ends without a connection leaves a terminal state;
      the ending is classified by who ended it, read from the caller's token and never from the
      exception; a withdrawal leaves `Disconnected` and everything else `Faulted`; the terminal value
      is a statement and not a gate; and the two rejected alternatives — `Faulted` for a withdrawal
      too (rejected: it would page on a routine shutdown, walking back ADR-0053) and `Initial` for a
      withdrawal (rejected: the client opened a socket and a linked source, so it is not as-created).
      Relate it to ADR-0010, ADR-0050 E6, ADR-0052, ADR-0053 and ADR-0054.
- [ ] 7.2 Add the ADR-0057 row to `docs/decisions/README.md` in numeric order, matching the style of
      the surrounding rows.
- [ ] 7.3 `CHANGELOG.md` `[Unreleased]`: one entry stating the observable change — after a failed
      first connect, `State` reads `Faulted`, or `Disconnected` when the caller cancelled, instead of
      `Connecting`; `AriHealthCheck` is `Unhealthy` before and after and only its message moves;
      `IsConnected` is unchanged; the exception reaches the caller unchanged. Say explicitly that this
      amends the sentence in the 2.5.3 entry *"`AriClient` kept reconnecting after Asterisk refused
      its credentials"* which documents the old behaviour. Leave the `(#N)` citation for close-out.
- [ ] 7.4 Put the release tier to the owner before merging: this changes an observable that consumers
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

- [ ] 9.1 Land the change; record the PR number.
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
