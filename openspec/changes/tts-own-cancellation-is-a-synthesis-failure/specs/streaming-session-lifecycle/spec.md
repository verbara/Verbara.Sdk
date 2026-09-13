# streaming-session-lifecycle — Delta

## ADDED Requirements

### Requirement: A synthesizer's own cancellation is a synthesis failure, not a barge-in
A voice pipeline SHALL report a synthesis as failed when the synthesizer ends it with its own
`OperationCanceledException` while neither the caller's token nor the synthesis's own source is
cancelled. It SHALL NOT report as failed a synthesis that ends with an `OperationCanceledException`
while a requested cancellation (a barge-in, disposing the pipeline, or the caller's token) is visible.
Among `OperationCanceledException`s the ending is classified by which source is cancelled when the
pipeline classifies it, never by the exception's concrete type, message, inner exception or token.
Exceptions of any other type are outside this requirement.

#### Scenario: A synthesizer's own deadline elapses while nobody asked it to stop
- **GIVEN** a pipeline session whose caller token is never cancelled, with no barge-in and no disposal
- **WHEN** the synthesizer raises an `OperationCanceledException` of its own, such as a `TaskCanceledException` whose inner exception is a `TimeoutException` and whose token belongs to a source the synthesizer cancelled, before yielding any audio or after yielding some
- **THEN** the synthesis is reported as failed: the pipeline publishes one `PipelineErrorEvent` with source `Tts` carrying that exception, increments `tts.syntheses.failed`, logs the error at Warning and sets the synthesis activity to `Error`
- **AND** it publishes no `SynthesisEndedEvent` for that turn and does not increment `tts.syntheses.completed`

#### Scenario: The session outlives the failed synthesis
- **GIVEN** a synthesis that failed because the synthesizer cancelled itself
- **WHEN** the session later ends normally
- **THEN** the failure was not rethrown to the caller of the session handler, and the session is counted in `voiceai.sessions.completed`, not in `voiceai.sessions.failed`
- **AND** the pipeline goes on to recognise, handle and synthesise the caller's next utterance

#### Scenario: A barge-in is not a synthesis failure
- **GIVEN** a synthesis in flight
- **WHEN** the caller speaks over it and the synthesis ends with an `OperationCanceledException` because its own source was cancelled
- **THEN** `tts.syntheses.failed` does not move, no `PipelineErrorEvent` is published, nothing is logged at Warning, and the synthesis activity is not set to `Error`

#### Scenario: Disposing the pipeline during a synthesis is not a synthesis failure
- **GIVEN** a synthesis in flight
- **WHEN** the pipeline is disposed and the synthesis ends with an `OperationCanceledException` because its own source was cancelled
- **THEN** `tts.syntheses.failed` does not move, nothing is logged at Warning, the synthesis activity is not set to `Error`, and the session is not counted as failed

#### Scenario: The caller's cancellation during a synthesis is not a synthesis failure
- **GIVEN** a synthesis in flight
- **WHEN** the caller cancels the token it handed the session handler and the synthesis ends with an `OperationCanceledException`
- **THEN** `tts.syntheses.failed` does not move, no `PipelineErrorEvent` is published, nothing is logged at Warning, and the synthesis activity is not set to `Error`
- **AND** the session handler returns without throwing and the session is counted in `voiceai.sessions.completed`

#### Scenario: A requested cancellation outranks the provider's own while it unwinds
- **GIVEN** a synthesizer that has raised its own `OperationCanceledException`, and whose sequence the pipeline is still disposing
- **WHEN** a barge-in, disposing the pipeline, or the caller's token cancels before that disposal completes
- **THEN** the synthesis is not reported as failed: `tts.syntheses.failed` does not move, no `PipelineErrorEvent` is published, nothing is logged at Warning, and the synthesis activity is not set to `Error`
- **AND** the session handler returns without throwing and the session is not counted in `voiceai.sessions.failed`

## Architectural Risk

- **Level:** LOW.
- **Affected:** the synthesis accounting of `VoiceAiPipeline` (`Verbara.Sdk.VoiceAi`), and downstream
  consumers of `tts.syntheses.*` and of `PipelineErrorEvent` and `SynthesisEndedEvent` in Pro and
  Platform. No public API changes, so nothing downstream needs to recompile.
- **Mitigation:** the failure clause the input moves into is the existing one. Regression tests pin
  both sides: the provider's own cancellation, before and after audio, as a failure that the session
  outlives into its next utterance; and a barge-in, a disposal and the caller's cancellation as
  requested endings that report no failure, including while the provider's own cancellation is still
  unwinding. Removing the filter, inverting it, dropping the clause, narrowing it by the exception's
  shape, widening it by the exception's token or by whether audio was yielded, and leaving the loop
  after the failure each fail at least one of them.
