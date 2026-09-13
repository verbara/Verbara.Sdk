# Tasks: tts-own-cancellation-is-a-synthesis-failure

## 1. Reproduce before fixing

- [ ] 1.1 Write the regression test first, against the unfixed pipeline, and record its verbatim
      failure. The synthesizer raises its own cancellation, both as a `TaskCanceledException` with an
      inner `TimeoutException`, carrying the token of a source the synthesizer owns and has cancelled,
      and as a plain `OperationCanceledException`, each before yielding any audio and after yielding
      one chunk, under a caller token that is never cancelled. Assert one `PipelineErrorEvent` with
      source `Tts` carrying that exception, no `SynthesisEndedEvent`, `tts.syntheses.failed` 1 and
      `tts.syntheses.completed` 0, `tts.synthesis.ttfa_ms` recorded exactly when audio was yielded,
      `voiceai.sessions.failed` 0, and no throw from `HandleSessionAsync`.
- [ ] 1.2 Add the controls that keep the fix from over-correcting: a barge-in, a `DisposeAsync` and
      the caller's cancellation during a synthesis are not reported as synthesis failures (no
      `tts.syntheses.failed`, no `PipelineErrorEvent`, nothing at Warning, the activity not `Error`).
      They must pass before the fix and after it.
- [ ] 1.3 Pin the precedence while a synthesizer's own cancellation is unwinding. The synthesizer
      raises its own `TaskCanceledException` with an inner `TimeoutException` and parks in its
      enumerator's `DisposeAsync`, which `PipelineLoop` awaits before the exception reaches its catch
      filters; a barge-in, a `DisposeAsync` or the caller's cancellation lands there; then the
      enumerator is released. No synthesis failure is reported and the session does not fault. It
      must pass before the fix and after it, and order the race by that seam, not by a sleep.
- [ ] 1.4 Pin that the session goes on to its next turn after that failure: the caller speaks again,
      and the pipeline recognises, handles and synthesises the next utterance. Wait for the second
      response cycle under the signal bound, and assert `tts.syntheses.started` 2.

## 2. Fix

- [ ] 2.1 Filter the barge-in clause in `VoiceAiPipeline.PipelineLoop` on
      `ttsCts.IsCancellationRequested`, and replace its comment with the reason. Confirm by reading
      that both intended cancellers reach that source, and that the source is still live when the
      filter runs.
- [ ] 2.2 Sweep `Verbara.Sdk.VoiceAi` for any other unfiltered `catch (OperationCanceledException)`
      that books a provider's own cancellation as a requested one. Fix it here only if it is inside
      `VoiceAiPipeline`; record anything elsewhere without changing it.

## 3. Verification

- [ ] 3.1 `dotnet build Verbara.Sdk.slnx -c Release`: 0 warnings, 0 errors.
- [ ] 3.2 Unit lane green under the CI filter, with coverage, `Verbara.Sdk.Governance.Tests` included.
- [ ] 3.3 Mutations: the filter removed, the filter on the wrong condition, the filter inverted, the
      barge-in clause dropped, the filter narrowed by the exception's shape, the filter widened by the
      exception's token, the filter widened to book an own cancellation as completed once audio was
      yielded, and the failure clause leaving the loop after an own cancellation. Each must fail at
      least one test.
- [ ] 3.4 The new tests pass 20 runs in a row.
- [ ] 3.5 `openspec validate --all --strict` green.
- [ ] 3.6 CI green.

## 4. Close-out

- [ ] 4.1 CHANGELOG `[Unreleased]` entry that states the telemetry change, with the PR number.
- [ ] 4.2 `openspec archive tts-own-cancellation-is-a-synthesis-failure --yes` once the fix is on
      `main`.
