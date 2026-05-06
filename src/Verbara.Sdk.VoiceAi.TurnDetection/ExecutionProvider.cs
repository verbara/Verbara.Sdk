namespace Verbara.Sdk.VoiceAi.TurnDetection;

/// <summary>
/// Specifies the hardware execution provider for ONNX Runtime inference.
/// </summary>
public enum ExecutionProvider
{
    /// <summary>CPU execution (default). Works on all platforms.</summary>
    Cpu,

    /// <summary>NVIDIA CUDA execution. Requires the CUDA runtime and Microsoft.ML.OnnxRuntime.Gpu package.</summary>
    Cuda,

    /// <summary>DirectML execution (Windows only). Requires Microsoft.ML.OnnxRuntime.DirectML package.</summary>
    DirectMl,
}
