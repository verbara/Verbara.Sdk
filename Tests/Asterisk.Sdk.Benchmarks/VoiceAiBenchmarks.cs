using Asterisk.Sdk.VoiceAi;
using Asterisk.Sdk.VoiceAi.Testing;
using BenchmarkDotNet.Attributes;

namespace Asterisk.Sdk.Benchmarks;

/// <summary>
/// Benchmarks the VoiceAi hot path. In particular, compares the v1.10.0
/// <c>ProviderName</c> virtual property (override returns a cached literal)
/// against the pre-v1.10 fallback of calling <c>GetType().Name</c> once per
/// utterance — exercised on every STT recognition and TTS synthesis activity.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class VoiceAiBenchmarks : IAsyncDisposable
{
    private SpeechRecognizer _recognizer = null!;
    private SpeechSynthesizer _synthesizer = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Fakes override ProviderName => "Fake" (v1.10+).
        _recognizer = new FakeSpeechRecognizer();
        _synthesizer = new FakeSpeechSynthesizer();
    }

    [GlobalCleanup]
    public async Task Cleanup() => await DisposeAsync().ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        await _recognizer.DisposeAsync().ConfigureAwait(false);
        await _synthesizer.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// v1.10+ path used by <c>VoiceAiPipeline</c>.
    /// Tags STT activities with the cached literal — no reflection, no alloc.
    /// </summary>
    [Benchmark(Baseline = true)]
    public string Stt_ProviderName() => _recognizer.ProviderName;

    /// <summary>
    /// Pre-v1.10 path. Present for comparison — each call allocates a string
    /// (the type name is computed and cached by the runtime after first call,
    /// but <c>GetType()</c> itself still incurs a virtual dispatch and the
    /// original had no literal override).
    /// </summary>
    [Benchmark]
    public string Stt_GetTypeName() => _recognizer.GetType().Name;

    [Benchmark]
    public string Tts_ProviderName() => _synthesizer.ProviderName;

    [Benchmark]
    public string Tts_GetTypeName() => _synthesizer.GetType().Name;
}
