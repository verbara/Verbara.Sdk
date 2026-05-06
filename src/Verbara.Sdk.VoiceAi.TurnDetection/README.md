# Verbara.Sdk.VoiceAi.TurnDetection

ML-based turn detection for the Verbara.Sdk VoiceAi pipeline using the Pipecat smart-turn-v3 ONNX model. Detects semantic end-of-turn boundaries — not just silence — for more natural conversational turn-taking.

## Installation

```bash
dotnet add package Verbara.Sdk.VoiceAi.TurnDetection
```

> **Note:** This package bundles the `smart-turn-v3.2-cpu.onnx` model as an embedded resource. CPU inference works on all platforms. GPU acceleration (CUDA / DirectML) requires the matching ONNX Runtime provider package.

## Quick Start

Replace the default `SilenceTurnDetector` with the smart ML-based detector by calling `AddSmartTurnDetection` in your DI setup:

```csharp
services.AddVoiceAiPipeline<MyHandler>();
services.AddSmartTurnDetection(opts =>
{
    opts.TurnConfidenceThreshold = 0.5f;   // probability threshold (0.0–1.0)
    opts.SilenceTriggerDuration  = TimeSpan.FromMilliseconds(200);
    opts.BargInVoiceThreshold    = TimeSpan.FromMilliseconds(200);
    opts.ExecutionProvider       = ExecutionProvider.Cpu;
    opts.IntraOpThreads          = 1;
});
```

`AddSmartTurnDetection` removes any previously registered `ITurnDetector` and registers `SmartTurnDetector` in its place.

## Configuration

| Property | Type | Default | Description |
|---|---|---|---|
| `TurnConfidenceThreshold` | `float` | `0.5` | Minimum model probability to classify a pause as end-of-turn |
| `SilenceThresholdDb` | `double` | `-40.0` | RMS energy threshold (dBFS) for silence detection |
| `SilenceTriggerDuration` | `TimeSpan` | `200ms` | Silence duration after speech before running the model |
| `BargInVoiceThreshold` | `TimeSpan` | `200ms` | Voice duration during TTS playback to trigger barge-in |
| `ExecutionProvider` | `ExecutionProvider` | `Cpu` | ONNX Runtime execution provider |
| `IntraOpThreads` | `int` | `1` | Number of intra-op threads for ONNX Runtime |

## License

MIT — see [LICENSE](../../LICENSE) in the repository root.
