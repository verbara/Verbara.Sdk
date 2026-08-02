# test-determinism — Delta

## MODIFIED Requirements

### Requirement: STT streaming observes cancellation deterministically
STT streaming recognizers SHALL observe a cancelled token deterministically: a token cancelled
before or during `StreamAsync` enumeration SHALL surface `OperationCanceledException` at the next
iteration boundary, independent of provider/mock latency. A pre-cancelled token SHALL throw before
the first provider request is issued. This contract MUST be asserted by a
`StreamAsync_ShouldAbort_WhenCancelled` test for every STT provider (Deepgram, Whisper,
AzureWhisper, Google, Speechmatics, AssemblyAI, Cartesia), not a subset. The frame generator these
per-provider tests use to keep the stream open until the cancellation seam is observed
(`EndlessFrames`) SHOULD live in a single shared test helper rather than being duplicated per
suite, so the seven-provider assertion rests on one non-duplicated generator.

#### Scenario: Pre-cancelled token throws before any provider call
- **GIVEN** a `CancellationTokenSource` cancelled before `StreamAsync` is enumerated
- **WHEN** the stream is enumerated (e.g. `ToListAsync(ct)`)
- **THEN** `OperationCanceledException` is thrown deterministically and no provider HTTP request is issued

#### Scenario: Cancellation tests do not race the mock
- **GIVEN** the per-provider `StreamAsync_ShouldAbort_WhenCancelled` tests (all 7 providers: Deepgram/Whisper/AzureWhisper/Google/Speechmatics/AssemblyAI/Cartesia)
- **WHEN** the suite runs repeatedly under load or coverage instrumentation
- **THEN** the tests pass deterministically — the assertion targets the iteration-boundary contract, not a scheduling race

#### Scenario: Cancellation-test frame generator is not duplicated per suite
- **GIVEN** the shared `EndlessFrames` frame generator used by the per-provider cancellation tests
- **WHEN** a new STT provider suite adds a `StreamAsync_ShouldAbort_WhenCancelled` test
- **THEN** it references the single shared helper instead of copying the generator, and the `Task.Delay` pacer carries exactly one `fence-allow: LOOP-DRIVER` annotation at the shared site
