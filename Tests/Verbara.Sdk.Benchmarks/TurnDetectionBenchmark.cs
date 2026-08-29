using Verbara.Sdk.VoiceAi;
using Verbara.Sdk.VoiceAi.TurnDetection;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Sdk.Benchmarks;

/// <summary>
/// The measurement behind <c>README.md</c>'s CPU inference-latency claim for the smart-turn
/// detector. Until this existed the figure had no benchmark at all — the benchmark project did not
/// even reference the package.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is measured, and why it is not upstream's number.</b> Pipecat publish their latency for
/// raw ONNX inference. This measures the path a caller of this SDK actually pays for at an
/// end-of-utterance decision: 8 kHz → 16 kHz resample and ring-buffer accumulation (amortised
/// across the utterance), then the mel-spectrogram front-end over the accumulated audio, then the
/// ONNX session. The two numbers are not comparable and the SDK should publish its own.
/// </para>
/// <para>
/// <b>Why the feed is in <c>[IterationSetup]</c>.</b> Inference fires once per utterance, on the
/// silence frame that crosses <c>SilenceTriggerDuration</c> — not on every frame. Feeding the
/// utterance inside the measured region would average one inference over a hundred cheap frames and
/// report a number an order of magnitude too small. So the utterance and all but the last silence
/// frame are fed unmeasured, and the benchmark measures the single <c>Analyze</c> call that
/// triggers mel + ONNX. <c>[IterationSetup]</c> carries a known overhead that matters at nanosecond
/// scale; this benchmark is milliseconds, where it is noise.
/// </para>
/// <para>
/// Driven entirely through the public DI surface, exactly as a consumer builds it — the detector's
/// constructor is <c>internal</c> and this project has no <c>InternalsVisibleTo</c>, which is the
/// right constraint: a benchmark that needs privileged access is not measuring what ships.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[ShortRunJob]
public class TurnDetectionBenchmark
{
    /// <summary>20 ms at the 8 kHz input rate the detector resamples from.</summary>
    private const int FrameSamples = 160;

    /// <summary>
    /// How much speech the decision runs over. Parameterised rather than fixed, because the
    /// mel front-end's cost scales with the accumulated audio — <c>numFrames = 1 + (len - 400)/160</c>
    /// — even though its output is padded to a fixed 80x800. A single published latency figure is
    /// therefore meaningless without the utterance length it was measured at, and 8 s is the ring
    /// buffer's ceiling, so it is the worst case a caller can actually reach.
    /// </summary>
    [Params(1, 2, 4, 8)]
    public int UtteranceSeconds { get; set; }

    private int SpeechFrames => UtteranceSeconds * 50;

    /// <summary>
    /// <c>SilenceTriggerDuration</c> defaults to 200 ms against a 20 ms frame, so the tenth silence
    /// frame is the one that runs the model. Nine are fed unmeasured; the tenth is the benchmark.
    /// </summary>
    private const int SilenceFramesBeforeTrigger = 9;

    private ServiceProvider _provider = null!;
    private ITurnDetector _detector = null!;
    private short[] _speechFrame = null!;
    private short[] _silenceFrame = null!;

    [GlobalSetup]
    public void Setup()
    {
        _provider = new ServiceCollection()
            .AddSmartTurnDetection()
            .BuildServiceProvider();
        _detector = _provider.GetRequiredService<ITurnDetector>();

        // A 300 Hz tone at roughly a third of full scale — comfortably above the -40 dB silence
        // floor, so the detector treats these frames as speech.
        _speechFrame = new short[FrameSamples];
        for (int i = 0; i < FrameSamples; i++)
            _speechFrame[i] = (short)(10000 * Math.Sin(2 * Math.PI * 300 * i / 8000.0));

        _silenceFrame = new short[FrameSamples];
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        (_detector as IDisposable)?.Dispose();
        _provider.Dispose();
    }

    [IterationSetup]
    public void PrimeUtterance()
    {
        _detector.Reset();
        for (int i = 0; i < SpeechFrames; i++)
            _detector.Analyze(_speechFrame, isAssistantSpeaking: false);
        for (int i = 0; i < SilenceFramesBeforeTrigger; i++)
            _detector.Analyze(_silenceFrame, isAssistantSpeaking: false);
    }

    /// <summary>Mel front-end plus ONNX session, for one end-of-utterance decision.</summary>
    [Benchmark]
    public TurnSignal EndOfUtteranceDecision()
        => _detector.Analyze(_silenceFrame, isAssistantSpeaking: false);
}
