# Tasks: voiceai-pipeline-cancellation-accounting

## 1. Reproduce

- [ ] 1.1 Order a barge-in against a synthesis completion by construction and confirm the
      `ObjectDisposedException` escapes `AudioMonitorLoop`. If no seam exists that is not a sleep,
      say so explicitly and price the `internal` hook rather than reaching for a delay.
- [ ] 1.2 Show that `VoiceAiPipelineTests.cs:202` stays green across both outcomes, so it is not
      mistaken later for coverage of either defect.
- [ ] 1.3 Assert the current disagreement directly: one test showing a cancelled pipeline session
      increments `voiceai.sessions.failed`, one showing a cancelled bridge session does not.

## 2. Decide

- [ ] 2.1 Decide what a cancelled session counts as, for both `ISessionHandler` implementations, and
      record it. This is a telemetry contract, so it must not be inferable only from the diff.
- [ ] 2.2 Decide `_ttsCts`'s single owner. State why the chosen shape makes cancel-after-dispose
      unreachable rather than merely unlikely.

## 3. Fix

- [ ] 3.1 Apply the §2.2 ownership decision to `_ttsCts`; `DisposeAsync` and `PipelineLoop`'s
      `finally` must not both be able to release it while `AudioMonitorLoop` can still reach it.
- [ ] 3.2 Apply the §2.1 decision to `HandleSessionAsync`'s handler, replacing the bare `catch`.
- [ ] 3.3 Confirm the §1 tests pass and still fail when the fix is reverted.

## 4. Verification and release

- [ ] 4.1 `dotnet build Verbara.Sdk.slnx` — 0 warnings, 0 errors.
- [ ] 4.2 Unit lane green with the four-exclusion CI filter.
- [ ] 4.3 Regression tests 30× green, idle and under CPU saturation.
- [ ] 4.4 CHANGELOG, stating any telemetry meaning that changed.
- [ ] 4.5 Version bump if `src/` behaviour moved.
- [ ] 4.6 `openspec validate --all --strict` green.
