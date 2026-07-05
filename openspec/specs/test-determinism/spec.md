# test-determinism Specification

## Purpose
Determinism fences for Sdk's async streaming tests so suites never depend on wall-clock or
scheduling races: cooperative-cancellation contracts observed at a deterministic seam (iteration
entry) instead of raced against a mock's completion timing, so a pre-cancelled token always
throws before any provider call regardless of mock latency or CI scheduling pressure. This is
Sdk's first capability instance of the ecosystem-wide deterministic-test-fences convergence
(verbara-meta ADR-0004, adopt-on-touch), mirroring Platform's `test-determinism` living spec
(C1→C3) at the seam Sdk actually owns: STT streaming recognizers. Coverage of this requirement
across all 7 providers is tracked by the open follow-up change
`stt-provider-cancellation-tests` (5 providers still lack an asserting test as of this writing).

## Requirements
### Requirement: STT streaming observes cancellation deterministically
STT streaming recognizers SHALL observe a cancelled token deterministically: a token cancelled
before or during `StreamAsync` enumeration SHALL surface `OperationCanceledException` at the next
iteration boundary, independent of provider/mock latency. A pre-cancelled token SHALL throw before
the first provider request is issued.

#### Scenario: Pre-cancelled token throws before any provider call
- **GIVEN** a `CancellationTokenSource` cancelled before `StreamAsync` is enumerated
- **WHEN** the stream is enumerated (e.g. `ToListAsync(ct)`)
- **THEN** `OperationCanceledException` is thrown deterministically and no provider HTTP request is issued

#### Scenario: Cancellation tests do not race the mock
- **GIVEN** the per-provider `StreamAsync_ShouldAbort_WhenCancelled` tests (Deepgram/Whisper/Azure/Google)
- **WHEN** the suite runs repeatedly under load or coverage instrumentation
- **THEN** the tests pass deterministically — the assertion targets the iteration-boundary contract, not a scheduling race

