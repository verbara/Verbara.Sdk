# test-determinism — Delta

## ADDED Requirements

### Requirement: An in-process AudioSocket harness orders its phases on a pipeline signal, never on a wall-clock delay
A test harness driving a `VoiceAiPipeline` over an in-process AudioSocket pair SHALL sequence every
phase — frames sent, an utterance ended, a synthesis started, a barge-in delivered — on an
observable signal emitted by the pipeline itself, and MUST NOT use a fixed `Task.Delay` as a stand-in
for "the pipeline has finished reacting." Three seams carry that meaning and are the sentinels:
`ITurnDetector.Analyze`, which the pipeline calls synchronously once per frame on the monitor loop's
own thread; a speech synthesizer that parks between chunks; and the pipeline's own event stream,
which announces each stage of a response cycle. A detector signal orders frames and a park orders
the synthesis window, but neither can speak for the recognition, handler and synthesis work that
runs on after an end-of-utterance decision — that is what the event stream is for. A harness waiting
on the end of a response cycle MUST treat the error event as an ending too, or a test written to
exercise a failing stage waits for a success event that will never be published. Each wait MUST be
bounded by a timeout set far above any plausible scheduling delay, so reaching it fails the test's
own assertion rather than pacing it.

#### Scenario: The harness waits for the frame's decision before sending the next
- **GIVEN** a harness that must know a frame has been acted on before it sends another
- **WHEN** it sends one frame and waits on that frame's own detector signal
- **THEN** it proceeds the instant the pipeline has decided on that frame, and no elapsed time is involved in the ordering

#### Scenario: A synthesis in flight is a fact, not a probability
- **GIVEN** a test that must act while the assistant is mid-sentence
- **WHEN** the synthesizer parks after its first chunk and announces it
- **THEN** anything the test does before releasing the park provably happens inside the synthesis window

#### Scenario: A response cycle that fails still ends the wait
- **GIVEN** a harness waiting for the pipeline to finish reacting to an utterance
- **WHEN** the stage under test fails and the pipeline publishes an error instead of a synthesis ending
- **THEN** the wait ends on that error, so a test written to exercise the failure is not the one test the sweep leaves hanging

#### Scenario: Removing the signal fails the test
- **GIVEN** a harness phase newly ordered by a signal instead of a delay
- **WHEN** that signal is removed
- **THEN** the dependent test fails, demonstrating the ordering is real rather than incidental

#### Scenario: A hang bound is kept, not swept
- **GIVEN** a multi-second cancellation token in the same file whose only role is to bound a deadlock
- **WHEN** the file's wall-clock barriers are retired
- **THEN** that token is examined and kept, and the decision to keep it is recorded so a later sweep does not remove the safety net

## Architectural Risk

Low. The requirement names seams that already exist in production code and are already exercised by
a shipped test class; nothing here is new design. The risk it guards against is a sweep that trades
13 visible delays for signals nobody has watched fail — a suite that reads as deterministic while
ordering nothing, which is the state ADR-0045 exists about.
