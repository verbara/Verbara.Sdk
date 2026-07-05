---
tier: PEQUEÑO
owner: Harol
approver: Harol
stakeholder: CI reliability (all downstream repos gate on Sdk CI)
decision_ref: verbara-meta/ADR-0004
---

# Proposal: stt-cancellation-test-fence

## Why

`Verbara.Sdk.VoiceAi.Stt.Tests.Deepgram.DeepgramSpeechRecognizerTests.StreamAsync_ShouldAbort_WhenCancelled`
failed on CI 2026-07-04 ("Expected a <System.OperationCanceledException> to be thrown, but no
exception was thrown") and passed on rerun — a flake. The pattern (shared by the Whisper twin,
`WhisperSpeechRecognizerTests.cs:56`): a **pre-cancelled** `CancellationTokenSource`, then the test
expects `StreamAsync(...).ToListAsync(ct)` to throw — but with a synchronous mock HTTP handler the
recognizer can complete the single-frame stream before any await observes the token, so whether the
`OperationCanceledException` fires is a scheduling race. This is exactly the class of wall-clock/
scheduling nondeterminism the deterministic-test-fences program (Platform C1→C3; ecosystem
convergence verbara-meta/ADR-0004, adopt-on-touch) eliminates — this change is Sdk's first
adopt-on-touch.

## What Changes

Make cooperative cancellation deterministic at the seam, not raced in the test:
- The STT streaming recognizers (Deepgram/Whisper/Azure/Google per shared base) observe the token
  at iteration entry (`ct.ThrowIfCancellationRequested()` before the first yield/HTTP call), so a
  pre-cancelled token deterministically throws regardless of mock latency.
- The `StreamAsync_ShouldAbort_WhenCancelled` tests across provider suites assert against that
  deterministic contract (no racing a synchronous mock).

## Capabilities

### New Capabilities

- `test-determinism`: Sdk's determinism-fence capability (named to mirror Platform's living spec),
  starting with the cooperative-cancellation contract of STT streaming.

### Modified Capabilities

(none)

## Impact

`Verbara.Sdk.VoiceAi.Stt` (streaming recognizers' cancellation seam — behavior-preserving for
non-cancelled tokens), `Tests/Verbara.Sdk.VoiceAi.Stt.Tests` (per-provider cancellation tests).

## Architectural Risk

**Level:** LOW. **Affected:** STT streaming call path (early-exit ordering only; a pre-cancelled
token now throws before the first provider call — arguably the already-intended contract).
**Mitigation:** all provider suites (Deepgram/Whisper/Azure/Google) get the same fence; zero
behaviour change for live tokens; `dotnet test` + AOT gates green.
