# test-determinism — Delta

## MODIFIED Requirements

### Requirement: STT streaming observes cancellation deterministically
STT streaming recognizers SHALL observe a cancelled token deterministically: a token cancelled
before or during `StreamAsync` enumeration SHALL surface `OperationCanceledException` at the next
iteration boundary, independent of provider/mock latency. A pre-cancelled token SHALL throw before
the first provider request is issued. This contract MUST be asserted by a
`StreamAsync_ShouldAbort_WhenCancelled` test for every STT provider (Deepgram, Whisper,
AzureWhisper, Google, Speechmatics, AssemblyAI, Cartesia), not a subset.

#### Scenario: Pre-cancelled token throws before any provider call
- **GIVEN** a `CancellationTokenSource` cancelled before `StreamAsync` is enumerated
- **WHEN** the stream is enumerated (e.g. `ToListAsync(ct)`)
- **THEN** `OperationCanceledException` is thrown deterministically and no provider HTTP request is issued

#### Scenario: Cancellation tests do not race the mock
- **GIVEN** the per-provider `StreamAsync_ShouldAbort_WhenCancelled` tests (all 7 providers: Deepgram/Whisper/AzureWhisper/Google/Speechmatics/AssemblyAI/Cartesia)
- **WHEN** the suite runs repeatedly under load or coverage instrumentation
- **THEN** the tests pass deterministically — the assertion targets the iteration-boundary contract, not a scheduling race
