# Tasks — consolidate-stt-cancellation-frame-helpers

## 1. Implementation

- [ ] 1.1 Add the shared `EndlessFrames` async frame generator to a helper under
      `Tests/Verbara.Sdk.VoiceAi.Stt.Tests/Helpers/` (alongside `MockHttpMessageHandler.cs`),
      carrying the `// fence-allow: LOOP-DRIVER` annotation on its `Task.Delay(10, ct)` pacer
- [ ] 1.2 Replace the private `EndlessFrames` copy in `DeepgramSpeechRecognizerTests` with the shared helper
- [ ] 1.3 Replace the private `EndlessFrames` copy in `AssemblyAiSpeechRecognizerTests` with the shared helper
- [ ] 1.4 Replace the private `EndlessFrames` copy in `CartesiaSpeechRecognizerTests` with the shared helper
- [ ] 1.5 Replace the private `EndlessFrames` copy in `SpeechmaticsSpeechRecognizerTests` with the shared helper

## 2. Verification

- [ ] 2.1 `dotnet test Tests/Verbara.Sdk.VoiceAi.Stt.Tests` — all `StreamAsync_ShouldAbort_WhenCancelled`
      tests still green (deterministic, no flakes under the repeat-run protocol)
- [ ] 2.2 `SyncFenceRegressionGuardTests` still green — exactly one annotated `LOOP-DRIVER` pacer at the shared site
- [ ] 2.3 `dotnet test` + zero warnings (TreatWarningsAsErrors)
