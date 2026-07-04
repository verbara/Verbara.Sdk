# Tasks — stt-cancellation-test-fence

## 1. Grounding

- [ ] 1.1 Locate the first-await seam per provider recognizer (shared base vs per-provider) and
      reproduce the race locally (repeat-run the cancellation tests under stress)

## 2. Implementation

- [ ] 2.1 Deterministic token observation at iterator entry (all four providers)
- [ ] 2.2 Align the four `StreamAsync_ShouldAbort_WhenCancelled` tests to the deterministic contract

## 3. Verification

- [ ] 3.1 Repeat-run (30x) the STT test suites under load — zero flakes
- [ ] 3.2 `dotnet test` + CI green (incl. AOT trim + pack gates), zero warnings; CHANGELOG entry
