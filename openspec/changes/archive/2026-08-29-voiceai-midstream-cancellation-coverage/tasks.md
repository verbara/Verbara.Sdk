# Tasks: voiceai-midstream-cancellation-coverage

Every claim below was read off the tree at `5a0458ba`, not carried over from the sweep's record —
the sweep's own summary of this gap turned out to be wrong in three places (see proposal §Why), and
its line numbers have drifted. Re-read before trusting a citation.

**Re-pointed 2026-08-26.** `5a0458ba` predates the 2.5.0 cut (tagged `d8fc879b`, 2026-08-25), which
shipped 11 `BREAKING` entries including changes to VoiceAi cancellation and session-ending semantics
(ADR-0052, ADR-0053, ADR-0054). Task 1.1 re-verifies every cited line number at HEAD before any of
them is used; §5.1 and §5.2 are already closed and carry their own notes.

## 1. Baseline — the numbers this change corrects

- [x] 1.1 Re-run the enumeration and check it in as the change's own working evidence: for each of
      the eight WebSocket fakes, the cancellation test (if any), where the token fires, and what the
      test asserts the fake saw. The current answer is seven tests over eight fakes — six
      pre-cancelled (`ReceivedFrameCount.Should().Be(0)` on the four STT surfaces,
      `ReceivedJsonMessages.Should().BeEmpty()` on Deepgram and ElevenLabs TTS), one on a live but
      deliberately silenced socket (`LmntSpeechSynthesizerTests.cs:444-491`), and Cartesia TTS with
      none.

      **Enumerated at HEAD.** Every line below was read off the file, not carried over. The
      seven-over-eight count holds, and so does every citation in the task text — including
      `:444-491`, which had not drifted.

      | surface | cancellation test | token fires | asserts the fake saw | socket at cancel | frames in flight | entry guard |
      |---|---|---|---|---|---|---|
      | AssemblyAi STT | `…Tests.cs:483` | pre-cancelled `:490` | `ReceivedFrameCount…Be(0)` `:502` | never opened, **not asserted** | none | `AssemblyAiSpeechRecognizer.cs:69` |
      | Cartesia STT | `…Tests.cs:408` | pre-cancelled `:415` | `ReceivedFrameCount…Be(0)` `:427` | never opened, **not asserted** | none | `CartesiaSpeechRecognizer.cs:53` |
      | Deepgram STT | `…Tests.cs:320` | pre-cancelled `:327` | `ReceivedFrameCount…Be(0)` `:339` | never opened, **not asserted** | none | `DeepgramSpeechRecognizer.cs:45` |
      | Speechmatics STT | `…Tests.cs:554` | pre-cancelled `:561` | `ReceivedFrameCount…Be(0)` `:573` | never opened, **not asserted** | none | `SpeechmaticsSpeechRecognizer.cs:53` |
      | Cartesia TTS | `…Tests.cs:428` *(added by §2.2)* | pre-cancelled `:431` | `ReceivedApiKey…BeNull` **+** `ReceivedJsonMessages…BeEmpty()` | never opened, **not asserted** | none | **ABSENT** — see §2.1 |
      | Deepgram TTS | `…Tests.cs:396` | pre-cancelled `:403` | `ReceivedJsonMessages…BeEmpty()` `:414` | never opened, **not asserted** | none | `DeepgramSpeechSynthesizer.cs:61` |
      | ElevenLabs TTS | `…Tests.cs:237` | pre-cancelled `:244` | `ReceivedJsonMessages…BeEmpty()` `:253` | never opened, **not asserted** | none | `ElevenLabsSpeechSynthesizer.cs:50` |
      | Lmnt TTS (WS) | `…Tests.cs:445` | fake-side signal `:474` | `SocketState == Open` `:488-491` | **Open — asserted** | **none — `AudioFramesToSend.Clear()` `:461`** | `LmntSpeechSynthesizer.cs:110` |

      *(Row 5 was `NONE` when this table was first written; §2.2 filled it, so the count is now
      eight over eight. Everything below still holds — a pre-cancelled test does not close the gap
      this change exists for.)*

      **The gap restated as a measurement: zero of eight cancel with a frame in flight.** Seven
      never open a socket at all; the eighth opens one and empties the send queue first. And on the Lmnt
      test the token fires on the fake's record of the *client's own outbound* init frame
      (`LmntWsFakeServer.cs:270`), so even there no server→client item has been yielded when it
      fires. Nothing in this suite has ever observed cancellation interrupt delivery.

      Seven findings the task text does not carry, each of which changes work downstream:

      1. **The pre-cancelled tests assert less than their own whitespace siblings.** Deepgram TTS's
         `…_WhenTextIsWhitespace` proves no session with `CapturedAuthorization.Should().BeNull("no
         session should have been opened at all")` **plus** empty JSON (`:391-392`); the
         cancellation test asserts only the latter (`:414`), so it still passes if the upgrade
         completed and no JSON was sent. Same shape on ElevenLabs (`:381-382` vs `:253`) and on all
         four STT surfaces, where the fakes capture `ReceivedRequestUri`/auth on session entry and
         no cancellation test reads them. These six are weaker than they read, and tightening them
         is nearly free once §3 is in.

      2. **Only one of the eight fakes can express a mid-flight cancel today.** Measured, not
         assumed:

         | fake | `SocketState` | `FirstMessageReceived` | hold-open |
         |---|---|---|---|
         | `AssemblyAiFakeServer` / `CartesiaFakeServer` (STT) / `DeepgramFakeServer` / `SpeechmaticsFakeServer` | — | — | — |
         | `CartesiaFakeServer` (TTS) / `ElevenLabsFakeServer` | — | — | — |
         | `DeepgramTtsFakeServer` | — | — | `HangForever:154` |
         | `LmntWsFakeServer` | `:101` | `:108` | `HoldOpenUntilDisposed:150` |

         Seven fakes have no way to say *when* a frame reached the caller and no way to report the
         socket's state, which are the two things the Lmnt test is built on. So §3 is not "write
         seven tests" — it first has to decide **where that capability lives**. All eight run on one
         substrate (`Tests/Verbara.Sdk.TestInfrastructure/WebSocket/WebSocketTestServer.cs`), which
         argues for putting it there once instead of seven times; §3.1 should settle that before
         any test is written.

      3. **A genuine mid-stream cancellation test already exists in-tree** —
         `LmntSpeechSynthesizerHttpTests.SynthesizeAsync_Http_ShouldThrowOperationCanceled_WhenCancelledMidStream`
         (`LmntSpeechSynthesizerTests.cs:632`), which cancels from inside the caller's own
         `await foreach` at `:655` after asserting the first chunk at `:654`. It runs over HTTP
         through `HttpProviderMockServer` and never enters `SynthesizeWebSocketAsync`, so it covers
         no WebSocket fake — but it is exactly the shape §3.1 prescribes, already working. Copy it;
         do not re-derive it.

      4. **Two Deepgram fakes and two Cartesia fakes, and the citations cross between them.**
         `HangForever` is on `DeepgramTtsFakeServer`, *not* on the STT `DeepgramFakeServer`.
         §4.2's `CartesiaFakeServer.cs:248` is the **STT** file; the TTS fake of the same name
         fences at `:227`. Any grep in §3/§4 has to be path-qualified.

      5. **§4.2's four `CloseSent` fence citations are all still exact at HEAD**: AssemblyAi `:237`,
         Cartesia STT `:248`, Deepgram STT `:215`, Speechmatics `:263` — each verbatim
         `while (ws.State is WebSocketState.Open or WebSocketState.CloseSent)`. §4.1's premise also
         re-verified: `CloseAsync|CloseOutputAsync` across `src/Verbara.Sdk.VoiceAi.Stt/` still
         returns exactly one hit and it is still a comment (`CartesiaSpeechRecognizer.cs:153`), so
         the fence still has no natural witness.

      6. **§2.1's premise holds at HEAD.** `src/Verbara.Sdk.VoiceAi.Tts/Cartesia/CartesiaSpeechSynthesizer.cs`
         has zero occurrences of `ThrowIfCancellationRequested`/`IsCancellationRequested`;
         `SynthesizeAsync` is declared `:45-48`, and its first executable statement is the blank-text
         `yield break` at `:52`, then `BuildUri()` `:54` and `ConnectAsync` `:71`. The other three
         TTS synthesizers all guard at iterator entry *before* their own blank-text branch
         (Deepgram `:61` before `:65`, ElevenLabs `:50` before `:54`, Lmnt `:110` before `:115`) —
         which answers §2.3's placement question with a precedent rather than a preference.

      7. **Four `_ShouldThrowTransportFailure_WhenServerAbortsMidSession` tests are the near-miss to
         avoid counting.** One per STT surface (`:381`, `:238`, `:190`, `:452`) plus Cartesia TTS
         `:266`; each sets `AbortAfterSend` and asserts `SpeechProviderFailureSignal.Transport`.
         They kill the socket server-side and involve no token. Cartesia TTS's is the one most
         likely to be mistaken for the cancellation test it does not have.

- [x] 1.2 Confirm the two hold-open flags' consumer status by grep, not by reading their remarks:
      `HangForever` (`DeepgramTtsFakeServer`) has declaration + its own `if` and no assignment
      anywhere; `HoldOpenUntilDisposed` (`LmntWsFakeServer`) is set only by
      `LmntSpeechSynthesizerWsTests.SynthesizeAsync_ShouldAbort_WhenCancelled`. Record the counts.

      **Measured at HEAD** (`grep -rn '<flag>' --include='*.cs' .`, whole repo, then the
      assignment-shaped subset `-E '<flag>\s*=[^=]'`):

      | flag | declaring fake | declaration | its own `if` | doc mentions | **assignments** |
      |---|---|---|---|---:|---:|
      | `HangForever` | `DeepgramTtsFakeServer` | `:154` | `:215` | 1 (`DeepgramTtsFakeServer.cs:219`) | **0** |
      | `HoldOpenUntilDisposed` | `LmntWsFakeServer` | `:150` | `:304` | 2 (`LmntWsFakeServer.cs:22,97`) | **1** |

      Both claims hold, class name included. `HangForever` has no consumer of any kind — two
      occurrences repo-wide, both inside its own file, so §3.4's "give it a consumer or delete it"
      is a live choice and not a formality. `HoldOpenUntilDisposed`'s single assignment is
      `LmntSpeechSynthesizerTests.cs:462`, inside
      `LmntSpeechSynthesizerWsTests.SynthesizeAsync_ShouldAbort_WhenCancelled` (`:445`) — the class
      name reads as a mismatch against the file name and is not one: this file holds **three**
      classes (`LmntTtsOptionsTests` `:20`, `LmntSpeechSynthesizerWsTests` `:88`,
      `LmntSpeechSynthesizerHttpTests` `:541`), because ADR-0050 D3 splits this provider by
      transport rather than by suite. Grepping the file name instead of the type is how that reads
      as wrong.

      One fact the task text does not carry, and the reason the raw grep does not agree with the
      table: a **second, unrelated** `HoldOpenUntilDisposed` exists on `RealtimeFakeServer`
      (`Tests/Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests/Internal/RealtimeFakeServer.cs:92`,
      consumed `:208`) with two assignments of its own
      (`OpenAiRealtimeBridgeTests.cs:227,312`). It is outside this change's eight-fake scope, but a
      bare name grep returns **3** assignments, not 1 — the count is only correct once scoped to
      the declaring type. Anyone re-running this check and reading 3 has not found a regression.

- [x] 1.3 Record the two suites' current wall clock and test counts through the same harness the
      sweep used (`Stt` 125, `Tts` 149 under the CI filter; `Tts` reports 153 unfiltered because the
      four `VoiceCatalogConformanceTests` are counted and skipped). The added tests must not move it
      materially, and "materially" needs a before number to mean anything.

      **Counts confirmed exactly as stated.** `Stt` 125/125 filtered and unfiltered (nothing
      skipped); `Tts` 149 filtered, and 153 unfiltered with 4 skipped — the four
      `VoiceCatalogConformanceTests`. 0 failures in either.

      **Wall clock, 30 runs per suite, `-c Release --no-build` under the CI filter** — the class-AB
      sweep's harness, not the Debug single-shot this was first measured with, because a number from
      a different harness cannot be compared against in §6:

      | suite | tests | min | median | max | spread | stdev |
      |---|---:|---:|---:|---:|---:|---:|
      | `Stt.Tests` | 125 | 438 ms | **466 ms** | 508 ms | 70 ms (15.0 %) | 17.5 |
      | `Tts.Tests` | 149 | 396 ms | **430 ms** | 457 ms | 61 ms (14.2 %) | 13.3 |

      Both suites are *faster* than the sweep recorded them (`Stt` 496 → 466 ms, `Tts` 455 →
      430 ms) at an unchanged relative spread (11.9 → 15.0 %, 17.3 → 14.2 %). The sweep's verdict
      still stands: this is real work and scheduling noise, not fixed-timeout mass, so §5.4's
      "nothing to recover" remains the honest claim.

      **One outlier, recorded rather than smoothed away.** Run 9/30 of `Stt` reported `Duration:
      5 s` against a 466 ms median — 1 of 60 runs, all 125 tests passing. It is not a timeout
      belonging to this suite: `FromSeconds(5)`/`FromMilliseconds(5000)` appears **nowhere** in
      `Verbara.Sdk.VoiceAi.Stt.Tests` or in the substrate (the only two 5 s ceilings in either
      suite are `ElevenLabsFakeServer.cs:65` and the TTS `CartesiaFakeServer.cs:75`, and the STT
      suite's own waits are the four 10 s `SessionCompleted` guards). It sits close enough to the
      4 987–4 992 ms concurrent-receive collision the class-AB sweep characterised elsewhere to be
      worth watching, and it is a single unreproduced observation — so it goes to §6 as a watch
      item, not into any claim.

## 2. Cartesia TTS — the surface with no cancellation test

- [x] 2.1 **Measure before writing.** `CartesiaSpeechSynthesizer` is the only one of the four TTS
      synthesizers with no `ct.ThrowIfCancellationRequested()`; the other three have exactly one
      each. Write a throwaway probe that enumerates `SynthesizeAsync` with a pre-cancelled token and
      record verbatim what is thrown and from where. The living spec requires the throw to land
      *before the first provider request is issued*, and on this surface the first thing reached is
      `BuildUri()` then `ConnectAsync`.

      **Probed against the live fake, 10 consecutive runs, identical every time.** The probe was a
      temporary `[Fact]` in `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Cartesia/`, deleted after the
      measurement — nothing of it is committed.

      ```
      THROWN TYPE   : System.Threading.Tasks.TaskCanceledException
      MESSAGE       : A task was canceled.
      is OCE        : True          OCE.CancellationToken == caller's ct : False
      STACK (top)   : System.Net.WebSockets.WebSocketHandle.ConnectAsync(...)
                      System.Net.WebSockets.ClientWebSocket.ConnectAsyncCore(...)
                      CartesiaSpeechSynthesizer.SynthesizeAsync(...)+MoveNext()
                        at src/Verbara.Sdk.VoiceAi.Tts/Cartesia/CartesiaSpeechSynthesizer.cs:72
      WHAT THE FAKE SAW : ReceivedApiKey = (null)   ReceivedJsonMessages = 0      [10/10]
      ```

      **The contract holds, by a different mechanism.** `TaskCanceledException` derives from
      `OperationCanceledException`, so `ThrowAsync<OperationCanceledException>()` is satisfied; and
      the fake recorded no session on any of the ten runs, so nothing was asked of the provider.
      Both halves of §2.2's condition are met.

      Two details worth keeping, because neither is visible from the source:

      * **The throw comes from `ConnectAsync` (`:72`), not from an entry guard.** `BuildUri()`
        (`:54`), `new ClientWebSocket()` (`:55`) and both `SetRequestHeader` calls (`:65-66`) all
        execute first. The guarantee is therefore borrowed from `ClientWebSocket` honouring an
        already-cancelled token before it does I/O, rather than stated by this client.
      * **The exception carries the linked `connectCts` token, not the caller's**
        (`callersToken=False`, 10/10). On the other three TTS surfaces the same probe returns
        `callersToken=True`, because their guards throw with `ct` itself. A caller that inspects
        `OperationCanceledException.CancellationToken` to decide *whose* cancellation it was gets a
        different answer from this provider than from the other three.

      The test written in §2.2 therefore asserts the **contract** (`OperationCanceledException` +
      no session) and not the mechanism, so it keeps holding if a guard is ever added.

- [x] 2.2 If 2.1 shows the contract holds (the throw comes out as `OperationCanceledException` with
      the fake recording no session), add the pre-cancelled test in the shape the other seven use
      and stop — no `src/` change.

      **This is the branch 2.1 routed to. No `src/` change.** Added
      `CartesiaSpeechSynthesizerTests.SynthesizeAsync_ShouldAbort_WhenCancelled`, in the shape the
      other seven use, with one deliberate strengthening: it asserts **both** of the fake's
      session-entry witnesses — `ReceivedApiKey.Should().BeNull(...)` *and*
      `ReceivedJsonMessages.Should().BeEmpty()` — rather than the JSON alone. That is §1.1
      finding 1 applied at the first opportunity: six of the seven pre-existing tests assert only
      the weaker half and would still pass if the upgrade had completed and no request been sent.
      The eighth surface starts at the stronger bar instead of joining the six.

      The test's `<remarks>` records the §2.1 mechanism (throw from `ConnectAsync`, linked token)
      so the next reader does not have to re-probe to learn why this surface has no entry guard.

      Class green: 20/20, 0 failures.

- [x] 2.3 If 2.1 shows it does not hold, add the entry guard to
      `src/Verbara.Sdk.VoiceAi.Tts/Cartesia/CartesiaSpeechSynthesizer.cs` **in its own commit** with
      its own CHANGELOG line under `Fixed`, because that is a product behaviour change and must not
      arrive disguised as test coverage. Note that `SynthesizeAsync` already opens with a
      `yield break` on blank text, so the guard's placement relative to that branch is a decision,
      not a detail — record which and why.

      **Branch not taken** — §2.1 measured the contract holding, so this change adds no guard and
      touches no `src/` file. But the placement question this task raised turns out to describe a
      real divergence, and it would be dishonest to close the task without stating it.

      **Measured: blank text + a pre-cancelled token, all four TTS synthesizers.**

      | synthesizer | result | from |
      |---|---|---|
      | **Cartesia** | **NO THROW, 0 frames** | blank-text `yield break` `:52` wins |
      | Deepgram | `OperationCanceledException`, caller's own token | guard `:61`, ahead of its `yield break` `:65` |
      | ElevenLabs | `OperationCanceledException`, caller's own token | guard `:50`, ahead of its `yield break` `:54` |
      | Lmnt | `OperationCanceledException`, caller's own token | guard `:110`, ahead of its `yield break` `:115` |

      So on exactly one input — blank text handed to an already-cancelled enumeration — Cartesia
      swallows the cancellation and returns empty where the other three throw. The other three all
      place the guard *before* the blank-text branch, which answers §2.3's placement question with
      a three-to-nothing precedent rather than a preference.

      **Deliberately left alone here.** §2.3's own gate is 2.1's result, and 2.1 held; and §2.3
      itself says a guard is a product behaviour change that "must not arrive disguised as test
      coverage" — which is equally true of one arriving inside a test-coverage change on a
      condition its gate did not trigger. Recorded as a finding for a follow-up proposal, not
      folded into this one. See §7.3.

- [x] 2.4 Either way, add Cartesia TTS to the per-surface enumeration in §1.1 so the count stops
      being seven-over-eight.

      Done — §1.1's table row for Cartesia TTS now reads
      `SynthesizeAsync_ShouldAbort_WhenCancelled`, pre-cancelled, asserting both session-entry
      witnesses. **The count is eight over eight.** What that does *not* yet change is the sentence
      underneath it: all eight are still pre-cancelled-or-silent, so "zero of eight cancel with a
      frame in flight" is unaffected. That is §3's work, and §2 deliberately did not pre-empt it.

## 3. Mid-flight cancellation, per surface

- [x] 3.1 Decide the delivery signal each fake needs. The test must cancel with a frame **already
      observed by the caller**, not merely sent by the fake — a frame written to the socket is not
      yet a frame the enumeration has yielded, and cancelling on the former reintroduces the race
      the sweep spent itself removing. Prefer cancelling from inside the caller's own
      `await foreach` after the first chunk, the shape
      `SpeechmaticsSpeechSynthesizerTests.cs:105-136` and `LmntSpeechSynthesizerTests.cs:632-660`
      already use over HTTP; reach for a fake-side signal only where the WebSocket path cannot.

      **Decision: one opt-in decorator on the shared substrate, not eight per-fake knobs.**

      "Stop writing after the Nth outbound message" is a property of the *transport*, not of any
      vendor's protocol. All eight fakes sit on the same `WebSocketTestServer`, so the hold is
      stated once, in one place, and one negative test falsifies it for all eight at once. Eight
      per-fake flags would have had to be argued — and negative-tested — eight times, and §1.1
      found exactly what that costs: `LmntWsFakeServer` was the only fake with any hold machinery
      at all, and its `HoldOpenUntilDisposed` has never been shown to be load-bearing (§3.5).

      What was added:

      | File | What |
      |---|---|
      | `Tests/Verbara.Sdk.TestInfrastructure/WebSocket/OutboundFrameGate.cs` | new — `OutboundFrameGate` (`HoldAfter`, `Delivered`, `Held`, `Release()`) and `GatedWebSocket`, a `System.Net.WebSockets.WebSocket` decorator that routes **both** `SendAsync` overloads through the gate and forwards everything else verbatim |
      | `WebSocketTestServer.cs` | `OutboundGate` property, `SocketState` (the live server-side socket), and one line in `HandleConnectionAsync` that wraps the raw socket **only when a gate is armed** |
      | the eight fakes | two forwarding lines each (`OutboundGate`, `SocketState`) — seven gained both; `LmntWsFakeServer` already had its own `SocketState` and keeps it |

      **Opt-in by construction, which is why the 274 pre-existing tests could not be perturbed.**
      With no gate armed, `HandleConnectionAsync` hands the session the raw socket and
      `GatedWebSocket` is never allocated — there is no code path through the decorator for a test
      that does not ask for one. The suite counts confirm it: Stt 125 → 129 and Tts 149 → 154
      (149 + §2.2 + the four here), zero failures, no pre-existing test touched.

      **The gate is not the cancel trigger.** The trigger is the caller's own `await foreach`,
      exactly as this task prefers; the gate is what guarantees the fake *still has something to
      send* when the trigger fires. Conflating the two is the race the sweep removed, so they are
      kept as two separate mechanisms and asserted separately (`observed` for the trigger,
      `gate.Held` / `gate.Delivered` for the condition).

      **Why `gate.Held` is read after the throw rather than awaited before the cancel.** Awaiting
      it inside the loop would make the mid-flight condition causally certain rather than
      observed. It was rejected: it introduces a hang with no ceiling. Once the caller cancels and
      the socket aborts, every fake's send loop breaks on
      `ws.State is not (WebSocketState.Open or WebSocketState.CloseReceived)` and never calls
      `PassAsync` again — so a mis-set `HoldAfter` (at or past the fake's frame count) would hang
      the run instead of failing it. The form chosen cannot hang, and its failure mode is a false
      RED, never a silent pass. Measured rather than argued: **60 Release runs
      (30 x both suites, 240 test executions) green, 0 failures**, durations 41-49 ms with no
      outlier — no `gate.Held` flake in any of them.

- [x] 3.2 Four STT surfaces: cancel after the first transcript has been yielded, with the fake still
      holding frames to send. Assert the throw comes from the recognizer (ADR-0052 F3 — the token
      goes to `StreamAsync` only, never to `ToListAsync`/`WithCancellation`) and assert the
      server-side socket state at the cancel, the way the Lmnt test does, so the test states what
      held rather than only that it threw.

      Four `StreamAsync_ShouldAbort_WhenCancelledMidDelivery` tests, one per surface. `HoldAfter`
      counts the fake's **outbound** messages, so it differs where the vendor opens with a greeting:

      | Surface | Test | `HoldAfter` | Why that number | Held back |
      |---|---|---|---|---|
      | AssemblyAi | `AssemblyAiSpeechRecognizerTests.cs:526` | 2 | `Begin` greeting, then the interim `Turn` | the final `Turn` |
      | Cartesia STT | `CartesiaSpeechRecognizerTests.cs:454` | 1 | no greeting — the first transcript is the first message | every later transcript |
      | Deepgram STT | `DeepgramSpeechRecognizerTests.cs:366` | 1 | no greeting | every later transcript |
      | Speechmatics | `SpeechmaticsSpeechRecognizerTests.cs:600` | 2 | `RecognitionStarted`, then the partial | the final transcript |

      Each asserts four things, not one: the throw is an `OperationCanceledException` out of
      `StreamAsync` with the consumer holding no token (F3); `observed == 1`, so the cancel landed
      *after* a transcript reached the caller; `SocketState == Open`, so it landed on a live socket
      rather than being credited with the server's own close; and `gate.Held` completed with
      `gate.Delivered == HoldAfter`, so the fake demonstrably still had a frame to give.

      **Result:** `Verbara.Sdk.VoiceAi.Stt.Tests` 125 → **129 passed, 0 failed**. Negative-tested —
      record in §3.6.

- [x] 3.3 Four TTS surfaces: the same, after the first audio chunk has been yielded. On Lmnt this is
      a **second** test, not a replacement — the existing one asserts cancellation on a live silent
      socket and that case stays covered.

      Four `SynthesizeAsync_ShouldAbort_WhenCancelledMidDelivery` tests. No TTS surface opens with a
      greeting, so all four hold at 1 — the first audio chunk is delivered, the second is not, and
      the end-of-utterance terminator behind it is never reached:

      | Surface | Test | Frames the fake has | Held back |
      |---|---|---|---|
      | Cartesia TTS | `CartesiaSpeechSynthesizerTests.cs:471` | 7 base64 `chunk` text frames | chunks 2-7, then `done` |
      | Deepgram TTS | `DeepgramSpeechSynthesizerTests.cs:440` | 8 binary frames | frames 2-8, then `Flushed` |
      | ElevenLabs | `ElevenLabsSpeechSynthesizerTests.cs:279` | 9 base64 `audio` text frames | frames 2-9, incl. the `isFinal` one |
      | Lmnt (WS) | `LmntSpeechSynthesizerTests.cs:519` | 6 binary frames | frames 2-6, then `finish` |

      **Lmnt keeps both tests, because they witness different conditions.** The pre-existing
      `SynthesizeAsync_ShouldAbort_WhenCancelled` holds the session open with *no* frames at all and
      fires once the fake records the client's first message: a caller blocked on a silent provider.
      The new one is the opposite — the provider is mid-answer and the caller has already been handed
      audio. Neither subsumes the other. The new one also needs none of the old one's machinery: no
      `HoldOpenUntilDisposed`, no `FirstMessageReceived` wait, no fire-and-forget trigger task. The
      gate is the hold and the caller's first iteration is the trigger.

      **Result:** `Verbara.Sdk.VoiceAi.Tts.Tests` 149 → **154 passed, 0 failed** (149 baseline + the
      §2.2 Cartesia test + the four here). Negative-tested — record in §3.6.

- [x] 3.4 Give `HangForever` a consumer or delete it. If a Deepgram TTS mid-flight test can be
      written against the normal hold path, `HangForever` is dead code and goes; if it is the only
      way to hold that surface open mid-delivery, it finally gets the assignment it has never had.
      Decide from the test that gets written, not in advance.

      **Deleted.** §3.3's `DeepgramSpeechSynthesizerTests.SynthesizeAsync_ShouldAbort_WhenCancelledMidDelivery`
      is the mid-flight test this surface never had, and it was written against the normal delivery
      path — the `OutboundFrameGate` holds the fake on its *outbound* side after the first audio
      frame. `HangForever` holds the session open by answering *nothing at all*, which is a different
      condition and not the one a mid-delivery test needs. The single argument that had kept the flag
      alive ("a mid-stream cancellation test on this surface would need it", its own remarks) is the
      one this task said to decide from, and it did not survive the test being written.

      Removed from `DeepgramTtsFakeServer.cs`: the property and its 17-line remarks (`:155-173`) and
      the unreachable `if (HangForever)` branch (`:234-247`) — the `Task.Delay(Timeout.Infinite, ct)`
      park, its `fence-allow: GUARD-TIMEOUT` annotation and its early `return`. Repo-wide
      `grep -rn 'HangForever' --include='*.cs'` now returns **one** hit, the sentence in the new
      test's `<remarks>` recording why it went. Suite after the deletion: **154 passed, 0 failed** —
      nothing depended on it, which is what "no assignment anywhere" had already said.

      Note this closes it the *opposite* way from the way this change's own proposal predicted
      ("the test that gives `HangForever` a consumer"). The prediction was wrong and the measurement
      decided, which is what this task was written to allow.

- [x] 3.5 Re-run the `HoldOpenUntilDisposed` swap experiment against the **new** Lmnt test: replace
      the flag's body with `await receiveTask` and record the result. The sweep measured 10/10 green
      against the old test. If the new one goes red, the flag is falsifiable at last and its remarks
      must be rewritten to say so; if it stays green, the flag is unconsumed by the spec's
      definition and that is the finding.

      **It stayed green — 10/10 on the whole `LmntSpeechSynthesizerWsTests` class (19 tests per run,
      both cancellation tests present), swap in place.** Same verdict as the sweep, now with a
      stronger instrument behind it.

      The reason is sharper than "still green", and it is the finding: **the new test never sets the
      flag.** §3.3's mid-flight test holds the fake on its *outbound* side with an
      `OutboundFrameGate` and leaves `HoldOpenUntilDisposed` alone, so the branch the swap mutates is
      not on its path at all. The test that was expected to falsify the flag turns out not to touch
      it. The flag therefore remains what the sweep called it: a *latent* guard — correct, worth
      keeping against a client that half-closes or faults its read, and unfalsifiable in this tree
      because `LmntSpeechSynthesizer` no longer half-closes after `eof`, so both spellings park for
      exactly as long.

      Unlike `HangForever` (§3.4) it is **not** deleted, and the distinction matters: `HangForever`
      had no assignment anywhere, so its branch never executed; `HoldOpenUntilDisposed` *is* set by a
      test and *does* execute — no assertion can merely tell it apart from its absence. Dead code and
      unfalsifiable code are different findings and get different remedies.

      Recorded where the next reader will hit it: a second `<remarks>` block on the property in
      `LmntWsFakeServer.cs` dated 2026-08-26, naming the test that was expected to falsify it and
      why it does not.

- [x] 3.6 Negative-test every new test the way the sweep's own added requirement demands: remove the
      fence or signal it depends on, run, record the failure verbatim, restore, re-run green. A test
      added by this change and never observed failing is not evidence.

      **All 13 tests this change adds have a recorded verbatim failure.** Nine are covered below;
      §4's four are covered in §4.3, where the record also belongs because the contrast with the four
      pre-existing half-close tests is that section's whole point.

      **A. The eight mid-flight tests (§3.2, §3.3).** Fence removed: the hold itself. In
      `OutboundFrameGate.PassAsync`, `await _released.Task.WaitAsync(ct)` was replaced by
      `await Task.CompletedTask.WaitAsync(ct)` — `Held` still completes and `Delivered` still counts,
      but nothing is ever held back. All eight went red, and every one of them on the same assertion:

      | Test | Verbatim |
      |---|---|
      | `DeepgramSpeechRecognizerTests` | `Expected observed to be 1 because the cancel must land after a transcript reached the caller, not before, but found 2.` |
      | `CartesiaSpeechRecognizerTests` | *idem*, `but found 2.` |
      | `AssemblyAiSpeechRecognizerTests` | *idem*, `but found 2.` |
      | `SpeechmaticsSpeechRecognizerTests` | *idem*, `but found 2.` |
      | `CartesiaSpeechSynthesizerTests` | `Expected observed to be 1 because the cancel must land after audio reached the caller, not before, but found 7.` |
      | `DeepgramSpeechSynthesizerTests` | *idem*, `but found 8.` |
      | `ElevenLabsSpeechSynthesizerTests` | *idem*, `but found 9.` |
      | `LmntSpeechSynthesizerWsTests` | *idem*, `but found 6.` |

      **The four TTS numbers are the whole point, and they were not predicted.** 7, 8, 9 and 6 are
      *exactly* the frame counts of each fake's recorded audio (2 008 / 2 408 / 2 808 / 1 808 bytes
      at `AudioFrameSize = 320`). With the hold gone the fake writes the entire utterance before the
      caller's first iteration returns, the client drains the whole buffered stream, and the cancel
      is observed only after the last frame. That is not a mid-flight cancellation at all — it is a
      completed stream with a late token.

      And it still threw. `await act.Should().ThrowAsync<OperationCanceledException>()` **passed in
      all eight neutralised runs** — the failures are all on the `observed` assertion that comes
      after it. So a test asserting only "it threw", which is what six of the seven pre-existing
      cancellation tests do, would have stayed green through a mutation that removes the entire
      condition it claims to test. That is the coverage gap this change exists for, demonstrated
      rather than asserted.

      Restored from a pristine copy and re-run: **STT 4/4, TTS 4/4 green**; then 60 Release runs
      (30 x both suites) with zero failures (§3.1).

      **B. The Cartesia pre-cancelled test (§2.2).** Signal removed: the cancel. Dropping
      `await cts.CancelAsync()` makes the session open normally, and the test goes red —

      > `Expected a <System.OperationCanceledException> to be thrown, but no exception was thrown.`

      Restored, re-run green (2/2). That falsifies the throw. The two session-entry witnesses it also
      asserts are proven live by an in-tree **positive control** rather than a probe:
      `SynthesizeAsync_ShouldAuthenticateTheUpgrade_WhenOpeningASession` asserts
      `ReceivedApiKey.Should().Be(TestApiKey)` on the same fake and passes, so `BeNull()` in the
      cancellation test is a signal that can be non-null and not a field nothing ever writes. A
      positive control is used deliberately here: §1.1 established that `WebSocketTestServer` swallows
      every exception a session handler raises, so a throwing probe on this substrate reports
      "never reached" whether or not it fired.

## 4. Witness the `CloseSent` fence

- [x] 4.1 Confirm the premise still holds at implementation time:
      `grep -rn 'CloseAsync\|CloseOutputAsync' src/Verbara.Sdk.VoiceAi.Stt/` returns exactly one hit
      today and it is a **comment** (`CartesiaSpeechRecognizer.cs:153`, describing a half-close that
      was removed). If a recognizer has since started sending a close frame, the fence has a natural
      witness and this section shrinks to an assertion.

      **Re-run 2026-08-26 — the premise holds, unchanged.** One hit, and it is the comment:

      > `src/Verbara.Sdk.VoiceAi.Stt/Cartesia/CartesiaSpeechRecognizer.cs:153:            // A bare half-close stood here — guarded by its own timeout, because CloseOutputAsync`

      No recognizer sends a close frame, so the fence still has no natural witness and §4.2 stays a
      raw-client test rather than an assertion.

      The four fence citations have **moved by this change's own §3.1 edits** (each fake gained two
      forwarding lines). Current: `AssemblyAiFakeServer.cs:256`, `CartesiaFakeServer.cs:267`,
      `DeepgramFakeServer.cs:234`, `SpeechmaticsFakeServer.cs:282` — was `:237`, `:248`, `:215`,
      `:263` in §4.2's text.

      **What the fence actually protects, which the sweep's inventory named but did not spell out.**
      It is *not* about a client that half-closes while the server is `Open` — that close is read from
      `Open` and needs no disjunct. It is about the fake's **own** close: the terminator branch answers,
      calls `CloseOutputAsync` (server socket → `CloseSent`), and returns to the top of the loop.
      `CloseSent` is what keeps the loop alive for one more read — the read that receives the client's
      close frame and sets `ReceivedClientCloseFrame`. Drop it and the loop exits on that evaluation,
      so the field stays `false` for every client alike. That is exactly what
      `AssemblyAiFakeServer.cs:241` has been saying in prose: *"a fake that stopped at the terminator
      would report every client as clean."*

- [x] 4.2 Add one test per STT fake driving it with a raw `System.Net.WebSockets.ClientWebSocket`:
      complete the handshake, send whatever frame the fake's protocol expects, `CloseOutputAsync`,
      and assert the session survives — the fake keeps reading and still delivers. The fence under
      test is `while (ws.State is WebSocketState.Open or WebSocketState.CloseSent)` at
      `AssemblyAiFakeServer.cs:237`, `CartesiaFakeServer.cs:248`, `DeepgramFakeServer.cs:215`,
      `SpeechmaticsFakeServer.cs:263`.

      Four `Session_ShouldKeepReadingPastItsOwnClose_WhenTheClientHalfCloses` tests, each a raw
      `ClientWebSocket` driven by hand — no recognizer involved, because no recognizer can produce
      the condition (§4.1):

      | Surface | Test | Path | Protocol frames sent |
      |---|---|---|---|
      | AssemblyAi | `AssemblyAiSpeechRecognizerTests.cs:620` | `/v3/ws` | 320 B binary, then `{"type":"Terminate"}` |
      | Cartesia STT | `CartesiaSpeechRecognizerTests.cs:548` | `/stt/websocket` | 320 B binary, then `done` |
      | Deepgram STT | `DeepgramSpeechRecognizerTests.cs:460` | `/v1/listen` | 320 B binary, then `{"type":"CloseStream"}` |
      | Speechmatics | `SpeechmaticsSpeechRecognizerTests.cs:703` | `/v2` | `StartRecognition` first (this fake consumes it outside the loop), then 320 B binary, then `EndOfStream` |

      Then `CloseOutputAsync` — the half-close none of the four recognizers performs — and
      `SessionEndedAsync()`.

      **Two assertions, and the first one is what makes the second mean anything.** The terminator
      having been recorded proves the fake reached the branch that closes its own output, so the
      close frame that follows is read from `CloseSent` rather than from `Open`; without it, a client
      that closed *without* sending a terminator would satisfy `ReceivedClientCloseFrame == true`
      while never touching the fence. Frame ordering is TCP's, not a race — the terminator is written
      first, so it is read first — so no wait, signal or ceiling is needed anywhere in these tests.

      Green 4/4 on first run; full `Verbara.Sdk.VoiceAi.Stt.Tests` 129 → **133 passed, 0 failed**.

- [x] 4.3 Negative-test it: drop the `or WebSocketState.CloseSent` disjunct, observe the new test
      red and the four existing `StreamAsync_ShouldNotHalfCloseTheOutputSide_WhenInputEnds` still
      green — that contrast **is** the point of the section and belongs in the record verbatim.

      Disjunct dropped on all four fakes (`while (ws.State is WebSocketState.Open)`), nothing else
      touched. The contrast is exactly as predicted, and measured on the **whole suite** rather than
      on the two filters, which sizes the blind spot precisely:

      > `Failed!  - Failed:     4, Passed:   129, Skipped:     0, Total:   133`

      **Four of 133 tests notice. They are the four added in §4.2.** All four verbatim, identically:

      > `Expected _server.ReceivedClientCloseFrame to be True because the loop must keep reading while the server socket is CloseSent, or the client's close frame is never seen and every client reads as one that did not half-close, but found False.`

      — at `AssemblyAiSpeechRecognizerTests`, `CartesiaSpeechRecognizerTests`,
      `DeepgramSpeechRecognizerTests` and `SpeechmaticsSpeechRecognizerTests`.

      And the four tests whose *name* is about half-closing stayed green:

      > `Passed!  - Failed:     0, Passed:     4` — the four `StreamAsync_ShouldNotHalfCloseTheOutputSide_WhenInputEnds`

      That is the point of the section. Those four assert `ReceivedClientCloseFrame.Should().BeFalse()`
      against recognizers that do not half-close, so `false` is the expected answer whether the fence
      works or is deleted. They cannot distinguish a fake that watches for a close frame from one that
      stopped looking — which is the failure mode the sweep found on the first version of this loop
      and fixed, with no test able to hold the fix in place until now.

      Restored from a pristine copy, all four fences back at
      `while (ws.State is WebSocketState.Open or WebSocketState.CloseSent)`; suite re-run
      **133 passed, 0 failed**.

- [x] 4.4 Do not touch `src/` to produce the condition. The sweep proved this fence live only by
      temporarily reinstating the removed half-close, which is a measurement technique and must not
      be committed.

      **Honoured. `src/Verbara.Sdk.VoiceAi.Stt/` is untouched by this change** — `git status` shows
      no file under it modified, and §4.1's grep still returns its single comment hit. The half-close
      is produced by a raw `ClientWebSocket` in the test, which is a client this suite controls and a
      condition a real client is free to create; the sweep's technique needed a temporary `src/` edit
      only because it drove the fake through the shipped recognizer.

      The §4.3 mutation is applied to the **fakes**, not to `src/`, and was reverted from a pristine
      copy before anything was committed.

## 5. The CHANGELOG correction

- [x] 5.1 Correct the sweep's CHANGELOG bullet: seven cancellation tests over eight fakes, six
      pre-cancelled and one on a live silent socket; `HoldOpenUntilDisposed` has a consumer that
      cannot falsify it, `HangForever` has none. Keep the bullet's point — the gap is real — and fix
      only what is false.
      **Done in this change's own opening PR** (`54524877`, #220, 2026-08-24), not left for the
      implementation PR. `CHANGELOG.md:94` now reads "**seven** cancellation tests" with an italic
      correction note at :102-105.

- [x] 5.2 Leave `openspec/changes/archive/2026-08-23-websocket-fake-class-ab-sweep/` untouched. It
      is a period-correct record; the correction lives in this change's proposal and in the
      CHANGELOG.
      **Re-pointed 2026-08-26:** the parenthetical said "2.5.0 untagged, latest tag `v2.4.0`". That
      is no longer true — `v2.5.0` was tagged at `d8fc879b` on 2026-08-25 and all 29 packages are on
      nuget.org, so the corrected bullet now sits *inside* the shipped `## [2.5.0]` section
      (:94, under a heading at :42), not under `[Unreleased]`. The correction still stands as
      written; what changed is that it is now part of a published record rather than a pre-publication
      fix, which is why §5.3's new entry goes under `[Unreleased]` and must not be folded into 2.5.0.

- [x] 5.3 Add this change's own `[Unreleased]` entry when it ships.

      Two sections at the top of `[Unreleased]`, above the ADR-0055 release-hygiene entry:

      - **`Added — Cancellation is now witnessed with frames in flight, on all eight WebSocket
        surfaces`** — the corrected statement of the gap (not "seven tests over eight fakes" but
        "none of the eight cancelled with a frame in flight"), the `OutboundFrameGate` substrate, the
        eight mid-flight tests with the neutralised-gate numbers, Cartesia TTS's new test and its
        `ConnectAsync` route, and the four `CloseSent` witnesses with the 4-of-133 measurement.
      - **`Removed — DeepgramTtsFakeServer.HangForever`** — why it went, and why
        `HoldOpenUntilDisposed` explicitly does not go with it.

      The heading carries a literal `(#PR)` placeholder, filled by §7.1 before archiving.

## 6. Verification

- [x] 6.1 `dotnet build Verbara.Sdk.slnx` — zero warnings, Debug and Release.

      Both configurations, whole solution, from the working tree with every change in place:

      | configuration | result | elapsed |
      |---|---|---|
      | Debug | `Build succeeded. 0 Warning(s), 0 Error(s)` | 00:00:11.01 |
      | Release | `Build succeeded. 0 Warning(s), 0 Error(s)` | 00:00:09.69 |

      Release is the one that matters for this change and is run separately rather than assumed:
      `TreatWarningsAsErrors` is on in both, but the two configurations do not compile the same
      code — a `#if DEBUG` or a Release-only nullable-flow conclusion can turn a Debug-clean tree
      into a Release failure, and `OutboundFrameGate` is new source in a project every test assembly
      references.
- [x] 6.2 Unit lane green under the four-exclusion CI filter, with the new per-surface counts stated.

      `dotnet test Verbara.Sdk.slnx -c Release --no-build --filter
      "Category!=Functional&Category!=Integration&Category!=Realtime&Category!=Spike"` —
      **30 assemblies, 3 288 passed, 0 failed, 0 skipped.**

      The two suites this change touches, against §1.3's before-numbers:

      | suite | before | after | delta | what the delta is |
      |---|---:|---:|---:|---|
      | `VoiceAi.Stt.Tests` | 125 | **133** | +8 | 4 half-close witnesses (§4.2) + 4 mid-flight cancellations |
      | `VoiceAi.Tts.Tests` | 149 | **154** | +5 | 1 Cartesia entry-guard test (§2.2) + 4 mid-flight cancellations |

      +13 against the 3 275 the release-status snapshot recorded on 2026-08-23, which is the whole
      of this change and nothing else — no assembly moved that this change did not touch.

      **`Tts` now reports 154/154 with nothing skipped, where §1.3 recorded 149 filtered and 153
      unfiltered with 4 skipped.** That is not this change dropping a skip: the CI filter excludes
      the four `VoiceCatalogConformanceTests` outright, so under the filter they are not counted at
      all, and the unfiltered run is where the 4 `Skipped` appear. §1.3's 153-with-4-skipped was the
      unfiltered figure; both numbers still read the same way they did.
- [x] 6.3 Determinism: 30× both suites idle and 30× under CPU saturation, all green. Mid-flight
      cancellation is exactly the shape that passes idle and fails loaded, so an idle-only run is
      not a measurement.

      **120 suite runs — 30 per suite per condition — 0 failures, 0 flakes.** Saturation is 48 busy
      loops against `nproc` = 24, so every test thread is contending for a core with a spinner:

      | condition | suite | runs | counts seen | wall median |
      |---|---|---:|---|---:|
      | idle | `Stt.Tests` | 30 | `133` on all 30 | 1 130 ms |
      | idle | `Tts.Tests` | 30 | `154` on all 30 | 1 095 ms |
      | 48 spinners | `Stt.Tests` | 30 | `133` on all 30 | 6 112 ms |
      | 48 spinners | `Tts.Tests` | 30 | `154` on all 30 | 5 834 ms |

      The count column is the part that matters as much as the pass column: a race that lost would
      not necessarily fail, it could report a different total, and every one of the 120 runs reported
      the same number.

      **What that buys, counted rather than asserted:** the eight mid-flight tests ran **480 times**
      (4 per suite × 30 runs × 2 suites × 2 conditions) and the four `CloseSent` witnesses **240
      times** (4 × 30 × 2 conditions). Load slowed the suites ~5.4× (`Stt` 1 130 → 6 112 ms wall,
      `Tts` 1 095 → 5 834 ms) — a scheduling share, not a stall, which is what makes it a real
      squeeze on the timing rather than a run that happened to take longer.

      **Why loaded is the condition that counts here.** The mid-flight test's shape is a race by
      construction: the fake is parked mid-delivery on a frame it must not send, and the caller
      cancels from inside its own `await foreach`. If the gate's hold and the consumer's cancel
      could interleave the wrong way, an idle machine — where the fake wins the scheduler every time
      — is exactly where that would never show. 48 spinners is the cheapest way to stop the fake
      winning by default.
- [x] 6.4 Wall clock through the §1.3 harness, reported against the recorded before-numbers with the
      spread, not as a bare delta.

      Same harness as §1.3 — `-c Release --no-build`, CI filter, 30 runs, `dotnet test`'s own
      reported duration — so the two tables are comparable:

      | suite | tests | median before | median after | delta | before spread | after spread | stdev |
      |---|---:|---:|---:|---:|---:|---:|---:|
      | `Stt.Tests` | 125 → **133** (+8) | 466 ms | **457 ms** | **−9 ms** | 70 ms (15.0 %) | 90 ms (19.7 %) | 17.5 → 19.1 |
      | `Tts.Tests` | 149 → **154** (+5) | 430 ms | **428 ms** | **−2 ms** | 61 ms (14.2 %) | 50 ms (11.7 %) | 13.3 → 13.0 |

      **Both medians moved down while the test count went up, which is the reason to report the
      spread and not the delta.** A −9 ms and a −2 ms "improvement" from adding 13 tests is not an
      improvement; it is a delta an order of magnitude inside a run-to-run spread of 70–90 ms that
      was already there before this change. The honest statement is that **the added tests do not
      move the wall clock at this harness's resolution** — not that they are free, which this
      measurement cannot show, and not that they cost nothing, which it also cannot show.

      What it *can* rule out is the failure mode worth ruling out: 13 tests each parking a fake on a
      held frame could have added fixed timeout mass, and fixed mass shows up as a median that moves
      with the count and a spread that does not. Neither happened. §5.4's "nothing to recover"
      still holds — this is real work and scheduling noise, same verdict as §1.3 and as the sweep.

      **Under load, `dotnet test` reports duration at 1-second granularity** (`2 s` / `3 s` / `4 s`),
      so the loaded numbers are read from wall clock instead and are in §6.3's table. Quoting a
      median of "3 s" against a 457 ms idle median would be an artefact of the printer, not a
      measurement.

      **The §1.3 watch item — the one 5 s `Stt` outlier — did not reproduce.** §1.3 recorded run
      9/30 of `Stt` reporting `Duration: 5 s` against a 466 ms median, 1 in 60 runs, and sent it here
      rather than into a claim. Across these 60 further `Stt` runs the maximum idle duration is
      **516 ms** and no run of either suite in either condition reported anything near 5 s. That
      makes it one unreproduced observation in 120 subsequent runs. It is **not** cleared — a
      once-in-120 event is exactly what would survive this — but nothing in this change depends on
      it, and it stays where §1.3 put it: a watch item, carried forward rather than closed.
- [x] 6.5 Every fence and signal this change adds has a recorded verbatim failure (§3.6, §4.3).

      Inventory of what this change adds that could rot, and where each one's red run is recorded:

      | added signal | how it was falsified | verbatim failure recorded in |
      |---|---|---|
      | the four TTS mid-flight assertions (`observed == 1`, `stateAtCancel == Open`, `gate.Held`, `gate.Delivered == 1`) | neutralised `OutboundFrameGate.PassAsync`'s hold, leaving the gate installed | §3.6 — `Expected observed to be 1 …, but found 7 / 8 / 9 / 6`, one per surface |
      | the four STT `CloseSent` witnesses (`ReceivedClientCloseFrame`) | dropped `or WebSocketState.CloseSent` from all four fakes' loop conditions | §4.3 — `Failed: 4, Passed: 129, Total: 133`, all four identical |

      Two things this section is claiming, and neither is "the tests pass":

      - **The gate mutation left `ThrowAsync<OperationCanceledException>` green in all eight
        neutralised runs** (§3.6). A test that asserted only "it threw" would have survived a
        mutation that removed the entire mid-flight condition — which is the exact test this change
        exists to replace, and the measurement is what makes that a fact rather than an argument.
      - **The fence mutation left the four `StreamAsync_ShouldNotHalfCloseTheOutputSide_WhenInputEnds`
        tests green** (§4.3). Those four already touched the same fakes and the same close path and
        could not tell the fence was gone; the contrast is the reason §4.2's witnesses are separate
        tests rather than extra assertions bolted onto the existing ones.

      Nothing else is added: `OutboundFrameGate` is inert with no gate armed (§3.1), so the
      substrate itself has no behaviour to fence beyond what these two mutations already cover.
- [x] 6.6 `openspec validate --all --strict` green.

      `Totals: 11 passed, 0 failed (11 items)` — 5 open changes (this one plus the four it was
      routed against in §7.3), the new `cartesia-tts-cancellation-precedence`, and 5 living specs
      including `streaming-session-lifecycle`, which the new change deltas.

      Run **after** §7.3 created the follow-up change, not before: the count moved 10 → 11 and a
      malformed proposal or spec delta in the new change would have failed the same CI gate this
      PR has to clear.

## 7. Close-out

- [x] 7.1 Fill the PR number into the CHANGELOG entry before archiving.

      **#227** — https://github.com/verbara/Verbara.Sdk/pull/227, two commits: `9c4d030c`
      (`test(voiceai):` — substrate, fakes and the thirteen tests) and `5db0b787`
      (`docs(openspec):` — this evidence and the follow-up change).

      **Placed in the body, not the heading, which is where §5.3 had put the placeholder.** Filling
      `(#PR)` in place would have made this the only `###` heading in the file carrying a PR number —
      the existing convention puts the reference inline next to the claim it belongs to
      (`CHANGELOG.md:98, :158, :191, :228`). Both of this change's sections carry it, since both
      shipped in the same PR: the `Added` section next to "none of the eight cancelled a session that
      had frames in flight", and the `Removed` section next to "the property and its dead branch are
      gone".
- [x] 7.2 `openspec archive voiceai-midstream-cancellation-coverage --yes` via the CLI, shipped as
      its own docs PR.

      **Archived after #227 landed on `main` as `dbceea02`** (merge queue, 2026-08-29). The CLI
      reported `test-determinism: update — + 2 added`, taking the living spec from 12 requirements
      to 14, and moved the change to
      `openspec/changes/archive/2026-08-29-voiceai-midstream-cancellation-coverage`.
      `openspec validate --all --strict`: **10 passed, 0 failed**.

      **No `--skip-specs`** — this change never went through `/opsx:sync`, confirmed by checking
      that neither added requirement was already present in the living spec. The delta is
      `## ADDED Requirements` only, so neither the dropped-scenario abort nor the
      capability-retirement abort was reachable. Closed with the CLI, not `/opsx:archive`, which
      never invokes it.

      **Referrer sweep clean.** `grep -rn "changes/voiceai-midstream-cancellation-coverage"` across
      the repo — not only Markdown — found nothing outside the change's own folder, so no workflow,
      script or fixture follows the moved path.

      **Step 7 of the closing routine is a no-op here.** It fills the `## Purpose` of living specs
      the archive *creates*, which are born `TBD`; `test-determinism` already existed with a
      written Purpose, and neither added requirement contradicts it — the fence-witness rule is the
      one its closing paragraph already describes, now stated normatively.
- [x] 7.3 Route anything found in `src/` that is not task 2.3 to an existing change rather than
      fixing it here — and if the named target has been archived, re-point it rather than dropping
      it, the way §5.7 of the sweep had to.

      **One finding to route.** §2.1 measured all four TTS synthesizers against blank text handed to
      an already-cancelled enumeration, and one of the four answers differently:

      | synthesizer | blank text + pre-cancelled token | why |
      |---|---|---|
      | **Cartesia** | **no throw, 0 frames** | blank-text `yield break` at `CartesiaSpeechSynthesizer.cs:52` runs first |
      | Deepgram | `OperationCanceledException`, caller's own token | guard `:61`, ahead of its `yield break` `:65` |
      | ElevenLabs | `OperationCanceledException`, caller's own token | guard `:50`, ahead of its `yield break` `:54` |
      | Lmnt | `OperationCanceledException`, caller's own token | guard `:110`, ahead of its `yield break` `:115` |

      **Why it is not fixed here.** §2.3's gate did not trigger: this change's licence to touch
      `src/` is task 2.3 and nothing else. A product behaviour change arriving inside a
      test-coverage PR is exactly the shape that gets waved through, and this one is a real
      behaviour change — a consumer who today receives an empty sequence would start receiving an
      exception.

      **Where it was routed, and why not to any of the four open changes.** Checked each against the
      finding rather than picking the nearest-sounding one:

      | open change | owns this? |
      |---|---|
      | `provider-dto-robustness-fences` | no — payload shape on the wire, not enumeration semantics |
      | `provider-schema-drift-train` | no — provider-side schema movement, nothing to do with tokens |
      | `longevity-soak-and-chaos` | no — behaviour over time and under fault, not a one-input divergence |
      | `enforce-unguarded-public-claims` | no — documentation claims vs. guards, not cancellation ordering |

      None of the four owns cancellation semantics, so re-pointing (the §5.7 move) had nothing to
      point at. What the finding *does* already have is a living spec that states the rule it
      breaks — `openspec/specs/streaming-session-lifecycle/spec.md:77-80`, *"Scenario: The
      consumer's own cancellation still faults … an `OperationCanceledException` is raised at the
      next iteration boundary, and this takes precedence over the sequence ending quietly"*.
      Cartesia ends quietly. That makes it a defect against a shipped requirement, not a new idea,
      so it was opened as its own change against that capability rather than dropped:

      **`openspec/changes/cartesia-tts-cancellation-precedence/`** — `proposal.md`
      (`tier: PEQUEÑO`, `decision_ref: Sdk/ADR-0053`), a `specs/streaming-session-lifecycle/spec.md`
      delta adding *"A requested cancellation outranks an empty-input shortcut"* with three
      scenarios (the fault, the unchanged non-cancelled shortcut, and all-four-surfaces parity), and
      an 11-task `tasks.md` that requires the failing test **before** the fix and a negative test
      **after** it.

      *(Archived 2026-08-29 to
      `openspec/changes/archive/2026-08-29-cartesia-tts-cancellation-precedence/`, shipped as #230.
      Note for anyone following this pointer: the paragraph above records the change **as opened**.
      Its own §1.1 re-measurement then found the same defect on Speechmatics — the package ships six
      synthesizers over seven selectable paths, not four — so what shipped was two guards and seven
      tests. The four-surface parity described here is the scope this record was written against,
      not the scope that landed.)*

      `openspec validate --all --strict` → `Totals: 11 passed, 0 failed (11 items)` — 10 before, plus
      the new change.
