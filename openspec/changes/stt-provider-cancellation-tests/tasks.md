# Tasks — stt-provider-cancellation-tests

## 1. Implementation

- [ ] 1.1 Add `StreamAsync_ShouldAbort_WhenCancelled` to `AzureWhisperSpeechRecognizerTests`
- [ ] 1.2 Add `StreamAsync_ShouldAbort_WhenCancelled` to `GoogleSpeechRecognizerTests`
- [ ] 1.3 Add `StreamAsync_ShouldAbort_WhenCancelled` to `SpeechmaticsSpeechRecognizerTests`
- [ ] 1.4 Add `StreamAsync_ShouldAbort_WhenCancelled` to `AssemblyAiSpeechRecognizerTests`
- [ ] 1.5 Add `StreamAsync_ShouldAbort_WhenCancelled` to `CartesiaSpeechRecognizerTests`

## 2. Verification

- [ ] 2.1 Repeat-run the new tests under load (mirror the 30x/32x protocol from
      `stt-cancellation-test-fence`) — zero flakes
- [ ] 2.2 `dotnet test` + zero warnings
