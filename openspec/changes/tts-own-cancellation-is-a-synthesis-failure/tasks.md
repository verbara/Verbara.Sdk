# Tasks: tts-own-cancellation-is-a-synthesis-failure

## 1. Reproduce before fixing

- [x] 1.1 Write the regression test first, against the unfixed pipeline, and record its verbatim
      failure. The synthesizer raises its own cancellation, both as a `TaskCanceledException` with an
      inner `TimeoutException`, carrying the token of a source the synthesizer owns and has cancelled,
      and as a plain `OperationCanceledException`, each before yielding any audio and after yielding
      one chunk, under a caller token that is never cancelled. Assert one `PipelineErrorEvent` with
      source `Tts` carrying that exception, no `SynthesisEndedEvent`, `tts.syntheses.failed` 1 and
      `tts.syntheses.completed` 0, `tts.synthesis.ttfa_ms` recorded exactly when audio was yielded,
      `voiceai.sessions.failed` 0, and no throw from `HandleSessionAsync`.

      `VoiceAiPipelineCancellationAccountingTests.HandleSessionAsync_ShouldPublishTtsPipelineError_WhenSynthesizerCancelsOnItsOwn`,
      first a `[Theory]` over both shapes raised before any audio, run against the unfixed pipeline
      before `src/` changed. Both cases failed on exactly the assertions the defect predicts. Verbatim
      for `shape: HttpClientTimeout`, trimmed to the assertion lines (the plain shape reads the same):

      ```
      Expected capture.Events.OfType<PipelineErrorEvent>() to contain a single item matching (Convert(e.Source, Int32) == 1) AndAlso ReferenceEquals(e.Exception, System.Threading.Tasks.TaskCanceledException: The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing. […]) because the synthesizer cancelled itself while nobody had asked it to stop, but the collection is empty.
      Expected capture.Events.OfType<SynthesisEndedEvent>() to be empty because nothing was spoken, so no synthesis ended, but found at least one item
      Expected ttsMetrics.Get("tts.syntheses.completed") to be 0L, but found 1L (difference of 1).
      Expected ttsMetrics.Get("tts.syntheses.failed") to be 1L, but found 0L (difference of -1).
      Expected logger.Entries to contain a single item matching (Convert(e.Level, Int32) == 3) AndAlso e.Message.Contains("[Tts]", Ordinal), but no such item was found.
      Expected activities.Statuses to be equal to {ActivityStatusCode.Error {value: 2}}, but {ActivityStatusCode.Unset {value: 0}} differs at index 0.
      Failed!  - Failed:     2, Passed:     4, Skipped:     0, Total:     6
      ```

      `HandleSessionAsync` did not throw and `voiceai.sessions.failed` read 0 before the fix too: the
      defect never reached the session, so only the synthesis assertions are red. The test also pins
      the Warning log and the activity status, the two other channels the failure clause owns, because
      both were silent as well.

      After review the theory gained a second axis, `chunksBeforeCancelling` 0 and 1, and all four
      cases now run on one file-local synthesizer that yields its chunks and then throws from the next
      `MoveNextAsync`, so with one chunk the pipeline has written audio to the session before the
      cancellation arrives.

      A second review found the `TaskCanceledException` shape built with `CancellationToken.None`,
      while both measured inputs carried a cancelled token, so a filter that also trusted the
      exception's token passed every test (3.3 (g)). The synthesizer now owns a source, cancels it
      right before it throws, and builds that shape around its token; the plain shape still carries
      none, and the theory asserts both premises. Against `origin/main`'s `VoiceAiPipeline.cs`, with
      the final tests, all four cases fail on the assertions above and so does 1.4
      (`Failed: 5, Passed: 57` over `Verbara.Sdk.VoiceAi.Tests.Pipeline`); the
      `tts.syntheses.started`, `tts.syntheses.silent`, `tts.synthesis.ttfa_ms`,
      `tts.synthesis.latency_ms` and session assertions pass on both sides. Verbatim for
      `shape: PlainOperationCanceled, chunksBeforeCancelling: 1`, trimmed to the assertion lines
      after the `PipelineErrorEvent` one:

      ```
      Expected capture.Events.OfType<SynthesisEndedEvent>() to be empty because a synthesis that failed did not end, but found at least one item
      Expected ttsMetrics.Get("tts.syntheses.completed") to be 0L, but found 1L (difference of 1).
      Expected ttsMetrics.Get("tts.syntheses.failed") to be 1L, but found 0L (difference of -1).
      Expected logger.Entries to contain a single item matching (Convert(e.Level, Int32) == 3) AndAlso e.Message.Contains("[Tts]", Ordinal), but no such item was found.
      Expected activities.Statuses to be equal to {ActivityStatusCode.Error {value: 2}}, but {ActivityStatusCode.Unset {value: 0}} differs at index 0.
      ```

- [x] 1.2 Add the controls that keep the fix from over-correcting: a barge-in, a `DisposeAsync` and
      the caller's cancellation during a synthesis are not reported as synthesis failures (no
      `tts.syntheses.failed`, no `PipelineErrorEvent`, nothing at Warning, the activity not `Error`).
      They must pass before the fix and after it.

      `HandleSessionAsync_ShouldNotReportASynthesisFailure_WhenABargeInCancelsIt`,
      `HandleSessionAsync_ShouldNotReportASynthesisFailure_WhenDisposalCancelsIt` and
      `HandleSessionAsync_ShouldNotReportASynthesisFailure_WhenTheCallerCancelsIt`, green before and
      after the fix. Each pins `tts.syntheses.failed` 0, nothing logged at Warning or above, a
      synthesis activity that is not `Error`, and `voiceai.sessions.failed` 0. The barge-in and caller
      controls also pin no `PipelineErrorEvent`, and the caller control `voiceai.sessions.completed`
      1 and no throw. The disposal control asserts no event: `DisposeAsync` completes the event
      stream, and a temporary probe saw `SynthesisEndedEvent` reach the subscriber anyway for a
      parked synthesis, because the cancellation's continuation ran before the stream completed. The
      counters, the log and the activity carry it.

      Each control also pins today's accounting, and says in the test that it is not required: a
      barge-in and a disposal count `tts.syntheses.completed` 1, the barge-in with one
      `SynthesisEndedEvent`, and the caller's cancellation counts neither and publishes no
      `SynthesisEndedEvent`. ADR-0050 E9 records counting a cancelled synthesis as completed as debt;
      this change keeps that accounting and does not require it.

      **The first red run caught a defect in the harness, not in the pipeline.** The disposal control
      happened to run first and recorded no synthesis activity at all. Its `ActivityListener` read
      `VoiceAiActivitySource.Source` inside `ShouldListenTo`, so that static was first initialised
      while the listener's own registration was still running, and the listener did not attach to the
      source. The recorder now reads the source before registering, and its comment says why.

- [x] 1.3 Pin the precedence while a synthesizer's own cancellation is unwinding. The synthesizer
      raises its own `TaskCanceledException` with an inner `TimeoutException` and parks in its
      enumerator's `DisposeAsync`, which `PipelineLoop` awaits before the exception reaches its catch
      filters; a barge-in, a `DisposeAsync` or the caller's cancellation lands there; then the
      enumerator is released. No synthesis failure is reported and the session does not fault. It
      must pass before the fix and after it, and order the race by that seam, not by a sleep.

      `HandleSessionAsync_ShouldNotReportASynthesisFailure_WhenARequestedEndingLandsWhileTheOwnCancellationUnwinds`,
      a `[Theory]` over `BargIn`, `PipelineDisposal` and `CallerToken`, green before and after the
      fix. `await foreach` awaits the enumerator's `DisposeAsync` before the exception reaches the
      catch filters, because an `await` in a `finally` is lowered as a catch, the await and a rethrow.
      The test waits for the synthesizer to say it is inside `DisposeAsync`, lands the ending and
      proves it landed (a barge-in by its `BargInDetectedEvent`, which the monitor loop publishes after
      `CancelSynthesis` returns; `DisposeAsync` cancels the source before it returns; `CancelAsync` is
      awaited), and only then releases the synthesizer. Each case takes a few milliseconds. It pins
      `tts.syntheses.failed` 0, nothing logged at Warning or above, a synthesis activity that is not
      `Error`, no `PipelineErrorEvent`, no throw, `voiceai.sessions.failed` 0 and
      `voiceai.sessions.completed` 1, plus today's `tts.syntheses.completed` (1, 1 and 0) as not
      required.

- [x] 1.4 Pin that the session goes on to its next turn after that failure: the caller speaks again,
      and the pipeline recognises, handles and synthesises the next utterance. Wait for the second
      response cycle under the signal bound, and assert `tts.syntheses.started` 2.

      `HandleSessionAsync_ShouldAnswerTheNextUtterance_WhenSynthesizerCancelsOnItsOwn`, added after
      the second review. The caller speaks twice. The synthesizer cancels its first synthesis on its
      own, with the `TaskCanceledException` shape of 1.1, and answers any later one with one chunk.
      The test waits for the second response cycle under `SignalTimeout` and records that wait
      instead of throwing it, so a loop that stopped still reports its counters. It pins the second
      cycle, two `TranscriptReceivedEvent`s and two `ResponseGeneratedEvent`s,
      `tts.syntheses.started` 2, `tts.syntheses.failed` 1 and `tts.syntheses.completed` 1, one `Tts`
      `PipelineErrorEvent` carrying the own cancellation, one `SynthesisEndedEvent`, no throw,
      `voiceai.sessions.failed` 0 and `voiceai.sessions.completed` 1.

      Against `origin/main`'s pipeline it fails on the classification, not on the next turn, which
      the unfixed pipeline also reaches. Trimmed to the counters:

      ```
      Expected ttsMetrics.Get("tts.syntheses.failed") to be 1L, but found 0L (difference of -1).
      Expected ttsMetrics.Get("tts.syntheses.completed") to be 1L because nothing cancelled the second synthesis, but found 2L.
      ```

      Only this test sees a failure clause that leaves the loop after an own cancellation (3.3 (h)).

## 2. Fix

- [x] 2.1 Filter the barge-in clause in `VoiceAiPipeline.PipelineLoop` on
      `ttsCts.IsCancellationRequested`, and replace its comment with the reason. Confirm by reading
      that both intended cancellers reach that source, and that the source is still live when the
      filter runs.

      Confirmed by reading. `AudioMonitorLoop`'s barge-in and `DisposeAsync` both call
      `CancelSynthesis`, which cancels `_ttsCts` under `_ttsGate`. That field holds this iteration's
      `ttsCts`, published under the same gate before the `try`, and cancelled right there instead if
      the pipeline was already disposed. Nothing else cancels it. `ttsCts` is a `using var` in the
      loop body, so it is disposed only when the iteration's block ends, after the `finally` that
      unpublishes it: it is live whenever the filter runs.

      The filter reads the source rather than comparing `ex.CancellationToken`. A synthesizer is
      handed the linked token, never `ttsCts.Token`, and one that links sources of its own (Cartesia,
      Deepgram and LMNT link `connectCts` over the handed token) raises whichever token it observed, so
      comparing tokens would miss a genuine barge-in. ADR-0053 records the same trap for the bridge's
      `ConnectAsync`.

      **A requested ending racing a provider's own cancellation is ordered, and tested (1.3).** The
      filters do not run where the synthesizer throws: `await foreach` first awaits the enumerator's
      `DisposeAsync`. A barge-in, a disposal or the caller's cancellation that lands before that
      disposal completes is therefore visible to the filters and wins, and the synthesizer's
      `DisposeAsync` is a seam a test can park on. By reading, a barge-in that lands after the filters
      have run finds a synthesis already classified as failed: it cancels a source that is still
      published but no longer affects that synthesis, or finds the field already null. Neither faults
      the session.

- [x] 2.2 Sweep `Verbara.Sdk.VoiceAi` for any other unfiltered `catch (OperationCanceledException)`
      that books a provider's own cancellation as a requested one. Fix it here only if it is inside
      `VoiceAiPipeline`; record anything elsewhere without changing it.

      `Verbara.Sdk.VoiceAi` has exactly one unfiltered `catch (OperationCanceledException)`, the one
      fixed here. In the same loop, the recognition and handler arms already let a cancellation that
      is not the caller's reach their failure clause, and `HandleSessionAsync` filters on the caller's
      token.

      Outside the package, recorded and not changed:

      | site | what the catch sees |
      |---|---|
      | `AudioSocketSession.ReadLoopAsync` | the session's own source, cancelled by its teardown |
      | `AudioSocketServer.AcceptLoopAsync`, twice | the server's stop token, around the accept and the backoff wait |
      | the request send of the four WebSocket TTS clients (Cartesia, Deepgram, ElevenLabs, LMNT) and the send loop of the four WebSocket STT clients (AssemblyAI, Cartesia, Deepgram, Speechmatics) | the token the send was given; on Deepgram TTS, a session source linked over the caller's token |
      | `DeepgramSpeechSynthesizer`'s `Close` send | its own 2-second deadline, linked over the session token; a teardown path, so the swallow ends no sequence the caller sees (the ADR-0050 addendum already classifies it) |
      | the receive loops of the same eight clients, `catch (OperationCanceledException) { break; }` | the token the loop was given; on Deepgram TTS, the same linked session source |

      The tokens were followed for Deepgram TTS; the rest are recorded by their shape and their own
      comments. None of these catches is where a synthesizer's own connect deadline is caught:
      `connectCts.CancelAfter(ConnectTimeoutSeconds)` wraps `ConnectAsync` outside all of them, which
      is why that cancellation reaches the pipeline at all. Whether a receive loop's `break` can turn a
      provider-side cancellation into a quiet ending is a question for the provider suites, not for
      this change.

## 3. Verification

- [x] 3.1 `dotnet build Verbara.Sdk.slnx -c Release`: 0 warnings, 0 errors.

      `0 Warning(s)`, `0 Error(s)`.

- [x] 3.2 Unit lane green under the CI filter, with coverage, `Verbara.Sdk.Governance.Tests` included.

      `dotnet test Verbara.Sdk.slnx -c Release` with the four-exclusion filter and
      `--collect:"XPlat Code Coverage" --settings coverlet.runsettings`: **30 assemblies, 3 547
      passed, 0 failed**. `Verbara.Sdk.VoiceAi.Tests` 86 (76 before this change plus its ten test
      cases), `Verbara.Sdk.Governance.Tests` 129. `tools/audit-test-asserts.sh`: `Violations: 0`.

      Coverage, merged with ReportGenerator, on two runs of this tree: line **83.22 %** and **83.25 %**
      (band 83–86), branch 68.07 % both times (floor 64). Patch coverage **100.0 %** on the committed
      diff, one changed executable line of one: the filter itself. Coverage-exclusion markers 0
      against a baseline of 0.

      The second review added one test case (1.4) after these runs. On the final tree, run on their
      own, `Verbara.Sdk.VoiceAi.Tests` passes 87 and `Verbara.Sdk.Governance.Tests` 129; the lane
      total and the coverage figures above were not re-run.

- [x] 3.3 Mutations: the filter removed, the filter on the wrong condition, the filter inverted, the
      barge-in clause dropped, the filter narrowed by the exception's shape, the filter widened by the
      exception's token, the filter widened to book an own cancellation as completed once audio was
      yielded, and the failure clause leaving the loop after an own cancellation. Each must fail at
      least one test.

      Each applied alone to the final tree's `VoiceAiPipeline.cs`, built, run against
      `Verbara.Sdk.VoiceAi.Tests.Pipeline` (62 tests), then restored:

      | mutation | what fails |
      |---|---|
      | (a) filter removed | the four own-cancellation cases and 1.4, `Failed: 5, Passed: 57` |
      | (b) `when (!ct.IsCancellationRequested)` | the four own-cancellation cases and 1.4, `Failed: 5, Passed: 57` |
      | (c) `when (!ttsCts.IsCancellationRequested)` | the four own-cancellation cases, 1.4, the barge-in and disposal controls, and the `BargIn` and `PipelineDisposal` precedence cases, `Failed: 9, Passed: 53` |
      | (d) the barge-in clause dropped | the barge-in and disposal controls, and the `BargIn` and `PipelineDisposal` precedence cases, `Failed: 4, Passed: 58` |
      | (e) `when (ttsCts.IsCancellationRequested && oce.InnerException is not TimeoutException)` | the `BargIn` and `PipelineDisposal` precedence cases, `Failed: 2, Passed: 60` |
      | (f) `ttfaRecorded` declared before the `try`, and `when (ttsCts.IsCancellationRequested \|\| ttfaRecorded)` | the two `chunksBeforeCancelling: 1` cases, `Failed: 2, Passed: 60` |
      | (g) M6-token: `when (ttsCts.IsCancellationRequested \|\| oce.CancellationToken.IsCancellationRequested)` | the two `HttpClientTimeout` cases and 1.4, `Failed: 3, Passed: 59` |
      | (h) M3-break: `if (ex is OperationCanceledException) break;` right after the failure clause publishes the `Tts` `PipelineErrorEvent` | 1.4 alone, `Failed: 1, Passed: 61` |
      | `VoiceAiPipeline.cs` from `origin/main`, with the final tests | the four own-cancellation cases and 1.4, on the §1.1 assertions, `Failed: 5, Passed: 57` |

      (b) behaves as (a) in every test because the caller's clause above it already took every
      cancellation whose caller token was cancelled when it was evaluated, so
      `!ct.IsCancellationRequested` is true at that position unless the caller cancels between the two
      filter evaluations.
      (d) is the over-correction: every cancellation that is not the caller's goes to the failure
      clause. Only the controls and the precedence cases see it, which is why they exist.

      (e) narrows the filter by the exception's shape, so a barge-in that landed before the pipeline
      classified a provider's `HttpClient.Timeout` loses to it. Only the precedence theory sees it.
      Verbatim for `ending: BargIn`, trimmed to its first assertion lines:

      ```
      Expected ttsMetrics.Get("tts.syntheses.failed") to be 0L because the requested ending was visible before the pipeline classified the synthesis, but found 1L (difference of 1).
      Expected activities.Statuses not to be ActivityStatusCode.Error {value: 2}, but it is.
      ```

      (f) books a synthesis whose provider cancelled it after some audio was played as completed. Only
      the after-audio cases see it. Verbatim for `shape: PlainOperationCanceled,
      chunksBeforeCancelling: 1`, trimmed to the counters:

      ```
      Expected ttsMetrics.Get("tts.syntheses.completed") to be 0L, but found 1L (difference of 1).
      Expected ttsMetrics.Get("tts.syntheses.failed") to be 1L, but found 0L (difference of -1).
      ```

      (g) widens the filter by the exception's token, so a synthesizer whose own deadline cancelled a
      token of its own is booked as a barge-in again. It passed every test while the
      `TaskCanceledException` shape carried `CancellationToken.None`, and fails since that shape
      carries the token of a source its synthesizer cancelled (1.1). Verbatim for
      `shape: HttpClientTimeout, chunksBeforeCancelling: 0`, trimmed to the counters:

      ```
      Expected ttsMetrics.Get("tts.syntheses.completed") to be 0L, but found 1L (difference of 1).
      Expected ttsMetrics.Get("tts.syntheses.failed") to be 1L, but found 0L (difference of -1).
      ```

      (h) leaves the loop after the failure: the session keeps reading audio and never answers again.
      It passed every test until 1.4, whose wait for the second cycle now reaches its 10-second bound.
      Verbatim, trimmed to the first four assertion lines:

      ```
      Expected secondCycle to be <null> because the pipeline goes on to the caller's next utterance, but found System.TimeoutException: The operation has timed out.
      Expected capture.Events.OfType<TranscriptReceivedEvent>() to contain 2 item(s) because both utterances were recognised, but found 1:
      Expected capture.Events.OfType<ResponseGeneratedEvent>() to contain 2 item(s) because both transcripts were handled, but found 1:
      Expected ttsMetrics.Get("tts.syntheses.started") to be 2L because both responses were synthesised, but found 1L.
      ```

- [x] 3.4 The new tests pass 20 runs in a row.

      `VoiceAiPipelineCancellationAccountingTests`, the class the new tests live in (thirteen test
      cases, including the two it already had), run 20 times against a Release build of the final
      tree: 20 of 20 runs `Passed: 13, Failed: 0`, between 160 and 175 ms each.

- [x] 3.5 `openspec validate --all --strict` green.

      `Totals: 10 passed, 0 failed (10 items)`.

- [ ] 3.6 CI green.

## 4. Close-out

- [ ] 4.1 CHANGELOG `[Unreleased]` entry that states the telemetry change, with the PR number.
- [ ] 4.2 `openspec archive tts-own-cancellation-is-a-synthesis-failure --yes` once the fix is on
      `main`.
