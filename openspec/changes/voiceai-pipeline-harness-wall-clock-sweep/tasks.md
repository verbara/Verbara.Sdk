# Tasks: voiceai-pipeline-harness-wall-clock-sweep

## 1. Measure before touching anything

- [ ] 1.1 Record the per-file wall-clock of the three pipeline classes as they stand, and the
      assembly total. `Verbara.Sdk.VoiceAi.Tests` is 76 tests in ~15 s today; attribute that time to
      files so the after-number has something to be compared against.
- [ ] 1.2 Confirm the three `sync-fence-baseline.json` entries still read 8 / 4 / 1 and that the
      counts match the `Task.Delay` sites actually present. If they have drifted, say so — a baseline
      that no longer matches the file is its own finding.
- [ ] 1.3 Classify every `CancellationTokenSource` in the three files as **hang bound** or **normal
      path**. The expected answer is one normal path (`VoiceAiPipelineTests.cs:222`, 200 ms) and the
      rest hang bounds. Write the list down here; tasks 3 and 4 depend on it and §5 asserts it was
      not quietly widened.

## 2. Establish the primitives as shared

- [ ] 2.1 `ScriptedTurnDetector` and `ParkingSpeechSynthesizer` exist as `file`-scoped types inside
      `VoiceAiPipelineCancellationAccountingTests.cs`. Decide whether they graduate to
      `Tests/Verbara.Sdk.VoiceAi.Tests/Internal/` or stay duplicated, and state the reason. Note the
      precedent already recorded in `MeterCapture`'s remarks: duplication was chosen there
      deliberately, to avoid dragging one suite's project reference into another.
- [ ] 2.2 Whatever is decided, the primitives MUST keep their defining property — the detector
      signals per frame, the synthesizer signals on park — and that property is what §4 negative-tests.

## 3. Convert the eight helpers

One task per helper, because each has its own phases and its own failure mode. Convert, then run.

- [ ] 3.1 `VoiceAiPipelineTests.RunPipelineWithSingleUtterance`
- [ ] 3.2 `VoiceAiPipelineTests.RunPipelineWithEndlessFrames`
- [ ] 3.3 `VoiceAiPipelineTests.RunPipelineWithMultipleUtterances`
- [ ] 3.4 `VoiceAiPipelineTests.RunPipelineWithContinuousVoice`
- [ ] 3.5 `VoiceAiPipelineTests.RunPipelineWithBargIn`
- [ ] 3.6 `VoiceAiPipelineTurnDetectorTests.RunPipelineWithFrames`
- [ ] 3.7 `VoiceAiPipelineTurnDetectorTests.RunPipelineWithBargInSequence`
- [ ] 3.8 `VoiceAiPipelineTtfaTests.RunPipelineWithSingleUtterance`
- [ ] 3.9 Where a helper's phases turn out to be genuinely independent, delete the barrier rather
      than replacing it, and record which ones those were. A signal invented to justify a barrier
      that never ordered anything is worse than the barrier.

## 4. Negative-test every replacement

- [ ] 4.1 For each signal introduced in §3: remove it, confirm the dependent test fails; restore it,
      confirm it passes. Record the result per helper in a table, the way
      `voiceai-pipeline-cancellation-accounting` §3.3 did. A row that cannot be made to fail means
      the signal is ordering nothing — report it rather than keeping it.
- [ ] 4.2 Run the three classes 30× idle and 30× under CPU saturation. The point is not that they
      pass; it is that removing the clock did not move the outcome under load.

## 5. The inverted token

- [ ] 5.1 Retire `HandleSessionAsync_ShouldTerminateCleanly_WhenCancelled` onto the signal it
      actually asserts, so the 200 ms token either becomes a hang bound or disappears.
- [ ] 5.2 Remove the `<remarks>` `ADR-0054` attached to that test once it is no longer true, and
      check that nothing else — `VoiceAiPipelineCancellationAccountingTests`' own class remarks
      included — still points at it as the uncovered case.
- [ ] 5.3 Confirm the hang bounds from §1.3 are untouched, and say so explicitly. Removing them is
      the over-reach failure this change is most likely to commit.

## 6. Verification and close-out

- [ ] 6.1 `dotnet build Verbara.Sdk.slnx` — 0 warnings, 0 errors.
- [ ] 6.2 Unit lane green with the four-exclusion CI filter.
- [ ] 6.3 Lower all three `sync-fence-baseline.json` entries in the same commit as the removals. Any
      count that cannot reach 0 is reported here with its reason, not parked silently above zero.
- [ ] 6.4 Record the measured before/after wall-clock from §1.1. State what was measured, not a
      figure borrowed from the Realtime suite's conversion.
- [ ] 6.5 `openspec validate --all --strict` green.
- [ ] 6.6 No `CHANGELOG.md` entry unless something user-visible moved — this is a test-only change.
      If that stays true, say so here rather than leaving the omission to be read as forgetfulness.
