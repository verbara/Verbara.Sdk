# Tasks: hangup-mid-playback-is-not-a-synthesis-failure

## 1. Reproduce before fixing

- [ ] 1.1 Write the regression test first, against the unfixed pipeline, and record its verbatim
      failure. `HandleSessionAsync_ShouldNotReportASynthesisFailure_WhenTheFarEndHangsUpMidPlayback`
      in `Tests/Verbara.Sdk.VoiceAi.Tests/Pipeline/VoiceAiPipelineCancellationAccountingTests.cs`,
      built only from seams that already exist in that file: `ScriptedTurnDetector(SpeechStarted,
      EndOfUtterance)`, `CreateAudioSessionAsync()`, `MeterCapture`, `RecordingLogger`,
      `SynthesisActivityRecorder` and `PipelineEventCapture`. Order it by construction, never by a
      delay: subscribe a `TaskCompletionSource` to `session.OnHangup` before the session starts,
      because the session's teardown sets its disposed flag before it fires that event
      (`AudioSocketSession.cs:197-204`, `:232`), so the completion of that task is the proof the next
      write must throw.

      Assert, under one `AssertionScope`: `tts.syntheses.failed` 0, no `PipelineErrorEvent`, nothing
      logged at Warning or above, the synthesis activity not `Error`, no throw from
      `HandleSessionAsync`, `voiceai.sessions.failed` 0 and `voiceai.sessions.completed` 1. Pin the
      accounting the ending moves into, marked in the test as today's accounting and not required
      (ADR-0050 E9): `tts.syntheses.completed` 1 and one `SynthesisEndedEvent`.

- [ ] 1.2 Give that test a synthesizer that keeps speaking after the hangup. A file-local
      `SpeechSynthesizer` that yields one chunk, parks until released, then yields a second chunk —
      the shape of `Internal/ParkingSpeechSynthesizer.cs` plus one chunk after the park, so the first
      chunk is written to a live session and the second meets a torn-down one. Have it count the
      chunks the pipeline actually pulled, and assert the pipeline stopped pulling after the write
      that found the session gone.

- [ ] 1.3 Add the control that keeps the catch scoped to the write: a synthesizer that raises an
      `ObjectDisposedException` of its own from `MoveNextAsync`, on a session that is still connected,
      is still a synthesis failure —
      `HandleSessionAsync_ShouldPublishTtsPipelineError_WhenTheSynthesizerIsUsedAfterItsOwnDisposal`.
      Assert `tts.syntheses.failed` 1, one `PipelineErrorEvent` with source `Tts` carrying that
      exception, a Warning entry and the activity `Error`. Green before the fix and after it.

- [ ] 1.4 Add the control that keeps the catch from widening to every exception at the write: with a
      synthesizer that ignores the token it is handed and parks between chunks, cancel the caller's
      token while it is parked, release it, and let the next `WriteAudioAsync` observe the cancelled
      token —
      `HandleSessionAsync_ShouldNotReportASynthesisFailure_WhenTheCallerCancelsTheWrite`. Assert the
      caller's clause still owns it: no fault, `voiceai.sessions.completed` 1,
      `tts.syntheses.failed` 0, and — as today's accounting, not a requirement —
      `tts.syntheses.completed` 0 with no `SynthesisEndedEvent`. If the flush turns out not to observe
      the token deterministically, say so in this task and pin mutation (c) of 4.3 by reading instead;
      do not leave the claim unmarked.

- [ ] 1.5 Confirm the existing controls are untouched and must stay green before and after:
      `HandleSessionAsync_ShouldNotReportASynthesisFailure_WhenABargeInCancelsIt`,
      `…_WhenDisposalCancelsIt`, `…_WhenTheCallerCancelsIt`, the precedence theory
      `…_WhenARequestedEndingLandsWhileTheOwnCancellationUnwinds`, and
      `VoiceAiPipelineTests.HandleSessionAsync_ShouldEmitPipelineErrorEvent_OnTtsError`.

## 2. Fix

- [ ] 2.1 In `VoiceAiPipeline.PipelineLoop`, wrap the `session.WriteAudioAsync` call
      (`src/Verbara.Sdk.VoiceAi/Pipeline/VoiceAiPipeline.cs:324`) — and only that call — in
      `catch (ObjectDisposedException)`, leave the synthesis enumeration, and account the turn exactly
      as the barge-in clause does: `tts.syntheses.completed` +1, one `SynthesisEndedEvent`, no
      `PipelineErrorEvent`, the activity untouched, and the turn **not** added to the conversation
      history. Do not fall through to the normal-completion tail, which adds the history turn and
      guards `tts.syntheses.silent`. Replace no existing clause.

- [ ] 2.2 Add `[LoggerMessage(LogLevel.Debug, …)]` to `src/Verbara.Sdk.VoiceAi/Internal/VoiceAiLog.cs`
      for this ending — neutral wording naming the channel, alongside `BargInDetected` — and call it
      from the new catch. Source-generated, internal, no reflection, no public API change.

- [ ] 2.3 Confirm by reading, and write the confirmation into this task, that an
      `ObjectDisposedException` out of `AudioSocketSession.WriteAudioAsync` can only mean the session
      has ended: the guard at `AudioSocketSession.cs:118` reads `_disposed`, which only the private
      terminate sets (`:232`), and the transport it disposes last (`:236`) is the exception's only
      other origin. Record that a transport failure on a still-connected session surfaces as
      `IOException`/`SocketException`, is not caught, and stays a synthesis failure — and that the
      narrow race between the guard and the flush is the residual ADR-0058 records.

- [ ] 2.4 Sweep `Verbara.Sdk.VoiceAi` for any other write to the audio session that sits inside a
      clause classifying a synthesis or a session. Fix it here only if it is inside `VoiceAiPipeline`;
      record anything elsewhere without changing it, including
      `OpenAiRealtimeBridge.cs:266-271`, which already carries this handling and is the precedent, not
      a defect.

## 3. Decision record

- [ ] 3.1 Land `docs/decisions/0058-a-write-to-a-departed-session-is-an-ending-not-a-failure.md`
      (**Sdk/ADR-0058**, Accepted). It records: the write-side guard fires for every ending while the
      read-side guard fires only for the owner's disposal, so ADR-0053 R2's reading of
      `ObjectDisposedException` does not carry across unchanged; a write that finds the session gone
      is ADR-0053 R3's ending seen from the other side; the ending is accounted as a barge-in is,
      under ADR-0050 E9's standing debt; the catch is scoped to the write so a provider's own
      `ObjectDisposedException` is untouched; and the flush-race residual, written down rather than
      left as a gap. Related: ADR-0053, ADR-0054, ADR-0050.

- [ ] 3.2 Add the ADR-0058 row to `docs/decisions/README.md` in numeric order, matching the format of
      the existing rows.

- [ ] 3.3 Repoint this change's `decision_ref` to `Sdk/ADR-0058` once that file exists, so the
      proposal cites the decision it rests on rather than the closest neighbour
## 4. Verification

- [ ] 4.1 `dotnet build Verbara.Sdk.slnx -c Release`: 0 warnings, 0 errors.

- [ ] 4.2 Unit lane green under the CI filter, with coverage, `Verbara.Sdk.Governance.Tests` included.
      Record the assembly and test counts, the coverage line and branch figures against their floors,
      and `tools/audit-test-asserts.sh` violations.

- [ ] 4.3 Mutations, each applied alone to the final tree, built, run against
      `Verbara.Sdk.VoiceAi.Tests.Pipeline`, then restored. Each must fail at least one test; record
      which, with the verbatim assertion lines:
      (a) the write catch removed;
      (b) the catch moved from the write to the whole synthesis `try`;
      (c) the write catch widened to `catch (Exception)`;
      (d) the new log call raised from Debug to Warning;
      (e) `VoiceAiPipeline.cs` from `origin/main` with the final tests.
      Record separately, as closed by reading and not by test, that routing the ending into the
      normal-completion tail instead of the barge-in accounting differs only in the conversation-history
      turn, which no test can observe after a hangup.

- [ ] 4.4 The new tests pass 20 runs in a row against a Release build, with the per-run timing.

- [ ] 4.5 `openspec validate --all --strict` green.

- [ ] 4.6 CI green; record the PR number and the commit that landed on `main`.

## 5. Close-out

- [ ] 5.1 `CHANGELOG.md` `[Unreleased]` entry stating the telemetry change in both directions: a turn
      the caller hung up on stops counting in `tts.syntheses.failed` and stops publishing
      `PipelineErrorEvent` and a Warning line; every provider failure, including a synthesizer's own
      cancellation, is unchanged. Leave the `(#N)` citation for close-out.

- [ ] 5.2 Reconcile the living spec and run
      `openspec archive hangup-mid-playback-is-not-a-synthesis-failure --yes` once the fix is on
      `main`, as its own `docs(openspec):` PR.
