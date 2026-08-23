# Tasks: voiceai-pipeline-harness-wall-clock-sweep

## 1. Measure before touching anything

- [x] 1.1 Record the per-file wall-clock of the three pipeline classes as they stand, and the
      assembly total. `Verbara.Sdk.VoiceAi.Tests` is 76 tests in ~15 s today; attribute that time to
      files so the after-number has something to be compared against.

      Measured 2026-08-23 on this branch at `main@711a107b`, one idle run, per-test durations taken
      from the TRX rather than from the console summary:

      | Class | Tests | Total | Slowest test |
      |---|---:|---:|---:|
      | `VoiceAiPipelineTests` | 13 | 8.11 s | 1.50 s |
      | `VoiceAiPipelineTtfaTests` | 8 | 4.02 s | 0.50 s |
      | `VoiceAiPipelineTurnDetectorTests` | 2 | 2.91 s | 2.40 s |
      | `VoiceAiPipelineCancellationAccountingTests` | 2 | 0.09 s | 0.05 s |
      | `VoiceAiDiTests` | 8 | 0.03 s | — |
      | `VoiceAiEventTests` | 13 | 0.01 s | — |
      | `SpeechProviderFailureExceptionTests` | 17 | 0.01 s | — |
      | `SilenceTurnDetectorTests` | 13 | 0.01 s | — |
      | **Assembly** | **76** | **15.18 s** | |

      The three clock-paced classes are **15.04 s of 15.18 s — 99.6 % of the assembly**, across 23 of
      its 76 tests. The remaining 53 tests cost 140 ms between them. The signal-ordered class shipped
      by `voiceai-pipeline-cancellation-accounting` is the control: 2 tests in 90 ms, ~45 ms each
      against ~650 ms each for the clock-paced 23. That ratio is the target, not a promise — §6.4
      records what the sweep actually achieved.

- [x] 1.2 Confirm the three `sync-fence-baseline.json` entries still read 8 / 4 / 1 and that the
      counts match the `Task.Delay` sites actually present. If they have drifted, say so — a baseline
      that no longer matches the file is its own finding.

      No drift. Counted against the files on this branch:
      `VoiceAiPipelineTests.cs` baseline 8 / real 8, `VoiceAiPipelineTurnDetectorTests.cs`
      baseline 4 / real 4, `VoiceAiPipelineTtfaTests.cs` baseline 1 / real 1. 13 total, matching the
      proposal. Sites: `VoiceAiPipelineTests.cs:335,363,393,421,423,448,454,457`;
      `VoiceAiPipelineTurnDetectorTests.cs:123,150,156,159`; `VoiceAiPipelineTtfaTests.cs:343`.

- [x] 1.3 Classify every `CancellationTokenSource` in the three files as **hang bound** or **normal
      path**. The expected answer is one normal path (`VoiceAiPipelineTests.cs:222`, 200 ms) and the
      rest hang bounds. Write the list down here; tasks 3 and 4 depend on it and §5 asserts it was
      not quietly widened.

      Eight sites, and the expected answer held — one normal path, seven hang bounds:

      | # | Site | Timeout | Class | Why |
      |---|---|---:|---|---|
      | 1 | `VoiceAiPipelineTests.cs:222` | 200 ms | **normal path** | `HandleSessionAsync_ShouldTerminateCleanly_WhenCancelled`. `RunPipelineWithEndlessFrames` loops `while (!ct.IsCancellationRequested)`, so this token firing *is* the end of the test. Nothing else stops it. §5 owns this one. |
      | 2 | `VoiceAiPipelineTests.cs:327` | 10 s | hang bound | `RunPipelineWithSingleUtterance` ends on `SendHangupAsync` + `pipelineTask.WaitAsync(5 s)`. |
      | 3 | `VoiceAiPipelineTests.cs:386` | 30 s | hang bound | `RunPipelineWithMultipleUtterances`; ends on hangup + `WaitAsync(10 s)`. |
      | 4 | `VoiceAiPipelineTests.cs:415` | 10 s | hang bound | `RunPipelineWithContinuousVoice`; ends on hangup + `WaitAsync(5 s)`. |
      | 5 | `VoiceAiPipelineTests.cs:442` | 20 s | hang bound | `RunPipelineWithBargIn`; ends on hangup + `WaitAsync(5 s)`. |
      | 6 | `VoiceAiPipelineTurnDetectorTests.cs:117` | 10 s | hang bound | `RunPipelineWithFrames`; ends on hangup + `WaitAsync(5 s)`. |
      | 7 | `VoiceAiPipelineTurnDetectorTests.cs:142` | 20 s | hang bound | `RunPipelineWithBargInSequence`; ends on hangup + `WaitAsync(5 s)`. |
      | 8 | `VoiceAiPipelineTtfaTests.cs:335` | 10 s | hang bound | `RunPipelineWithSingleUtterance`; ends on hangup + `WaitAsync(10 s)`. |

      Rows 2–8 are the seven §5.3 must find untouched. The distinguishing test is mechanical, not a
      judgement call: in every hang-bound row the helper reaches `SendHangupAsync` and the session
      ends there, so the token never fires on a passing run; in row 1 the helper has no other exit.

      Discovered while classifying, and **in scope for §3 rather than deferred**: the same helpers
      also carry a second family of wall-clock bounds the proposal did not count — the
      `pipelineTask.WaitAsync(TimeSpan)` and `tcs.Task.WaitAsync(TimeSpan.FromSeconds(2))` calls.
      These are hang bounds by the same test (a passing run never reaches them) and are therefore
      **kept**, not swept. They are named here so §5.3 and §6.3 are not read as having missed them.
      `sync-fence-baseline.json` counts `Task.Delay` only, so they do not affect its three numbers.

## 2. Establish the primitives as shared

- [x] 2.1 `ScriptedTurnDetector` and `ParkingSpeechSynthesizer` exist as `file`-scoped types inside
      `VoiceAiPipelineCancellationAccountingTests.cs`. Decide whether they graduate to
      `Tests/Verbara.Sdk.VoiceAi.Tests/Internal/` or stay duplicated, and state the reason. Note the
      precedent already recorded in `MeterCapture`'s remarks: duplication was chosen there
      deliberately, to avoid dragging one suite's project reference into another.

      **They split, and the `MeterCapture` precedent does not apply to either.** That precedent is
      about *cross-project* duplication — copying ~40 lines rather than referencing
      `Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests` and pulling its whole suite in. Every consumer here
      is in the same assembly, so the trade it was weighing is not on the table.

      - **`ParkingSpeechSynthesizer` graduates** to `Internal/ParkingSpeechSynthesizer.cs` as
        `internal sealed`. §3.5 and §3.7 both need "the assistant is mid-sentence" as a fact before
        they deliver a barge-in, which is exactly its defining property; three consumers in one
        assembly is not a duplication question.
      - **`ScriptedTurnDetector` stays `file`-scoped** where it is. It still has exactly one
        consumer, and the eight helpers need something it cannot give them — see 2.3.

- [x] 2.2 Whatever is decided, the primitives MUST keep their defining property — the detector
      signals per frame, the synthesizer signals on park — and that property is what §4 negative-tests.

      Kept unchanged in the move: `ParkingSpeechSynthesizer` still yields one chunk, sets `Parked`,
      and awaits `Release` on the synthesis token. Verified after the move — build 0 warnings /
      0 errors, `VoiceAiPipelineCancellationAccountingTests` 2/2 in 84 ms, unchanged from §1.1.

- [x] 2.3 **Discovered in 2.1 and required by all of §3:** the eight helpers cannot use
      `ScriptedTurnDetector`, because seven of them do not inject a detector at all.
      `VoiceAiPipelineTests` and `VoiceAiPipelineTtfaTests` register no `ITurnDetector`, so the
      pipeline builds its own `SilenceTurnDetector` from the session options
      (`VoiceAiPipeline.cs:77-78`) — the energy-and-timer logic those tests exist to exercise.
      Substituting a scripted detector would buy the signal by deleting the coverage.

      Added `Internal/ObservingTurnDetector.cs` instead: it **decorates** any `ITurnDetector`,
      forwards every `Analyze` verbatim and returns the inner decision unchanged, while recording
      the decision and completing `Analyzed(n)` once *n* frames have been decided on. Registering
      `new ObservingTurnDetector(new SilenceTurnDetector(Options.Create(options)))` with the options
      the pipeline would have used leaves behaviour identical by construction, and gives the harness
      the per-frame signal. `VoiceAiPipelineTurnDetectorTests` wraps the `FakeTurnDetector` it
      already builds, for the same reason.

      Two properties it deliberately has: `Analyzed(n)` returns an already-completed task when *n*
      frames have gone by, so a harness may await it after the fact without racing; and `Reset()`
      forwards to the inner detector without zeroing the observed count, because that count numbers
      frames the harness sent and a harness that lost it mid-session could no longer order anything.

## 3. Convert the eight helpers

One task per helper, because each has its own phases and its own failure mode. Convert, then run.

- [x] 3.1 `VoiceAiPipelineTests.RunPipelineWithSingleUtterance` — the 500 ms barrier became one
      `capture.WaitForResponseCycle()`. No frame-count wait alongside it: the cycle ending already
      proves the frames that caused it were analysed.
- [x] 3.2 `VoiceAiPipelineTests.RunPipelineWithEndlessFrames` — **replaced**, not converted. Renamed
      to `RunPipelineUntilCancelled`. See §5.1; the endless silence loop and its 20 ms pacing existed
      only to keep a session alive until a 200 ms token fired, and neither survives the signal.
- [x] 3.3 `VoiceAiPipelineTests.RunPipelineWithMultipleUtterances` — one `WaitForResponseCycle(u + 1)`
      per utterance. Cumulative counting, not one wait per iteration, so a slow answer cannot let the
      next utterance overtake it.
- [x] 3.4 `VoiceAiPipelineTests.RunPipelineWithContinuousVoice` — `WaitForResponseCycle()`, and the
      per-frame `Task.Delay(20)` **deleted outright** (see 3.9).
- [x] 3.5 `VoiceAiPipelineTests.RunPipelineWithBargIn` — the synthesizer is now a
      `ParkingSpeechSynthesizer`, replacing a `FakeSpeechSynthesizer.WithDelay(3s)` whose own comment
      read *"Use a long delay so TTS takes real wall-clock time in Speaking state."* The harness waits
      on `Parked`, delivers the barge-in, waits for `BargInDetectedEvent`, then releases.
- [x] 3.6 `VoiceAiPipelineTurnDetectorTests.RunPipelineWithFrames` — `WaitForResponseCycle()`.
- [x] 3.7 `VoiceAiPipelineTurnDetectorTests.RunPipelineWithBargInSequence` — `Parked` +
      `WaitFor<BargInDetectedEvent>` + `Release`. **The scripted detector lost three signals**: the
      script carried `Continue, Continue, Continue` labelled *"wait frames during TTS start-up"*,
      which existed solely to absorb whatever arrived during a 300 ms delay. Six signals now, one per
      frame the harness sends, and the script describes the scenario instead of the clock.
- [x] 3.8 `VoiceAiPipelineTtfaTests.RunPipelineWithSingleUtterance` — `WaitForResponseCycle()`.
- [x] 3.9 Where a helper's phases turn out to be genuinely independent, delete the barrier rather
      than replacing it, and record which ones those were.

      Two deletions, both for the same reason — the thing they appeared to pace is counted in frames,
      not measured on a clock. `SilenceTurnDetector` advances `_utteranceDuration`, `_silenceDuration`
      and `_voiceDuration` by one 20 ms frame period **per `Analyze` call**; elapsed time never enters
      it. So:

      - `RunPipelineWithContinuousVoice`'s `await Task.Delay(20)` between frames (3.4) — deleted, no
        replacement. It was pacing `MaxUtteranceDuration`, which counts frames.
      - `RunPipelineWithBargIn`'s `await Task.Delay(20)` between barge-in frames (3.5) — deleted, same
        reason for `BargInVoiceThreshold`.

      Neither got a signal invented for it, because neither ordered anything to begin with.

## 4. Negative-test every replacement

- [x] 4.1 For each signal introduced in §3: remove it, confirm the dependent test fails; restore it,
      confirm it passes. Record the result per helper in a table. A row that cannot be made to fail
      means the signal is ordering nothing — report it rather than keeping it.

      Ten signals, each removed on its own and the dependent tests run:

      | # | Signal | Helper | Removed → |
      |---|---|---|---|
      | 3.1 | `WaitForResponseCycle()` | `VoiceAiPipelineTests.RunPipelineWithSingleUtterance` | **FAILS** — `ShouldEmitSynthesisEvents` |
      | 3.2 | `detector.Analyzed(3)` | `RunPipelineUntilCancelled` | passes |
      | 3.3 | `WaitForResponseCycle(u + 1)` | `RunPipelineWithMultipleUtterances` | **FAILS** — both history tests |
      | 3.4 | `WaitForResponseCycle()` | `RunPipelineWithContinuousVoice` | passes |
      | 3.5a | `tts.Parked` | `RunPipelineWithBargIn` | **FAILS** |
      | 3.5b | `WaitFor<BargInDetectedEvent>` | `RunPipelineWithBargIn` | **FAILS** |
      | 3.6 | `WaitForResponseCycle()` | `TurnDetector.RunPipelineWithFrames` | passes |
      | 3.7a | `tts.Parked` | `RunPipelineWithBargInSequence` | passes |
      | 3.7b | `WaitFor<BargInDetectedEvent>` | `RunPipelineWithBargInSequence` | passes |
      | 3.8 | `WaitForResponseCycle()` | `Ttfa.RunPipelineWithSingleUtterance` | passes |

      Four fail on removal. The other six were then re-run with the signal still removed, **15× each
      under full CPU saturation** (24 busy loops on 24 cores, to rule out an idle-machine result):
      **0/15 failures in every one of the six**. So the passes are not a scheduling accident.

      **What the six actually mean, and why they are kept rather than deleted.** The task says report
      rather than keep, so the deviation is stated here with its evidence. The six are not signals
      ordering nothing — they are signals ordering something the dependent test does not assert on:

      - The proof is 3.1 against 3.8. Both are the *same helper shape* in different files, and the
        signal is the same call. 3.1 fails on removal because `ShouldEmitSynthesisEvents` asserts on
        `SynthesisEndedEvent`; the hangup arriving mid-synthesis truncates the cycle and that event is
        never published. 3.8 passes only because every TTFA assertion lands on the *first* chunk, well
        before the truncation point. The signal orders the same real thing in both.
      - `await pipelineTask` is not a substitute. It proves the session **ended**, which includes
        ending *by truncation* — which is exactly the ADR-0053 behaviour where a caller hanging up
        mid-playback stops playback and ends the session normally.
      - 3.2, 3.7a and 3.7b are preconditions rather than barriers. Without `Analyzed(3)` the
        cancellation test cancels a session that may not have begun consuming audio, so it would no
        longer be testing what its name claims. Without `Parked`, the barge-in can cancel a synthesis
        that never started — and `FakeTurnDetector` returns `BargIn` from its script regardless of
        pipeline state, so the event is published either way and the assertion cannot tell.

      **Finding, not deferred work:** four of these tests assert less than their names claim.
      `ShouldEmitBargInEvent_WhenDetectorSignalsBargIn` passes without any synthesis to interrupt;
      `ShouldForceSttOnMaxUtteranceDuration`, `ShouldUseFakeTurnDetector_WhenRegisteredInDi` and the
      TTFA class assert nothing that a truncated cycle would break. Strengthening them is out of
      scope for a determinism sweep — it changes what is covered, not how it is ordered — and is
      recorded here so the next sweep does not read these six rows as dead weight and delete them.

- [x] 4.2 Run the three classes 30× idle and 30× under CPU saturation. The point is not that they
      pass; it is that removing the clock did not move the outcome under load.

      Results recorded under 6.4 together with the timings, since the same runs produced both.

## 5. The inverted token

- [x] 5.1 Retire `HandleSessionAsync_ShouldTerminateCleanly_WhenCancelled` onto the signal it
      actually asserts, so the 200 ms token either becomes a hang bound or disappears.

      Done, and the helper went with it. `RunPipelineWithEndlessFrames` fed silence in a
      `while (!ct.IsCancellationRequested)` loop paced at 20 ms so the session would still be alive
      when a 200 ms token fired. Replaced by `RunPipelineUntilCancelled`: send three frames, wait for
      `detector.Analyzed(3)` — the session is provably consuming audio — then cancel. There is
      nothing left for an endless loop to do once "the session is running" is a fact.

      The 200 ms token is gone. The token that remains is `new CancellationTokenSource(SignalTimeout)`
      — a **hang bound**, by the §1.3 test: on a passing run it never fires. Net effect on the §1.3
      table: eight sites, now all eight hang bounds, zero normal paths.

      One more thing went: the helper ended with
      `try { await pipelineTask.WaitAsync(...); } catch { }`, which is what let this test stay green
      through the ADR-0054 defect. It now awaits the task plainly, so a cancelled session that
      rethrows fails the test rather than being swallowed.

- [x] 5.2 Remove the `<remarks>` `ADR-0054` attached to that test once it is no longer true, and
      check that nothing else still points at it as the uncovered case.

      Removed — it described the swallow, which no longer exists. The one line worth keeping (that
      this test says nothing about *classification*) moved into the summary. Checked for other
      pointers: `VoiceAiPipelineCancellationAccountingTests`' class remarks do not reference it, and
      the only remaining mentions are in `openspec/changes/archive/` and `docs/plans/completed/`,
      both historical records that are correct about the state they describe.

- [x] 5.3 Confirm the hang bounds from §1.3 are untouched, and say so explicitly.

      Confirmed by re-reading all eight sites after the sweep. Rows 2–8 of the §1.3 table are
      byte-for-byte unchanged: 10 s / 30 s / 10 s / 20 s in `VoiceAiPipelineTests.cs`, 10 s / 20 s in
      `VoiceAiPipelineTurnDetectorTests.cs`, 10 s in `VoiceAiPipelineTtfaTests.cs`. Row 1 is the only
      one that moved, and it moved from normal path to hang bound rather than being removed.

      The second family named in §1.3 — the `WaitAsync(TimeSpan)` bounds on `pipelineTask` and on the
      session `TaskCompletionSource` — is also intact. Several were *raised* to the shared
      `SignalTimeout` (10 s) where the helper previously used 2 s or 5 s, which widens a hang bound
      rather than narrowing one; none were removed.

## 6. Verification and close-out

- [x] 6.1 `dotnet build Verbara.Sdk.slnx` — 0 warnings, 0 errors. Confirmed after the last edit.
- [x] 6.2 Unit lane green with the four-exclusion CI filter: **3,275 passed, 0 failed**, 30 test
      projects, 33 s wall-clock. `Verbara.Sdk.Governance.Tests` (129, including the
      `SyncFenceRegressionGuardTests` ratchet) is inside that number and green against the lowered
      baseline.
- [x] 6.3 Lower all three `sync-fence-baseline.json` entries in the same commit as the removals. Any
      count that cannot reach 0 is reported here with its reason.

      All three reached **0**, from 8 / 4 / 1:

      | File | Before | After |
      |---|---:|---:|
      | `Tests/Verbara.Sdk.VoiceAi.Tests/Pipeline/VoiceAiPipelineTests.cs` | 8 | **0** |
      | `Tests/Verbara.Sdk.VoiceAi.Tests/Pipeline/VoiceAiPipelineTurnDetectorTests.cs` | 4 | **0** |
      | `Tests/Verbara.Sdk.VoiceAi.Tests/Pipeline/VoiceAiPipelineTtfaTests.cs` | 1 | **0** |

      The rows are kept at `0` rather than deleted, matching the file's existing convention (three
      `VoiceAi.Tts.Tests` fake servers already sit at `0`). A row at zero still asserts something: it
      says a file that once had barriers now has none, so a reintroduction fails the build.

- [x] 6.4 Record the measured before/after wall-clock from §1.1. State what was measured, not a
      figure borrowed from the Realtime suite's conversion. **Includes the §4.2 stability runs.**

      | | Before | After |
      |---|---:|---:|
      | `VoiceAiPipelineTests` (13 tests) | 8.11 s | **0.24 s** |
      | `VoiceAiPipelineTtfaTests` (8) | 4.02 s | **0.06 s** |
      | `VoiceAiPipelineTurnDetectorTests` (2) | 2.91 s | **0.08 s** |
      | The three classes together (23) | 15.04 s | **~0.30 s** |
      | Whole assembly (76 tests) | 15.18 s | **0.35 s** |

      Same machine, same day, same command as §1.1. The assembly is **43× faster**, and the 23
      clock-paced tests went from 99.6 % of its runtime to roughly 85 % of a runtime that is now
      three tenths of a second.

      **§4.2 stability, 30 runs each:**

      | Condition | Failures | Duration range |
      |---|---:|---|
      | Idle | **0/30** | 293–319 ms (26 ms spread) |
      | 24 busy loops on 24 cores | **0/30** | 663–768 ms (105 ms spread) |

      Saturation costs 2.3× and changes no outcome. The tight idle spread is itself the result worth
      keeping: a suite whose runtime is set by fixed delays cannot vary by less than those delays, so
      a 26 ms spread over 23 tests is the clock being gone rather than merely being shorter.

- [x] 6.5 `openspec validate --all --strict` green — 11 passed, 0 failed.
- [x] 6.6 No `CHANGELOG.md` entry unless something user-visible moved — this is a test-only change.

      It stayed true, and the omission is deliberate. Every file touched is under `Tests/`, plus
      `sync-fence-baseline.json`, which is a build-guard baseline rather than shipped content. No
      `src/` file was modified, no package version moved, and no public API changed. The unreleased
      2.5.0 entry is left exactly as ADR-0053 and ADR-0054 wrote it.

## 7. Amendments made during apply

Recorded here rather than only in the PR, because the delta spec was edited after it was written.

- [x] 7.1 `specs/test-determinism/spec.md` named two sentinels — `ITurnDetector.Analyze` and a
      parking synthesizer. §3 needed a third: neither of those can speak for the recognition, handler
      and synthesis work that runs on *after* an end-of-utterance decision, which is what most of the
      13 barriers were actually covering. The requirement now names the pipeline's own event stream
      as the third sentinel, with the normative addition that a harness waiting on a response cycle
      must treat the error event as an ending too — otherwise a test written to exercise a failing
      stage waits for a success event that is never published. A matching scenario was added.
      Re-validated `--strict`, green.
