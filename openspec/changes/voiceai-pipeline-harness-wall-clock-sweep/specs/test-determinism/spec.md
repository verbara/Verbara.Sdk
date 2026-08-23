# test-determinism — Delta

## ADDED Requirements

### Requirement: An in-process AudioSocket harness orders its phases on a pipeline signal, never on a wall-clock delay
A test harness driving a `VoiceAiPipeline` over an in-process AudioSocket pair SHALL sequence every
phase — frames sent, an utterance ended, a synthesis started, a barge-in delivered — on an
observable signal emitted by the pipeline itself, and MUST NOT use a fixed `Task.Delay` as a stand-in
for "the pipeline has finished reacting." Two seams already carry that meaning and are the sentinels:
`ITurnDetector.Analyze`, which the pipeline calls synchronously once per frame on the monitor loop's
own thread, and a speech synthesizer that parks between chunks. Each wait MUST be bounded by a
timeout set far above any plausible scheduling delay, so reaching it fails the test's own assertion
rather than pacing it.

#### Scenario: The harness waits for the frame's decision before sending the next
- **GIVEN** a harness that must know a frame has been acted on before it sends another
- **WHEN** it sends one frame and waits on that frame's own detector signal
- **THEN** it proceeds the instant the pipeline has decided on that frame, and no elapsed time is involved in the ordering

#### Scenario: A synthesis in flight is a fact, not a probability
- **GIVEN** a test that must act while the assistant is mid-sentence
- **WHEN** the synthesizer parks after its first chunk and announces it
- **THEN** anything the test does before releasing the park provably happens inside the synthesis window

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
