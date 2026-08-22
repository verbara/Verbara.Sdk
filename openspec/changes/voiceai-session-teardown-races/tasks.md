# Tasks: voiceai-session-teardown-races

## 1. Reproduce every ordering, deterministically, before fixing anything

- [x] 1.1 `ReadAudioAsync_ShouldDeliverBufferedAudioThenEnd_WhenTheHangupCompletesBeforeTheFirstRead`.
      Ordered by construction with no delay: the client's read ends on EOF, and the FIN that produces
      it is emitted by `_client.Dispose()` — the *last* statement of the session's teardown, after
      `_cts.Dispose()`. When the drain loop returns, the teardown has provably completed, and the
      ordering cannot invert because the FIN cannot precede the statement that emits it. Recorded
      pre-fix failure: `System.ObjectDisposedException : The CancellationTokenSource has been
      disposed. at System.Threading.CancellationTokenSource.get_Token() at
      AudioSocketSession.ReadAudioAsync(CancellationToken ct)+MoveNext() in AudioSocketSession.cs:74`
      — the `ObjectName` is empty, so the exception never names the session.
- [x] 1.2 `OpenAiRealtimeBridgeSetupWindowTests`, three tests: cancelled *during* connect,
      cancelled *before* it, and — the control — a connect the far end genuinely *rejects*
      (`RejectingHandshakeListener` answers the upgrade with `401`), which must still be counted as
      a failure, logged as `SessionError` and rethrown. Without that third test the first two are
      satisfied by a `catch` that swallows everything, so the pair proves only half of ADR-0053.
      The seam for the first two is `StalledHandshakeListener` — it accepts the TCP connection,
      reads the upgrade request, signals, and never writes the `101`, so once `RequestReceived` has
      fired the *only* thing that can end `ConnectAsync` is the token. The alternative outcome is not
      unlikely, it is impossible. Pre-fix both fail on four assertions at once: the escaping
      `TaskCanceledException`, `sessions.completed = 0`, `session.duration_ms = 0` and no
      `SessionEnded` entry. Note the clean close is *not* among them — see §3.4. The rejection test
      fails pre-fix for the mirror-image reason: the escaping `WebSocketException` skipped the
      terminal block entirely, so `sessions.failed` stayed `0` and nothing was logged either.
      (The bridge's fake could not supply this seam: `WebSocketTestServer` writes its `101` before
      invoking the per-protocol handler, so any signal inside `RealtimeFakeServer` fires on the wrong
      side of the window.)
- [x] 1.3 All four AudioSocket tests failed pre-fix, each on its own stated reason and none on an
      adjacent one; the R1 test and the existing ordering-(b) pin
      (`AudioSocketServerTests.Session_ReadAudioAsync_ShouldYieldAudioFrames`) passed pre-fix, which
      is what says the suite discriminates rather than merely reporting red. Bridge: see §1.2.
- [x] 1.4 Covered, plus the EOF leak: `…_ShouldEndTheSequence_WhenTheSessionIsDisposedMidEnumeration`
      (pre-fix `System.OperationCanceledException: The operation was canceled. at
      ChannelReader.ReadAllAsync+MoveNext()`), `…_ShouldThrowObjectDisposedException_WhenTheOwnerDisposedTheSession`
      (pre-fix: no exception at the call at all), and
      `ReadLoop_ShouldReleaseTheTransport_WhenTheSocketEndsWithoutAHangupFrame` (pre-fix
      `IsConnected` still `True`). `…_ShouldThrowOperationCanceled_WhenTheCallersTokenIsCancelled`
      pins R1 and passed before and after.

## 2. Decide the AudioSocket termination semantics — before writing the fix

- [x] 2.1 **Decided: three orthogonal rules, not a disjunction.** The proposal framed this as
      "hangup completes normally" *versus* "hangup throws deterministically", but the five orderings
      do not collapse onto one axis — they differ in *who ended the session*, and that is the only
      thing a consumer can act on. **R1** — the consumer's own cancellation raises
      `OperationCanceledException` at the next iteration boundary (ADR-0052 F1). **R2** —
      `ObjectDisposedException` means only "the owner disposed, then someone read", is thrown from
      the call rather than a later `MoveNext`, and names `AudioSocketSession`. **R3** — every other
      ending (far-end hangup, error frame, `HangupAsync`, EOF, an owner disposal that lands after
      enumeration legitimately began) **ends the sequence** after delivering frames already
      received. Precedence is structural rather than a written rule: R2 is evaluated at call time,
      R1 and R3 at iteration boundaries, and R1 before R3.
      Reasoning: a hangup is how calls end, so R3 spares every consumer a catch on the commonest
      path; R2 keeps the type consistent with its own other members (`:90`, `:100`, `:111`) without
      letting an *involuntary* ending borrow the disposal signal; and separating them is what stops
      a routine host shutdown from being indistinguishable from a use-after-dispose bug.
- [x] 2.2 `docs/decisions/0053-session-endings-are-classified-by-who-ended-them.md`. Carries the
      five-ordering table with its measured outcomes, the recorded pre-fix exception text, all four
      rejected alternatives (capture-the-token, collapse (a) and (c), a second terminal block, force
      the protocol close on the cancelled path) and the telemetry consequences.
- [x] 2.3 **All five orderings are covered by the requirement**, plus the EOF resource leak the
      sweep surfaced: (a) hangup before first read → R3; (b) hangup mid-enumeration → R3, already
      correct today and must stay so; (b-variant) owner dispose mid-enumeration → R3, a behaviour
      change; (c) read after owner dispose → R2, a behaviour change in *where* it throws and in the
      exception naming the type; (d) EOF with no hangup frame → R3 plus transport release.

## 3. Fix

- [x] 3.1 `AudioSocketSession.ReadAudioAsync`: the read path reads **no session lifetime state at
      iteration time**. Split the iterator into an eager wrapper (carrying the R2 guard) and a
      private core that enumerates on the caller's token alone, ending when the channel completes.
      *Capturing `_cts.Token` at construction was the proposal's suggestion and is wrong*: it
      converts the `ObjectDisposedException` into an `OperationCanceledException` carrying a foreign
      token, still strands the buffered frames, and turns the regression test green with the
      headline symptom intact.
      Done: an eager `ReadAudioAsync` wrapper carrying the R2 guard plus a private
      `ReadAudioCoreAsync` that drains with `TryRead` and parks on `WaitToReadAsync(ct)`, so
      `yield return` sits outside any handler and `_cts` is never touched at iteration time.
- [x] 3.2 `DisposeAsync` now sets `_consumerDisposed` and delegates to a private `TerminateAsync`;
      `HangupAsync` and the read loop call `TerminateAsync` directly. `TerminateAsync` completes the
      channel itself, which is what makes R3 structural rather than dependent on the read loop
      unwinding first. The teardown runs before `FireHangup`, so an `OnHangup` subscriber observes a
      session that has genuinely released its transport.
- [x] 3.3 Closed: the hangup/error case now just `return`s and the single `finally` owns both the
      teardown and the hangup, so EOF and transport errors release the transport on the same path.
      The frame write also moved from `WriteAsync(copy, ct)` to `TryWrite(copy)` — the channel is
      `DropOldest` so it never blocked, but once the teardown completes it, `WriteAsync` throws
      `ChannelClosedException` and the loop's `catch (Exception)` would log a perfectly normal
      ending as a failure.
- [x] 3.4 Merged. `HandleSessionAsync` has one entry, one exit and one `finally`, so each
      instrument is touched at most once per session and `!sessionFailed` keeps completed and failed
      mutually exclusive exactly as before.
      **Spec correction found here:** "a clean protocol close is *sent* on the cancelled path" is not
      achievable and was reworded before implementation. Cancelling any WebSocket operation *aborts*
      the socket — platform contract, not a defect in this code — so the state is `Aborted` or
      `Closed` and the `ws.State == Open` gate skips the close. This is equally true of the loops
      path today and always has been. The requirement now says the ending is routed through one
      teardown and the close is attempted when, and only when, the transport is still open. Forcing
      it would mean `OutputLoop` abandoning its pending receive rather than cancelling it — a
      redesign with a fresh concurrent-send hazard, recorded in ADR-0053 as not done.
- [x] 3.5 Kept, with the reason in a comment at the filter itself rather than only in the ADR.
- [x] 3.6 `catch (ObjectDisposedException) { return; }` around the write-back. Under ADR-0053 a
      far-end hangup leaves the session ended, so this call throws — and unguarded it reaches the
      `catch (Exception)` that counts `SessionsFailed`, on the commonest real ending of all.
- [x] 3.7 With `src/` reverted: **6 failed, 0 passed**. With the fix: 6/6 green, and the two
      suites 98/98 and 61/61.

## 4. Sweep for the same shape elsewhere

- [x] 4.1 Swept all 29 packages / 1040 `.cs` files for both shapes. **Class 1** (`_cts.Token` or
      other disposable session state read from inside an iterator body): 23 iterator methods, seven
      benign hits, **zero new harmful instances**. Recorded so a later sweep does not re-open them:
      `AzureSpeechSynthesizer.cs:95`, `GoogleSpeechSynthesizer.cs:87`, `WhisperSpeechRecognizer.cs:51`
      and `AzureWhisperSpeechRecognizer.cs:52` are never disposed by their owner mid-stream;
      `AudioSocketClient.cs:69`, `LmntSpeechSynthesizer.cs:406` and
      `SpeechmaticsSpeechSynthesizer.cs:101` are reachable only through caller-side use-after-dispose.
      **None should be "fixed".** **Class 2** (an `await …(ct)` outside the `try` whose
      `OperationCanceledException` handler guards it): `OpenAiRealtimeBridge` L67/L81/L82 are the
      only instances in `src/`.
- [x] 4.2 Nothing to route: the sweep's two shapes are `src/`-side. The only test-side edits here
      are the two `<remarks>` in `OpenAiRealtimeBridgeTests` that documented *these* defects as live
      production races and justified the `LoopsRunningMarkerEvent` sentinel. Both are rewritten to
      point at ADR-0053 in the past tense. The sentinel is kept — it still establishes that both
      loops are running, which the live-socket-state assertions depend on.
- [x] 4.3 Routed to the new change `voiceai-pipeline-cancellation-accounting`, after re-verifying
      both against `main` rather than trusting the sweep's record:
      `VoiceAiPipeline`'s `_ttsCts` is disposed from two places while a third cancels a snapshot of
      it and a fourth reads `.Token`, so a barge-in landing as synthesis completes can fault the
      session; and `VoiceAiPipeline.HandleSessionAsync`'s bare `catch` counts every cancellation as
      `SessionsFailed` where the bridge counts it as completed, so the two `ISessionHandler`
      implementations disagree about what a cancelled session is. Confirmed at
      `VoiceAiPipeline.cs:276/279/142-144/337-339/366` and `:90-96`.

## 5. Verification and release

- [x] 5.1 `dotnet build Verbara.Sdk.slnx` — 0 warnings, 0 errors.
- [x] 5.2 Unit lane green with the four-exclusion CI filter.
- [x] 5.3 30 iterations idle and 30 under CPU saturation (48 spinners on 24 cores): **0 failures
      in 60**.
- [x] 5.4 Governance 129/129, which covers `CancellationProvenanceScanner`,
      `FakeServerCaptureScanner` and the `sync-fence-baseline.json` ratchet.
      The ratchet caught one net-new barrier: `StalledHandshakeListener` held its socket open with
      `Task.Delay(Timeout.Infinite, token)`. None of the four `fence-allow` categories honestly
      describes a hold-open, so the barrier was removed rather than annotated — the hold now awaits a
      `TaskCompletionSource` released on disposal, and no clock is involved at all.
- [x] 5.5 Serialisation was required and measured, three warm runs each: **0.550/0.582/0.562 s
      serialised against 0.344/0.337/0.348 s parallel** — ~215 ms, and still under the 0.8 s
      ADR-0045 recorded, with two more tests in the suite.
- [x] 5.6 CHANGELOG under `Fixed`, stating the behaviour changes plainly: what a consumer saw
      before, what it sees now, that a `catch (ObjectDisposedException)` around `ReadAudioAsync`
      becomes unnecessary, and that `openai_realtime.sessions.failed` will start reading non-zero
      for connect failures that previously escaped with no accounting at all.
- [x] 5.7 `Directory.Build.props` **2.4.0 → 2.5.0** — minor, per ADR-0052 F4's precedent for a
      behaviour break in a public surface.
- [x] 5.8 `openspec validate --all --strict` green — 11 items.
