# Tasks — consolidate-stt-cancellation-frame-helpers

## 1. Implementation

- [x] 1.1 Add the shared `EndlessFrames` async frame generator to a helper under
      `Tests/Verbara.Sdk.VoiceAi.Stt.Tests/Helpers/` (alongside `MockHttpMessageHandler.cs`),
      carrying the `// fence-allow: LOOP-DRIVER` annotation on its `Task.Delay(10, ct)` pacer
      → `Helpers/SttFrameGenerators.cs`, `internal static class SttFrameGenerators`
- [x] 1.2 Replace the private `EndlessFrames` copy in `DeepgramSpeechRecognizerTests` with the shared helper
- [x] 1.3 Replace the private `EndlessFrames` copy in `AssemblyAiSpeechRecognizerTests` with the shared helper
- [x] 1.4 Replace the private `EndlessFrames` copy in `CartesiaSpeechRecognizerTests` with the shared helper
- [x] 1.5 Replace the private `EndlessFrames` copy in `SpeechmaticsSpeechRecognizerTests` with the shared helper
- [x] 1.6 **(discovered during apply)** Lower the `sync-fence-baseline.json` entry for
      `DeepgramSpeechRecognizerTests.cs`. Deepgram's copy was the one *unannotated* pacer of the four
      and was grandfathered at count 1; deleting it takes the file's real unmarked count to 0, so the
      entry is removed outright. The baseline's own rule permits lowering, never raising.

## 2. Verification

- [x] 2.1 `dotnet test Tests/Verbara.Sdk.VoiceAi.Stt.Tests` — all `StreamAsync_ShouldAbort_WhenCancelled`
      tests still green (deterministic, no flakes under the repeat-run protocol)
      → 46/46 green; cancellation filter 7/7 green across **30 consecutive runs, 0 failures**
- [x] 2.2 `SyncFenceRegressionGuardTests` still green — exactly one annotated `LOOP-DRIVER` pacer at the shared site
      → `Verbara.Sdk.Governance.Tests` 42/42 green
- [x] 2.3 `dotnet test` + zero warnings (TreatWarningsAsErrors)
      → `dotnet build Verbara.Sdk.slnx` 0 warnings / 0 errors; unit lane 2,991 passed
