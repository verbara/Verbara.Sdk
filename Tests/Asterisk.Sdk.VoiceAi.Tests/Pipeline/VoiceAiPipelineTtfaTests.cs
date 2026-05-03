using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Asterisk.Sdk.Audio;
using Asterisk.Sdk.VoiceAi.AudioSocket;
using Asterisk.Sdk.VoiceAi.Diagnostics;
using Asterisk.Sdk.VoiceAi.Pipeline;
using Asterisk.Sdk.VoiceAi.Testing;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Asterisk.Sdk.VoiceAi.Tests.Pipeline;

/// <summary>
/// Tests for the <c>tts.synthesis.ttfa_ms</c> histogram recorded by <see cref="VoiceAiPipeline"/>.
/// Uses <see cref="MeterListener"/> — the AOT-safe subscription API — to capture metric emissions
/// without reflection or dynamic binding.
/// </summary>
/// <remarks>
/// Each test uses a per-test unique <c>ProviderName</c> tag value so that parallel test runs on the
/// shared process-level <see cref="Meter"/> cannot cross-contaminate captured measurements.
/// </remarks>
public class VoiceAiPipelineTtfaTests
{
    private static VoiceAiPipeline BuildPipeline(
        SpeechSynthesizer tts,
        FakeSpeechRecognizer? stt = null,
        FakeConversationHandler? handler = null,
        VoiceAiPipelineOptions? options = null)
    {
        stt ??= new FakeSpeechRecognizer().WithTranscript("hola");
        handler ??= new FakeConversationHandler().WithResponse("respuesta");
        options ??= new VoiceAiPipelineOptions
        {
            EndOfUtteranceSilence = TimeSpan.FromMilliseconds(60),
            BargInVoiceThreshold = TimeSpan.FromMilliseconds(40),
        };

        var services = new ServiceCollection();
        services.AddSingleton<IConversationHandler>(handler);
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        return new VoiceAiPipeline(
            stt, tts, scopeFactory,
            Options.Create(options),
            NullLogger<VoiceAiPipeline>.Instance);
    }

    private static ReadOnlyMemory<byte> VoiceFrame()
    {
        var buf = new byte[320];
        for (int i = 0; i < 160; i++)
        {
            short sample = 5000;
            buf[i * 2] = (byte)(sample & 0xFF);
            buf[i * 2 + 1] = (byte)(sample >> 8);
        }
        return buf;
    }

    private static ReadOnlyMemory<byte> SilenceFrame() => new byte[320];

    // ---- Tests ----

    [Fact]
    public async Task Pipeline_ShouldRecordTtfaMetric_WhenSynthesizerYieldsFirstAudio()
    {
        var uid = $"ttfa-{Guid.NewGuid():N}";
        // 2 frames × 20 ms = 40 ms of audio — ensures at least one audio chunk is yielded
        var tts = new TrackedFakeSynthesizer(uid)
            .WithSilence(TimeSpan.FromMilliseconds(40));
        var pipeline = BuildPipeline(tts);

        var captured = await RunAndCaptureTtfaAsync(pipeline, uid);

        captured.Should().ContainSingle("TTFA should be recorded exactly once per synthesis");
        captured[0].Provider.Should().Be(uid);
        captured[0].Value.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Pipeline_ShouldNotRecordTtfaMetric_WhenSynthesizerYieldsZeroAudio()
    {
        var uid = $"ttfa-{Guid.NewGuid():N}";
        // WithSilence(Zero) → 0 frames → empty async enumerable → no TTFA recording
        var tts = new TrackedFakeSynthesizer(uid).WithSilence(TimeSpan.Zero);
        var pipeline = BuildPipeline(tts);

        var captured = await RunAndCaptureTtfaAsync(pipeline, uid);

        captured.Should().BeEmpty("TTFA should not be recorded when the synthesizer yields no audio");
    }

    [Fact]
    public async Task Pipeline_ShouldRecordTtfaBeforeTotalLatency_WhenSynthesizerStreams()
    {
        // 4 frames × 20 ms — gives the pipeline enough chunks that TTFA < total latency
        var uid = $"ttfa-{Guid.NewGuid():N}";
        var tts = new TrackedFakeSynthesizer(uid).WithSilence(TimeSpan.FromMilliseconds(80));
        var pipeline = BuildPipeline(tts);

        var ttfaMeasurements = new List<double>();
        var latencyMeasurements = new List<double>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == SpeechSynthesisMetrics.Meter.Name &&
                (instrument.Name == "tts.synthesis.ttfa_ms" ||
                 instrument.Name == "tts.synthesis.latency_ms"))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "tts.synthesis.ttfa_ms")
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "voiceai.provider" && tag.Value?.ToString() == uid)
                        lock (ttfaMeasurements) ttfaMeasurements.Add(value);
                }
            }
            else if (instrument.Name == "tts.synthesis.latency_ms")
            {
                // Latency is emitted without provider tag in the current impl;
                // use a short capture window — the test runs solo via uid TTS, so
                // we accept any latency emitted in this test's narrow window.
                // We validate relative order (ttfa ≤ latency), not absolute isolation.
                lock (latencyMeasurements) latencyMeasurements.Add(value);
            }
        });
        listener.Start();

        await RunPipelineWithSingleUtterance(pipeline, voiceFrameCount: 3, silenceFrameCount: 4);

        ttfaMeasurements.Should().ContainSingle("exactly one TTFA per synthesis");
        latencyMeasurements.Should().NotBeEmpty("at least one latency measurement must exist");
        // The latency measurement closest in value is the one from this synthesis cycle.
        // TTFA ≤ total latency always holds because TTFA is sampled before the loop ends.
        ttfaMeasurements[0].Should().BeLessThanOrEqualTo(
            latencyMeasurements.Max(),
            "TTFA (time to first frame) must not exceed total synthesis latency");
    }

    [Fact]
    public async Task Pipeline_ShouldRecordTtfaOnce_WhenSynthesizerYieldsManyChunks()
    {
        var uid = $"ttfa-{Guid.NewGuid():N}";
        // 50 frames × 20 ms = 1 000 ms worth of silence chunks
        var tts = new TrackedFakeSynthesizer(uid).WithSilence(TimeSpan.FromMilliseconds(1000));
        var pipeline = BuildPipeline(tts);

        var captured = await RunAndCaptureTtfaAsync(pipeline, uid);

        captured.Should().ContainSingle(
            "TTFA must be recorded exactly once regardless of how many audio chunks the synthesizer yields");
    }

    [Fact]
    public async Task Pipeline_ShouldNotRecordTtfaMetric_WhenSynthesizerThrows()
    {
        var uid = $"ttfa-{Guid.NewGuid():N}";
        // Exception before any frame yielded (afterCount=0 means throw on the very first call)
        var tts = new TrackedFakeSynthesizer(uid)
            .WithError(new InvalidOperationException("tts boom"), afterCount: 0);
        var pipeline = BuildPipeline(tts);

        var captured = await RunAndCaptureTtfaAsync(pipeline, uid);

        captured.Should().BeEmpty(
            "TTFA should not be recorded when the synthesizer throws before yielding the first audio frame");
    }

    // ---- Helper: run and capture TTFA for a specific provider uid ----

    private static async Task<List<(double Value, string? Provider)>> RunAndCaptureTtfaAsync(
        VoiceAiPipeline pipeline,
        string expectedUid,
        int voiceFrameCount = 3,
        int silenceFrameCount = 4)
    {
        var captured = new List<(double Value, string? Provider)>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == SpeechSynthesisMetrics.Meter.Name &&
                instrument.Name == "tts.synthesis.ttfa_ms")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            string? provider = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "voiceai.provider")
                    provider = tag.Value?.ToString();
            }
            if (provider == expectedUid)
                lock (captured) captured.Add((value, provider));
        });
        listener.Start();

        await RunPipelineWithSingleUtterance(pipeline, voiceFrameCount, silenceFrameCount);

        return captured;
    }

    // ---- Infrastructure: AudioSocket server/client pair (mirrors VoiceAiPipelineTests) ----

    private static async Task RunPipelineWithSingleUtterance(
        VoiceAiPipeline pipeline, int voiceFrameCount, int silenceFrameCount)
    {
        var server = new AudioSocketServer(
            new AudioSocketOptions { Port = 0 },
            NullLogger<AudioSocketServer>.Instance);

        TaskCompletionSource<AudioSocketSession> tcs = new();
        server.OnSessionStarted += session => { tcs.TrySetResult(session); return ValueTask.CompletedTask; };

        await server.StartAsync(CancellationToken.None);
        var port = server.BoundPort;

        await using var client = new AudioSocketClient("127.0.0.1", port, Guid.NewGuid());
        await client.ConnectAsync(CancellationToken.None);

        var session = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pipelineTask = pipeline.HandleSessionAsync(session, cts.Token).AsTask();

        for (int i = 0; i < voiceFrameCount; i++)
            await client.SendAudioAsync(VoiceFrame());
        for (int i = 0; i < silenceFrameCount; i++)
            await client.SendAudioAsync(SilenceFrame());

        await Task.Delay(500);
        await client.SendHangupAsync();

        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(10));
        await server.StopAsync(CancellationToken.None);
    }
}

/// <summary>
/// A <see cref="FakeSpeechSynthesizer"/> subclass that reports a per-test unique
/// <see cref="ProviderName"/> so that <see cref="MeterListener"/> callbacks can
/// filter by provider and avoid cross-contamination in parallel test runs.
/// </summary>
file sealed class TrackedFakeSynthesizer : SpeechSynthesizer
{
    private readonly string _providerName;
    private TimeSpan _silenceDuration;
    private Exception? _error;
    private int _errorAfterCount;
    private int _callIndex;

    public TrackedFakeSynthesizer(string providerName) => _providerName = providerName;

    public override string ProviderName => _providerName;

    public TrackedFakeSynthesizer WithSilence(TimeSpan duration) { _silenceDuration = duration; return this; }

    public TrackedFakeSynthesizer WithError(Exception exception, int afterCount = 0)
    {
        _error = exception;
        _errorAfterCount = afterCount;
        return this;
    }

    public override async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
        string text,
        AudioFormat outputFormat,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_error != null && _callIndex >= _errorAfterCount)
        {
            _callIndex++;
            throw _error;
        }

        _callIndex++;

        int frameSizeBytes = 320;
        int totalFrames = (int)(_silenceDuration.TotalMilliseconds / 20);
        var silence = new byte[frameSizeBytes];
        for (int i = 0; i < totalFrames; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return silence.AsMemory();
        }
    }
}
