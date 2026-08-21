# Tasks — websocket-fake-protocol-contract

Execution follows Subagent-Driven Development with FCM batching:
**Phase A (batch)** = §1 baseline + §2 substrate · **Phase B (focused)** = §3 fences + §4 tests ·
**Phase C (batch)** = §5 ratchet/guard + §6 records + §7 verification.

§5.7–§5.11 were handed to this change by ADR-0052 after it was written; §5.11 is a spec correction
and gates §5.7.

Sections §2, §3 and §4 are separable on purpose: §2 must be green with the test files untouched
before §3 changes any timing, and §3–§4 stand on either substrate if §2 is abandoned.

## 1. Baseline — evidence before any edit

- [x] 1.1 Record per-test wall clock for `Tests/Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests` as it
      stands, `-c Release`, `--logger "console;verbosity=detailed"`. Starting point already measured
      on 2026-08-10: five bridge tests at **5 s** each (their own CTS expiring), cancellation test at
      245 ms, whole project 44.3 s including build. Re-measure at least 3 runs so the after-figure in
      §7.4 compares against a spread, not a single sample
      — **re-measured 2026-08-20, 3 runs, `-c Release --no-build`** (the 44.3 s of 2026-08-10 included
      the build; these figures exclude it, so §7.4 must compare like for like). 59 tests, all green.

      | Test | run 1 | run 2 | run 3 |
      |---|---|---|---|
      | `HandleSessionAsync_SendsSessionUpdate_OnConnect` | 5 s | 5 s | 5 s |
      | `HandleSessionAsync_PublishesResponseStartedAndEndedEvents` | 5 s | 5 s | 5 s |
      | `HandleSessionAsync_PublishesTranscriptEvents` | 5 s | 5 s | 5 s |
      | `HandleSessionAsync_PublishesSpeechEvents` | 5 s | 5 s | 5 s |
      | `HandleSessionAsync_PublishesErrorEvent_OnOpenAiError` | 5 s | 5 s | 5 s |
      | `HandleSessionAsync_CancellationToken_TerminatesBothLoops` | 202 ms | 203 ms | 199 ms |
      | `Bridge_ExecutesFunction_AndSendsResultToServer` | 308 ms | 303 ms | 303 ms |
      | `Bridge_FunctionThrows_SendsErrorJsonToServer` | 336 ms | 329 ms | 331 ms |
      | `Bridge_UnknownFunction_DoesNotCrash` | 302 ms | 301 ms | 303 ms |
      | **whole project (excl. build)** | **25.58 s** | **25.49 s** | **25.49 s** |

      **The 25 s is the whole project's wall clock, not a share of it.** xunit parallelises across
      test *classes*, not within one, so `OpenAiRealtimeBridgeTests`' five 5-second tests run in
      series and `FunctionCallTests` finishes inside that window. The project's critical path is
      therefore exactly the bridge class, and the run-to-run spread is 0.09 s — a noise floor two
      orders of magnitude below the budget §4 reclaims.
- [x] 1.2 Pin the fake's close timeline. Instrument `RealtimeFakeServer` locally to log the instant
      it calls `CloseAsync`, and confirm it lands at ~130 ms (30 ms + 5 ms/event + 100 ms) — i.e.
      **before** the 200 ms cancel in `HandleSessionAsync_CancellationToken_TerminatesBothLoops`.
      This is the evidence that the test's `OutputLoop` half is not exercised today; the timeline is
      currently read off the source, not observed
      — **confirmed, and now observed rather than read off the source.** Temporary `Stopwatch` marks
      from the accept, `-c Release`; the instrumentation was reverted before §1.5. Cancellation test:

      ```
      accept                                    @   0.4 ms
      session.created sent                      @   1.1 ms
      recv text #1 (the client's session.update)@   5.5 ms
      30 ms delay elapsed                       @  33.5 ms
      100 ms pre-close delay elapsed; state=Open@ 134.1 ms   <-- CloseAsync is entered here
      recv loop threw: WebSocketException       @ 192.7 ms   <-- the test's 200 ms cancel
      close threw: OperationCanceledException   @ 193.1 ms
      HandleWebSocketAsync RETURNS              @ 193.1 ms
      ```

      The close decision lands at **134 ms**, ~59 ms *before* the cancel — the predicted ~130 ms, and
      the ordering the task exists to establish. The same three marks in the five-second tests land
      at 129.7 / 131.9 / 138.0 / 146.4 ms, so the figure is stable across tests, not a single sample.
- [x] 1.3 Negative-test the Class B claim on the current code: make the cancellation test assert the
      socket is still open when the token fires, watch it fail against today's fake, then revert.
      Record the failure text — it is what §4.7 must turn green
      — **it fails, and the observed state is sharper than "not open".** A temporary `ProbeState`
      on the fake exposing the live server-side `WebSocket.State`, asserted in
      `HandleSessionAsync_CancellationToken_TerminatesBothLoops` immediately before `cts.CancelAsync()`:

      ```
      Expected fakeOpenAi.ProbeState to be WebSocketState.Open {value: 2} because the socket must
      still be live when the token fires, or the loop exits on the server's close, but found
      WebSocketState.CloseSent {value: 3}.
      ```

      **`CloseSent`, not `Aborted` and not `Open`** — the server had already written its close frame
      and was waiting for the reply. That is the exact shape of the Class B defect: by the time the
      token fires the teardown is already half-done, so the test's `OutputLoop` half cannot be
      attributed to cancellation. This is the assertion §4.7 must turn green; both the probe and the
      assertion were reverted afterwards.
- [x] 1.4 Confirm or refute the concurrent-receive observation at `RealtimeFakeServer.cs:112-123`:
      `CloseAsync` is called while the background receive loop has an outstanding `ReceiveAsync` on
      the same socket, and any resulting exception is swallowed by the surrounding `catch { }`.
      Determine empirically whether the peer still observes the close frame. Whatever the answer, it
      is deleted by §2 — record it so the proposal's claim is settled rather than carried forward
      — **CONFIRMED as a violation; the peer DOES observe the close frame.** Settled with a
      purpose-built probe (a raw `ClientWebSocket` against the fake, no competing receive, deleted
      afterwards) rather than by reading `ManagedWebSocket`:

      ```
      [PEER] connected                                            @  16.5 ms
      [PEER] sent session.update                                   @  17.4 ms
      [PEER] received Text: {"type":"session.created","session":{}}@  18.6 ms
      [PEER] received Text: {"type":"response.created"}            @  46.5 ms
      [PEER] *** RECEIVED CLOSE FRAME *** status=NormalClosure desc='done' @ 154.5 ms
      ```

      So the ordering the proposal declined to assume resolves in the harmless direction for the
      *frame*: `CloseAsync` writes it before it waits, so it reaches the wire. **What never completes
      is the handshake.** The outstanding `ReceiveAsync` owns the receive path, so `CloseAsync` can
      never read the peer's close reply; it blocks, and the server's own receive loop never observes
      a `WebSocketMessageType.Close` either. Both unwind only when the socket is finally torn down,
      at which point `CloseAsync` throws `OperationCanceledException` into the `catch { }` and the
      server socket ends **`Aborted`** — in every run measured, never once `Closed`.

      **One consequence the proposal did not state, and it is the larger one:** because `CloseAsync`
      blocks until teardown, `HandleWebSocketAsync` does not return at ~130 ms — it returns when the
      *client* dies. In the five 5-second tests that is **4 987–4 992 ms**, i.e. the fake's session
      handler is pinned for 4.86 s per test doing nothing but waiting on a close reply that cannot
      arrive. This does not change the plan — §2 deletes the path either way, and `WebSocketTestServer`
      gives the per-connection handler sole ownership of the socket — but it is the mechanism behind
      the wall clock §1.1 measured, so it belongs in ADR-0045's evidence rather than only here.
- [x] 1.5 Pre-change flake baseline: 30× repeat run of the suite (the repo's determinism protocol),
      so a post-change flake can be attributed. Note the protocol's known limit — it multiplies runs,
      not machines
      — **30/30 green, 2026-08-20, `-c Release --no-build`, 59 tests each run.** Wall clock across
      the 30 runs: min **25.87 s**, median **25.90 s**, max **26.06 s** (mean 25.90 s). The spread is
      **0.19 s over thirty runs** — 0.7 % of the total.

      That tightness is itself evidence rather than reassurance. A suite whose runtime is dominated
      by real work varies with machine load; one whose runtime is dominated by fixed timeouts does
      not. 25.9 s ± 0.1 s is the signature of five 5-second tokens expiring on schedule, and it
      confirms from the outside what §1.1 measured from the inside: the suite is not doing 25 s of
      work, it is waiting 25 s.

      **The protocol's known limit applies in full here.** Thirty runs on one machine multiply runs,
      not machines: they cannot surface a race that this CPU count, this scheduler and this loopback
      stack happen to win every time. Class A is precisely such a race — the `Task.Delay(30)` at
      `:98` wins 30/30 here and is still a race. So this baseline establishes *"no flake was
      observable before the change"*, which is what §7.3 needs to attribute a post-change flake; it
      does **not** establish that the current fake is sound. §1.2–§1.4 are the evidence for that, and
      they say it is not.

## 2. Substrate migration (no test-file changes in this section)

- [x] 2.1 Add `<ProjectReference Include="..\Verbara.Sdk.TestInfrastructure\..." />` to
      `Tests/Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests/Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.csproj`
      — the project does not reference it today
      — added. **One consequence worth stating, because the proposal's Impact says "no new external
      dependency":** `Verbara.Sdk.TestInfrastructure` carries a `PackageReference` to
      `Testcontainers`, so that package now flows transitively into this test project. It is not new
      to the repo (it is already pinned in `Directory.Packages.props` and referenced by
      `Verbara.Sdk.VoiceAi.Tts.Tests`, `Verbara.Sdk.IntegrationTests` and `Verbara.Sdk.FunctionalTests`)
      and nothing here instantiates a container, so **no Docker daemon is required to run this suite**
      — the claim holds at the repo level and is qualified at the project level.
- [x] 2.2 Rewrite `RealtimeFakeServer` on `WebSocketTestServer`: supply a per-connection handler
      taking a `WebSocketTestSession`, and delete the `HttpListener` field, `AcceptLoopAsync`, the
      `AcceptWebSocketAsync` call and the `HttpListener` close path
      — done. 142 lines → 102. `HandleSessionAsync(WebSocketTestSession)` is now the whole server
      surface; `_listener`, `AcceptLoopAsync`, `AcceptWebSocketAsync`, the `_cts` and the
      `HttpListener` close path are gone, and `WebSocketTestServer` owns accept, the RFC 6455
      handshake and disposal. The three `Task.Delay` calls and the close sequence were carried over
      **verbatim** so §2.5 measures the substrate and nothing else — §3 is where they go.
- [x] 2.3 Delete the TOCTOU port probe and its retry loop (`RealtimeFakeServer.cs:23-50`, including
      the `goto success`). `WebSocketTestServer` binds `TcpListener(IPAddress.Loopback, 0)` and
      exposes `Port` directly — ADR-0044's "unavoidable for `HttpListener`" no longer applies
      — deleted: 28 lines of probe, ten-attempt retry loop and `goto success` replaced by
      `public int Port => _server.Port;`. The reason it is deletable rather than merely tidied is
      recorded in the type's own `<remarks>`, so the next reader does not reintroduce it: the probe
      existed because `HttpListener` cannot adopt an already-bound socket, and `WebSocketTestServer`
      binds its listener in its constructor and keeps it.
- [x] 2.4 Keep `Port`, `Start()`, `EventsToSend` and `ReceivedMessages` byte-identical in name and
      shape so `OpenAiRealtimeBridgeTests` and `FunctionCallTests` compile and run **unmodified** in
      this section
      — held. `git status` after the migration lists exactly two changed files in the test project,
      the fake and the `.csproj`; neither test file is touched. `ReceivedMessages` is still
      `public List<string>` at this point — that is the Class C defect, and fixing it here would have
      forced the test edits this section exists to avoid. §3.6 changes it.
- [x] 2.5 Suite green with the test files untouched, and per-test durations unchanged from §1.1 —
      this section must not move timing in either direction. If it does, the migration changed
      behaviour and the cause is found before §3 starts
      — **green and unchanged.** Build 0 warnings / 0 errors; 59/59 passed on each of 3 runs,
      `-c Release --no-build --logger "console;verbosity=detailed"`:

      | Test | §1.1 (before) | run 1 | run 2 | run 3 |
      |---|---|---|---|---|
      | `HandleSessionAsync_SendsSessionUpdate_OnConnect` | 5 s | 5 s | 5 s | 5 s |
      | `HandleSessionAsync_PublishesResponseStartedAndEndedEvents` | 5 s | 5 s | 5 s | 5 s |
      | `HandleSessionAsync_PublishesTranscriptEvents` | 5 s | 5 s | 5 s | 5 s |
      | `HandleSessionAsync_PublishesSpeechEvents` | 5 s | 5 s | 5 s | 5 s |
      | `HandleSessionAsync_PublishesErrorEvent_OnOpenAiError` | 5 s | 5 s | 5 s | 5 s |
      | `HandleSessionAsync_CancellationToken_TerminatesBothLoops` | 199–203 ms | 200 ms | 203 ms | 203 ms |
      | `Bridge_ExecutesFunction_AndSendsResultToServer` | 303–308 ms | 302 ms | 304 ms | 303 ms |
      | `Bridge_FunctionThrows_SendsErrorJsonToServer` | 329–336 ms | 325 ms | 326 ms | 328 ms |
      | `Bridge_UnknownFunction_DoesNotCrash` | 301–303 ms | 302 ms | 300 ms | 302 ms |
      | **whole project (excl. build)** | **25.49–25.58 s** | **25.89 s** | **25.91 s** | **25.90 s** |

      Every per-test figure is inside the before-spread. The project total sits ~0.35 s above §1.1's
      three runs but exactly on the §1.5 30-run baseline (25.87–26.06 s, median 25.90 s) — §1.1's
      three samples were the low end of that distribution, not a different number. The substrate did
      not move timing.

## 3. Protocol fences in the fake (Class A + Class B + Class C)

- [x] 3.1 Add a `TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)` released
      when the client's `session.update` frame arrives — the bridge's unconditional first frame
      (`src/Verbara.Sdk.VoiceAi.OpenAiRealtime/OpenAiRealtimeBridge.cs:80-84`). Match the sentinel
      naming and XML-doc style of `DeepgramTtsFakeServer._requestComplete`, including *why* this
      frame is the sentinel and which frames are not
      — `_sessionUpdateReceived`, doc'd in the `_requestComplete` shape and stating which frames are
      **not** sentinels: `input_audio_buffer.append` only appears once the caller speaks and
      `conversation.item.create` only after a function call, so a session with neither would never
      release a sentinel keyed on those. Exposed to tests as `SessionUpdateReceived`.
- [x] 3.2 Replace `await Task.Delay(30)` (`:98`) with a bounded wait on that sentinel, mirroring
      `WaitForRequestOrTimeoutAsync` in the Deepgram and LMNT fakes. Timeout long enough that
      reaching it means the protocol assumption is wrong, not that the machine was busy
      — `WaitForSessionUpdateOrTimeoutAsync`, bounded at **10 s** (Deepgram uses 2 s; this one gates a
      full AudioSocket + WebSocket bring-up, so it is set where reaching it can only mean the
      protocol assumption is wrong).

      **Negative test — and the first attempt at it was worthless, which is the finding.** Replacing
      the sentinel with the `Task.Delay(30)` it superseded left the suite **20/20 green under CPU
      saturation**, and `Task.Delay(0)` did too. Nothing else in the suite could see the fence's
      absence: the fake's drain loop captures `session.update` whenever it arrives, and every
      assertion read only the end state. A fence nothing can observe is a fence nobody will keep.

      So the fake now records `FramesCapturedWhenAnswering` — the client frames it had captured at
      the instant it began delivering `EventsToSend` — and
      `HandleSessionAsync_SendsSessionUpdate_OnConnect` asserts `session.update` is among them. With
      the fence in place: green. With the fence replaced by `Task.Delay(30)`, under CPU saturation:

      ```
      Expected fakeOpenAi.FramesCapturedWhenAnswering {empty} to have an item matching
      m.Contains(""session.update"") because the fake answers on the client's request frame, not on
      a timer.
      ```

      1 failure in 5 runs. The fence is now load-bearing rather than assumed, which is what the
      requirement's *"Removing the fence fails the test"* scenario asks for.
- [x] 3.3 Remove the 5 ms inter-event delay (`:106`) — configured events go out back to back; the
      client's receive loop frames them, the fake does not need to pace them
      — removed. WebSocket message framing preserves boundaries, so two `SendAsync` calls with
      `endOfMessage: true` are two messages however close together they are issued. **One assertion
      was resting on this delay** and is corrected in §4.2.
- [x] 3.4 Replace the 100 ms pre-close delay (`:110`) with an explicit close decision: close when the
      configured events are delivered, unless the fake is holding open
      — removed; the close now follows delivery directly. **The close call changed with it, and had
      to:** `CloseAsync` also *waits* for the peer's close frame, which means receiving, and the
      drain loop already owns the receive path — the §1.4 violation, which would have survived the
      substrate migration untouched. `CloseOutputAsync` sends the frame without receiving, and the
      drain loop reads the client's reply, so the handshake completes with a single receiver. The
      server socket now reaches `Closed` instead of `Aborted`.
- [x] 3.5 Add `HoldOpenUntilDisposed`, implemented as `Task.Delay(Timeout.Infinite, ct)` on the
      server's own token, then drain the receive loop. **Not** `await receiveTask` — carry the
      comment explaining that the client's half-close ends that loop while the socket is still
      readable, so returning there is exactly the defect this flag exists to avoid
      — added, with the `// fence-allow: GUARD-TIMEOUT` marker so it does not consume a baseline slot
      (the `LmntWsFakeServer` shape, whose baseline entry is 0; Deepgram's equivalent is unmarked and
      still costs it 1). Negative-tested in §4.7.
- [x] 3.6 Convert `ReceivedMessages` to a snapshot: private backing `List<string>`, appended under a
      `lock`, exposed as `IReadOnlyList<string>` returning `ToArray()` under the same lock — the
      `CartesiaFakeServer` / `LmntFakeServer` shape
      — done. `_receivedMessages` is private; `ReceivedMessages` returns `ToArray()` under the lock
      the drain loop appends under. Both new observation surfaces follow the same rule:
      `FramesCapturedWhenAnswering` is an array snapshot taken under that lock, and
      `RealtimeEventCollector.Events` (§4) does the same for the bridge's published events, which the
      bridge appends from its own `OutputLoop` thread. Guard for the class: §5.2–§5.6.
- [x] 3.7 Leave `EventsToSend` a plain `List<string>`: test→server configuration written before
      `Start()`, not a capture. Say so in a comment so the next reader does not "fix" it
      — left plain, with the reason on the property and a closing *"do not 'fix' it"*. The detector
      in §5.4 has to agree with this comment, or one of the two is wrong.

## 4. Tests end on their own signal

- [x] 4.1 `HandleSessionAsync_SendsSessionUpdate_OnConnect` — await the fake's `session.update`
      sentinel, hang up, then assert. No 5-second token on the success path
      — awaits `SessionUpdateReceived`, then asserts. **5 s → 51 ms.** Two departures from the task
      text, both forced by races the rewrite exposed and both recorded under §4.6.
- [x] 4.2 `HandleSessionAsync_PublishesResponseStartedAndEndedEvents` — complete on the second
      subscribed event rather than on token expiry
      — completes on both events via `RealtimeEventCollector`. **5 s → 4 ms.**

      **One assertion had to change, and it is worth stating plainly rather than burying.** The old
      `ended.Duration.Should().BeGreaterThan(TimeSpan.Zero)` held *because the fake slept 5 ms
      between the two events* (§3.3). It asserted the fake's timer, not the bridge, and with the
      timer gone it is a coin flip on clock resolution. It is replaced by what the bridge actually
      guarantees and what needs no delay at all: `Duration >= Zero`, and
      `Duration.Should().BeCloseTo(ended.Timestamp - started.Timestamp, 50 ms)` — the interval
      between the two events it published. Weaker in one direction, meaningful in a way the original
      was not.
- [x] 4.3 `HandleSessionAsync_PublishesTranscriptEvents` — same, on the two transcript events
      — **5 s → 2 ms.**
- [x] 4.4 `HandleSessionAsync_PublishesSpeechEvents` — same, on the two speech events
      — **5 s → 2 ms.**
- [x] 4.5 `HandleSessionAsync_PublishesErrorEvent_OnOpenAiError` — same, on the error event
      — **5 s → 2 ms.**
- [x] 4.6 Keep a cancellation token in each of §4.1–§4.5 purely as a hang bound, and make its expiry
      a *failure* rather than the normal exit — a test that still passes when its token fires is
      back where it started
      — **done, and done more strictly than the task asked: the session token now carries no timer at
      all.** Every bound is a `WaitAsync(SignalTimeout)` whose expiry is a `TimeoutException`, so
      there is no token expiry left that a test could pass through. Two races found on the way, both
      real and neither previously visible:

      **(a) Ending the session by hanging up is not safe.** The natural rewrite — signal, hang up,
      await the session — fails **1 run in 10 under CPU saturation**:

      ```
      System.ObjectDisposedException : The CancellationTokenSource has been disposed.
        at AudioSocketSession.ReadAudioAsync(CancellationToken ct)+MoveNext()
           in src/Verbara.Sdk.VoiceAi.AudioSocket/AudioSocketSession.cs:line 74
      ```

      `AudioSocketSession`'s hangup path completes the audio channel and then disposes the session's
      `CancellationTokenSource`; `ReadAudioAsync` reads `_cts.Token` on its first `MoveNext`, so a
      hangup that overtakes that first `MoveNext` throws out of the bridge. It never surfaced before
      because the old tests hung up in *Cleanup*, after `HandleSessionAsync` had already returned.
      This is a production-side race in `Verbara.Sdk.VoiceAi.AudioSocket`, **out of scope here**
      (`src/` untouched) — recorded as follow-up in §6.4. The tests cancel first and hang up in
      cleanup.

      **(b) Cancelling on the server-side capture of `session.update` is not safe either.** That
      capture says the fake read the frame, not that the client's `SendAsync` completed — and that
      send is the one await the bridge does not guard (it precedes
      `Task.WhenAll(InputLoop, OutputLoop)`), so cancelling there faults the session:

      ```
      System.Threading.Tasks.TaskCanceledException : A task was canceled.
        at ManagedWebSocket.SendFrameFallbackAsync(...)
        at OpenAiRealtimeBridge.HandleSessionAsync(...) in OpenAiRealtimeBridge.cs:line 82
      ```

      1 run in 10 under saturation. The sentinel that *is* safe is a **published** event: nothing is
      published until `OutputLoop` is running, which is after that send completed. §4.2–§4.5 already
      wait on one; §4.1 and §4.7 now deliver a `LoopsRunningMarkerEvent` for exactly this and say so.
      After the fix: **20/20 green under CPU saturation** (background spinners on all 24 cores).
- [x] 4.7 `HandleSessionAsync_CancellationToken_TerminatesBothLoops` sets `HoldOpenUntilDisposed`, so
      `OutputLoop` is blocked on a **live** socket when the token fires. Turn §1.3's recorded failure
      green, and negative-test it: clear the flag, watch the test stop proving anything (it will pass
      for the old reason), restore it. Rename if the name no longer matches what it proves
      — done. The test now waits for a published event, asserts `SocketState == Open`, cancels, and
      asserts the session ran to completion. **245 ms → 2 ms**, and §1.3's assertion is green for the
      first time.

      **Negative test — better than the task predicted.** Clearing the flag does not leave the test
      quietly passing for the old reason; it fails, 3/3, reproducing §1.3's text exactly:

      ```
      Expected fakeOpenAi.SocketState to be WebSocketState.Open {value: 2} because cancellation has
      to be observed on a live socket, or the loops end on the server's close instead, but found
      WebSocketState.CloseSent {value: 3}.
      ```

      The flag was restored and the test is green. **Name kept:** with the hold in place both loops
      genuinely terminate on the token, so `TerminatesBothLoops` describes what it proves — arguably
      for the first time.
- [x] 4.8 `FunctionCallTests` — replace the three `await Task.Delay(300)` barriers
      (`:134`, `:164`, `:191`) with a wait on the frame each test asserts on
      (`conversation.item.create` / `response.create` for the first two; for
      `Bridge_UnknownFunction_DoesNotCrash`, which asserts an *absence*, state explicitly what
      signals "the bridge got far enough to have crashed" — an absence assertion needs a positive
      sentinel or it proves nothing)
      — all three replaced, via a new `WaitForClientFrameAsync(fragment)` on the fake that is
      race-free by construction: it satisfies from frames already captured, under the same lock, so a
      caller registering after the frame arrived does not wait forever.

      - `Bridge_ExecutesFunction_AndSendsResultToServer` waits on **all three** of its signals —
        both frames and the published event — because the frames land on the fake's drain loop and
        the event on the bridge's `OutputLoop`, so waiting on one and asserting the others is the
        race being removed. **302 ms → 3 ms.**
      - `Bridge_FunctionThrows_SendsErrorJsonToServer` waits on `conversation.item.create`.
        **325 ms → 51 ms.**
      - `Bridge_UnknownFunction_DoesNotCrash` — **the positive sentinel is a second event.** The
        bridge answers an unknown function with nothing at all (it logs and returns), so there is no
        frame to wait for, and "did not crash" measured against no signal is satisfied by a bridge
        that never ran. The fake now delivers `response.done` behind the unknown call on the same
        socket; `OutputLoop` awaits each handler before reading the next message, so the resulting
        `RealtimeResponseEndedEvent` proves the unknown call was processed and the loop came out the
        other side. A second assertion was added while the sentinel was there —
        `ReceivedMessages` must contain nothing for `call_id: call-x` — which is the absence the
        test's name claims and never checked. **302 ms → 4 ms.**
- [x] 4.9 Re-read every assertion that touches `ReceivedMessages` and confirm it reads the snapshot
      property, not a captured reference held across an await
      — checked, all six call sites (`OpenAiRealtimeBridgeTests` ×2 including the new
      `FramesCapturedWhenAnswering` assertion, `FunctionCallTests` ×4). Every one is a property read
      at the point of assertion; none stores the result in a local across an `await`. The same check
      applies to `RealtimeEventCollector.Events`: §4.2 and §4.8 take a single snapshot into a local
      **after** the last await and assert against that, so the several assertions in one test see one
      consistent list rather than three separate reads.

## 5. Ratchet and guard

- [x] 5.1 Lower the three `sync-fence-baseline.json` entries to the counts that actually survive
      (today: `Bridge/OpenAiRealtimeBridgeTests.cs` 2, `FunctionCalling/FunctionCallTests.cs` 3,
      `Internal/RealtimeFakeServer.cs` 3 — **8** total). Delete an entry outright if it reaches zero.
      Never raise a count
      — **all three reached zero and all three entries are deleted.** The suite's only remaining
      `Task.Delay` is the `Timeout.Infinite` hold in the fake, which carries a
      `fence-allow: GUARD-TIMEOUT` marker and therefore does not count. Baseline: 78 entries / 329
      barriers → **75 / 321**. Ratchet green.
- [x] 5.2 Add a Class C detector to `Tests/Verbara.Sdk.Governance.Tests/` following the
      `LoopbackSeamScanner` / `LoopbackSeamGuardTests` idiom: a `*FakeServer` type must not expose a
      mutable collection its receive loop writes
      — `FakeServerCaptureScanner.cs` + `FakeServerCaptureGuardTests.cs`, Roslyn-syntactic like the
      loopback pair, scanning `Tests/` only (fakes live nowhere else).

      **The discriminator is who writes, not what it is called.** A capture is written by the fake
      (`_received.Add(frame)` in the session handler); configuration is written by the test and only
      *read* inside the type. So the rules fire on members the declaring type mutates — no name list,
      no ignore list.

      Two rules, because one is trivially bypassed:
      - **MutableCapture** — an exposed member of mutable collection type that the type writes to,
        under its own name or through a private field it aliases.
      - **LiveCaptureAlias** — an exposed member typed `IReadOnlyList<T>` whose getter returns the
        private list *bare*, with no `ToArray()` between. Same defect wearing an interface: nothing
        can be added through it, but it still enumerates a list another thread is appending to.
        Without this rule, widening the property type would satisfy the guard and change nothing.
- [x] 5.3 Detector unit tests — true positive: a `public List<T>` capture property on a fake-server
      type is reported with a 1-based line number and the file named in the failure message
      — four true positives: the plain `public List<T>` capture (asserting rule, member name **and**
      path), the 1-based line number on a member that is not on line 1, the read-only interface over
      a live field, and a mutable property aliasing a renamed private field.

      **That last one is not decoration — it was a real hole.** The first draft keyed rule 1 on the
      member's own name, so `public List<string> ReceivedMessages => _receivedMessages;` passed
      clean: the mutation is on the field, the exposure is on the property. §5.6's negative test is
      what surfaced it, which is the entire argument for negative-testing a guard rather than
      shipping it green.
- [x] 5.4 Detector unit tests — false-positive immunity: configuration collections written by the
      test before `Start()` (`EventsToSend`, `AudioFramesToSend`, `ResultMessages`) are NOT reported,
      and neither is a snapshot property backed by a private field
      — six immunity tests: the three configuration collections, the snapshot-under-lock shape every
      fake was converted to, a wholesale-republished snapshot array, a private list never exposed, a
      non-fake-server type, and a capture shape sitting in a plain string literal (which is what
      keeps this file's own fixtures from self-flagging).

      **One immunity had to be discovered rather than predicted.** The first run reported five real
      files — `ResultMessages` on all four STT fakes and `AudioFramesToSend` on all four TTS ones.
      They are configuration, and the rule was still right: those fakes *do* write to them, in their
      **constructors**, seeding a recorded default payload so a test that does not care about the
      payload still exercises a realistic one. A constructor runs before any caller holds the object,
      so no reader can be racing it. Constructor writes are now excluded, structurally rather than by
      name, and a test pins the boundary: a list seeded in the constructor **and** written by the
      session handler is still reported.
- [x] 5.5 Liveness self-test — the scan must walk more than a conservative floor of files, so an
      empty enumeration cannot read as green
      — two dimensions, because the file count alone is not enough. `Guard_ShouldScanManyFiles_…`
      floors the walk at 250 files (real: 414). `Guard_ShouldRecogniseTheRepoFakeServers_…` floors
      the **fake-server types actually recognised** at 6 (real: 10) and names `WebSocketTestServer`
      explicitly. Walking every file proves nothing if the naming convention moves and the detector
      recognises none of them — that failure mode leaves the file count untouched.
- [x] 5.6 Negative-test the guard end to end: revert §3.6, watch the guard fail naming the exact file
      and line, restore it, watch the suite return to green
      — done twice, once per revert shape, and the first attempt is the reason the detector has the
      rule it has.

      **Shape 1 — alias the private field** (`public List<string> ReceivedMessages => _receivedMessages;`).
      The guard **passed**. Rule 1 keyed on the member's own name, and the mutation was on the field.
      Rule added, re-run:

      ```
      Tests/Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests/Internal/RealtimeFakeServer.cs:72
        [MutableCapture]  'ReceivedMessages' is a mutable collection the fake itself writes, …
      ```

      **Shape 2 — the literal §3.6 pre-state** (`public List<string> ReceivedMessages { get; } = [];`,
      receive loop calling `ReceivedMessages.Add`):

      ```
      Tests/Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests/Internal/RealtimeFakeServer.cs:53
        [MutableCapture]  'ReceivedMessages' is a mutable collection the fake itself writes, …
      ```

      Restored after each; `git diff` on the fake is empty and the Governance project is **116/116
      green**.

### The second detector, handed here by ADR-0052 (2026-08-19)

ADR-0052 closed the E6-vs-`test-determinism` contradiction and left three items it deliberately did
not scope, all of the same class §5 already builds for. They land here rather than in their own
change because **the scaffolding is the expensive part, not the detector** — §5.2–§5.6 build a
scanner, a guard test, true-positive and false-positive unit tests and a liveness self-test, and a
second detector arriving after this change closes would rebuild all five.

- [ ] 5.7 Add a cancellation-token-provenance detector to `Tests/Verbara.Sdk.Governance.Tests/`,
      reusing the scanner scaffolding §5.2 builds rather than standing up a second one: in a test
      method that cancels a `CancellationTokenSource`, the enumeration of the subject MUST NOT
      receive that token. `ToListAsync(ct)`, `ToArrayAsync(ct)` and `WithCancellation(ct)` are the
      reported forms (ADR-0052 F3)
- [ ] 5.8 Detector unit tests — true positive: a `.ToListAsync(cts.Token)` in a method that cancels
      `cts` is reported with a 1-based line number and the file named in the failure message
- [ ] 5.9 Detector unit tests — false-positive immunity: `ToListAsync(CancellationToken.None)`, a
      no-argument `ToListAsync()`, and a `ToListAsync(ct)` in a method whose token is never cancelled
      are NOT reported. The last one matters most — a token that is only ever a hang bound is the
      legitimate case, and a detector that cannot tell it apart will be muted
- [ ] 5.10 Negative-test the detector against history rather than against a fixture: restore the ten
      pre-fix cancellation tests from `c4756fbd^`, run the detector, confirm it reports **exactly
      those ten**. A guard that cannot re-find the defect it was written for is not evidence, and
      this is the one defect whose full extent is already known
- [ ] 5.11 Amend the living `test-determinism` TTS cancellation requirement via the delta in
      `specs/test-determinism/spec.md` (see the `## MODIFIED Requirements` block). Two defects: its
      pre-cancelled scenario instructs the exact pattern §5.7 detects — *"WHEN the stream is
      enumerated (e.g. `ToListAsync(ct)`)"* — and its provider list closes at "(Deepgram, ElevenLabs,
      Lmnt)" beneath a normative sentence binding every TTS synthesizer, which is how Speechmatics
      TTS and LMNT-over-HTTP went uncovered entirely. **§5.11 gates §5.7:** a guard that contradicts
      the spec it enforces gets deleted as a false positive by the next reader

## 6. Decision record and docs

- [ ] 6.1 Write `docs/decisions/0045-websocket-fake-protocol-contract.md` — the three defect classes
      as rules plus the substrate rule, with the concrete instances as evidence. Related: ADR-0009
      (three-tier test pyramid), ADR-0014 (raw `ClientWebSocket` for VoiceAi providers), ADR-0041
      (transport split; the WebSocket surfaces stay in-process), ADR-0044 (IPv4 loopback literal),
      verbara-meta/ADR-0004 (deterministic-test-fences programme — the net-new-only barrier ratchet
      the `sync-fence-baseline.json` comment refers to; note that this repo's own ADR-0004 is central
      package management, so the citation must stay repo-qualified per ADR-0037)
- [ ] 6.2 Add the ADR-0045 row to `docs/decisions/README.md`, in numeric order
- [ ] 6.3 `CHANGELOG.md` — one entry under `[Unreleased]` in the existing `### Fixed — Tests` shape.
      **No `Directory.Build.props` version bump**: test-only, ships with the next release train
- [ ] 6.4 Record the follow-up explicitly rather than leaving it implied: the remaining WebSocket
      surfaces not swept for Class B/C. Name them, and say that sweeping them is a separate change

## 7. Verification

- [ ] 7.1 `dotnet build Verbara.Sdk.slnx` — 0 warnings, 0 errors (`TreatWarningsAsErrors`)
- [ ] 7.2 `dotnet test Verbara.Sdk.slnx --filter "Category!=Functional&Category!=Integration&Category!=Realtime&Category!=Spike"`
      green — the four-exclusion filter CI actually uses (`ci.yml`), not the two documented in
      `CLAUDE.md`
- [ ] 7.3 30× repeat-run determinism protocol on the OpenAiRealtime suite, and again under CPU
      saturation. Compare against §1.5
- [ ] 7.4 Measured wall clock before/after, same machine, same configuration, ≥3 runs each — report
      the spread, not a single pair. State plainly if the delta is smaller than the run-to-run noise
      floor instead of claiming a win the numbers do not support
- [ ] 7.5 `openspec validate websocket-fake-protocol-contract --type change --strict` clean
- [ ] 7.6 CI green on the PR, zero warnings; enqueue with `gh pr merge <pr> --auto` (merge queue —
      never `--squash`/`--delete-branch`)
