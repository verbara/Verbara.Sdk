using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Verbara.Sdk.VoiceAi.TurnDetection.Internal;

internal sealed class OnnxSessionManager : IDisposable
{
    private readonly InferenceSession _session;

    public OnnxSessionManager(ExecutionProvider provider = ExecutionProvider.Cpu, int intraOpThreads = 1)
    {
        // Assembly access is required to load the embedded ONNX model resource.
        // This package already opts out of AOT (IsAotCompatible=false).
#pragma warning disable RS0030 // Banned API — no source-generator alternative for embedded resources
        var assembly = typeof(OnnxSessionManager).Assembly;
        using var stream = assembly.GetManifestResourceStream("smart-turn-v3.2-cpu.onnx")
#pragma warning restore RS0030
            ?? throw new InvalidOperationException("Embedded ONNX model resource not found.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);

        var options = new SessionOptions
        {
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = intraOpThreads,
        };

        if (provider == ExecutionProvider.Cuda)
        {
            options.AppendExecutionProvider_CUDA();
        }

        _session = new InferenceSession(ms.ToArray(), options);
    }

    /// <summary>
    /// Run inference on a mel spectrogram and return the turn-end probability.
    /// </summary>
    /// <param name="melSpectrogram">Flat row-major float[80 * 800] mel spectrogram.</param>
    /// <returns>Sigmoid probability (0.0-1.0) that the speaker has finished their turn.</returns>
    public float RunInference(float[] melSpectrogram)
    {
        var inputTensor = new DenseTensor<float>(melSpectrogram, [1, 80, 800]);
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor("input_features", inputTensor)]);
        var logit = results[0].AsTensor<float>()[0];
        return 1.0f / (1.0f + MathF.Exp(-logit));
    }

    public void Dispose() => _session.Dispose();
}
