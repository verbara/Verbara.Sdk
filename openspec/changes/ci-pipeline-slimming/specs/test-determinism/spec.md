# test-determinism — Delta

## ADDED Requirements

### Requirement: TTS synthesis observes cancellation deterministically

TTS speech synthesizers SHALL observe a cancelled token deterministically: a token cancelled
before or during `SynthesizeAsync` enumeration SHALL surface `OperationCanceledException` at the
next iteration boundary, independent of provider/mock latency. A pre-cancelled token SHALL throw
before the first provider request is issued. Per-provider cancellation tests MUST NOT race a
wall-clock timer (`CancellationTokenSource(delay)`) against fake-server behaviour.

#### Scenario: Pre-cancelled token throws before any provider call

- **GIVEN** a `CancellationTokenSource` cancelled before `SynthesizeAsync` is enumerated
- **WHEN** the stream is enumerated (e.g. `ToListAsync(ct)`)
- **THEN** `OperationCanceledException` is thrown deterministically and no provider request is issued

#### Scenario: Cancellation tests do not race the fake server

- **GIVEN** the per-provider `SynthesizeAsync_ShouldAbort_WhenCancelled` tests (Deepgram, ElevenLabs, Lmnt)
- **WHEN** the suite runs repeatedly under load or coverage instrumentation
- **THEN** the tests pass deterministically — the assertion targets the iteration-boundary contract, not a timer-vs-connect scheduling race
