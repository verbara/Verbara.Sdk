# Tasks: voiceai-pipeline-cancellation-accounting

## 1. Reproduce

- [x] 1.1 Order a barge-in against a synthesis completion by construction and confirm the
      `ObjectDisposedException` escapes `AudioMonitorLoop`. If no seam exists that is not a sleep,
      say so explicitly and price the `internal` hook rather than reaching for a delay.

      Two orderings reach that throw, and only one of them is reproducible:

      - **Barge-in after `DisposeAsync`** — reproduced, deterministically, with no `internal` hook.
        `DisposeAsync` disposed `_ttsCts` without nulling it, so the field stayed published and
        disposed indefinitely; the window is unbounded, not a race. Two existing seams order the
        whole test: a `SpeechSynthesizer` that parks after its first chunk (so `_ttsCts` is provably
        assigned and `PipelineLoop`'s `finally` provably has not run), and `ITurnDetector.Analyze`,
        which the pipeline calls synchronously once per frame on the monitor loop's own thread — so
        "send one frame, wait for its signal" is an exact ordering primitive. Pre-fix it fails on
        `VoiceAiPipeline.cs:144` with `ObjectDisposedException: The CancellationTokenSource has been
        disposed`, `voiceai.sessions.failed == 1`, `voiceai.sessions.completed == 0`.
      - **Barge-in racing `PipelineLoop`'s `finally`** — **no seam that is not a sleep, and the
        `internal` hook is not worth its price.** The window is between the field read and the next
        statement: `CancelAsync()` throws synchronously, before any await point, so there is nothing
        to interpose on. Either side of that pair a test can reach, the state is coherent — the
        `finally` nulls the field *before* disposing, so a seam after it sees `null` and skips. A
        hook would have to sit between two adjacent instructions, would exist only for a window the
        fix closes, and would have no meaning in production code. Closed by construction instead,
        and written down in `ADR-0054` rather than left as a gap.

      Test: `Tests/Verbara.Sdk.VoiceAi.Tests/Pipeline/VoiceAiPipelineCancellationAccountingTests.cs`
      → `HandleSessionAsync_ShouldEndCleanlyAndStayAccountedFor_WhenABargeInFollowsDisposal`.

- [x] 1.2 Show that `VoiceAiPipelineTests.cs:202` stays green across both outcomes, so it is not
      mistaken later for coverage of either defect.

      `HandleSessionAsync_ShouldTerminateCleanly_WhenCancelled` passes pre-fix (session counted
      failed, `OperationCanceledException` rethrown) and post-fix (session counted completed, nothing
      thrown). Its helper `RunPipelineWithEndlessFrames` ends in `try { … } catch { }`, so the
      session task's outcome never reaches an assertion. Recorded where it will be read: a
      `<remarks>` on the test names what it does not cover and points at the new class.

- [x] 1.3 Assert the current disagreement directly: one test showing a cancelled pipeline session
      increments `voiceai.sessions.failed`, one showing a cancelled bridge session does not.

      Pipeline half added: `HandleSessionAsync_ShouldEndCleanlyAndStayAccountedFor_WhenTheCallerCancels`
      — pre-fix, `fault` is an `OperationCanceledException`, `failed == 1`, `completed == 0`. Bridge
      half already exists from `ADR-0053`:
      `OpenAiRealtimeBridgeSetupWindowTests.HandleSessionAsync_ShouldEndCleanlyAndStayAccountedFor_WhenCancelledDuringConnect`
      asserts `openai_realtime.sessions.failed == 0` and `completed == 1`. The two names are
      deliberately identical past the `When`, because after `ADR-0054` they assert the same contract.

## 2. Decide

- [x] 2.1 Decide what a cancelled session counts as, for both `ISessionHandler` implementations, and
      record it. This is a telemetry contract, so it must not be inferable only from the diff.

      **A requested cancellation is a completion, and is not rethrown** — the pipeline moves to the
      bridge, not the other way round. `ADR-0053` argued classification from *who ended the session*,
      and nobody but the caller ends a cancelled one. Recorded as `ADR-0054` R1, indexed in
      `docs/decisions/README.md`, and stated in the CHANGELOG as a telemetry break in the same family
      and style as `ADR-0053`'s.

- [x] 2.2 Decide `_ttsCts`'s single owner. State why the chosen shape makes cancel-after-dispose
      unreachable rather than merely unlikely.

      **Owner: `PipelineLoop`** — the only member that creates or disposes the source. Everyone else
      expresses intent only, through a private `CancelSynthesis()`. A `Lock` covers every read, write
      and cancel; the `finally` nulls the field *under* the gate and disposes *outside* it.

      That ordering is what makes the throw unreachable rather than unlikely: the only reference
      anyone can obtain is one taken while the field was live, and the source is disposed only after
      nobody can obtain one at all. Whichever side wins the gate, the loser sees a coherent state — a
      live source to cancel, or a null field to skip. `ADR-0054` R2/R3.

## 3. Fix

- [x] 3.1 Apply the §2.2 ownership decision to `_ttsCts`; `DisposeAsync` and `PipelineLoop`'s
      `finally` must not both be able to release it while `AudioMonitorLoop` can still reach it.

      `DisposeAsync` no longer releases it at all — it cancels, and `PipelineLoop`'s `finally`
      disposes (`ADR-0054` R4, `ADR-0053`'s intent-versus-mechanism split applied to a second
      disposable). Publishing a new source and observing an already-disposed pipeline happen under
      the same gate, so a `DisposeAsync` landing as a synthesis starts cannot cancel nothing and
      leave that synthesis running past the disposal meant to stop it.

- [x] 3.2 Apply the §2.1 decision to `HandleSessionAsync`'s handler, replacing the bare `catch`.

      `catch (OperationCanceledException) when (ct.IsCancellationRequested)` now sits ahead of it and
      swallows. The bare `catch` is unchanged for everything else.

- [x] 3.3 Confirm the §1 tests pass and still fail when the fix is reverted.

      Both pass. Reverted one component at a time, each test fails on its own half and only its own
      half — so neither is a tautology and neither is carried by the other:

      | Reverted | Cancellation test | Barge-in test |
      |---|---|---|
      | nothing (fixed) | pass | pass |
      | the `OperationCanceledException` filter | **fail** | pass |
      | the `_ttsGate` ownership change | pass | **fail** |
      | only `_events.Dispose()`'s removal | pass | **fail** |
      | all of `src/` | **fail** | **fail** |

## 4. Verification and release

- [x] 4.1 `dotnet build Verbara.Sdk.slnx` — 0 warnings, 0 errors.
- [x] 4.2 Unit lane green with the four-exclusion CI filter.
- [x] 4.3 Regression tests 30× green, idle and under CPU saturation.
- [x] 4.4 CHANGELOG, stating any telemetry meaning that changed.
- [x] 4.5 Version bump if `src/` behaviour moved.

      No further bump. `Directory.Build.props` already reads **2.5.0**, unreleased and untagged, and
      it is already a minor because `ADR-0053` put behavioural breaks in it. This change adds another
      break to the same unreleased minor; the CHANGELOG paragraph is what distinguishes them.

- [x] 4.6 `openspec validate --all --strict` green.

## Discovered while applying

Recorded here rather than in the PR prose, per the closing routine.

- **`_events.Dispose()` was a second, independent path to the same defect**, and the proposal does
  not name it. `Subject<T>.OnNext` after `OnCompleted` is a silent no-op; after `Dispose` it throws
  `ObjectDisposedException`. Both loops outlive `DisposeAsync` and publish as they unwind — the
  barge-in's own `Publish(new BargInDetectedEvent(…))` is the first one — so fixing `_ttsCts` alone
  left the barge-in test failing on the subject instead. Reverting only this line still fails the
  test. Fixed here (`ADR-0054` R5) because a partial fix would leave `DisposeAsync` mid-session
  throwing for the same reason, in the same method, one statement later.

- **A third symptom of the same defect, which the proposal does not record:** disposing a parent
  `CancellationTokenSource` with a live linked child does **not** throw and leaves the child
  **uncancelled**. So pre-fix, `DisposeAsync` landing mid-synthesis silently broke barge-in for that
  turn — the barge-in's `Cancel` threw, and even had it not, `linked` would have stayed live. The
  ODE was the loud half of a defect whose quiet half was a barge-in that did nothing.

- **The pipeline suite's own harness is built on wall-clock barriers, and no open change owns it.**
  `RunPipelineWithSingleUtterance`, `RunPipelineWithMultipleUtterances`, `RunPipelineWithBargIn` and
  `RunPipelineWithContinuousVoice` order their phases with `Task.Delay(200)`/`Task.Delay(500)`, and
  `HandleSessionAsync_ShouldTerminateCleanly_WhenCancelled` ends its session with
  `CancellationTokenSource(TimeSpan.FromMilliseconds(200))` — a token expiring on schedule, which is
  `ADR-0045`'s second contract inverted. `websocket-fake-class-ab-sweep` covers the WebSocket fakes,
  not this AudioSocket harness, so this is currently unowned. Not folded in here: it is a test-only
  sweep across ~12 tests with no `src/` component, and this change already carries two failure modes.

- **`ADR-0053` was never added to `docs/decisions/README.md`.** Indexed here alongside `ADR-0054`.
