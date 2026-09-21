# Tasks: hangup-mid-playback-is-not-a-synthesis-failure

## 1. Reproduce before fixing

- [x] 1.1 Write the regression test first, against the unfixed pipeline, and record its verbatim
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

      **Done.** Written from those seams only, and run against the unfixed pipeline with `src/`
      untouched (the write at `VoiceAiPipeline.cs:324` is still inside the try whose last clause
      books a synthesis failure). It fails, on six assertions at once inside the single
      `AssertionScope`. Verbatim `dotnet test` output, with the absolute repo prefix replaced by
      `<repo>` and nothing else changed:

          [xUnit.net 00:00:00.15]     Verbara.Sdk.VoiceAi.Tests.Pipeline.VoiceAiPipelineCancellationAccountingTests.HandleSessionAsync_ShouldNotReportASynthesisFailure_WhenTheFarEndHangsUpMidPlayback [FAIL]
            Failed Verbara.Sdk.VoiceAi.Tests.Pipeline.VoiceAiPipelineCancellationAccountingTests.HandleSessionAsync_ShouldNotReportASynthesisFailure_WhenTheFarEndHangsUpMidPlayback [75 ms]
            Error Message:
             Expected ttsMetrics.Get("tts.syntheses.failed") to be 0L because nothing failed — the far end left while the assistant was still speaking, but found 1L (difference of 1).
          Expected capture.Events.OfType<PipelineErrorEvent>() to be empty because a departed session is not a provider letting the pipeline down, but found at least one item
          {
              Verbara.Sdk.VoiceAi.Events.PipelineErrorEvent
              {
                  Exception = System.ObjectDisposedException: Cannot access a disposed object.
          Object name: 'Verbara.Sdk.VoiceAi.AudioSocket.AudioSocketSession'.
             at Verbara.Sdk.VoiceAi.AudioSocket.AudioSocketSession.WriteAudioAsync(ReadOnlyMemory`1 pcmData, AudioSocketFrameType frameType, CancellationToken ct) in <repo>/src/Verbara.Sdk.VoiceAi.AudioSocket/AudioSocketSession.cs:line 118
             at Verbara.Sdk.VoiceAi.Pipeline.VoiceAiPipeline.PipelineLoop(AudioSocketSession session, ChannelReader`1 utteranceReader, IConversationHandler handler, List`1 history, CancellationToken ct) in <repo>/src/Verbara.Sdk.VoiceAi/Pipeline/VoiceAiPipeline.cs:line 324
             at Verbara.Sdk.VoiceAi.Pipeline.VoiceAiPipeline.PipelineLoop(AudioSocketSession session, ChannelReader`1 utteranceReader, IConversationHandler handler, List`1 history, CancellationToken ct) in <repo>/src/Verbara.Sdk.VoiceAi/Pipeline/VoiceAiPipeline.cs:line 313,
                  Message = "Cannot access a disposed object.
          Object name: 'Verbara.Sdk.VoiceAi.AudioSocket.AudioSocketSession'.",
                  Source = PipelineErrorSource.Tts {value: 1},
                  Timestamp = <2026-09-21 04:10:23.4277287 +0h>
              }
          }.
          Expected logger.Entries
          {
              Verbara.Sdk.VoiceAi.Tests.Pipeline.<VoiceAiPipelineCancellationAccountingTests>F8F26A11AF5D9B4DC9AC1F7834AFB4A1F9AAB5FC960C3166BC6F6A9981E2A06FB__LogEntry
              {
                  Level = LogLevel.Information {value: 2},
                  Message = "VoiceAi pipeline started for channel c4915f38-bf9d-4d02-aceb-c4b372308886"
              },
              Verbara.Sdk.VoiceAi.Tests.Pipeline.<VoiceAiPipelineCancellationAccountingTests>F8F26A11AF5D9B4DC9AC1F7834AFB4A1F9AAB5FC960C3166BC6F6A9981E2A06FB__LogEntry
              {
                  Level = LogLevel.Warning {value: 3},
                  Message = "VoiceAi pipeline error [Tts] for channel c4915f38-bf9d-4d02-aceb-c4b372308886: Cannot access a disposed object.
          Object name: 'Verbara.Sdk.VoiceAi.AudioSocket.AudioSocketSession'."
              },
              Verbara.Sdk.VoiceAi.Tests.Pipeline.<VoiceAiPipelineCancellationAccountingTests>F8F26A11AF5D9B4DC9AC1F7834AFB4A1F9AAB5FC960C3166BC6F6A9981E2A06FB__LogEntry
              {
                  Level = LogLevel.Information {value: 2},
                  Message = "VoiceAi pipeline stopped for channel c4915f38-bf9d-4d02-aceb-c4b372308886"
              }
          } to not have any items matching (Convert(e.Level, Int32) >= 3) because the way every call normally ends must not reach the stream an operator pages on, but found
          {
              Verbara.Sdk.VoiceAi.Tests.Pipeline.<VoiceAiPipelineCancellationAccountingTests>F8F26A11AF5D9B4DC9AC1F7834AFB4A1F9AAB5FC960C3166BC6F6A9981E2A06FB__LogEntry
              {
                  Level = LogLevel.Warning {value: 3},
                  Message = "VoiceAi pipeline error [Tts] for channel c4915f38-bf9d-4d02-aceb-c4b372308886: Cannot access a disposed object.
          Object name: 'Verbara.Sdk.VoiceAi.AudioSocket.AudioSocketSession'."
              }
          }.
          Expected activities.Statuses not to be ActivityStatusCode.Error {value: 2}, but it is.
          Expected ttsMetrics.Get("tts.syntheses.completed") to be 1L, but found 0L (difference of -1).
          Expected capture.Events.OfType<SynthesisEndedEvent>() to contain a single item, but the collection is empty.

            Stack Trace:
               at FluentAssertions.Execution.LateBoundTestFramework.Throw(String message)
             at FluentAssertions.Execution.TestFrameworkProvider.Throw(String message)
             at FluentAssertions.Execution.CollectingAssertionStrategy.ThrowIfAny(IDictionary`2 context)
             at FluentAssertions.Execution.AssertionScope.Dispose()
             at Verbara.Sdk.VoiceAi.Tests.Pipeline.VoiceAiPipelineCancellationAccountingTests.HandleSessionAsync_ShouldNotReportASynthesisFailure_WhenTheFarEndHangsUpMidPlayback() in <repo>/Tests/Verbara.Sdk.VoiceAi.Tests/Pipeline/VoiceAiPipelineCancellationAccountingTests.cs:line 664
             at Verbara.Sdk.VoiceAi.Tests.Pipeline.VoiceAiPipelineCancellationAccountingTests.HandleSessionAsync_ShouldNotReportASynthesisFailure_WhenTheFarEndHangsUpMidPlayback()
             at Verbara.Sdk.VoiceAi.Tests.Pipeline.VoiceAiPipelineCancellationAccountingTests.HandleSessionAsync_ShouldNotReportASynthesisFailure_WhenTheFarEndHangsUpMidPlayback() in <repo>/Tests/Verbara.Sdk.VoiceAi.Tests/Pipeline/VoiceAiPipelineCancellationAccountingTests.cs:line 666
             at Verbara.Sdk.VoiceAi.Tests.Pipeline.VoiceAiPipelineCancellationAccountingTests.HandleSessionAsync_ShouldNotReportASynthesisFailure_WhenTheFarEndHangsUpMidPlayback() in <repo>/Tests/Verbara.Sdk.VoiceAi.Tests/Pipeline/VoiceAiPipelineCancellationAccountingTests.cs:line 666
          --- End of stack trace from previous location ---

          Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 75 ms - Verbara.Sdk.VoiceAi.Tests.dll (net10.0)

      What went red, and what did not: `tts.syntheses.failed` 1 (wanted 0), one `PipelineErrorEvent`
      with source `Tts` carrying the session's own `ObjectDisposedException`, a Warning entry
      (`VoiceAiLog.PipelineError`), the activity `Error`, `tts.syntheses.completed` 0 (today's
      accounting, wanted 1) and no `SynthesisEndedEvent`. Already green pre-fix, and therefore not
      part of the red: no throw from `HandleSessionAsync`, `voiceai.sessions.failed` 0,
      `voiceai.sessions.completed` 1 — the exception never reaches the session, exactly as the
      proposal reads it. The stack trace confirms the site: `AudioSocketSession.cs:118` reached from
      `VoiceAiPipeline.cs:324`.


      **Amended after task 4.3 (main session).** The mutation check found that deleting the
      `VoiceAiLog.PlaybackStoppedSessionEnded` call left the whole suite green: the delta spec's first
      scenario asks for the ending to be *"visible in the log below Warning"*, and nothing bound that
      half — the instruments half was pinned, the log half was not. A tree-wide grep for the symbol
      and for its message text returned zero references in any test assembly. One assertion was added
      to this test:

      ```csharp
      logger.Entries.Should().ContainSingle(
          e => e.Level == LogLevel.Debug
               && e.Message.Contains("the audio session had already ended", StringComparison.Ordinal),
          "the requirement asks for the ending to be visible in the log below Warning, so the "
          + "entry must be written and not merely available to write");
      ```

      Proved to bind rather than assumed: with the assertion in place the assembly is 90/90, and
      deleting the log call again turns this test red (`Failed: 1, Passed: 0, Total: 1`) where before
      it stayed green. Tree restored afterwards and re-verified.

- [x] 1.2 Give that test a synthesizer that keeps speaking after the hangup. A file-local
      `SpeechSynthesizer` that yields one chunk, parks until released, then yields a second chunk —
      the shape of `Internal/ParkingSpeechSynthesizer.cs` plus one chunk after the park, so the first
      chunk is written to a live session and the second meets a torn-down one. Have it count the
      chunks the pipeline actually pulled, and assert the pipeline stopped pulling after the write
      that found the session gone.

      **Done.** `KeepsSpeakingAfterTheParkSpeechSynthesizer`, file-local in the same file: chunk,
      park on a `TaskCompletionSource`, chunk. `ChunksPulled` is incremented before each
      `yield return` **and once more after the second one** — a point an async iterator's
      `DisposeAsync` can never reach, because it unwinds through `finally` blocks only and never
      resumes the body past a yield. So `2` is the pipeline having stopped pulling and `3` is it
      having gone on draining the answer; with only the two increments the count would have been `2`
      either way and the assertion vacuous. The regression test asserts
      `tts.ChunksPulled.Should().Be(2, "the pipeline stops pulling audio once the far end is gone")`.
      That one assertion is green pre-fix too — the unfixed pipeline stops because the exception
      unwinds the loop — so it guards the fix's shape (leave the enumeration; do not `continue`)
      rather than forming part of 1.1's red.

- [x] 1.3 Add the control that keeps the catch scoped to the write: a synthesizer that raises an
      `ObjectDisposedException` of its own from `MoveNextAsync`, on a session that is still connected,
      is still a synthesis failure —
      `HandleSessionAsync_ShouldPublishTtsPipelineError_WhenTheSynthesizerIsUsedAfterItsOwnDisposal`.
      Assert `tts.syntheses.failed` 1, one `PipelineErrorEvent` with source `Tts` carrying that
      exception, a Warning entry and the activity `Error`. Green before the fix and after it.

      **Done, and green before the fix.** `SelfDisposedSpeechSynthesizer` yields one chunk to a
      still-connected session, then throws its own `ObjectDisposedException` from the next
      `MoveNextAsync`; the test records `session.IsConnected` before it hangs up, so "the transport
      was fine" is asserted rather than assumed, and pins the exception by reference. Run together
      with 1.4 against the unfixed pipeline:
      `Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 54 ms`, and
      15 consecutive repeats of the pair, all `Failed: 0, Passed: 2`.

- [x] 1.4 Add the control that keeps the catch from widening to every exception at the write: with a
      synthesizer that ignores the token it is handed and parks between chunks, cancel the caller's
      token while it is parked, release it, and let the next `WriteAudioAsync` observe the cancelled
      token —
      `HandleSessionAsync_ShouldNotReportASynthesisFailure_WhenTheCallerCancelsTheWrite`. Assert the
      caller's clause still owns it: no fault, `voiceai.sessions.completed` 1,
      `tts.syntheses.failed` 0, and — as today's accounting, not a requirement —
      `tts.syntheses.completed` 0 with no `SynthesisEndedEvent`. If the flush turns out not to observe
      the token deterministically, say so in this task and pin mutation (c) of 4.3 by reading instead;
      do not leave the claim unmarked.

      **Done, and green before the fix. The conditional does not fire: the flush observes the
      caller's token deterministically, so mutation (c) of 4.3 stays a mutation and is not pinned by
      reading.** `TokenIgnoringParkingSpeechSynthesizer` parks on a `TaskCompletionSource` with no
      `WaitAsync(ct)` at all, so the cancellation is still unobserved when the second chunk reaches
      the write. `AudioSocketSession.WriteAudioAsync` buffers the frame and then awaits
      `_writer.FlushAsync(ct)`, and `StreamPipeWriter` registers the caller's token on the source it
      hands the socket send, which short-circuits on an already-cancelled token — so the write
      raises `OperationCanceledException` and the caller's clause rethrows it.

      The test proves that rather than assuming it: `tts.syntheses.completed` 0 and no
      `SynthesisEndedEvent` are reachable only if the write threw, because a flush that had
      succeeded would have run the normal-completion tail on the next `MoveNextAsync` and counted
      one of each. Measured 15 consecutive runs of the pair with 1.3, all
      `Failed: 0, Passed: 2` — no sleep, no retry, no relaxed assertion anywhere in it.

- [x] 1.5 Confirm the existing controls are untouched and must stay green before and after:
      `HandleSessionAsync_ShouldNotReportASynthesisFailure_WhenABargeInCancelsIt`,
      `…_WhenDisposalCancelsIt`, `…_WhenTheCallerCancelsIt`, the precedence theory
      `…_WhenARequestedEndingLandsWhileTheOwnCancellationUnwinds`, and
      `VoiceAiPipelineTests.HandleSessionAsync_ShouldEmitPipelineErrorEvent_OnTtsError`.

      **Done.** None of the five was edited. Listed by filter to confirm the set is exactly those
      five — 7 cases, the precedence theory contributing `BargIn`, `PipelineDisposal` and
      `CallerToken` — and run against the unfixed pipeline:
      `Passed!  - Failed:     0, Passed:     7, Skipped:     0, Total:     7, Duration: 108 ms`.
      The whole `Verbara.Sdk.VoiceAi.Tests` assembly is `Failed: 1, Passed: 89, Total: 90`, the one
      failure being 1.1 by design; `Verbara.Sdk.Governance.Tests` is 129/129 (the sync-fence guard
      included, with no baseline change) and `tools/audit-test-asserts.sh` reports 0 violations.

## 2. Fix

- [x] 2.1 In `VoiceAiPipeline.PipelineLoop`, wrap the `session.WriteAudioAsync` call
      (`src/Verbara.Sdk.VoiceAi/Pipeline/VoiceAiPipeline.cs:324`) — and only that call — in
      `catch (ObjectDisposedException)`, leave the synthesis enumeration, and account the turn exactly
      as the barge-in clause does: `tts.syntheses.completed` +1, one `SynthesisEndedEvent`, no
      `PipelineErrorEvent`, the activity untouched, and the turn **not** added to the conversation
      history. Do not fall through to the normal-completion tail, which adds the history turn and
      guards `tts.syntheses.silent`. Replace no existing clause.

      **Done.** The write at `VoiceAiPipeline.cs:324` now sits in a `try` of its own *inside* the
      `await foreach`, with `catch (ObjectDisposedException) { farEndGone = true; break; }` and
      nothing else in that try. The `break` is what leaves the enumeration, so `await foreach`'s own
      `DisposeAsync` releases the provider sequence instead of draining an answer nobody can hear —
      which is why `tts.ChunksPulled` stays at 2. After the loop, `if (farEndGone)` accounts the
      ending exactly as the barge-in clause does: `SpeechSynthesisMetrics.SynthesesCompleted.Add(1)`
      and one `SynthesisEndedEvent`, no `PipelineErrorEvent`, `ttsActivity` never touched. The
      pre-existing normal-completion tail — the `tts.syntheses.silent` guard, the completed counter,
      the ended event and `history.Add` — moved unchanged into the `else`, so this ending cannot fall
      through to it: no conversation-history turn, no silent sample. No existing clause was replaced;
      the two cancellation filters, the failure clause and the `finally` are byte-identical, and the
      only other lines that changed are the new `var farEndGone = false;` and the re-indentation the
      `else` forces.

      Measured on the changed tree: 1.1 green; 1.1 + 1.3 + 1.4 together
      `Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3, Duration: 59 ms`; the
      five controls of 1.5 `Passed!  - Failed:     0, Passed:     7, Skipped:     0, Total:     7,
      Duration: 108 ms` — the same 7 cases as before the fix; the whole
      `Verbara.Sdk.VoiceAi.Tests` assembly `Passed!  - Failed:     0, Passed:    90, Skipped:     0,
      Total:    90` against the `Failed: 1, Passed: 89` baseline; `Verbara.Sdk.Governance.Tests`
      129/129, unchanged; `dotnet build Verbara.Sdk.slnx -c Release` **0 Warning(s), 0 Error(s)**.
      `PublicAPI.Unshipped.txt` is untouched in every package, as the proposal expects — the two
      files this task and 2.2 changed are the only `src/` edits.

- [x] 2.2 Add `[LoggerMessage(LogLevel.Debug, …)]` to `src/Verbara.Sdk.VoiceAi/Internal/VoiceAiLog.cs`
      for this ending — neutral wording naming the channel, alongside `BargInDetected` — and call it
      from the new catch. Source-generated, internal, no reflection, no public API change.

      **Done.** Added directly after `BargInDetected`, in the same form every entry in the file
      uses — no explicit `EventId` (the generator allocates it), `internal static partial`, `ILogger`
      first, the channel as the only parameter:

          [LoggerMessage(LogLevel.Debug, "Playback stopped for channel {ChannelId}: the audio session had already ended")]
          internal static partial void PlaybackStoppedSessionEnded(ILogger logger, Guid channelId);

      Source-generated, so no reflection and no public API change. Called once, from the
      `if (farEndGone)` branch, before the two accounting statements.

      That the call is live rather than merely present was checked, not assumed:
      `RecordingLogger.IsEnabled` returns `true` for every level, and raising **only** this entry to
      `Warning` turns the regression test red on its Warning assertion
      (`Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1`). That was a
      throwaway check, restored immediately and the tree re-verified at 90/90 — 4.3 (d) still owes
      the recorded run with its verbatim assertion lines against the final tree.

- [x] 2.3 Confirm by reading, and write the confirmation into this task, that an
      `ObjectDisposedException` out of `AudioSocketSession.WriteAudioAsync` can only mean the session
      has ended: the guard at `AudioSocketSession.cs:118` reads `_disposed`, which only the private
      terminate sets (`:232`), and the transport it disposes last (`:236`) is the exception's only
      other origin. Record that a transport failure on a still-connected session surfaces as
      `IOException`/`SocketException`, is not caught, and stays a synthesis failure — and that the
      narrow race between the guard and the flush is the residual ADR-0057 records.

      **Confirmed, by reading `src/Verbara.Sdk.VoiceAi.AudioSocket/AudioSocketSession.cs` end to end
      rather than the proposal's account of it.**

      *The guard is armed by one writer only.* `WriteAudioAsync` (`:116-123`) opens with
      `ObjectDisposedException.ThrowIf(_disposed == 1, this)` (`:118`). `_disposed` appears in the
      whole file six times and nowhere else: its declaration (`:23`), three reads in the guards of
      `WriteAudioAsync`, `WriteSilenceAsync` and `HangupAsync` (`:118`, `:128`, `:139`), one read in
      `IsConnected` (`:37`), and **one write** — `Interlocked.CompareExchange(ref _disposed, 1, 0)`
      (`:232`), the first statement of the private `TerminateAsync`. Nothing outside the type can
      reach it; the field is private and the type is `sealed`. `TerminateAsync` has exactly three
      callers, all in this file: `HangupAsync` (`:142`), the read loop's `finally` (`:202`) and
      `DisposeAsync` (`:223`) — an ending, every one.

      *The other origin is the same teardown's last statement.* Between the guard and the return,
      `WriteAudioAsync` runs three things. `AudioSocketFrameCodec.WriteFrame(_writer, …)` takes an
      `IBufferWriter<byte>` and calls only `GetSpan`/`Advance` on it (`AudioSocketFrameCodec.cs:47-60`)
      — it fills the writer's own pooled buffer and never touches the transport, so it is not an
      origin. The two `AudioSocketMetrics.*.Add` calls (`:121-122`) cannot throw this. That leaves
      `await _writer.FlushAsync(ct)` (`:120`). `_writer` is `PipeWriter.Create(client.GetStream())`
      (`:64`); nothing ever completes it (`_writer` occurs only at `:64` and the three `FlushAsync`
      calls), and the only use of `_client` after the constructor is `_client.Dispose()` at `:236` —
      the **last** statement of that same `TerminateAsync`. A flush that reaches a disposed
      `NetworkStream` raises `ObjectDisposedException`. So both origins are the one private teardown,
      which is what makes the exception's type a sound discriminator here (ADR-0057 R4).

      *No third implementation can widen it.* The call site is typed to the concrete sealed class —
      `HandleSessionAsync(AudioSocketSession session, …)` (`VoiceAiPipeline.cs:71-73`) — not to an
      interface, so the two origins above are the complete set.

      *A transport failure on a still-connected session stays a synthesis failure.* With `_disposed`
      still 0, the guard passes and the flush writes to a live `NetworkStream`, which reports a broken
      socket as `IOException` (wrapping a `SocketException`), never as `ObjectDisposedException`. The
      new catch at `VoiceAiPipeline.cs:330` is typed, so it does not see it; the exception reaches
      `catch (Exception ex)` at `:414` and is still `tts.syntheses.failed` +1, the activity `Error`, a
      Warning line and a `PipelineErrorEvent` with source `Tts`.

      *The residual is the window between the guard and the flush.* `:118` and `:120` are not atomic.
      A teardown that lands inside that window can be observed by the socket send rather than by the
      guard, and then surfaces as `IOException` instead of `ObjectDisposedException` — the same
      hangup, counted as a synthesis failure. That is exactly what ADR-0057 records under *"Residual,
      recorded rather than closed: the flush race"*: the window is the width of one buffered write, so
      the misclassification is rare rather than impossible, and closing it would mean reading the
      exception's type **and** asking the session whether it is still connected — a conjunct no seam
      available today can tell from its absence, which is why the ADR files it as a residual rather
      than shipping it unguarded.

- [x] 2.4 Sweep `Verbara.Sdk.VoiceAi` for any other write to the audio session that sits inside a
      clause classifying a synthesis or a session. Fix it here only if it is inside `VoiceAiPipeline`;
      record anything elsewhere without changing it, including
      `OpenAiRealtimeBridge.cs:266-271`, which already carries this handling and is the precedent, not
      a defect.

      **Done. Swept, nothing fixed, nothing new owed.** Scope: all 100 non-generated `.cs` files
      across the seven `src/Verbara.Sdk.VoiceAi*` packages, widened to the whole of `src/` for the
      call-site grep so a write from outside the family could not hide.

      Three passes, because a write can be found from either end:
      1. every member `AudioSocketSession` exposes that writes —
         `grep -rn --include='*.cs' -E '\.(WriteAudioAsync|WriteSilenceAsync|HangupAsync)\(' src/`;
      2. every member access through a session variable in the family —
         `grep -rn --include='*.cs' -E '\bsession\.[A-Za-z]' src/Verbara.Sdk.VoiceAi*`, 30 hits,
         each one read;
      3. from the classifier's side rather than the write's —
         `grep -rn -E 'SessionsFailed|SynthesesFailed' src/`, then reading the `try` each clause
         closes, so a write reached indirectly would still be caught.

      Writes found, both of them:

      | site | what it is | classifying clause it sits in | verdict |
      |---|---|---|---|
      | `src/Verbara.Sdk.VoiceAi/Pipeline/VoiceAiPipeline.cs:328` | `session.WriteAudioAsync` — the playback write | the synthesis `try` at `:309`, whose last clause books `tts.syntheses.failed` (`:424`); and outside it the session `try` at `:98`, whose `catch` books `voiceai.sessions.failed` (`:115`) | **this change's fix** — now in its own `try` with `catch (ObjectDisposedException)` at `:330` |
      | `src/Verbara.Sdk.VoiceAi.OpenAiRealtime/OpenAiRealtimeBridge.cs:270` | `session.WriteAudioAsync` — the realtime output loop's write | the bridge's session `try`, whose `catch (Exception ex)` books `RealtimeMetrics.SessionsFailed` (`:128`) | **already handled**: `catch (ObjectDisposedException) { return; }` at `:271`, under the ADR-0053 comment at `:266-269`. Exactly the lines the task predicted. The precedent, not a defect — **not changed** |

      Paths checked that turned out clean, named so the sweep is re-runnable:

      | path | what it holds | why clean |
      |---|---|---|
      | `src/Verbara.Sdk.VoiceAi.AudioSocket/AudioSocketSession.cs` | `WriteAudioAsync`/`WriteSilenceAsync`/`HangupAsync` themselves | the definitions and their guards, not a caller; no classifying clause in the file |
      | `src/Verbara.Sdk.VoiceAi.AudioSocket/AudioSocketServer.cs` | `session.DisposeAsync` (`:93`, `:220`), `session.OnHangup` (`:208`), `session.StartReadLoop` (`:225`) | lifecycle only — no write, and the server books no synthesis or session failure |
      | `src/Verbara.Sdk.VoiceAi/Pipeline/VoiceAiPipeline.cs`, elsewhere | `session.ChannelId` (`:82`, `:84`, `:126`, `:165`, `:210`), `session.ReadAudioAsync` (`:143`) | reads and a property; the read side is settled by ADR-0053 and is a different guard (`_consumerDisposed`) |
      | `src/Verbara.Sdk.VoiceAi/Pipeline/VoiceAiSessionBroker.cs` | `session.ChannelId` (`:41`) | property read only |
      | `src/Verbara.Sdk.VoiceAi.OpenAiRealtime/OpenAiRealtimeBridge.cs`, elsewhere | `session.ChannelId` (`:76`, `:204`), `session.ReadAudioAsync` (`:163`) | reads only. The package's other `session.*` hits — `session.update`, `session.created`, `session.updated` in `RealtimeProtocol.cs`, `RealtimeMessages.cs`, `RealtimeJsonContext.cs`, `RealtimeLog.cs`, `IRealtimeFunctionHandler.cs`, `OpenAiRealtimeOptions.cs` — are OpenAI wire-protocol strings, not the audio session |
      | `src/Verbara.Sdk.VoiceAi.Stt`, `.Tts`, `.TurnDetection`, `.Testing` | — | zero `session.` member accesses of any kind: these packages never hold an audio session |

      Recorded, out of scope, unchanged: `Verbara.Sdk.Ari` has a **different** `AudioSocketSession`
      whose write is `WriteFrameAsync` (`src/Verbara.Sdk.Ari/Audio/AudioSocketSession.cs:153`, and
      `WebSocketAudioSession.cs:201`, declared on `src/Verbara.Sdk/IAriClient.cs:686`). Across all of
      `src/` it has **zero call sites**, so no clause anywhere classifies a failure around it, and
      nothing in the VoiceAi family touches it. Outside `src/` the only callers are eight lines in the
      `Verbara.Sdk.VoiceAi.AudioSocket.Tests` assembly exercising the session itself; `Examples/` has
      none.

      **Finding: none.** There is no second write sitting inside a classifying clause, so nothing here
      becomes a new open change.

## 3. Decision record

- [x] 3.1 Land `docs/decisions/0057-a-write-to-a-departed-session-is-an-ending-not-a-failure.md`
      (**Sdk/ADR-0057**, Accepted). It records: the write-side guard fires for every ending while the
      read-side guard fires only for the owner's disposal, so ADR-0053 R2's reading of
      `ObjectDisposedException` does not carry across unchanged; a write that finds the session gone
      is ADR-0053 R3's ending seen from the other side; the ending is accounted as a barge-in is,
      under ADR-0050 E9's standing debt; the catch is scoped to the write so a provider's own
      `ObjectDisposedException` is untouched; and the flush-race residual, written down rather than
      left as a gap. Related: ADR-0053, ADR-0054, ADR-0050.

      **Done.** Written as **Sdk/ADR-0057**, Status Accepted, Date 2026-09-21, Deciders Harol A.
      Reina H., Related ADR-0053 / ADR-0054 / ADR-0050 (E9), each with the one-line reason it is
      related. 1,818 words — between ADR-0051 (2,521) and ADR-0053 (1,522), and the same five-heading
      shape as ADR-0058, which is the closest in form. No absolute path and no line number anywhere in
      it: the two `AudioSocketSession` fields are named (`_disposed`, `_consumerDisposed`) instead,
      since line pointers in a doc go stale and the fields do not.

      Headline rule: **a write that finds the audio session already gone is that session's ending
      arriving on the write path — ADR-0053 R3 seen from the other side — and not a fault of the
      synthesizer.** Under it, six numbered rules: R1 ADR-0053 R2 does not carry across the two doors,
      because the read guard reads the owner's intent (`_consumerDisposed`) and the write guard reads
      the flag every ending sets (`_disposed`) — one exception type, two doors, two meanings; R2 the
      ending is accounted exactly as a barge-in is, inheriting ADR-0050 E9's standing debt rather than
      settling it; R3 the catch is scoped to the write call alone; R4 the exception's type is the
      discriminator only because both of its origins on this sealed type are the same teardown, so a
      transport failure on a still-connected session stays a synthesis failure; R5 playback stops
      rather than draining; R6 the ending is visible at Debug and nowhere an operator pages.

      **Numbering.** 0056 is left as a gap, reserved by `ari-failed-connect-and-silent-catches`, which
      has not landed. Deliberate and free: the count guard counts files, not a contiguous sequence, and
      `docs/decisions/` already has holes at 0046 and 0047. Nothing was renumbered.

      Both measured consequences of this apply are recorded as consequences of where the ADR draws its
      line, not as defects:

      - **Where the rule stops is load-bearing, and the tests say so.** Mutation (c) of 4.3 — widening
        the write's catch to `catch (Exception)` — is caught by exactly two assertions, and both are
        the ones this suite labels *"today's accounting, not a requirement"* (ADR-0050 E9 debt). Every
        requirement-level assertion is satisfied by the widened catch, so the boundary between "the far
        end left" and "something else went wrong at the write" is currently held by accounting the
        requirement does not mandate. The ADR says so, and says whoever settles E9's debt inherits the
        job of re-anchoring those two lines.
      - **The catch is scoped to the write call, not to the synthesis.** Mutation (b) — moving it to
        the whole synthesis `try` — leaves the hangup regression test green and fails the control that
        keeps a synthesizer's own `ObjectDisposedException` a synthesis failure, on all seven of its
        classification assertions. Scope is not observable from the hangup side alone, which is why R3
        needs its own control.

      Two further findings from 4.3 are carried into the Consequences rather than left in this file:
      mutation (h) — the accounting branch is pinned by reading, because the two tails differ only in a
      conversation-history turn nothing can observe after a hangup — and the build-time contrast with
      ADR-0058: no analyzer refuses any of these mutations (`catch (Exception)` does not trip CA1031 at
      this `AnalysisLevel`, an uncalled internal `[LoggerMessage]` partial does not trip CA1811/IDE0051),
      so the tests are the whole guard. The flush race is the recorded residual, and mutation (g)'s
      history — that "visible below Warning" needs a positive assertion, not the absence of a negative
      one — is a consequence of R6.

- [x] 3.2 Add the ADR-0057 row to `docs/decisions/README.md` in numeric order, matching the format of
      the existing rows.

      **Done.** Inserted between the ADR-0055 and ADR-0058 rows — numeric order, the 0056 gap left
      alone — in the catalog's established shape (`- [ADR-NNNN](file.md) — summary. (Accepted, date)`):

          - [ADR-0057](0057-a-write-to-a-departed-session-is-an-ending-not-a-failure.md) — A write that
            finds the audio session already gone is that session's ending arriving on the write path —
            ADR-0053 R3 seen from the other side — and not a fault of the synthesizer: the write-side
            guard reads the flag *every* ending sets, so ADR-0053 R2's reading of
            `ObjectDisposedException` does not carry across the two doors. The ending is accounted
            exactly as a barge-in is, under ADR-0050 E9's standing debt; the catch is scoped to the
            single write call, so a synthesizer's own `ObjectDisposedException` stays a synthesis
            failure; playback stops rather than draining an answer nobody can hear; and the ending is
            visible at Debug and nowhere an operator pages. The flush race that can still surface as
            `IOException` is written down as the residual rather than left as a gap. (Accepted, 2026-09-21)

      (one line in the file; wrapped here only for this record.)

      That the row is load-bearing was measured, not assumed. `TheDecisionCatalog_ShouldListEveryAdrOnDisk`
      — the third case, which landed yesterday and compares the ADR ids on disk against the catalog's
      link *targets* by set equality — was run with the row deleted and nothing else changed:
      `Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1`. The file was restored
      from a byte copy and `md5sum -c` confirms it (`docs/decisions/README.md: OK`).

- [x] 3.3 Land the ADR-count guard's other two edits **in this same PR**:
      `StatusBlockCoherenceTests.ThePublishedAdrCount_ShouldMatchTheDecisionsOnDisk` (#279) counts
      `docs/decisions/*.md` against the figure `README.md` publishes, and ADR-0042 D1 requires a
      changed figure's registry row to move with it.
      - bump the `**N ADRs**` figure in `README.md`;
      - update its row in `docs/claim-registry.md`.
      Key both edits to the **figure**, never to a line number: #280 has just moved this claim from
      `README.md:74` to `:67` and re-based the registry's line pointers with it, so a number recorded
      here goes stale on the next docs PR. Adding ADR-0057's file without these two fails the `Unit Tests` job.

      **Done, both keyed to the figure.** Counted rather than trusted first: the ADR files in
      `docs/decisions/`, with the catalog `README.md` excluded by name — which is exactly what
      `ThePublishedAdrCount_ShouldMatchTheDecisionsOnDisk` counts — read **54** before the new file and
      **55** after.

      - `README.md` Status block: `**54 ADRs**` → `**55 ADRs**`, matched on the figure. It sits at
        `:67` today, and the line was not used to find it.
      - `docs/claim-registry.md`, the ADR-0042 D1 row, matched on `| **54 ADRs** |`:

            before: | 67 | **54 ADRs** | ENFORCING | `StatusBlockCoherenceTests` — counts `docs/decisions/*.md`, excluding the catalog `README.md` | **OK** |
            after:  | 67 | **55 ADRs** | ENFORCING | `StatusBlockCoherenceTests` — counts `docs/decisions/*.md`, excluding the catalog `README.md` | **OK** |

        The row's `67` is a pointer into `README.md`, not into the registry, and adding an ADR moves no
        line in `README.md`, so it is still correct and was left alone. Class, guard and verdict are
        unchanged — the claim was already ENFORCING and **OK**; only the number moved.

      Proof, on the tree with all three edits in place — **three** cases, not two, since
      `TheDecisionCatalog_ShouldListEveryAdrOnDisk` landed yesterday:

          $ dotnet test Tests/Verbara.Sdk.OpenTelemetry.Tests/ --filter "FullyQualifiedName~StatusBlockCoherenceTests"
          Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3, Duration: 10 ms - Verbara.Sdk.OpenTelemetry.Tests.dll (net10.0)

- [x] 3.4 Repoint this change's `decision_ref` to `Sdk/ADR-0057` once that file exists, so the
      proposal cites the decision it rests on rather than the closest neighbour

      **Done.** `docs/decisions/0057-a-write-to-a-departed-session-is-an-ending-not-a-failure.md`
      exists (3.1), so the frontmatter now cites it. One line changed, in the frontmatter only; the
      body of `proposal.md` is untouched:

          --- a/openspec/changes/hangup-mid-playback-is-not-a-synthesis-failure/proposal.md
          +++ b/openspec/changes/hangup-mid-playback-is-not-a-synthesis-failure/proposal.md
          @@ -3,7 +3,7 @@ tier: PEQUEÑO
           owner: Harol
           approver: Harol
           stakeholder: Operators who page on VoiceAi synthesis telemetry, and applications that subscribe to VoiceAiPipeline.Events to learn that a turn went wrong
          -decision_ref: Sdk/ADR-0053
          +decision_ref: Sdk/ADR-0057
           ---

      `openspec validate --all --strict` was re-run after the edit and is green (4.5).
## 4. Verification

- [x] 4.1 `dotnet build Verbara.Sdk.slnx -c Release`: 0 warnings, 0 errors.

      **Done, twice.** Incrementally and again with `--no-incremental`, because an up-to-date project
      is skipped and never re-emits the warnings it emitted before — a green incremental build is not
      by itself proof of a warning-free tree.

          $ dotnet build Verbara.Sdk.slnx -c Release
          Build succeeded.
              0 Warning(s)
              0 Error(s)

          $ dotnet build Verbara.Sdk.slnx -c Release --no-incremental
          Build succeeded.
              0 Warning(s)
              0 Error(s)

      The clean run compiled 91 project outputs and its log contains no `: warning ` or `: error `
      line at all.

- [x] 4.2 Unit lane green under the CI filter, with coverage, `Verbara.Sdk.Governance.Tests` included.
      Record the assembly and test counts, the coverage line and branch figures against their floors,
      and `tools/audit-test-asserts.sh` violations.

      **Done. The step list below was derived by reading `.github/workflows/ci.yml` on this branch**,
      job by job, not from memory and not from "the tests of the projects I touched" — this change
      moves files in `src/`, a test project, `docs/` and `openspec/`, and the tree-scanning guards are
      tripped by a change anywhere. Every deterministic, non-service step of that file ran here; the
      two that need a service are named and skipped.

      | ci.yml job → step | result |
      |---|---|
      | `gate` → `scripts/ci/classify-docs-only.sh` | **n/a on this tree** — the classifier diffs two commits, and the whole change is still in the working tree (HEAD `3c352e70` *is* `origin/main`). Probed live on `HEAD~1..HEAD` to show it runs: `docs_only=true`, exit 0. On the real PR the diff includes `src/**` and `Tests/**`, so `docs_only=false` and every heavy job runs. Its 37-case harness is green below |
      | `unit-tests` → `dotnet build Verbara.Sdk.slnx -c Release` | 0 Warning(s), 0 Error(s) — see 4.1 |
      | `unit-tests` → `dotnet test Verbara.Sdk.slnx --no-build -c Release --filter "Category!=Functional&Category!=Integration&Category!=Realtime&Category!=Spike" --collect:"XPlat Code Coverage" --settings coverlet.runsettings` | exit 0. **30 test assemblies, 3580 tests: Passed 3580, Failed 0, Skipped 0**; 30 × `Test Run Successful.`, zero `Test Run Failed` |
      | ↳ of which `Verbara.Sdk.Governance.Tests` | `Failed: 0, Passed: 129, Total: 129, Duration: 1 s` — the sync-fence ratchet included, and no baseline file is modified (`git status` lists only this change's own seven files) |
      | ↳ of which `Verbara.Sdk.VoiceAi.Tests` | `Failed: 0, Passed: 90, Total: 90, Duration: 453 ms` — against the `Failed: 1, Passed: 89` pre-fix baseline of 1.5 |
      | ↳ of which `StatusBlockCoherenceTests` (ADR-count guard, 3.3) | `Failed: 0, Passed: 3, Total: 3, Duration: 8 ms` |
      | `coverage` → `reportgenerator` merge | exit 0, Cobertura + TextSummary written |
      | `coverage` → `python3 scripts/check-coverage-floor.py` | exit 0 — **Line 83.6 % (band [83.0, 86.0]) · Branch 68.16 % (floor 64.0 %, blocking) · Lines measured 13354 (min 12315)** → `Coverage band OK.` Line sits 0.6 pt above the floor and 2.4 pt below the stale-floor ceiling, so no ratchet bump is owed |
      | `coverage` → `python3 scripts/check-patch-coverage.py` | exit 0, but **vacuous here and the one step that cannot be exercised locally**: it measures `git diff <merge-base origin/main HEAD>...HEAD`, and this branch has no commits yet, so it reports `no measurable cobertura lines in this diff (no instrumented line added). floor 85.0% — n/a, pass.` It will measure the added `VoiceAiPipeline.cs` / `VoiceAiLog.cs` lines for real once the change is committed and pushed (4.6) |
      | `coverage` → `python3 scripts/check-exclusion-baseline.py` | exit 0 — `markers: 0 (baseline 0, 865 files scanned under 'src/**/*.cs')` |
      | `coverage-scripts` → `python3 -m unittest discover scripts/tests` | exit 0 — `Ran 257 tests … OK` |
      | `coverage-scripts` → `scripts/tests/test_classify_docs_only.sh` | exit 0 — `passed=37 failed=0` |
      | `coverage-scripts` → `scripts/tests/test_release_hygiene.sh` | exit 0 — `passed=39 failed=0` |
      | `coverage-scripts` → `scripts/tests/test_release_provenance.sh` | exit 0 — `passed=131 failed=0` |
      | `coverage-scripts` → `scripts/tests/test_report_perf_breach.sh` | exit 0 — `27 passed, 0 failed` |
      | `coverage-scripts` → `scripts/tests/test_package_validation_coverage.sh` | exit 0 — `passed=106 failed=0` (its real-MSBuild cases self-skip here and run in the pack job, as CI arranges) |
      | `coverage-scripts` → `scripts/tests/test_filter_codeql_sarif.sh` | exit 0 — `passed=448 failed=0` |
      | `aot-check` → `bash tools/verify-aot.sh` | exit 0 — `AOT verification passed for RID=linux-x64 — 0 trim warnings`, and the canary smoke-ran: `AOT Canary — all SDK types are trim-safe` |
      | `pack-check` → `dotnet pack Verbara.Sdk.slnx -c Release --no-build -p:TreatWarningsAsErrors=true` | exit 0 — **29 `.nupkg`**, and the log holds zero `: warning ` / `: error ` lines |
      | `pack-check` → `PKV_MSBUILD_CASES=1 scripts/tests/test_package_validation_coverage.sh` | exit 0 — `passed=120 failed=0` (the 14 real-MSBuild cases now included) |
      | `pack-check` → `scripts/ci/check-package-validation-coverage.sh` | exit 0 — `all 29 shipped project(s) are validated against 2.5.3` |
      | `audit-test-asserts` → `bash tools/audit-test-asserts.sh` | exit 0 — 444 files scanned, ~2915 `[Fact]`/`[Theory]`, **Violations: 0** |
      | `audit-test-asserts` → `python3 scripts/check-recording-redaction.py .` | exit 0 — 72 files across 2 `Recordings/` trees, OK |
      | `functional-tests` → pre-pull + `dotnet test --filter "Category=Functional|Category=Integration|Category=Realtime"` | **SKIPPED — needs a Docker daemon** (Testcontainers builds and runs Asterisk 22/23, postgres, toxiproxy, sipp). CI itself skips these two steps on a `pull_request` without the `ci:functional` label; the merge queue runs the full matrix |
      | `openspec` → `openspec validate --all --strict` | exit 0 — 12 passed, 0 failed; see 4.5 |
      | *(not in `ci.yml`)* `Analyze (C#)` in `.github/workflows/codeql.yml` | **SKIPPED — needs the CodeQL toolchain and a SARIF upload**, i.e. a network service |

      Every exit code above was captured directly from the command (`rc=$?` on the command itself),
      never downstream of a pipe — in zsh `${PIPESTATUS[0]}` is empty and `cmd | tail` reports
      `tail`'s status, so a guard written that way reports green over a red step.

- [x] 4.3 Mutations, each applied alone to the final tree, built, run against
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

      **Done. All five are caught — and two mutations beyond the five survive the whole suite; see
      "What survives" below, which is the finding this task is worth more for than for the five.**

      Method. Baseline re-measured on the final tree first: `dotnet build Verbara.Sdk.slnx -c Release`
      **0 Warning(s), 0 Error(s)**, and `Tests/Verbara.Sdk.VoiceAi.Tests` under the CI unit filter
      (`Category!=Functional&Category!=Integration&Category!=Realtime&Category!=Spike`)
      `Passed!  - Failed:     0, Passed:    90, Skipped:     0, Total:    90`. Each mutation was then
      applied **alone**, from a byte-exact copy of the final tree, built in Release, run under that
      same filter — the whole assembly, a superset of `…Tests.Pipeline`, so a mutation that broke a
      neighbouring test would show — and reverted before the next was applied. The three touched
      files were md5-compared against the copy at the end, and the tree re-verified at
      **0/0 warnings/errors, VoiceAi 90/90, `Verbara.Sdk.Governance.Tests` 129/129**. Nothing but
      this task's record moved. Assertion lines are verbatim; only the multi-line object dumps
      FluentAssertions prints after "but found" are elided, marked `{ … }`, and the absolute repo
      prefix is replaced by `<repo>`.

      | # | mutation | caught by | run |
      |---|---|---|---|
      | a | the write catch removed | `…_WhenTheFarEndHangsUpMidPlayback` (6 assertions) | `Failed: 1, Passed: 89, Total: 90` |
      | b | the catch moved to the whole synthesis `try` | `…_WhenTheSynthesizerIsUsedAfterItsOwnDisposal` (7 assertions) | `Failed: 1, Passed: 89, Total: 90` |
      | c | the write catch widened to `catch (Exception)` | `…_WhenTheCallerCancelsTheWrite` (2 assertions) | `Failed: 1, Passed: 89, Total: 90` |
      | d | the new log call raised from Debug to Warning | `…_WhenTheFarEndHangsUpMidPlayback` (1 assertion) | `Failed: 1, Passed: 89, Total: 90` |
      | e | `VoiceAiPipeline.cs` from `origin/main` | `…_WhenTheFarEndHangsUpMidPlayback` (6 assertions) | `Failed: 1, Passed: 89, Total: 90` |
      | f | `break` → `continue` in the write catch (**extra, not named by 4.3**) | `…_WhenTheFarEndHangsUpMidPlayback` (1 assertion) | `Failed: 1, Passed: 89, Total: 90` |
      | g | the `VoiceAiLog.PlaybackStoppedSessionEnded` call deleted (**extra**) | **nothing — survives** | `Passed: 90, Total: 90` |
      | h | the ending routed into the normal-completion tail (**extra**) | **nothing — survives** | `Passed: 90, Total: 90` |

      None of the eight was refused at build time: every one compiled **0 Warning(s), 0 Error(s)** in
      Release, so no analyzer and no `TreatWarningsAsErrors` promotion stands behind any of them —
      the tests are the whole guard. Two are worth naming for that reason: `catch (Exception)` at the
      write does **not** trip CA1031 at this `AnalysisLevel`, and an uncalled internal
      `[LoggerMessage]` partial does **not** trip CA1811/IDE0051 (mutations (e), (g) and (h) all left
      `PlaybackStoppedSessionEnded` with no call site and still built clean).

      **(a) the write catch removed** — the `try`/`catch (ObjectDisposedException)` deleted, the write
      left bare inside the `await foreach`; `farEndGone` and the `if`/`else` left in place, so the
      diff is the catch and nothing else. Caught by
      `HandleSessionAsync_ShouldNotReportASynthesisFailure_WhenTheFarEndHangsUpMidPlayback`, on the
      same six assertions 1.1 recorded pre-fix:

          Expected ttsMetrics.Get("tts.syntheses.failed") to be 0L because nothing failed — the far end left while the assistant was still speaking, but found 1L (difference of 1).
          Expected capture.Events.OfType<PipelineErrorEvent>() to be empty because a departed session is not a provider letting the pipeline down, but found at least one item { … }
          Expected logger.Entries { … } to not have any items matching (Convert(e.Level, Int32) >= 3) because the way every call normally ends must not reach the stream an operator pages on, but found { … }
          Expected activities.Statuses not to be ActivityStatusCode.Error {value: 2}, but it is.
          Expected ttsMetrics.Get("tts.syntheses.completed") to be 1L, but found 0L (difference of -1).
          Expected capture.Events.OfType<SynthesisEndedEvent>() to contain a single item, but the collection is empty.

      The elided `PipelineErrorEvent` carries the session's own exception, with the stack naming the
      site: `AudioSocketSession.WriteAudioAsync … AudioSocketSession.cs:line 118`, reached from
      `VoiceAiPipeline.PipelineLoop … VoiceAiPipeline.cs:line 326`. `tts.ChunksPulled` stayed 2 and
      did not fire — as 1.2 said it would not, because the uncaught exception unwinds the loop.

      **(b) the catch moved from the write to the whole synthesis `try`** — built from
      `origin/main`'s file with a `catch (ObjectDisposedException)` clause added to the outer `try`,
      after the two cancellation filters and before `catch (Exception ex)`, doing the same accounting
      the fix does (`PlaybackStoppedSessionEnded`, `SynthesesCompleted.Add(1)`, one
      `SynthesisEndedEvent`). The regression test **passes** under this mutation; what refuses it is
      the control of 1.3, `HandleSessionAsync_ShouldPublishTtsPipelineError_WhenTheSynthesizerIsUsedAfterItsOwnDisposal`,
      on all seven of its classification assertions:

          Expected ttsMetrics.Get("tts.syntheses.failed") to be 1L, but found 0L (difference of -1).
          Expected capture.Events.OfType<PipelineErrorEvent>() to contain a single item matching (Convert(e.Source, Int32) == 1) AndAlso ReferenceEquals(e.Exception, System.ObjectDisposedException: The provider client was disposed before this synthesis ended. { … }), but the collection is empty.
          Expected capture.Events.OfType<PipelineErrorEvent>() to contain a single item matching (Convert(e.Source, Int32) == 1) AndAlso ReferenceEquals(e.Exception, System.ObjectDisposedException: The provider client was disposed before this synthesis ended. { … }), but no such item was found.
          Expected capture.Events.OfType<SynthesisEndedEvent>() to be empty because a synthesis that failed did not end, but found at least one item { … }
          Expected ttsMetrics.Get("tts.syntheses.completed") to be 0L, but found 1L (difference of 1).
          Expected logger.Entries to contain a single item matching (Convert(e.Level, Int32) == 3) AndAlso e.Message.Contains("[Tts]", Ordinal), but no such item was found.
          Expected activities.Statuses to be equal to {ActivityStatusCode.Error {value: 2}}, but {ActivityStatusCode.Unset {value: 0}} differs at index 0.

      That the regression test is green here and the control red is the whole reason the pair exists:
      scope is not observable from the hangup side alone.

      **(c) the write catch widened to `catch (Exception)`** — the type on the existing clause
      changed, nothing else. The regression test and 1.3's control both stay green; the refusal comes
      from 1.4's control, `HandleSessionAsync_ShouldNotReportASynthesisFailure_WhenTheCallerCancelsTheWrite`,
      on exactly two assertions and no others:

          Expected ttsMetrics.Get("tts.syntheses.completed") to be 0L, but found 1L (difference of 1).
          Expected capture.Events.OfType<SynthesisEndedEvent>() to be empty, but found at least one item { … }

      **This confirms 1.4's finding and sharpens it.** The flush does observe the caller's token
      deterministically, so (c) is a real mutation and not one pinned by reading — but the single
      thread holding it is the pair of assertions that test marks *"today's accounting, not a
      requirement"*. An `AssertionScope` reports every failure it collected, and only those two are
      in the report: `fault`, `PipelineErrorEvent`, `tts.syntheses.failed`, `voiceai.sessions.*`,
      the Warning scan and the activity status are all **satisfied** by the widened catch. Deleting
      those two lines as "not required" would let (c) through. They are load-bearing and should be
      read as such, whatever the comment above them says.

      **(d) the new log call raised from Debug to Warning** — `LogLevel.Debug` → `LogLevel.Warning`
      on the `[LoggerMessage]` in `VoiceAiLog.cs`, the pipeline untouched. Caught by the regression
      test on its Warning assertion alone:

          Expected logger.Entries
          {
              … Level = LogLevel.Information …, Message = "VoiceAi pipeline started for channel a440243c-…"
              … Level = LogLevel.Warning …,     Message = "Playback stopped for channel a440243c-…: the audio session had already ended"
              … Level = LogLevel.Information …, Message = "VoiceAi pipeline stopped for channel a440243c-…"
          } to not have any items matching (Convert(e.Level, Int32) >= 3) because the way every call normally ends must not reach the stream an operator pages on, but found
          {
              … Level = LogLevel.Warning …,     Message = "Playback stopped for channel a440243c-…: the audio session had already ended"
          }.

      Run properly against the final tree, not cited from 2.2's throwaway check. It proves the call
      is live and that its **level** is pinned. It does not prove the call must exist — see (g).

      **(e) `VoiceAiPipeline.cs` from `origin/main` with the final tests** — `git checkout
      origin/main -- src/Verbara.Sdk.VoiceAi/Pipeline/VoiceAiPipeline.cs`, with `VoiceAiLog.cs`'s new
      entry left in place as 4.3 specifies. It builds clean even though the entry is then uncalled.
      Caught by the regression test on the same six assertions as (a), reproducing 1.1's pre-fix red
      against the final test file:

          Expected ttsMetrics.Get("tts.syntheses.failed") to be 0L because nothing failed — the far end left while the assistant was still speaking, but found 1L (difference of 1).
          Expected capture.Events.OfType<PipelineErrorEvent>() to be empty because a departed session is not a provider letting the pipeline down, but found at least one item { … }
          Expected logger.Entries { … } to not have any items matching (Convert(e.Level, Int32) >= 3) because the way every call normally ends must not reach the stream an operator pages on, but found
          {
              … Level = LogLevel.Warning …, Message = "VoiceAi pipeline error [Tts] for channel a7652531-…: Cannot access a disposed object.
          Object name: 'Verbara.Sdk.VoiceAi.AudioSocket.AudioSocketSession'."
          }.
          Expected activities.Statuses not to be ActivityStatusCode.Error {value: 2}, but it is.
          Expected ttsMetrics.Get("tts.syntheses.completed") to be 1L, but found 0L (difference of -1).
          Expected capture.Events.OfType<SynthesisEndedEvent>() to contain a single item, but the collection is empty.

      **(f) `break` → `continue`, an extra beyond the five.** 4.3 names no mutation for the spec
      scenario *Playback stops rather than draining the rest of the answer*, so one was run for it.
      Caught by the regression test, on the one assertion 1.2 built the third increment for:

          Expected tts.ChunksPulled to be 2 because the pipeline stops pulling audio once the far end is gone, but found 3.

      The `ChunksPulled` seam is therefore non-vacuous, exactly as 1.2 argued it would be.

      ### What survives — two mutations, reported rather than moved past

      **(g) the log call deleted — SURVIVES.** Removing the single line
      `VoiceAiLog.PlaybackStoppedSessionEnded(_logger, channelId);` from the `if (farEndGone)` branch,
      changing nothing else, builds **0 Warning(s), 0 Error(s)** and leaves the assembly
      `Passed!  - Failed:     0, Passed:    90, Skipped:     0, Total:    90`. A tree-wide grep for
      `PlaybackStoppedSessionEnded` and for the message text finds the declaration in `VoiceAiLog.cs`
      and nothing in any test, in any assembly. The regression test asserts only that **nothing** is
      at Warning or above; no test asserts the Debug entry is **there**.

      That leaves the second half of the delta spec's first scenario unbound: *"**AND** the ending is
      recorded in the same instruments a barge-in is recorded in, **and is visible in the log below
      Warning**"*. The instruments half is pinned (`tts.syntheses.completed` 1 and one
      `SynthesisEndedEvent`); the log half is not. Mutation (d) shows the call is live and its level
      fixed *if it is written*; nothing makes writing it required. Closing it costs one assertion in
      the regression test — `logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Debug &&
      e.Message.Contains("the audio session had already ended", StringComparison.Ordinal))` — and is
      left as a call for whoever owns 1.1/2.2, not made here: this task's brief is byte-identity
      outside `tasks.md`.

      **(h) the ending routed into the normal-completion tail — SURVIVES, as 4.3 predicted.**
      `origin/main`'s file plus a `catch (ObjectDisposedException) { break; }` around the write and
      no `farEndGone` branch at all, so the hangup falls through to the pre-existing tail: builds
      clean, `Passed: 90, Total: 90`. 4.3 said to close this one by reading, and the reading holds
      and is now also measured. The tail differs from the barge-in accounting in three things and
      none is observable here: the `tts.syntheses.silent` guard is `if (!ttfaRecorded)` and
      `ttfaRecorded` is already `true` (the first chunk was written to a live session), so no silent
      sample is taken either way; the Debug log call is absent, which no test sees (that is (g));
      and `history.Add(new ConversationTurn(...))` runs. `history` is a `List<ConversationTurn>`
      created at `VoiceAiPipeline.cs:95`, passed only into `PipelineLoop` (`:102`, `:207`), and read
      at exactly one place — `:262-269`, where the **next** turn trims it into
      `ConversationContext.History`. After the far end is gone there is no next turn, and nothing
      exposes the list. So the extra turn is genuinely unobservable, and (h) surviving is a property
      of the ending, not a hole in the tests. It is worth writing into ADR-0057's residual alongside
      the flush race: the choice of accounting branch is pinned by (a)/(d)/(f) and by reading, not by
      a test that could tell the two tails apart.

      ### Do the artifacts' claims hold up

      Yes, with one correction of emphasis. proposal.md § Architectural Risk claims four mutations
      each fail a committed test — (a)→the regression test, (b)→the synthesizer's-own-`ObjectDisposedException`
      control, (c)→the cancelled-write control, (d)→the regression test's log assertion. All four
      measured exactly as claimed, each on the named test and no other. The delta spec's Mitigation
      paragraph makes the same four claims and holds too. 1.4's finding — that the flush observes the
      caller's token deterministically, so (c) stays a mutation — holds: (c) was run, not pinned by
      reading. The correction: proposal.md and the spec both say "logging the ending at Warning fails
      the regression test's log assertion", which is true, and neither says anything about **not
      logging it**, which nothing fails. The spec's own AND-clause about the Debug line is the only
      requirement in this change with no test behind it.


      **Mutation (g) no longer survives.** It did when this task first ran — deleting the log call
      left 90/90 — and that is recorded above as the finding it was. The main session closed it by
      adding the missing log assertion to test 1.1 (see the amendment under 1.1) and re-ran the
      mutation: the test now fails. The survival was a real gap in what the tests bound, not a
      mutation that deserved to pass.

      Left open deliberately, and worth a reader's attention: mutation (c) is caught **only** by the
      two assertions this suite labels *"today's accounting, not a requirement"* (ADR-0050 E9 debt).
      Every requirement-level assertion — fault, `PipelineErrorEvent`, `tts.syntheses.failed`, the
      Warning scan, the activity status, `voiceai.sessions.*` — is satisfied by a widened
      `catch (Exception)`. Deleting those two lines as "not required" would let (c) through. That is
      a property of where the requirement stops, not a defect in the test.
- [x] 4.4 The new tests pass 20 runs in a row against a Release build, with the per-run timing.

      **Done. 20/20, no flake.** The three tests this change adds —
      `…_WhenTheFarEndHangsUpMidPlayback` (1.1), `…_WhenTheSynthesizerIsUsedAfterItsOwnDisposal` (1.3)
      and `…_WhenTheCallerCancelsTheWrite` (1.4) — run 20 times in a row against the Release build
      (`dotnet test Tests/Verbara.Sdk.VoiceAi.Tests/ --no-build -c Release --filter
      "FullyQualifiedName~WhenTheFarEndHangsUpMidPlayback|FullyQualifiedName~WhenTheSynthesizerIsUsedAfterItsOwnDisposal|FullyQualifiedName~WhenTheCallerCancelsTheWrite"`).
      Every run: exit 0, `Failed: 0, Passed: 3, Skipped: 0, Total: 3`.

      Per-run xunit duration, in order (ms):
      60, 61, 61, 62, 61, 61, 61, 59, 63, 59, 60, 60, 61, 59, 62, 59, 59, 62, 62, 64 —
      **min 59 ms, max 64 ms, spread 5 ms**. Process wall time 0.72–0.76 s each. A 5 ms spread across
      20 runs is what "ordered by construction, never by a delay" (1.1) looks like from the outside:
      there is no sleep anywhere in the three tests for the timing to drift around.

- [x] 4.5 `openspec validate --all --strict` green.

      **Done**, run after the 3.4 frontmatter edit so it covers the new `decision_ref`, with the
      version `ci.yml` pins:

          $ npx -y @fission-ai/openspec@1.13.1 validate --all --strict --no-interactive
          ✓ change/hangup-mid-playback-is-not-a-synthesis-failure
          Totals: 12 passed, 0 failed (12 items)

      Exit 0. The only diagnostics in the output are `[INFO]` "requirement text is very long" notes on
      six pre-existing specs; this change's own items carry none.

- [x] 4.6 CI green; record the PR number and the commit that landed on `main`. Enqueue it **alone**: the open changes that add a **new** ADR
      file (0046, 0047, 0056, 0057, 0058, 0059) all bump the same `**N ADRs**` figure, and the queue
      squashes. Whether git even sees the collision depends on where the two catalog rows land: rows
      inserted at the same spot conflict textually and the queue ejects the second before it builds;
      rows at different spots — the common case, since every one of those tasks adds its row *in
      numeric order* — merge cleanly and the second then fails `Unit Tests` inside the queue, a
      semantic conflict rather than a textual one. Either way the practice is the same: one
      ADR-adding change in the queue at a time, and re-count the figure after any rebase, because
      `strict:false` does not force one. Order does not otherwise matter — the guard counts files,
      not a contiguous sequence — so this change keeps ADR-0057 whenever it lands.

      **Done.** PR #284, enqueued alone as required — it was the only ADR-adding change in the queue
      at any point. 14 required contexts green on the PR, then the queue built and merged it as
      `b86f8906` (2026-09-21T09:38:08Z). `classify-docs-only.sh` returned `docs_only=false`, so the
      full lane ran rather than being skipped. The ADR figure went 54 → 55 in the same PR, and
      `ADR-0056` was left as a gap for `ari-failed-connect-and-silent-catches`.

## 5. Close-out

- [x] 5.1 `CHANGELOG.md` `[Unreleased]` entry stating the telemetry change in both directions: a turn
      the caller hung up on stops counting in `tts.syntheses.failed` and stops publishing
      `PipelineErrorEvent` and a Warning line; every provider failure, including a synthesizer's own
      cancellation, is unchanged. Leave the `(#N)` citation for close-out.

- [x] 5.2 Reconcile the living spec and run
      `openspec archive hangup-mid-playback-is-not-a-synthesis-failure --yes` once the fix is on
      `main`, as its own `docs(openspec):` PR.
