# streaming-session-lifecycle — Delta

## ADDED Requirements

### Requirement: The far end leaving mid-playback is not a synthesis failure
A voice pipeline SHALL NOT report a synthesis as failed when writing its audio finds the audio session
already ended. An ending nobody in the process asked for is a termination of the playback, not a fault
of the synthesizer (ADR-0053 R3), and the pipeline MUST NOT increment `tts.syntheses.failed`, publish a
`PipelineErrorEvent`, log at Warning or above, or set the synthesis activity to `Error` for it. It
SHALL account that turn exactly as it accounts a barge-in, so that one future instrument separating
"the caller heard the answer" from "the caller heard part of it" moves both endings together; what
that accounting counts today is recorded as debt (ADR-0050 E9) and is not required here. A write that
fails while the session is **still connected** is untouched by this requirement and remains a
synthesis failure, as does any failure raised by the synthesizer itself.

This is the synthesis-layer companion to the session-layer scenario already in this capability. That
one says the session ends normally when the far end departs mid-playback; it says nothing about the
synthesis counters, and both are now pinned.

#### Scenario: The caller hangs up while the assistant is speaking
- **GIVEN** a pipeline session writing synthesized audio back to a caller, with no barge-in, no disposal and a caller token that is never cancelled
- **WHEN** the caller hangs up, the session tears its transport down, and the next chunk's write finds the session already ended
- **THEN** `tts.syntheses.failed` does not move, no `PipelineErrorEvent` is published, nothing is logged at Warning or above, and the synthesis activity is not set to `Error`
- **AND** the ending is recorded in the same instruments a barge-in is recorded in, and is visible in the log below Warning

#### Scenario: Playback stops rather than draining the rest of the answer
- **GIVEN** a synthesizer with more audio to yield for the current turn
- **WHEN** a write finds the audio session already ended
- **THEN** the pipeline stops pulling audio from the synthesizer for that turn and releases the provider sequence, rather than synthesizing an answer nobody can hear

#### Scenario: The session ends normally after the abandoned playback
- **GIVEN** a turn whose playback was abandoned because the far end left
- **WHEN** the session handler returns
- **THEN** nothing is rethrown to its caller, and the session is counted in `voiceai.sessions.completed`, not in `voiceai.sessions.failed`

#### Scenario: A failure the synthesizer itself raises is still a synthesis failure
- **GIVEN** a session that is still connected
- **WHEN** the synthesizer ends the turn with a failure of its own, including one raised because a provider client was used after its own disposal
- **THEN** the turn is reported as failed — `tts.syntheses.failed` increments, one `PipelineErrorEvent` with source `Tts` carries that exception, the failure is logged at Warning and the synthesis activity is set to `Error` — because the guard for a departed session covers the write and nothing else

#### Scenario: A requested cancellation of the write still belongs to whoever asked for it
- **GIVEN** a write in flight for the current turn
- **WHEN** the caller's token, a barge-in or disposing the pipeline cancels it
- **THEN** the ending is classified exactly as it is when the synthesizer observes that cancellation, rather than being absorbed as a departed session

## Architectural Risk

- **Level:** LOW.
- **Affected:** the synthesis accounting of `VoiceAiPipeline` (`Verbara.Sdk.VoiceAi`), and downstream
  consumers of `tts.syntheses.*`, `PipelineErrorEvent` and `SynthesisEndedEvent` in Pro and Platform.
  No public API changes, so nothing downstream needs to recompile.
- **Mitigation:** the ending moves into the accounting a barge-in already has, which existing controls
  cover. Regression tests pin both sides: a hangup delivered mid-playback through the real audio
  session, ordered by the session's own end-of-session event rather than by a delay; and controls that
  a failure raised by the synthesizer, a cancelled write, a barge-in, a disposal and the caller's own
  cancellation each keep the classification they had. Removing the guard, widening it from the write
  to the whole synthesis, widening it to every exception at the write, or reporting the ending at
  Warning each fail at least one of them.
