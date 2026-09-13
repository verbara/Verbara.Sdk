---
tier: PEQUEÑO
owner: Harol
approver: Harol
stakeholder: Operators who read VoiceAi synthesis telemetry, and applications that subscribe to VoiceAiPipeline.Events to learn that a caller heard nothing
decision_ref: Sdk/ADR-0050
---

# Proposal: tts-own-cancellation-is-a-synthesis-failure

## Why

`VoiceAiPipeline.PipelineLoop` runs each synthesis under a token linked over two sources: the
caller's token, and `ttsCts`, the synthesis's own source, which a barge-in and `DisposeAsync` cancel.
It then sorts an `OperationCanceledException` into three `catch` clauses:

| clause | filter | what it does |
|---|---|---|
| caller | `when (ct.IsCancellationRequested)` | rethrows; `HandleSessionAsync` counts the session completed (ADR-0054 R1) |
| barge-in | **none** | `tts.syntheses.completed` +1, `SynthesisEndedEvent` |
| failure | `catch (Exception ex)` | `tts.syntheses.failed` +1, activity `Error`, Warning log, `PipelineErrorEvent` with source `Tts` |

The middle clause is commented *"Barge-in — _ttsCts was cancelled"*, and nothing checks it. Every
cancellation that is not the caller's lands there, including one the synthesizer raised on its own
while nobody had asked it to stop. For that input the failure clause is unreachable.

The input is ordinary. Measured at `b973e994`, with the caller's token live throughout:

| where the cancellation came from | what the synthesizer threw |
|---|---|
| a real `HttpClient` with a 300 ms `Timeout`, against a `127.0.0.1` listener that never answers | `TaskCanceledException`, inner `TimeoutException` |
| `DeepgramSpeechSynthesizer` with `ConnectTimeoutSeconds = 1`, against the same kind of listener | `TaskCanceledException`, inner `IOException` |

In both cases the caller heard nothing for that turn, and the pipeline reported a successful
synthesis: `SynthesisEndedEvent`, `tts.syntheses.completed` 1, `tts.syntheses.failed` 0, no
`PipelineErrorEvent`, no Warning log, and the `voiceai.tts.synthesis` activity left `Unset`. A genuine
barge-in, measured on the same instruments, reports the same synthesis outcome and adds a
`BargInDetectedEvent` and a Debug log line.

Five of the six synthesizers the package ships can raise it: Deepgram as measured, the rest by
reading. Cartesia, Deepgram and LMNT over WebSocket link a connect deadline (`ConnectTimeoutSeconds`)
over the token they are handed. LMNT over HTTP and Speechmatics set `HttpClient.Timeout`. Azure uses
the `HttpClient` its host supplies, whose `Timeout` defaults to 100 seconds. ElevenLabs connects on the
handed token alone. Any third-party subclass of the public `SpeechSynthesizer` base can raise it too.

This contradicts two accepted decisions. ADR-0050 E6 says cancellation is never a failure because it
is the caller's own, and E8 makes the token the discriminator. A synthesizer's own deadline cancels no
token that the pipeline or its caller holds. ADR-0053 classifies an ending by who ended it: here the
provider ended the synthesis, and the pipeline books it as the caller's barge-in.

## What Changes

1. Filter the middle clause on the synthesis's own source:
   `catch (OperationCanceledException) when (ttsCts.IsCancellationRequested)`. A barge-in and a
   disposal are the only things that cancel that source, so neither becomes a synthesis failure.
   Every other `OperationCanceledException` falls to the existing failure clause, which does not
   change.
2. Filter on the source, not on the exception's token. A synthesizer is handed the linked token, never
   `ttsCts.Token`, and one that links sources of its own raises whichever token it observed, so
   comparing tokens would miss a genuine barge-in. ADR-0053 records the same trap for the Realtime
   bridge's `ConnectAsync`. Nor may the filter trust the exception's token: both measured inputs
   carried a cancelled token of their own, so trusting it would book them as requested endings again.
3. Keep the precedence the caller's clause already has: a requested cancellation that is visible when
   the ending is classified wins over the provider's own, the caller's token first and the synthesis's
   own source second. The classification waits for the synthesizer. `await foreach` awaits the
   enumerator's `DisposeAsync` before the exception reaches the catch filters, because an `await` in a
   `finally` is lowered as a catch, the await and a rethrow. A barge-in, a disposal or the caller's
   cancellation that lands while a provider's own cancellation is still unwinding is therefore visible
   to the filters, and a test orders that race by parking the enumerator in `DisposeAsync`.
4. Keep the accounting of a requested ending as it is. A barge-in or a disposal during a synthesis
   still counts in `tts.syntheses.completed` and publishes `SynthesisEndedEvent`, and the caller's
   cancellation still counts in neither. ADR-0050 E9 records counting a cancelled synthesis as
   completed as adjacent debt. This change does not fix that debt and does not require that
   accounting. For a requested ending, its requirement is only that a synthesis ending with an
   `OperationCanceledException` is not reported as failed; an exception of any other type is outside
   the requirement.
5. Tests, failing first: a `TaskCanceledException` with an inner `TimeoutException`, carrying the
   token of a source the synthesizer owns and has cancelled, and a plain `OperationCanceledException`,
   each raised before any audio and after one chunk, under a caller token that is never cancelled. A
   next-turn test has the caller speak again after that failure and waits for the pipeline to
   recognise, handle and synthesise the next utterance. Controls pin that a barge-in, a disposal and
   the caller's cancellation are not synthesis failures, both during a parked synthesis and while a
   synthesizer's own cancellation is unwinding, because a fix that sent every cancellation to the
   failure clause would pass the first test and book every barge-in as a failure.

## Impact

- `src/Verbara.Sdk.VoiceAi/Pipeline/VoiceAiPipeline.cs`: one filter and its comments. No public API
  change.
- `Tests/Verbara.Sdk.VoiceAi.Tests/Pipeline/VoiceAiPipelineCancellationAccountingTests.cs`: the
  regression theory, the next-turn test, three controls and the precedence theory.
- **A telemetry change in the same family as ADR-0053 and ADR-0054.** A synthesis the provider
  cancelled on its own moves from `tts.syntheses.completed` to `tts.syntheses.failed`. It publishes
  `PipelineErrorEvent` with source `Tts` instead of `SynthesisEndedEvent`, logs `PipelineError` at
  Warning, and sets the `voiceai.tts.synthesis` activity to `Error`. A dashboard that read zero
  synthesis failures through provider timeouts will rise without the failure rate having changed.
- Unchanged: a barge-in, `DisposeAsync` during a synthesis, and the caller's own cancellation,
  including when one of them lands while a provider's own cancellation is unwinding. These endings
  move `voiceai.sessions.*` exactly as before: the caller's cancellation still counts the session in
  `voiceai.sessions.completed` and records `voiceai.session.duration_ms` (ADR-0054 R1). The new
  failure is reported and not rethrown, so it adds nothing to `voiceai.sessions.failed`.
- Downstream (Pro, Platform): nothing to recompile. Anything that counts `tts.syntheses.*`, or treats
  `SynthesisEndedEvent` as "the caller heard the answer", sees the reclassification.

## Architectural Risk

- **Level:** LOW.
- **Affected:** `VoiceAiPipeline`'s synthesis accounting, and downstream consumers of its synthesis
  telemetry and events. The failure path the input moves into already exists and is already covered
  by `HandleSessionAsync_ShouldEmitPipelineErrorEvent_OnTtsError`.
- **Mitigation:** the filter reads a source owned by the same method, which is released only when
  that iteration's block ends, so it is live when the filter runs. These mistakes each fail a
  committed test: removing the filter fails the regression theory; inverting it or dropping the
  clause fails the controls; narrowing it by the exception's shape fails the precedence theory;
  widening it by the exception's token fails the regression theory's `TaskCanceledException` cases;
  widening it to book an own cancellation as completed once audio was yielded fails the after-audio
  cases; and leaving the loop after the failure fails the next-turn test.
