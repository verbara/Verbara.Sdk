# Tasks: voiceai-midstream-cancellation-coverage

Every claim below was read off the tree at `5a0458ba`, not carried over from the sweep's record —
the sweep's own summary of this gap turned out to be wrong in three places (see proposal §Why), and
its line numbers have drifted. Re-read before trusting a citation.

## 1. Baseline — the numbers this change corrects

- [ ] 1.1 Re-run the enumeration and check it in as the change's own working evidence: for each of
      the eight WebSocket fakes, the cancellation test (if any), where the token fires, and what the
      test asserts the fake saw. The current answer is seven tests over eight fakes — six
      pre-cancelled (`ReceivedFrameCount.Should().Be(0)` on the four STT surfaces,
      `ReceivedJsonMessages.Should().BeEmpty()` on Deepgram and ElevenLabs TTS), one on a live but
      deliberately silenced socket (`LmntSpeechSynthesizerTests.cs:444-491`), and Cartesia TTS with
      none.

- [ ] 1.2 Confirm the two hold-open flags' consumer status by grep, not by reading their remarks:
      `HangForever` (`DeepgramTtsFakeServer`) has declaration + its own `if` and no assignment
      anywhere; `HoldOpenUntilDisposed` (`LmntWsFakeServer`) is set only by
      `LmntSpeechSynthesizerWsTests.SynthesizeAsync_ShouldAbort_WhenCancelled`. Record the counts.

- [ ] 1.3 Record the two suites' current wall clock and test counts through the same harness the
      sweep used (`Stt` 125, `Tts` 149 under the CI filter; `Tts` reports 153 unfiltered because the
      four `VoiceCatalogConformanceTests` are counted and skipped). The added tests must not move it
      materially, and "materially" needs a before number to mean anything.

## 2. Cartesia TTS — the surface with no cancellation test

- [ ] 2.1 **Measure before writing.** `CartesiaSpeechSynthesizer` is the only one of the four TTS
      synthesizers with no `ct.ThrowIfCancellationRequested()`; the other three have exactly one
      each. Write a throwaway probe that enumerates `SynthesizeAsync` with a pre-cancelled token and
      record verbatim what is thrown and from where. The living spec requires the throw to land
      *before the first provider request is issued*, and on this surface the first thing reached is
      `BuildUri()` then `ConnectAsync`.

- [ ] 2.2 If 2.1 shows the contract holds (the throw comes out as `OperationCanceledException` with
      the fake recording no session), add the pre-cancelled test in the shape the other seven use
      and stop — no `src/` change.

- [ ] 2.3 If 2.1 shows it does not hold, add the entry guard to
      `src/Verbara.Sdk.VoiceAi.Tts/Cartesia/CartesiaSpeechSynthesizer.cs` **in its own commit** with
      its own CHANGELOG line under `Fixed`, because that is a product behaviour change and must not
      arrive disguised as test coverage. Note that `SynthesizeAsync` already opens with a
      `yield break` on blank text, so the guard's placement relative to that branch is a decision,
      not a detail — record which and why.

- [ ] 2.4 Either way, add Cartesia TTS to the per-surface enumeration in §1.1 so the count stops
      being seven-over-eight.

## 3. Mid-flight cancellation, per surface

- [ ] 3.1 Decide the delivery signal each fake needs. The test must cancel with a frame **already
      observed by the caller**, not merely sent by the fake — a frame written to the socket is not
      yet a frame the enumeration has yielded, and cancelling on the former reintroduces the race
      the sweep spent itself removing. Prefer cancelling from inside the caller's own
      `await foreach` after the first chunk, the shape
      `SpeechmaticsSpeechSynthesizerTests.cs:105-136` and `LmntSpeechSynthesizerTests.cs:632-660`
      already use over HTTP; reach for a fake-side signal only where the WebSocket path cannot.

- [ ] 3.2 Four STT surfaces: cancel after the first transcript has been yielded, with the fake still
      holding frames to send. Assert the throw comes from the recognizer (ADR-0052 F3 — the token
      goes to `StreamAsync` only, never to `ToListAsync`/`WithCancellation`) and assert the
      server-side socket state at the cancel, the way the Lmnt test does, so the test states what
      held rather than only that it threw.

- [ ] 3.3 Four TTS surfaces: the same, after the first audio chunk has been yielded. On Lmnt this is
      a **second** test, not a replacement — the existing one asserts cancellation on a live silent
      socket and that case stays covered.

- [ ] 3.4 Give `HangForever` a consumer or delete it. If a Deepgram TTS mid-flight test can be
      written against the normal hold path, `HangForever` is dead code and goes; if it is the only
      way to hold that surface open mid-delivery, it finally gets the assignment it has never had.
      Decide from the test that gets written, not in advance.

- [ ] 3.5 Re-run the `HoldOpenUntilDisposed` swap experiment against the **new** Lmnt test: replace
      the flag's body with `await receiveTask` and record the result. The sweep measured 10/10 green
      against the old test. If the new one goes red, the flag is falsifiable at last and its remarks
      must be rewritten to say so; if it stays green, the flag is unconsumed by the spec's
      definition and that is the finding.

- [ ] 3.6 Negative-test every new test the way the sweep's own added requirement demands: remove the
      fence or signal it depends on, run, record the failure verbatim, restore, re-run green. A test
      added by this change and never observed failing is not evidence.

## 4. Witness the `CloseSent` fence

- [ ] 4.1 Confirm the premise still holds at implementation time:
      `grep -rn 'CloseAsync\|CloseOutputAsync' src/Verbara.Sdk.VoiceAi.Stt/` returns exactly one hit
      today and it is a **comment** (`CartesiaSpeechRecognizer.cs:153`, describing a half-close that
      was removed). If a recognizer has since started sending a close frame, the fence has a natural
      witness and this section shrinks to an assertion.

- [ ] 4.2 Add one test per STT fake driving it with a raw `System.Net.WebSockets.ClientWebSocket`:
      complete the handshake, send whatever frame the fake's protocol expects, `CloseOutputAsync`,
      and assert the session survives — the fake keeps reading and still delivers. The fence under
      test is `while (ws.State is WebSocketState.Open or WebSocketState.CloseSent)` at
      `AssemblyAiFakeServer.cs:237`, `CartesiaFakeServer.cs:248`, `DeepgramFakeServer.cs:215`,
      `SpeechmaticsFakeServer.cs:263`.

- [ ] 4.3 Negative-test it: drop the `or WebSocketState.CloseSent` disjunct, observe the new test
      red and the four existing `StreamAsync_ShouldNotHalfCloseTheOutputSide_WhenInputEnds` still
      green — that contrast **is** the point of the section and belongs in the record verbatim.

- [ ] 4.4 Do not touch `src/` to produce the condition. The sweep proved this fence live only by
      temporarily reinstating the removed half-close, which is a measurement technique and must not
      be committed.

## 5. The CHANGELOG correction

- [ ] 5.1 Correct the sweep's `[Unreleased]` bullet: seven cancellation tests over eight fakes, six
      pre-cancelled and one on a live silent socket; `HoldOpenUntilDisposed` has a consumer that
      cannot falsify it, `HangForever` has none. Keep the bullet's point — the gap is real — and fix
      only what is false.

- [ ] 5.2 Leave `openspec/changes/archive/2026-08-23-websocket-fake-class-ab-sweep/` untouched. It
      is a period-correct record; the correction lives in this change's proposal and in the
      CHANGELOG, which is still unreleased (2.5.0 untagged, latest tag `v2.4.0`).

- [ ] 5.3 Add this change's own `[Unreleased]` entry when it ships.

## 6. Verification

- [ ] 6.1 `dotnet build Verbara.Sdk.slnx` — zero warnings, Debug and Release.
- [ ] 6.2 Unit lane green under the four-exclusion CI filter, with the new per-surface counts stated.
- [ ] 6.3 Determinism: 30× both suites idle and 30× under CPU saturation, all green. Mid-flight
      cancellation is exactly the shape that passes idle and fails loaded, so an idle-only run is
      not a measurement.
- [ ] 6.4 Wall clock through the §1.3 harness, reported against the recorded before-numbers with the
      spread, not as a bare delta.
- [ ] 6.5 Every fence and signal this change adds has a recorded verbatim failure (§3.6, §4.3).
- [ ] 6.6 `openspec validate --all --strict` green.

## 7. Close-out

- [ ] 7.1 Fill the PR number into the CHANGELOG entry before archiving.
- [ ] 7.2 `openspec archive voiceai-midstream-cancellation-coverage --yes` via the CLI, shipped as
      its own docs PR.
- [ ] 7.3 Route anything found in `src/` that is not task 2.3 to an existing change rather than
      fixing it here — and if the named target has been archived, re-point it rather than dropping
      it, the way §5.7 of the sweep had to.
