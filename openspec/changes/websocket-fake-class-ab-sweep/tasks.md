# Tasks: websocket-fake-class-ab-sweep

Eight fakes, the same five steps each. The order below sweeps STT first because those four have no
protocol sentinel at all and therefore carry the larger unknown.

## 1. Baseline before touching anything

- [x] 1.1 Record the current wall clock of `Verbara.Sdk.VoiceAi.Stt.Tests` and
      `Verbara.Sdk.VoiceAi.Tts.Tests` over 30 runs each, `-c Release --no-build`. A tight spread is
      the signature of fixed timeouts dominating; a wide one is real work. Note which it is —
      the *after* claim must be measured through this same harness, not against a number recorded
      at a different point.

      **Measured (30 runs each, `-c Release --no-build`, idle, 0 failures):**

      | suite | tests | min | median | max | spread | stdev |
      |---|---:|---:|---:|---:|---:|---:|
      | `Stt.Tests` | 125 | 468 ms | **496 ms** | 527 ms | 59 ms (11.9 % of median) | 13.4 |
      | `Tts.Tests` | 149 | 414 ms | **455 ms** | 493 ms | 79 ms (17.3 % of median) | 16.1 |

      **Verdict: real work, not fixed timeouts.** The discriminator is the *relative* spread. The
      converted Realtime suite ran 25.87–26.06 s over thirty runs — a 0.19 s spread on a 26 s
      median, 0.7 %, which is five tokens expiring on schedule. These two sit at 12 % and 17 % of a
      half-second median, which is scheduling noise on a suite that is doing its work and stopping.
      **So the wall-clock claim this change can make is already bounded at "nothing to recover":**
      there is no fixed-timeout mass to remove, and §5.4 must state that as zero rather than
      searching for a number.

      One contaminated run was discarded rather than reported: the first attempt had the Stt
      assembly rebuilt underneath it at run 24/30 while instrumentation was being added. Re-run
      clean from a restored tree.

- [x] 1.2 For each of the eight, instrument the session handler's entry and return and record the
      measured return time. This is what surfaced the 4 987–4 992 ms concurrent-receive collision in
      the converted suite; reading the code did not.

      Instrumented at the **substrate** rather than in eight places: `WebSocketTestServer`
      `HandleConnectionAsync` is the single call site of every fake's handler
      (`await _onConnection(session)`), so one temporary patch times all eight and attributes each
      session by the handler target's type. Temporary — removed before any commit.

      **Measured session-handler return time, one full pass of each suite:**

      | fake | sessions | min | median | max | > 1 000 ms |
      |---|---:|---:|---:|---:|---:|
      | `AssemblyAiFakeServer` | 19 | 0.1 ms | 0.3 ms | 24.8 ms | 0 |
      | `CartesiaFakeServer` (STT) | 17 | 0.0 ms | 0.4 ms | 24.7 ms | 0 |
      | `DeepgramFakeServer` | 16 | 0.0 ms | 0.3 ms | 24.8 ms | 0 |
      | `SpeechmaticsFakeServer` | 19 | 0.1 ms | 0.4 ms | 29.3 ms | 0 |
      | `CartesiaFakeServer` (TTS) | 17 | 0.0 ms | 0.4 ms | 24.7 ms | 0 |
      | `DeepgramTtsFakeServer` | 13 | 0.2 ms | 0.4 ms | 22.7 ms | 0 |
      | `ElevenLabsFakeServer` | 18 | 0.1 ms | 0.4 ms | 22.7 ms | 0 |
      | `LmntWsFakeServer` | 62 | 0.0 ms | 0.1 ms | 22.9 ms | 0 |

      **The 4 987–4 992 ms collision is absent from all eight**, measured rather than assumed. No
      handler anywhere in either suite spends more than 30 ms. That answers the proposal's open
      question in the direction it said to accept if that is what the measurement gives.

- [x] 1.3 Grep each fake's `CloseAsync` site for an outstanding `ReceiveAsync` on the same socket at
      that moment. Record present/absent per fake — the answer is expected to differ.

      Grepping answered the structural half and measurement answered a question the grep could not
      reach. Both are recorded because they disagree in an interesting way.

      **Structural (present/absent), as asked:**

      - **The four STT fakes: absent.** Their receive loop is *inline* in `HandleSessionAsync`, not a
        `Task.Run`. `CloseWithConfiguredStatusAsync` runs only after that loop has exited, so there
        is exactly one receiver on the socket at all times.
      - **The four TTS fakes: present.** Each starts `receiveTask = Task.Run(...)` and calls its
        close helper while that loop still has a `ReceiveAsync` outstanding; the drain
        (`await receiveTask`) comes *after* the close.

      **Measured, by instrumenting the close call itself with the entry state, the branch taken, the
      elapsed time and the exception type and message — one full pass of each suite, 100 close
      attempts:**

      | fake | entry state → outcome |
      |---|---|
      | `AssemblyAiFakeServer` | 15× `Aborted` → **body skipped**; 3× `Open` → `WebSocketException: The remote party closed the WebSocket connection without completing the close handshake.` |
      | `CartesiaFakeServer` (STT) | 13× skipped; 4× `WebSocketException` (same message) |
      | `DeepgramFakeServer` | 12× skipped; 3× `WebSocketException` (same message) |
      | `SpeechmaticsFakeServer` | 15× skipped; 3× `WebSocketException` (same message) |
      | `CartesiaFakeServer` (TTS) | 15/15 `Open` → `OperationCanceledException: Aborted` |
      | `DeepgramTtsFakeServer` | 12/12 `Open` → `OperationCanceledException: Aborted` |
      | `ElevenLabsFakeServer` | 17/17 `Open` → `OperationCanceledException: Aborted` |
      | `LmntWsFakeServer` | 11/11 `Open` → `OperationCanceledException: Aborted` |

      **Not one close handshake completes anywhere in either suite — 0 of 100.** The two failure
      modes split exactly along the STT/TTS line, which is the line the structural finding drew:

      - **STT (single receiver, close is safe):** in ~79 % of sessions the socket is already
        `Aborted` when the helper runs, so the `if (ws.State is Open or CloseReceived)` guard skips
        the whole body and `CloseStatus`/`CloseStatusDescription` never reach the wire at all. In
        the rest the peer has gone and `CloseAsync` throws.
      - **TTS (concurrent receiver, close collides):** entry state is `Open` every single time and
        `CloseAsync` throws `OperationCanceledException("Aborted")` immediately — median 0.2 ms.
        `Aborted` is what a `ManagedWebSocket` says when the socket was aborted out from under a
        pending operation, which is the concurrent-receive collision arriving as an abort instead of
        as the converted suite's 4.99 s stall.

      **What still works, and why the suites are green:** eight tests (one per fake) set
      `CloseStatus`/`CloseStatusDescription` and assert the client read that code and reason —
      `failure.Code.Should().Be("1008")`, `.Should().Contain("Missing sample_rate")`. They pass, so
      the close *frame* does reach the peer; it is only the wait for the peer's reply that never
      completes. Same shape as the finding behind ADR-0045 — the close frame lands, the handshake
      does not — with the cost here being 0.2 ms rather than 4.99 s.

      **Consequence for the sweep:** `CloseAsync` is the wrong call in all eight. `CloseOutputAsync`
      sends the frame without taking the receive path, which is what the converted fake already
      does, and the eight close-code tests are the evidence that the frame is the part that matters.
      Carried into §3 and §5 rather than done here, so the change stays measure-then-act.

## 2. STT fakes (no sentinel today)

For each of `AssemblyAiFakeServer`, `CartesiaFakeServer` (STT), `DeepgramFakeServer`,
`SpeechmaticsFakeServer`:

- [x] 2.1 Identify what actually sequences the fake with the client today, and write it down before
      changing it. "Nothing identified" is a finding, not a blocker.

      **The proposal's table is wrong in both directions.** It records "no protocol sentinel" for
      all four. Reading the four handlers end to end, three have *more* sequencing than claimed and
      one has sequencing the table denies outright:

      | fake | what gates the *greeting* | what gates the answer to *end of input* | shape |
      |---|---|---|---|
      | `AssemblyAiFakeServer` | nothing — `Begin` goes out on connect | receive loop, `Text` branch → `Termination` + `CloseOutputAsync` | inline loop |
      | `CartesiaFakeServer` (STT) | nothing — transcripts go out on connect | receive loop, `Text == "done"` → recorded ack + `CloseOutputAsync` | inline loop |
      | `DeepgramFakeServer` | nothing — `Results` go out on connect | receive loop, `Text` branch → `Metadata` + `CloseOutputAsync` | inline loop |
      | `SpeechmaticsFakeServer` | **blocking `ReceiveAsync` for `StartRecognition`** (`Speechmatics/SpeechmaticsFakeServer.cs:176`) | receive loop, `Text` branch → `EndOfTranscript` + `CloseOutputAsync` | inline loop |

      Three findings, none of which the table anticipated:

      1. **Every one of the four already answers on a protocol sentinel** — the client's
         unconditional end-of-input frame (`Terminate` / `"done"` / `CloseStream` / `EndOfStream`).
         It is spelled as an inline `while (ws.State is Open or CloseSent)` receive loop rather than
         as a `TaskCompletionSource`, which is why a shape-based reading missed it. The rule
         ADR-0045 states is *answer on protocol, not on a clock*; the loop satisfies it exactly.
      2. **`SpeechmaticsFakeServer` has a second, pre-greeting sentinel** the table calls absent. It
         blocks on `ReceiveAsync` until `StartRecognition` arrives before sending
         `RecognitionStarted`, because Speechmatics' wire protocol genuinely is two-phase. This is
         the single strongest sentinel in either suite and it was recorded as "none".
      3. **The three that answer on connect are correct to do so.** `Begin`, `Results` and Cartesia's
         transcripts are server-initiated on the real wire too — the vendor sends them unprompted.
         Gating them on a client frame would invent a protocol dependency that does not exist, and
         the fake would then be asserting a contract the service does not honour.

      **What is genuinely missing is a *ceiling*, not a sentinel.** `session.ServerCancellationToken`
      is the `WebSocketTestServer`'s own `_cts` (`WebSocketTestServer.cs:35`, cancelled only in
      `DisposeAsync` at :214). That is the correct token per ADR-0045 rule 2 — but it is not a bound.
      A fake waiting on a frame the client never sends does not fail with "the protocol assumption is
      wrong"; it blocks until the test disposes it, and the suite reports a hang. That is the one
      half of task 2.2 that applies here.

      **A fourth fence nobody has watched fail:** `CloseSent` in each loop condition. All four carry
      the same comment claiming that without it "the half-close test passed against a client that
      half-closed" — a defect claim the tree makes in prose, in four places, with no recorded
      failure behind it. §2.3 negative-tests it.
- [x] 2.2 Replace it with a `TaskCompletionSource` sentinel on the client's first unconditional
      frame, bounded by a generous timeout so expiry means the protocol assumption is wrong rather
      than that the machine was busy.

      **Half of this step was written against a premise §2.1 disproved, and that half was not done.
      The other half was, on all four fakes.**

      *The `TaskCompletionSource` rewrite: not done, deliberately.* The step assumes no sentinel
      exists. §2.1 measured that all four already answer on the client's unconditional end-of-input
      frame — the wait is spelled as an inline `while (ws.State is Open or CloseSent)` receive loop
      rather than as a TCS. Converting the spelling buys nothing and costs something real: a TCS
      exists to bridge a **concurrent** receive loop to a handler that must send while it reads,
      which is the TTS shape (`DeepgramTtsFakeServer` starts `StartReceiveLoopAsync` on a separate
      task and then awaits `_requestComplete`). The four STT handlers are single-threaded — the loop
      *is* the main path — so a TCS would have no rendezvous to bridge and would import the TTS
      suite's concurrency into four fakes that do not need it. Requirement 1 is about a fence's
      **evidence**, not its spelling; §2.3 supplies the evidence for the loop as written.

      *The ceiling: done, and it is the half §2.1 named as genuinely missing.* Neither side of an STT
      session carried any bound. The fake's `ReceiveAsync` has no read timeout and the recognizer's
      receive loop has none either, so a client that never sends the terminator parks both.

      **Measured before it was built, so the fence is not speculative.** Suppressing the terminator
      in `DeepgramSpeechRecognizer.SendLoopAsync` — one token, `WebSocketMessageType.Text` →
      `Binary`, so the frame still crosses the socket but the fake's `Text` branch never fires:

      | run | result |
      |---|---|
      | unbounded, one test (`StreamAsync_ShouldSendAudioFrames`) | still running at a 90 s kill |
      | unbounded, whole class (18 tests) | still running at a 600 s kill |
      | restored client, same class + siblings (21 tests) | `Passed! — Failed: 0, Passed: 21, Duration: 101 ms` |

      An unbounded park with no diagnostic on either side, against 101 ms for the same tests. The
      client edit was reverted; it exists only in this measurement (`git diff --stat src/` empty).

      **What was added, in all four fakes:** a `private static readonly TimeSpan
      SessionReceiveCeiling = TimeSpan.FromSeconds(10)` with the arithmetic in its `<remarks>` (the
      whole 125-test suite runs in 4-6 s under CPU saturation, so well under 100 ms per session —
      the ceiling is two orders of magnitude above the observed need, because expiry has to mean
      "the protocol assumption is wrong", never "the runner was busy"), and a linked
      `CancellationTokenSource` + `CancelAfter` whose token replaces `ct` on the loop's
      `ReceiveAsync`. `SpeechmaticsFakeServer` takes its ceiling at handler entry instead, because
      it has a **second** unbounded wait — §2.1's finding 2, the pre-greeting `StartRecognition`
      `ReceiveAsync` — and one linked source covers both.

      **The `fence-allow: GUARD-TIMEOUT` marker on it is a label, not an exemption**, and each site
      says so in as many words. `SyncFenceScanner`'s `BannedCalls` is exactly
      `Task.Delay`/`Thread.Sleep`/`Thread.SpinWait`/`SpinWait.SpinUntil`, so `CancelAfter` is
      invisible to the ratchet — the same blindness §4.1 recorded. The marker is there so
      `grep fence-allow` still enumerates every deliberate timed arm in the tree. `sync-fence-baseline.json`
      is unchanged and the Governance suite is 129/129 green.

      **What the ceiling does not buy, stated because a later reader will assume it does.** The
      failure a bounded expiry produces is the generic transport one —
      `SpeechProviderFailureException : Deepgram: the connection failed mid-session, so the result is
      incomplete.` / `WebSocketException : The remote party closed the WebSocket connection without
      completing the close handshake.` It does not say "no terminator arrived". The legible signal is
      the **duration**: every affected test fails at exactly the ceiling. Sharpening it — closing
      with a distinct status naming the ceiling — was considered and rejected: these suites already
      assert on the fake's `CloseStatus` knob for the abnormal-close cases, and a second writer of
      that value would be a new unwatched fence in a change whose thesis is that unwatched fences are
      not evidence.

- [x] 2.3 Negative-test it: remove the wait, confirm a dependent test fails; restore it, confirm it
      passes. Record both outcomes.

      **Two fences were negative-tested here: the receive loop itself (§2.1's finding 1) and the
      ceiling added by §2.2. Per requirement 2 the results are per fake, not per class.**

      *The receive-loop sentinel — measured by the four sibling agents, one per fake, idle and under
      CPU saturation (48 spinners, load avg 80-172). All four **HOLD**.*

      | fake | fence A (end-of-input sentinel) | idle | saturated | verbatim failure |
      |---|---|---|---|---|
      | `AssemblyAiFakeServer` | receive loop, `:211` | 6/6 red | 5/5 red | `Expected _server.ReceivedTerminatorText to be "{"type":"Terminate"}", but found <null>.` · `Expected _server.AudioMessageByteCounts to be equal to {320, 320}, but found empty collection.` (+4 more) |
      | `CartesiaFakeServer` (STT) | receive loop, `:222` | 6/6 red | 5/5 red | `Expected _server.ReceivedTerminatorText to be "done", but found <null>.` · `… because the acknowledgement only follows the terminator, but found <null>.` |
      | `DeepgramFakeServer` | receive loop, `:189` | 6/6 red | 5/5 red | `Expected _server.ReceivedFrameCount to be greater than or equal to 3, but found 0 (difference of -3).` · `Expected _server.ReceivedTerminatorText to be "{"type":"CloseStream"}", but found <null>.` |
      | `SpeechmaticsFakeServer` | receive loop, `:237-272` | 6/6 red | 5/5 red | `System.ArgumentNullException : Value cannot be null. (Parameter 'json')` — `JsonDocument.Parse(_server.ReceivedEndOfStreamJson!)` with the capture null |
      | `SpeechmaticsFakeServer` | **fence C**, pre-greeting `StartRecognition` wait, `:173-180` | 6/6 red | 5/5 red | `Expected _server.ReceivedStartRecognitionJson not to be <null> or empty, but found <null>.` |

      Every one restored green under the *same* saturated regime (control runs 5/5 at 125/125), so
      the red is the fence's and not the load's. **None of these is load-gated** — unlike three of
      the four TTS sentinels in §3.1, which were green idle and red only saturated. The mechanism is
      visible: an answered-request sentinel's absence leaves a capture property permanently `null`,
      which is a deterministic assertion failure, not a race the scheduler can win.

      *The `CloseSent` fence in the loop condition (§2.1's "fourth fence nobody has watched fail") —
      **HOLDS-NOTHING** on all four, and this is the sweep's most load-bearing negative result.* It
      is recorded under §2.3 rather than skipped because "checked and absent" and "not checked" must
      not collapse into one sentence.

      | fake | runs with `CloseSent` removed | probe |
      |---|---|---|
      | `AssemblyAiFakeServer` | 19 green | marker file: `P1-loop-iteration-in-CloseSent` 15 hits/run, `P2-close-branch-reached` **0** |
      | `CartesiaFakeServer` (STT) | 16 green | `Environment.FailFast` + positive control |
      | `DeepgramFakeServer` | 27 green | two `FailFast` probes: loop *is* re-entered in `CloseSent` (host killed 3/3), Close branch never reached |
      | `SpeechmaticsFakeServer` | 6 idle + 8 saturated green | throwing probe, 625 test executions, `CLOSE-BRANCH-REACHED` 0 — validated by the positive control in §3.2b |

      **The prose claim duplicated verbatim in all four files is TRUE and unwitnessed.** Two agents
      proved it the only way it can be proved — by changing the subject. Reinstating the
      pre-remediation half-close in the production client (`DeepgramSpeechRecognizer.SendLoopAsync`,
      an added `CloseOutputAsync` right after the terminator) turns the fence into a live guard:
      fence ON → 3/3 runs red, always
      `StreamAsync_ShouldNotHalfCloseTheOutputSide_WhenInputEnds`, verbatim `Expected
      _server.ReceivedClientCloseFrame to be False, but found True.`; fence OFF → 3/3 runs green, the
      fake reporting the half-closing client as clean. Cartesia's agent reproduced the same pair
      independently. Both client edits were reverted.

      So `CloseSent` is a **regression tripwire for a client that no longer exists** — no recognizer
      under `src/Verbara.Sdk.VoiceAi.Stt/` calls `CloseOutputAsync` or `CloseAsync` at all; the sole
      grep hit is a past-tense comment at `CartesiaSpeechRecognizer.cs:153` ("A bare half-close stood
      here"). And the assertion it feeds is **one-way**:
      `ReceivedClientCloseFrame.Should().BeFalse()` is the only read of that member in each suite.
      Removing the fence does not turn that test red — **it turns it vacuous**, which no pass/fail
      sweep can see. That distinction is the reason requirement 1 exists, and it is why the four
      fences are recorded as HOLDS-NOTHING *with the prose kept*, not deleted.

      *The ceiling added by §2.2 — **HOLDS**, with the failure watched on the way in and out.* Same
      probe as §2.2's measurement (terminator sent as `Binary`), now against the bounded fakes:

      - **with the ceiling**: `Failed! — Failed: 12, Passed: 6, Total: 18, Duration: 2 m`, and
        **every one of the 12 failed at exactly `[10 s]`** — the ceiling's own value. Verbatim:
        `Verbara.Sdk.VoiceAi.SpeechProviderFailureException : Deepgram: the connection failed
        mid-session, so the result is incomplete.` / `---- System.Net.WebSockets.WebSocketException :
        The remote party closed the WebSocket connection without completing the close handshake.`
      - **without it** (the state before this change): the same class ran past a 600 s kill and a
        single test past 90 s, with no output at all.
      - **restored client, ceiling in place**: `Passed! — Failed: 0, Passed: 125, Total: 125,
        Duration: 474 ms` on the full suite, and 517 ms on the run before it.

      That is the whole point of the step: the fence's presence and its absence now have different,
      recorded observable shapes.

      **The compiler is not a backstop here either.** As in §3.1, deleting any of these waits leaves
      a build that is 0 warnings under `TreatWarningsAsErrors` — with one trap worth naming, because
      it can be mistaken for a fence result: deleting a receive loop removes the only `ref` write to
      `_receivedFrameCount` and fails the build on **CS0649** ("Field is never assigned to"), not on
      the CA1822 the task brief predicted. The sibling agents neutralised it with
      `_ = Interlocked.Exchange(ref _receivedFrameCount, 0);`, which writes the field's existing
      default and changes nothing.

- [x] 2.4 Add a hold-open path parked on the fake's own token **only if** the suite has a
      cancellation test that needs the socket alive when the token fires. Do not add one
      speculatively — an unused flag is another fence nobody watches.

      **None added, on all four. The condition is not met, and it is not met identically in all four
      suites — which is a finding, not an absence.**

      Every STT suite has exactly one cancellation test, `StreamAsync_ShouldAbort_WhenCancelled`
      (`AssemblyAiSpeechRecognizerTests.cs:483`, `CartesiaSpeechRecognizerTests.cs:408`,
      `DeepgramSpeechRecognizerTests.cs:320`, `SpeechmaticsSpeechRecognizerTests.cs:554`), and all
      four hand `StreamAsync` a **pre-cancelled** token. Each recognizer calls
      `ct.ThrowIfCancellationRequested()` at iterator entry before its `ConnectAsync`
      (`AssemblyAi` :69/:96, `Cartesia` :53/:72, `Deepgram` :45/:61, `Speechmatics` :53/:67), so **no
      session is ever opened** and the fake's socket is never touched. Each test then asserts
      `_server.ReceivedFrameCount.Should().Be(0)`, which is consistent with that.

      Falsified empirically rather than argued, in the idiom §3.3 used for `ElevenLabsFakeServer`:
      disposing the whole fake immediately before the act leaves the test green —
      `Passed! — Failed: 0, Passed: 1, Total: 1, Duration: 17 ms` (measured on
      `DeepgramSpeechRecognizerTests`; the probe was reverted). The other three are established
      structurally by the `ThrowIfCancellationRequested`-before-`ConnectAsync` ordering above and
      were **not** individually re-measured — named here rather than folded into the measured one.

      Verdict per fake: **NOT-APPLICABLE**, not `HOLDS-NOTHING`. The latter would assert a timing
      dependency the measurement shows is absent.

      **The real gap this exposes is coverage, not a missing flag**, and it is the same one §3.3
      recorded on the TTS side: *no suite in either the STT or the TTS tree cancels a session that is
      already streaming.* Eight fakes, eight cancellation tests, and every one of them throws before
      the socket opens. A mid-stream cancellation test is what would give a hold-open path a
      consumer — and it is exactly the test that would have made
      `DeepgramTtsFakeServer.HangForever` (§3.2, unreachable) and
      `LmntWsFakeServer.HoldOpenUntilDisposed` (§3.2a, unfalsifiable) falsifiable. Routed as a
      follow-up under §5.7 rather than built here: it is new coverage, not a fence this sweep can
      evidence.

## 3. TTS fakes (sentinel present, unverified)

For each of `CartesiaFakeServer` (TTS), `DeepgramTtsFakeServer`, `ElevenLabsFakeServer`,
`LmntWsFakeServer`:

- [x] 3.1 Negative-test the existing sentinel. This is the whole point for these four: the shape is
      already right, and what is missing is evidence that it holds.

      **All four sentinels HOLD.** Each was deleted, the suite run in two regimes, restored, and
      re-run green. Suite total stayed 149 in every run — no test was ever lost, only failed.

      | fake | sentinel | idle | saturated | verdict |
      |---|---|---|---|---|
      | `CartesiaFakeServer` (TTS) | `Task.WhenAny(requestReceived, …)` :263 | 18/27 green | — | **HOLDS** (9/27 red overall) |
      | `DeepgramTtsFakeServer` | `WaitForRequestOrTimeoutAsync` :199 | **6/6 green** | **5/5 red** | **HOLDS** |
      | `ElevenLabsFakeServer` | `Task.WhenAny(endOfInputReceived, …)` :250 | **6/6 green** | **9/10 red** | **HOLDS** |
      | `LmntWsFakeServer` | `_requestComplete` wait :203 | 3/18 green | — | **HOLDS** (15/18 red) |

      Recorded failures, verbatim (§5.5's evidence, and requirement 1's "names what actually broke"):

      - **Cartesia (TTS)** — three tests, all of them readers of `_server.ReceivedJsonMessages`:
        `SynthesizeAsync_ShouldSendADistinctContextId_PerRequest` → `Expected ids to contain 2
        item(s), but found 1: {"1d79a972-f951-46bc-add3-3c7aca6d2f1e"}.` (and, in other runs,
        `…but found 0: {empty}.`); `SynthesizeAsync_ShouldSendANonEmptyContextId_WhenTheEndpointRequiresOne`
        → `System.ArgumentOutOfRangeException : Index was out of range. Must be non-negative and
        less than the size of the collection. (Parameter 'index')`;
        `SynthesizeAsync_ShouldSendRequestJson_WithModelAndVoice` → `Expected
        _server.ReceivedJsonMessages not to be empty.`
      - **DeepgramTts** — one test: `SynthesizeAsync_ShouldSendSpeakMessageWithText` → `Expected
        speakMsg not to be <null>.`
      - **ElevenLabs** — one test, two distinct messages depending on how far the receive loop got:
        `SynthesizeAsync_ShouldSendTextChunk` → `Expected _server.ReceivedJsonMessages.Any(m =>
        m.Contains("hola mundo", StringComparison.Ordinal)) to be True, but found False.` (8 of 9
        red runs) and `Expected _server.ReceivedJsonMessages not to be empty.` (1 run). **A reader
        grepping for only one of these two strings would mis-classify a genuine fence failure.**
      - **Lmnt** — five tests in `LmntSpeechSynthesizerWsTests`, of which
        `SynthesizeAsync_WsInit_ShouldIncludeFlushAndEof_InSubsequentMessages` failed in all 15 red
        runs → `Expected allMessages "{…"speed":1}{"flush":true}" to contain ""eof"".`; also
        `SynthesizeAsync_ShouldSendInitMessage_WithApiKeyVoiceAndFormat` → `Expected init
        "{"text":"hello world"}" to contain ""X-API-Key"".`;
        `SynthesizeAsync_ShouldSendTextMessage_WithCorrectText` → `Expected
        textMessages.Any(m => m.Contains("hello lmnt", StringComparison.Ordinal)) to be True, but
        found False.`; `SynthesizeAsync_ShouldSendModelField_WhenModelIsConfigured` → `Expected init
        "{"text":"hello"}" to contain ""model":"blizzard"".` A fifth,
        `SynthesizeAsync_ShouldOmitModelField_WhenModelIsNotConfigured`, was seen red once but its
        assertion line was not captured, so it is named here without a quoted message rather than
        paraphrased.

      **The load finding, and it is the most transferable result of this change.** Three of the four
      sweeps independently discovered that *an idle run is not a measurement*. Deleting the
      ElevenLabs sentinel is green 6/6 on an idle 24-core box and red 9/10 under ten concurrent
      copies of the suite; Deepgram is 0/6 idle and 5/5 under 48 spinners; Cartesia's first three
      post-removal runs were green before it settled at 9/27. **Every one of those three would have
      been written down as `HOLDS-NOTHING` by a single-run protocol** — which is the same error the
      converting change made, running in the opposite direction: not "green for months while holding
      nothing" but "green once while holding plenty." Controls were run in both directions: the
      restored build under the identical load is 10/10 and 3/3 green, so the failures are the
      fence's and not the load's.

      **The compiler is not a backstop.** Deleting a sentinel leaves an orphaned private method
      (`WaitForRequestOrTimeoutAsync`) or an unreferenced field (`EndOfInputWaitCeiling`) and the
      Release build still reports 0 warnings under `TreatWarningsAsErrors`. Nothing in the toolchain
      would flag a future accidental removal of any of these four.

- [x] 3.2 Negative-test the hold-open flag where one exists (`DeepgramTtsFakeServer`,
      `LmntWsFakeServer`) — clear it, confirm the cancellation test fails on the live socket state,
      restore it, confirm it passes.

      **Neither hold-open holds. Both are Class B in the purest form the change predicted, and for
      two different reasons.**

      **`DeepgramTtsFakeServer.HangForever` — unreachable.** Replacing its
      `Task.Delay(Timeout.Infinite, ct)` park with `await receiveTask` (the exact Class B trap) is
      green 6/6, idle *and* saturated. The reason was proven, not inferred: a repo-wide grep for
      `HangForever` hits only its declaration (:140) and its own `if` (:201) — **no test assigns
      it**, so the branch is unreachable by construction. The grep is the whole proof, and it has to
      be: the agent that measured this fence also justified it with a reachability probe
      (`throw new NotSupportedException("FENCE B REACHED")` in place of the branch body, suite green
      at 149/149), and **that evidence is void** — see §3.2b. The branch is dead code. `CHANGELOG.md:1368` records that this flag "carried the Class B defect
      with zero consumers; corrected rather than left as a trap" — it was corrected in *shape* and
      never given a *consumer*, so what remains is a correctly-written fence that is never executed.

      **`LmntWsFakeServer.HoldOpenUntilDisposed` — reachable, live, and unobservable.** The Class B
      trap edit is green 10/10 on the full suite and 10/10 on the flag's own test in isolation
      (`SynthesizeAsync_ShouldAbort_WhenCancelled`,
      `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Lmnt/LmntSpeechSynthesizerTests.cs:438`, class
      `LmntSpeechSynthesizerWsTests` — the task brief above named it
      `SynthesizeAsync_ShouldThrow_WhenCancelled`, which does not exist). The mechanism is the
      interesting part: `src/Verbara.Sdk.VoiceAi.Tts/Lmnt/LmntSpeechSynthesizer.cs:266` records that
      the client's `CloseOutputAsync(NormalClosure)` after `eof` was *deliberately removed* (it cost
      all the audio against the live endpoint). With no client half-close and no client close frame,
      the receive loop never ends, so `await receiveTask` parks for exactly as long as
      `Task.Delay(Timeout.Infinite, ct)` does. **The fence is correct; the suite simply cannot see
      it.** `LmntWsFakeServer` exposes `ClientSentCloseFrame` — the *client's* behaviour — and
      nothing that records the *server's* socket state at the instant the test's token fired. The
      reference fake exposes exactly that (`RealtimeFakeServer.SocketState`), which is why its
      cancellation test can prove the socket was still open.

      This is the one place where the change's own spec delta obliges a fix rather than a note:
      requirement 1, scenario *"The recorded failure names what actually broke"*, requires the
      recorded failure to state "the live server-side socket state at the moment of the cancel."
      That is unsatisfiable for this fake as written. Task 3.2a below closes it.

      It also makes the prose claim at `LmntSpeechSynthesizerTests.cs:446` —
      "`HoldOpenUntilDisposed` is what actually enforces 'no close'" — an unverified assertion in
      the tree, the same species that `enforce-unguarded-public-claims` exists for.

- [x] 3.2a **(added mid-apply — discovered by 3.2, not planned)** Give `LmntWsFakeServer` a
      server-side socket-state observable in the `RealtimeFakeServer.SocketState` idiom, assert it in
      `SynthesizeAsync_ShouldAbort_WhenCancelled`, and re-run the 3.2 negative test to confirm the
      hold-open now flips the test red. Then delete `DeepgramTtsFakeServer.HangForever` and its dead
      branch: it has no consumer, and §2.4's rule — "an unused flag is another fence nobody watches"
      — applies to a flag that survived a correctness fix without ever gaining one.

      **Both halves of that plan were wrong, and working through the mechanism is what showed it.
      Recorded rather than quietly re-scoped.**

      *The observable does not falsify the fence, and claiming otherwise would have manufactured the
      exact evidence this change exists to reject.* The plan assumed asserting `SocketState == Open`
      at the cancel would go red once the hold-open was swapped for `await receiveTask`. It would
      not. The reason 3.2 measured is that **nothing ends the receive loop**: with no client
      half-close and no client close frame, `await receiveTask` parks for exactly as long as
      `Task.Delay(Timeout.Infinite, ct)`, so the socket is `Open` at the cancel under *both*
      spellings. No assertion available to this suite can tell them apart, because the condition the
      fence guards against — a client that half-closes or faults its read — does not occur in this
      tree at all. **`HoldOpenUntilDisposed` is not verified-correct and not broken; it is
      unfalsifiable**, and requirement 1's first scenario is explicit that such a fence "is treated
      as unverified regardless of how it reads."

      So what was actually done, and what each part is worth:

      - `LmntWsFakeServer` gained `SocketState` (the `RealtimeFakeServer` idiom: a `volatile`
        socket field, `WebSocketState? SocketState => _socket?.State`). It is a **real strengthening
        of a different claim** — it states the condition at the moment of the cancel, which is what
        requirement 1's second scenario asks for, and it would catch a future edit that let the
        stream end on the server's close and then credited cancellation with someone else's work.
        Its XML doc says in as many words that it does **not** distinguish the hold-open from its
        own absence, so no later reader mistakes the new assertion for the missing evidence.
      - `HoldOpenUntilDisposed`'s doc now records the measurement — 10/10 green with the Class B
        trap, and why — and calls the flag a latent guard that must not be cited as verified.
      - `LmntSpeechSynthesizerTests.cs:446`'s claim that the flag "is what actually enforces 'no
        close'" is gone, replaced by what measurement supports.

      *`HangForever` was not deleted.* Deleting it was the plan and it does not survive contact with
      the requirements. Nothing in the spec delta asks for removal — requirement 1 governs what a
      fence may be **counted** as, not whether it may exist — and §2.4's rule is about **adding** a
      speculative flag, not about removing a correctly-written one. Deleting it would also destroy
      the only ready-made facility for the mid-stream cancellation test this surface still lacks.
      What it needed was the honest label, so it now carries: a `fence-allow: GUARD-TIMEOUT` marker
      matching its `LmntWsFakeServer` twin, and an XML remark recording that **no test sets it**,
      that the proof is the two-hit repo-wide grep (declaration + its own `if`, no assignment), that
      a throwing probe is *not* valid evidence on this substrate and why, and that none of it may be
      counted as evidence this fake satisfies the hold-open rule.

      Verified: `Tts.Tests` builds 0 warnings / 0 errors under `TreatWarningsAsErrors`, and
      `LmntSpeechSynthesizerWsTests` is 18/18 green.

- [x] 3.2b **(added mid-apply — a methodology result that invalidated evidence already written)**
      Record why a throwing reachability probe is not proof on this substrate, and replace the one
      piece of evidence in this change that leaned on one.

      **`WebSocketTestServer` turns a session-handler throw into a silent session teardown, not a
      test failure.** `Tests/Verbara.Sdk.TestInfrastructure/WebSocket/WebSocketTestServer.cs:121-124`
      catches `Exception` around the handler with the comment *"Swallow per-connection failures; the
      test asserts on observable side effects"*, and the `finally` at :125-129 still calls
      `_sessionCompleted.TrySetResult()` — so even `SessionCompleted`, the suite's join point,
      completes exactly as it would have. Nothing bubbles to xunit. All eight fakes in this sweep
      (four STT, four TTS) run on this one server.

      **The consequence is positional, and that nuance is the finding.** A throw does abort the
      session, so at a *mid-session* branch it is loudly visible — the client's next read fails and
      tests go red. At an *end-of-session* branch it is indistinguishable from the session ending
      normally, because nothing needs the socket afterwards. Both fences this change wanted to probe
      by throwing sit at the end: the STT fakes' `CloseSent` close-branch, and
      `DeepgramTtsFakeServer.HangForever`'s hold-open. **A silent throwing probe there is not
      evidence of unreachability; it is no evidence at all.**

      Measured, not reasoned: the Speechmatics agent moved the same unprotected `throw` from the
      Close branch to the Text branch (`SpeechmaticsFakeServer.cs:256`, identical protection level)
      and the run went `Failed: 15, Passed: 110, Total: 125` —
      `SpeechProviderFailureException: Speechmatics: the connection failed mid-session, so the result
      is incomplete.` / `WebSocketException: The remote party closed the WebSocket connection without
      completing the close handshake.` Same throw, same handler, opposite visibility; only the
      position changed.

      **Probe idiom for this substrate, for anyone extending the sweep:** use
      `Environment.FailFast` (unswallowable — it kills the test host, which is unmistakable) or an
      append-to-scratch-file marker, and **always pair it with a positive control that makes it
      fire**. The AssemblyAi and Cartesia STT agents both arrived at this independently and used it;
      the Deepgram-TTS agent did not, which is how the void evidence entered.

      **What was corrected.** §3.2's `HangForever` verdict cited a `throw`-based probe leaving the
      suite green at 149/149, and the same sentence had already been written into
      `DeepgramTtsFakeServer.cs`'s XML `<remarks>`. Both now cite the proof that stands on its own: a
      repo-wide `grep -rn "HangForever" --include='*.cs'` returns exactly two hits, the declaration
      and its own `if` — **no assignment anywhere**, so the branch is unreachable by construction and
      no probe is needed. The verdict (HOLDS-NOTHING) is unchanged; only its evidence is.

- [x] 3.3 For `CartesiaFakeServer` (TTS) and `ElevenLabsFakeServer`, which have no hold-open flag,
      check whether their suites contain a cancellation test that silently depends on the server
      staying up. If one does, it is the Class B defect wearing a different absence.

      **Neither does, and for opposite reasons — which is exactly the non-uniformity requirement 2
      says must be preserved rather than flattened into one sentence.**

      **`ElevenLabsFakeServer`: checked, and the independence was *measured*, not read.** The folder
      holds one cancellation test, `SynthesizeAsync_ShouldAbort_WhenCancelled`
      (`ElevenLabsSpeechSynthesizerTests.cs:237`). It hands `SynthesizeAsync` a **pre-cancelled**
      token, and `src/Verbara.Sdk.VoiceAi.Tts/ElevenLabs/ElevenLabsSpeechSynthesizer.cs:50` calls
      `ct.ThrowIfCancellationRequested()` at iterator entry, before the connect at :56-68 — so no
      session is ever opened and the fake's socket is never touched. Falsified empirically rather
      than argued: disposing the whole server immediately before the act still leaves the test green
      (`Passed! - Failed: 0, Passed: 1, Total: 1, Duration: 15 ms`). Verdict **NOT-APPLICABLE**, not
      `HOLDS-NOTHING` — the latter would assert a timing dependency measurement shows is absent.

      **`CartesiaFakeServer` (TTS): there is no cancellation test to check.** A grep of
      `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Cartesia/` for `Cancel|Token|cts|CancellationTokenSource`
      returns **zero hits** across all 19 `[Fact]`s. Every test calls
      `SynthesizeAsync(text, format)` with the token defaulted. The four tests that end a session all
      end it on a **server**-side signal — `ws.Abort()`, close code 1008, an error frame, a clean
      close with no audio — so none of them can be attributing anything to cancellation. Verdict
      **NOT-APPLICABLE**: there is nothing for a hold-open to hold, and adding one would fence an
      empty case.

      **Deferred, and named here rather than left in PR prose:** Cartesia is the only WebSocket TTS
      provider in the suite with *no cancellation coverage at all* — ElevenLabs (:243), Lmnt (:452,
      :631), Speechmatics (:120) and Deepgram (:402) each build a `CancellationTokenSource` and pass
      its token into `SynthesizeAsync`. That is a coverage gap, not a fence defect, and it is out of
      scope for a sweep whose contract is "measure the fences that exist." Writing that test is what
      would make a hold-open on this fake necessary. **Not opened as a change here** — it belongs
      with the next provider-coverage proposal, and this entry is the record that it was found.

## 4. The test-side corollary

- [x] 4.1 Sweep both suites for a `CancellationTokenSource(delay)` whose expiry is the *normal* path
      to an assertion rather than a hang bound, and for `Task.Delay` used to let something settle.
      `sync-fence-baseline.json` lists the candidates; the entries for
      `Cartesia/CartesiaFakeServer.cs`, `ElevenLabs/ElevenLabsFakeServer.cs` and
      `Lmnt/LmntWsFakeServer.cs` currently sit at zero and were left alone as out of scope of the
      previous change — confirm that is still accurate rather than inheriting it.

      Swept every `Task.Delay` / `Thread.Sleep` / `SpinWait` / `new CancellationTokenSource(` /
      `DateTime.UtcNow +` in both suites. Full census — this is the whole population, not a sample:

      | site | what it is | scanner-visible | verdict |
      |---|---|:---:|---|
      | `Stt.Tests/Helpers/SttFrameGenerators.cs:24` | `Task.Delay(10)` pacing an endless generator | yes | already `fence-allow: LOOP-DRIVER` — correct |
      | `Tts.Tests/Cartesia/CartesiaFakeServer.cs:269` | third arm of a `WhenAny` ceiling | yes | already `fence-allow: GUARD-TIMEOUT` — correct |
      | `Tts.Tests/ElevenLabs/ElevenLabsFakeServer.cs:256` | third arm of a `WhenAny` ceiling | yes | already `fence-allow: GUARD-TIMEOUT` — correct |
      | `Tts.Tests/Lmnt/LmntWsFakeServer.cs:278` | `Task.Delay(Timeout.Infinite, ct)` hold-open | yes | already `fence-allow: GUARD-TIMEOUT` — correct |
      | `Tts.Tests/Deepgram/DeepgramTtsFakeServer.cs:206` | `Task.Delay(Timeout.Infinite, ct)` hold-open | yes | **unannotated** — the `LmntWsFakeServer:278` twin is annotated and this one is not. §4.2 target. |
      | `Tts.Tests/Lmnt/LmntSpeechSynthesizerTests.cs:458,464` | `DateTime.UtcNow + 5 s` deadline driving a `Task.Delay(5)` poll for `ReceivedJsonMessages.Count == 0` | yes | **the only wall-clock barrier in test code in either suite.** §4.2 target. |
      | `Tts.Tests/Lmnt/LmntWsFakeServer.cs:199` | `new CancellationTokenSource(RequestDrainTimeout)` | **no** | ceiling on a causal wait — the `_requestComplete` TCS is the winning arm. Legitimate, but see below. |
      | `Tts.Tests/Deepgram/DeepgramTtsFakeServer.cs:242` | `new CancellationTokenSource(RequestDrainTimeout)` | **no** | same shape, same verdict. |

      **The `CancellationTokenSource(delay)` half of this task has no scanner behind it.**
      `SyncFenceScanner` matches four call shapes only — `Task.Delay`, `Thread.Sleep`,
      `Thread.SpinWait`, `SpinWait.SpinUntil` (`Tests/Verbara.Sdk.Governance.Tests/SyncFenceScanner.cs:36-39`).
      A timed `CancellationTokenSource` is invisible to the ratchet, so the two above are unbaselined
      not because they were judged and cleared but because nothing looks. They *are* clean on
      reading — both are ceilings whose expiry is the failure path, not the assertion path — but that
      verdict came from this sweep, not from a gate, and a future one will need the same manual read.
      Recorded here rather than widened into a scanner change: that is a governance change and
      belongs in its own proposal.

      **Every other `new CancellationTokenSource(...)` in both suites is argless** (eight sites:
      `Whisper`, `Google`, `AzureWhisper`, `Speechmatics`, `AssemblyAi`, `Deepgram`, `Cartesia` STT
      recognizer tests, plus `ElevenLabs`/`Lmnt`/`Speechmatics`/`Deepgram` synthesizer tests). No
      test in either suite drives an assertion off a timer expiring. The "expiry as the normal path"
      defect this task hunts for **does not exist here** — the finding is the two `Task.Delay` sites
      above and nothing else.

      The three inherited zero rows re-verified rather than trusted: `Cartesia/CartesiaFakeServer.cs`,
      `ElevenLabs/ElevenLabsFakeServer.cs` and `Lmnt/LmntWsFakeServer.cs` each hold exactly one
      scanner-visible barrier and each one carries a valid `fence-allow`. Zero is accurate.
- [x] 4.2 Retire each one found onto the signal the test actually asserts. Where a barrier must
      stay, mark it `// fence-allow: <REASON> — <why>` using the closed enum
      (`SIMULATED-WORK|GUARD-TIMEOUT|SETTLE|LOOP-DRIVER`).

      Two sites, one of each kind — retired and annotated respectively.

      **Retired.** `SynthesizeAsync_ShouldAbort_WhenCancelled` polled
      `_server.ReceivedJsonMessages.Count == 0` every 5 ms against a `DateTime.UtcNow + 5 s`
      deadline. It now awaits `LmntWsFakeServer.FirstMessageReceived`, a `TaskCompletionSource` the
      fake's receive loop releases on the client's first recorded text frame — the protocol event
      itself rather than a timer sampling for its effect. The wait is bounded by a `FirstFrameTimeout`
      of 10 s whose expiry means the synthesizer never sent anything, which is the "generous bound,
      expiry means the assumption is wrong" shape §2.2 asks for. `DateTime.UtcNow` is gone from both
      suites, and so is the `TimeoutException` the poll threw. **This retires the fence rather than
      annotating it, which is the outcome 4.2 prefers** — the previous change's Lmnt row was the one
      test-code barrier left in either suite.

      One design note that is not incidental: the new trigger **captures the socket state, then
      cancels, then asserts** on the captured value after the throw. Asserting before the cancel
      would mean a surprising state skips `CancelAsync` and hangs the test instead of failing it.

      **Annotated.** `DeepgramTtsFakeServer.cs`'s `Task.Delay(Timeout.Infinite, ct)` hold-open got
      the marker its `LmntWsFakeServer` twin already had —
      `// fence-allow: GUARD-TIMEOUT — Timeout.Infinite; the cancellation token is the only arm` —
      plus the two-line explanation of why the timed arm can never win.

- [x] 4.3 Check every cancellation test in both suites against ADR-0052 F3 — the
      `CancellationProvenanceScanner` guard already fails the build on a cancelled token handed to
      the enumerator, so this is confirming the guard's verdict, not re-deriving it.

      `dotnet test Tests/Verbara.Sdk.Governance.Tests --filter "FullyQualifiedName~Cancellation"` —
      **13/13 green**, including after this change's edit to
      `SynthesizeAsync_ShouldAbort_WhenCancelled`. The guard walks the whole `Tests/` tree from the
      repo root with a `MinimumScannedFiles = 250` liveness floor
      (`CancellationProvenanceGuardTests.cs:17,23`), so its coverage is the enumeration and
      requirement 2's first scenario applies: this one may be claimed for both suites as a whole
      rather than per surface.

      The edit preserved the F3 property deliberately: the consumer still calls
      `ToListAsync(CancellationToken.None)` while only the subject receives `cts.Token`, so an
      `OperationCanceledException` seen by the assertion was raised by the synthesizer and not by the
      enumerator on the test's behalf. Read while there: every other cancellation test in both suites
      either pre-cancels before a subject that checks at iterator entry, or uses a bare
      `await foreach` with no `WithCancellation`. None hands a cancelled token to an enumerator.

- [x] 4.4 Delete any `sync-fence-baseline.json` entry that reaches zero, rather than leaving a
      zero-valued row behind.

      **Eight rows deleted; the file now has no zero-valued row at all.** 75 rows / sum 308 → **67
      rows / sum 306**.

      - The two that reached zero here: `Deepgram/DeepgramTtsFakeServer.cs` (1 → annotated) and
        `Lmnt/LmntSpeechSynthesizerTests.cs` (1 → retired).
      - The three §4.1 re-verified rather than inherited: `Cartesia/CartesiaFakeServer.cs`,
        `ElevenLabs/ElevenLabsFakeServer.cs`, `Lmnt/LmntWsFakeServer.cs`.
      - Three more found dangling at zero while doing it, from the previous change's suite:
        `VoiceAi.Tests/Pipeline/{VoiceAiPipelineTests,VoiceAiPipelineTtfaTests,VoiceAiPipelineTurnDetectorTests}.cs`.
        Each confirmed at zero by scan before removal. Deleting them is not scope creep but the same
        instruction applied consistently: leaving a zero row behind is what 4.4 forbids, and the
        ratchet treats a missing entry and a zero entry identically
        (`SyncFenceRegressionGuardTests.cs:46`, "missing file => baseline 0"), so the deletion
        changes nothing except that the file stops carrying rows that assert nothing.

      Guard re-run after the deletion: `--filter "FullyQualifiedName~SyncFence"` **19/19 green**.

## 5. Verification

- [x] 5.1 `dotnet build Verbara.Sdk.slnx` — 0 warnings, 0 errors.

      `Build succeeded. 0 Warning(s) 0 Error(s)` — Debug and, for §5.4's harness, Release as well.

- [x] 5.2 Unit lane green with the four-exclusion CI filter
      (`Category!=Functional&Category!=Integration&Category!=Realtime&Category!=Spike`).

      **30 assemblies, 3 275 tests, 0 failed, 32 s.** The two suites this change touches:
      `Verbara.Sdk.VoiceAi.Stt.Tests` 125/125 and `Verbara.Sdk.VoiceAi.Tts.Tests` 149/149.
      `Verbara.Sdk.Governance.Tests` 129/129 — the guard suite that owns `SyncFenceScanner`,
      `FakeServerCaptureScanner` and `CancellationProvenanceScanner`, so the `fence-allow` markers
      and the retired baseline rows are covered by it and not only by inspection.

      *Worth stating because it looks like a discrepancy and is not:* `Tts.Tests` reports **149**
      under this filter and **153 with 4 skipped** unfiltered. The four are
      `VoiceCatalogConformanceTests`, `[Trait("Category", "Realtime")]` and gated behind
      `RequiresVendorCredentialFactAttribute` — the filter drops them from the total, an unfiltered
      run counts and skips them, and 149 tests execute either way. §1.1's baseline and §5.4's
      after-measurement both use the filtered numbers, so they are like for like; §5.3's determinism
      runs are unfiltered, which is why they read 153.

- [x] 5.3 30× repeat-run determinism on both suites, idle and under CPU saturation
      (spinners at 2× core count; confirm they are reaped afterwards).

      | regime | runs | result | wall clock | per-run duration |
      |---|---|---|---|---|
      | idle | 30 × `Stt` + 30 × `Tts` | **60/60 green**, 0 failures | 73 s | `Stt` 445-521 ms, `Tts` 404-495 ms |
      | saturated (48 spinners on 24 cores, load avg 8 → 60) | 30 × `Stt` + 30 × `Tts` | **60/60 green**, 0 failures | 252 s | `Tts` 2 s × 30; `Stt` 2 s × 29 and 6 s × 1 |

      Every one of the 120 runs reported its full suite total (`Stt` 125, `Tts` 153 of which 4
      skipped, per §5.2) — no test was silently dropped in either regime. Zero logs across both directories contain `Failed!`.

      The single 6 s `Stt` run is the outlier worth naming rather than smoothing: it is 3× the
      saturated median and still 4× under the new 10 s `SessionReceiveCeiling`, which is the margin
      §2.2's arithmetic was chosen for. A ceiling set at the ElevenLabs suite's 5 s would have been
      inside striking distance of that run.

      Spinners reaped and verified: `ps -eo pid,pcpu,args | grep VERBARA_SWEEP_SPINNER` returns
      nothing, no busy-loop process appears in the top-CPU list, and load decayed 60 → 34 → idle
      after the kill.

- [x] 5.4 Like-for-like wall clock before/after through the §1.1 harness. State the delta against
      the measured noise floor — and state it as zero if that is what it is.

      Same harness as §1.1: 30 runs each, `-c Release --no-build`, same CI filter, idle.

      | suite | tests | median before | median after | delta | before spread | after spread | before stdev | after stdev |
      |---|---:|---:|---:|---:|---:|---:|---:|---:|
      | `Stt.Tests` | 125 | 496 ms | 478 ms | −18 ms (−3.6 %) | 59 ms | 56 ms | 13.4 | 14.2 |
      | `Tts.Tests` | 149 | 455 ms | 442 ms | −13 ms (−2.9 %) | 79 ms | 59 ms | 16.1 | 13.7 |

      **The delta is zero, and it is reported as zero.** Both movements are ~1 standard deviation of
      their own sample and roughly a quarter of the before-spread they sit inside — indistinguishable
      from run-to-run scheduling noise on a 24-core desktop that is also running a browser and a
      remote-desktop daemon. Calling −18 ms a 3.6 % improvement would be reading the noise floor as a
      result.

      This is the outcome §1.1 predicted and pre-committed to: the discriminator there was relative
      spread (12 % and 17 % of a half-second median, versus 0.7 % for the Realtime suite whose 26 s
      *was* five fixed timeouts), and it bounded the claim at "nothing to recover" before any fence
      was touched. **The value of this change is evidential, not temporal** — and the one place it
      does move the clock is in the opposite direction, on the failure path: §2.2's ceiling turns an
      unbounded hang into a bounded 10 s failure.

- [x] 5.5 Record per fake which fences were negative-tested and what each failure looked like. A
      converted fake with no recorded failure text has not been swept.

      **All eight fakes swept, thirteen fences negative-tested, every verdict backed by a recorded
      observation.** Full failure text lives under §2.3 (STT) and §3.1-§3.3 (TTS); this is the index.

      | fake | fence | verdict | evidence |
      |---|---|---|---|
      | `AssemblyAiFakeServer` | end-of-input receive loop `:211` | **HOLDS** | 11/11 red (6 idle + 5 sat), 6 tests, e.g. `Expected _server.ReceivedTerminatorText to be "{"type":"Terminate"}", but found <null>.` |
      | `AssemblyAiFakeServer` | `CloseSent` in loop condition | **HOLDS-NOTHING** | 19 runs green; marker probe: 15 loop-iterations-in-`CloseSent`/run, close branch **0** |
      | `CartesiaFakeServer` (STT) | end-of-input receive loop `:222` | **HOLDS** | 11/11 red, `Expected _server.ReceivedTerminatorText to be "done", but found <null>.` |
      | `CartesiaFakeServer` (STT) | `CloseSent` in loop condition | **HOLDS-NOTHING** | 16 runs green, `Environment.FailFast` probe + positive control |
      | `DeepgramFakeServer` | end-of-input receive loop `:189` | **HOLDS** | 11/11 red, `Expected _server.ReceivedFrameCount to be greater than or equal to 3, but found 0 (difference of -3).` |
      | `DeepgramFakeServer` | `CloseSent` in loop condition | **HOLDS-NOTHING** | 27 runs green; two `FailFast` probes — loop re-entered in `CloseSent` (host killed 3/3), close branch never reached |
      | `SpeechmaticsFakeServer` | end-of-input receive loop `:237-272` | **HOLDS** | 11/11 red, `System.ArgumentNullException : Value cannot be null. (Parameter 'json')` |
      | `SpeechmaticsFakeServer` | **fence C** — pre-greeting `StartRecognition` wait `:173-180` | **HOLDS** | 11/11 red, `Expected _server.ReceivedStartRecognitionJson not to be <null> or empty, but found <null>.` |
      | `SpeechmaticsFakeServer` | `CloseSent` in loop condition | **HOLDS-NOTHING** | 14 runs green, 625 probe executions, close branch 0 — probe validated by a positive control |
      | all four STT fakes | `SessionReceiveCeiling` (added by §2.2) | **HOLDS** | with: 12 failures at exactly `[10 s]`; without: >600 s with no output; restored: 125/125 in 474 ms |
      | `CartesiaFakeServer` (TTS) | `Task.WhenAny(requestReceived, …)` `:263` | **HOLDS** | 9/27 red, `Expected ids to contain 2 item(s), but found 1: {…}` (+2 more) |
      | `DeepgramTtsFakeServer` | `WaitForRequestOrTimeoutAsync` `:199` | **HOLDS** | 6/6 green idle, **5/5 red saturated**, `Expected speakMsg not to be <null>.` |
      | `DeepgramTtsFakeServer` | `HangForever` hold-open `:206` | **HOLDS-NOTHING** (unreachable) | repo-wide grep: 2 hits, declaration + its own `if`, **no assignment** (§3.2b) |
      | `ElevenLabsFakeServer` | `Task.WhenAny(endOfInputReceived, …)` `:250` | **HOLDS** | 6/6 green idle, **9/10 red saturated**, two distinct messages (§3.1) |
      | `ElevenLabsFakeServer` | cancellation-test independence | **NOT-APPLICABLE** | disposing the fake before the act leaves it green, 1/1 in 15 ms |
      | `LmntWsFakeServer` | `_requestComplete` wait `:203` | **HOLDS** | 15/18 red, 5 tests, `Expected allMessages "…{"flush":true}" to contain ""eof"".` |
      | `LmntWsFakeServer` | `HoldOpenUntilDisposed` `:278` | **HOLDS-NOTHING** (unfalsifiable) | 10/10 green with the Class B trap, full suite and in isolation; no client half-close exists to end the receive loop (§3.2a) |
      | `CartesiaFakeServer` (TTS) | cancellation coverage | **ABSENT** | the suite has no cancellation test at all — recorded in §3.3, not repaired here |
      | all four STT fakes | cancellation-test independence | **NOT-APPLICABLE** | pre-cancelled token throws before `ConnectAsync`; falsified on `Deepgram` (green with the fake disposed), structural for the other three (§2.4) |

      **Three things this table is deliberately shaped to keep visible**, per requirement 2's
      "non-uniform state is reported as non-uniform":

      1. **`HOLDS-NOTHING` and `NOT-APPLICABLE` are different verdicts.** The first means a fence
         exists and nothing watches it; the second means the dependency the fence would guard is
         measurably absent. Collapsing them would have turned five real findings into one wrong one.
      2. **Load-gating is a property of the fence, not of the suite.** Three of four TTS sentinels
         are green idle and red only saturated; **none** of the STT fences is load-gated. A
         single-regime protocol would have written three `HOLDS` down as `HOLDS-NOTHING`.
      3. **Two fences are correct and cannot be shown to be.** `HangForever` has no consumer;
         `HoldOpenUntilDisposed` has no condition in this tree that distinguishes it from its own
         absence. Both are labelled in source as latent guards that must not be cited as verified.

- [x] 5.6 `openspec validate --all --strict` green.

- [x] 5.7 Route anything found in `src/` to `voiceai-session-teardown-races` rather than fixing it
      here.

      **The named target no longer exists** — `voiceai-session-teardown-races` was archived into
      `streaming-session-lifecycle` (commit `064fe2bc`). Re-pointed rather than dropped.

      **One `src/` finding, and it is a measured one, not an inspection note.** Neither side of a
      VoiceAi STT session carries a read bound. §2.2 bounded the fake side (that is test code and in
      scope); the client side is not: `ReceiveLoopAsync` in all four recognizers under
      `src/Verbara.Sdk.VoiceAi.Stt/` has no receive timeout, so a vendor socket that stays open and
      goes silent parks `StreamAsync` forever. The evidence is §2.2's: with the terminator
      suppressed, one test ran past a 90 s kill and its whole class past 600 s.

      Routed to **`longevity-soak-and-chaos` task 2.6a**, filed beside its existing 2.6 ("half-open
      socket: one direction alive, peer silently gone") because it is the same scenario class on a
      different transport. Not fixed here: it is production behaviour and this change may not touch
      `src/`.

      **Nothing else in `src/` needs routing, and that was checked rather than assumed.**
      `LmntSpeechSynthesizer.cs:266` (the deliberately removed client half-close) and
      `ElevenLabsSpeechSynthesizer.cs:50` (`ThrowIfCancellationRequested` at iterator entry) are both
      recorded, correct decisions — §3.2a and §3.3 measured their consequences rather than treating
      them as defects.

      **Three test-side follow-ups fall out of this sweep. They are named here rather than built,
      because each is new coverage and this change's contract is evidence for fences that exist:**

      1. **No suite in either tree cancels a session that is already streaming** (§2.4). Eight fakes,
         eight cancellation tests, every one throwing before the socket opens. This is the test that
         would give `HangForever` a consumer and make `HoldOpenUntilDisposed` falsifiable.
      2. **`CartesiaFakeServer` (TTS) has no cancellation test at all** (§3.3) — not a weak one, none.
      3. **The `CloseSent` fence in all four STT fakes is true but unwitnessed** (§2.3). A test with a
         raw half-closing `ClientWebSocket` would witness it without touching the production client;
         two agents proved the fence live that way, but only by temporarily reinstating a defect in
         `src/`, which no committed test may do.

      A natural home for 1-3 is `enforce-unguarded-public-claims` (its subject is exactly a claim in
      the tree with nothing executing it); they are recorded here so the choice is made deliberately
      at close-out rather than lost.
