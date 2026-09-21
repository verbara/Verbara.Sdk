---
tier: PEQUEÑO
owner: Harol
approver: Harol
stakeholder: Operators who page on VoiceAi synthesis telemetry, and applications that subscribe to VoiceAiPipeline.Events to learn that a turn went wrong
decision_ref: Sdk/ADR-0057
---

# Proposal: hangup-mid-playback-is-not-a-synthesis-failure

## Why

`VoiceAiPipeline.PipelineLoop` writes every synthesized chunk to the audio session from inside the
same `try` whose last clause books a synthesis failure:

```csharp
await foreach (var audioChunk in _tts.SynthesizeAsync(response, _options.OutputFormat, linked.Token))
{
    // ttfa recorded on the first chunk
    await session.WriteAudioAsync(audioChunk, linked.Token);   // VoiceAiPipeline.cs:324
}
```

That `try` sorts its ending into three clauses:

| clause | filter | what it does |
|---|---|---|
| caller | `when (ct.IsCancellationRequested)` | rethrows; the session counts completed (ADR-0054 R1) |
| the synthesis's own source | `when (ttsCts.IsCancellationRequested)` | `tts.syntheses.completed` +1, `SynthesisEndedEvent` |
| everything else | `catch (Exception ex)` | `tts.syntheses.failed` +1, activity `Error`, Warning log, `PipelineErrorEvent` with source `Tts` |

The **write** reaches the third clause with nothing having failed.
`AudioSocketSession.WriteAudioAsync` opens with `ObjectDisposedException.ThrowIf(_disposed == 1, this)`
(`AudioSocketSession.cs:118`), and that field is documented `0 = live, 1 = torn down — by any cause`
(`:23`). The read loop's `finally` runs the same private teardown for every ending it has — a hangup
frame, an error frame, a socket EOF, a transport error (`:175-177`, `:197-204`) — and that teardown
sets the flag as its first statement, before it fires `OnHangup` (`:232`). The write-side guard is
therefore not the read side's: `ReadAudioAsync` guards on `_consumerDisposed`, the owner's own
disposal (`:84`), while the write guards on *any* ending.

So the commonest ending a call has — the caller hangs up while the assistant is still speaking —
lands on the next chunk as an `ObjectDisposedException`. It is not an `OperationCanceledException`,
and a far-end hangup cancels neither the caller's token nor `ttsCts`, so neither of the first two
clauses can see it. Measured against the code at `ddbf9af6`, by reading, the pipeline reports that
turn as: `tts.syntheses.failed` 1, `tts.syntheses.completed` 0, one `PipelineErrorEvent` with source
`Tts`, `VoiceAiLog.PipelineError` — which is `[LoggerMessage(LogLevel.Warning, …)]` — and the
`voiceai.tts.synthesis` activity set to `Error`. A dashboard of TTS failures therefore rises with the
hangup rate, not with the failure rate, and the one signal an operator has for "the caller heard
nothing" fires on the way every call normally ends.

The session layer is already right, which is why nothing caught this. The exception never leaves the
synthesis catch chain, so `HandleSessionAsync` counts the session completed and rethrows nothing. The
defect is exactly one layer wide.

**This SDK already decided the answer, at the other door.** `OpenAiRealtimeBridge` wraps its own
`session.WriteAudioAsync` in `catch (ObjectDisposedException) { return; }`, under a comment that calls
the caller hanging up mid-speech "the commonest ending of all" and cites ADR-0053
(`OpenAiRealtimeBridge.cs:266-271`). The living spec carries that ruling as *The far end departing
mid-playback is not a fault* — but its THEN is about playback stopping and the **session** ending
normally, and ADR-0053's Decision scopes the sentence to the bridge ("The far end departing
mid-playback is handled where the write happens"). Synthesis counters were never in its reach.
ADR-0053's Consequences hand the pipeline's defects of this class to a change of their own; ADR-0054
took two of them — the shared cancellation source and the classification of a cancelled session.
This is a third site of the same class, in the same file, and the first one on the write path.

No test reaches it. Every hangup in the pipeline suites lands after the turn is over:
`VoiceAiPipelineTtfaTests.cs:352-356` hangs up only after `WaitForResponseCycle`, and each hangup in
`VoiceAiPipelineCancellationAccountingTests.cs` (`:361`, `:414`, `:552`) lands after the synthesis has
already ended by another cause.

## What Changes

1. Wrap **only** the write in a typed catch: an `ObjectDisposedException` raised by
   `session.WriteAudioAsync` stops playback for that turn and is not a synthesis failure. The catch
   goes around the single call, not around the synthesis, so an `ObjectDisposedException` raised by
   the *synthesizer* — a provider client used after its own disposal — still reaches the failure
   clause it belongs to.
2. Take the exception's type as the discriminator here because, for this sealed session type, both of
   its origins are the teardown: the guard at `AudioSocketSession.cs:118`, which only the private
   terminate can arm, and a disposed transport inside the flush, which that same terminate closes last
   (`:236`). A genuine transport failure on a session that is **still connected** surfaces as
   `IOException`/`SocketException` from the flush, is not caught, and stays a synthesis failure. That
   is the line between "the far end left" and "the transport broke while it was still there".
3. Account the ending **exactly as a barge-in is accounted**: `tts.syntheses.completed` +1, one
   `SynthesisEndedEvent`, no `PipelineErrorEvent`, `tts.syntheses.failed` untouched, the activity not
   set to `Error`, and the turn left out of the conversation history. Both are "someone outside the
   pipeline ended this playback"; they differ only in who, and tying the two together means one future
   instrument that separates "the caller heard it" from "the caller heard part of it" moves both at
   once. That accounting is today's, not a claim the caller heard the answer — ADR-0050 E9 records
   counting a cut-short synthesis as completed as debt, and this change neither fixes nor requires it.
4. Log it at Debug, through a new `[LoggerMessage]` in `VoiceAiLog` alongside `BargInDetected`, so the
   ending is visible to anyone who asks for it and absent from the Warning stream an operator pages on.
   Source-generated, internal, no reflection.
5. Stop pulling audio from the synthesizer once the far end is gone: leaving the enumeration disposes
   the provider sequence rather than draining an answer nobody can hear.
6. Tests, failing first: a hangup delivered through the real AudioSocket client while the synthesizer
   is parked mid-answer, ordered by the session's own `OnHangup` — which the teardown fires strictly
   after it sets the disposed flag — and never by a delay. Controls pin that a synthesizer's own
   `ObjectDisposedException` is still a failure, that a cancelled write still belongs to the caller,
   and that the three existing requested-ending controls do not move.

## Impact

- `src/Verbara.Sdk.VoiceAi/Pipeline/VoiceAiPipeline.cs`: one catch around the write, one branch after
  the loop, one log call. No public API change.
- `src/Verbara.Sdk.VoiceAi/Internal/VoiceAiLog.cs`: one new internal `[LoggerMessage]` at Debug.
- `Tests/Verbara.Sdk.VoiceAi.Tests/Pipeline/VoiceAiPipelineCancellationAccountingTests.cs`: the
  regression test and two controls, in the class that already owns this family and its xUnit
  collection.
- `docs/decisions/`: ADR-0057 and its index row.
- **A telemetry change in the same family as ADR-0053 and ADR-0054.** A turn the caller hung up on
  moves from `tts.syntheses.failed` to `tts.syntheses.completed`, publishes `SynthesisEndedEvent`
  instead of `PipelineErrorEvent`, and stops logging at Warning. A dashboard whose TTS failure rate
  tracked the hangup rate will **drop**, without the failure rate having changed.
- Unchanged: `voiceai.sessions.*` — the exception never reached the session, so those numbers were
  already right; a barge-in, disposing the pipeline, the caller's own cancellation; and every provider
  failure, including a synthesizer's own cancellation, which ADR-0050 E6/E8 and the change archived on
  2026-09-13 route to the failure clause.
- Downstream (Pro, Platform): nothing to recompile. Anything counting `tts.syntheses.*`, or treating
  `PipelineErrorEvent` with source `Tts` as "a provider let us down", sees the reclassification.
- **Residual, recorded not fixed:** a hangup that lands between the guard and the flush can surface as
  `IOException` instead, and still counts as a synthesis failure. Closing it would mean asking the
  session whether it is still connected as well as reading the exception's type, and no test available
  today can tell that conjunct from its absence. It is written down in ADR-0057 rather than left as a
  gap someone re-discovers.

## Architectural Risk

- **Level:** LOW.
- **Affected:** `VoiceAiPipeline`'s synthesis accounting, and downstream consumers of its synthesis
  telemetry and events. The accounting the ending moves into is the existing barge-in accounting,
  already covered by `HandleSessionAsync_ShouldNotReportASynthesisFailure_WhenABargeInCancelsIt`.
- **Mitigation:** the catch is typed and scoped to one call, so the only input it can absorb is an
  ending the session type raises from its own teardown. These mistakes each fail a committed test:
  removing the catch fails the regression test; widening it from the write to the whole synthesis
  fails the synthesizer's-own-`ObjectDisposedException` control; widening it to every exception at the
  write fails the cancelled-write control; logging the ending at Warning fails the regression test's
  log assertion. Routing the ending into the normal-completion tail rather than the barge-in
  accounting is closed by reading, and that is said in the task rather than claimed as a mutation.
