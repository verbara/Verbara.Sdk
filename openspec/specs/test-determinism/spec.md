# test-determinism Specification

## Purpose
Determinism fences for Sdk's async streaming tests so suites never depend on wall-clock or
scheduling races: cooperative-cancellation contracts observed at a deterministic seam (iteration
entry) instead of raced against a mock's completion timing, so a pre-cancelled token always
throws before any provider call regardless of mock latency or CI scheduling pressure. This is
Sdk's first capability instance of the ecosystem-wide deterministic-test-fences convergence
(verbara-meta ADR-0004, adopt-on-touch), mirroring Platform's `test-determinism` living spec
(C1→C3) at the seam Sdk actually owns: STT streaming recognizers. All 7 providers (Deepgram,
Whisper, AzureWhisper, Google, Speechmatics, AssemblyAI, Cartesia) now assert this contract via a
`StreamAsync_ShouldAbort_WhenCancelled` test — the coverage gap closed by
`stt-provider-cancellation-tests` (archived 2026-07-18).

## Requirements
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

