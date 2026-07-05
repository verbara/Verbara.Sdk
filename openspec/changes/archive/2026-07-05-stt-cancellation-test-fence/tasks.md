# Tasks — stt-cancellation-test-fence

## 1. Grounding

- [x] 1.1 Locate the first-await seam per provider recognizer (shared base vs per-provider) and
      reproduce the race locally (repeat-run the cancellation tests under stress)
      — **Correction**: `SpeechRecognizer` is `abstract` only (no shared `StreamAsync` body); each
      of **7** providers (Deepgram, Whisper, AzureWhisper, Google, Speechmatics, AssemblyAI,
      Cartesia — not just the 4 named in the proposal) implements `StreamAsync` independently as
      its own `async IAsyncEnumerable` iterator. "One shared fix" means one uniform idiom applied
      per-provider, not a single shared code seam. Also: only Deepgram + Whisper had a
      `StreamAsync_ShouldAbort_WhenCancelled` test pre-change (Azure/Google/Speechmatics/
      AssemblyAI/Cartesia had none) and Deepgram's existing test raced mid-flight (polling
      `ReceivedFrameCount`), not pre-cancelled as the proposal described. 30 local runs of the
      pre-existing tests did not reproduce the flake (CI-load-dependent), consistent with a
      scheduling-pressure-only race.

## 2. Implementation

- [x] 2.1 Deterministic token observation at iterator entry (all four providers)
      — extended to all 7 providers for a uniform contract (Requirement text is provider-agnostic:
      "STT streaming recognizers SHALL...").
- [x] 2.2 Align the four `StreamAsync_ShouldAbort_WhenCancelled` tests to the deterministic contract
      — only 2 tests existed (Deepgram, Whisper); Deepgram's was rewritten from mid-flight polling
      to pre-cancelled (matching Whisper + the spec scenario). AzureWhisper/Google/Speechmatics/
      AssemblyAI/Cartesia had no such test to align — adding new cancellation tests for those 5
      providers was out of scope for this PEQUEÑO fence (not a pre-existing flake); left as a
      follow-up if desired.

## 3. Verification

- [x] 3.1 Repeat-run (30x) the STT test suites under load — zero flakes
      — 30 sequential runs of the abort-filter + 32 runs under 4-way concurrent load, 0 failures;
      full `Verbara.Sdk.VoiceAi.Stt.Tests` suite (41 tests) green.
- [x] 3.2 `dotnet test` + zero warnings; CHANGELOG entry
      — full solution build 0 warnings; full unit-test filter (`Category!=Functional&Category!=Integration`)
      green across all projects. CI green on PR#77 (merged 2026-07-05): Unit Tests, AOT Trim Check,
      Pack Warnings Gate, Coverage Ratchet, Functional Tests (Testcontainers), CodeQL, OpenSpec Validate
      all SUCCESS.
