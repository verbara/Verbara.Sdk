using Asterisk.Sdk.Audio.Resampling;
using FluentAssertions;

namespace Asterisk.Sdk.Audio.Tests.Resampling;

public sealed class PolyphaseResamplerTests
{
    [Fact]
    public void Create_ShouldSucceed_ForAllSupportedRatePairs()
    {
        var pairs = new[]
        {
            (8000, 16000), (16000, 8000),
            (8000, 24000), (24000, 8000),
            (16000, 24000), (24000, 16000),
            (8000, 48000), (48000, 8000),
            (16000, 48000), (48000, 16000),
            (24000, 48000), (48000, 24000),
        };

        foreach (var (input, output) in pairs)
        {
            using var resampler = ResamplerFactory.Create(input, output);
            resampler.Should().NotBeNull($"rate pair {input}->{output} should be supported");
        }
    }

    [Fact]
    public void Create_ShouldThrow_ForUnsupportedRatePair()
    {
        var act = () => ResamplerFactory.Create(8000, 22050);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Process_8kTo16k_ShouldProduceApproximatelyDoubleOutputSamples()
    {
        using var resampler = ResamplerFactory.Create(8000, 16000);

        // 160 samples @ 8kHz = 20ms
        short[] input = new short[160];
        for (int i = 0; i < input.Length; i++)
            input[i] = (short)(short.MaxValue * 0.5f * Math.Sin(2 * Math.PI * 440 * i / 8000.0));

        short[] output = new short[ResamplerFactory.CalculateOutputSize(input.Length, 8000, 16000)];
        int written = resampler.Process(input.AsSpan(), output.AsSpan());

        written.Should().BeGreaterThanOrEqualTo(input.Length * 2 - 4);
        written.Should().BeLessThanOrEqualTo(input.Length * 2 + 4);
    }

    [Fact]
    public void Process_16kTo8k_ShouldProduceApproximatelyHalfOutputSamples()
    {
        using var resampler = ResamplerFactory.Create(16000, 8000);

        short[] input = new short[320]; // 20ms @ 16kHz
        for (int i = 0; i < input.Length; i++)
            input[i] = (short)(short.MaxValue * 0.5f * Math.Sin(2 * Math.PI * 440 * i / 16000.0));

        short[] output = new short[ResamplerFactory.CalculateOutputSize(input.Length, 16000, 8000)];
        int written = resampler.Process(input.AsSpan(), output.AsSpan());

        written.Should().BeGreaterThanOrEqualTo(input.Length / 2 - 4);
        written.Should().BeLessThanOrEqualTo(input.Length / 2 + 4);
    }

    [Fact]
    public void Process_8kTo24k_ShouldProduceApproximatelyTripleOutputSamples()
    {
        using var resampler = ResamplerFactory.Create(8000, 24000);

        short[] input = new short[160];
        for (int i = 0; i < input.Length; i++)
            input[i] = (short)(short.MaxValue * 0.5f * Math.Sin(2 * Math.PI * 440 * i / 8000.0));

        short[] output = new short[ResamplerFactory.CalculateOutputSize(input.Length, 8000, 24000)];
        int written = resampler.Process(input.AsSpan(), output.AsSpan());

        written.Should().BeGreaterThanOrEqualTo(input.Length * 3 - 4);
        written.Should().BeLessThanOrEqualTo(input.Length * 3 + 4);
    }

    [Fact]
    public void Process_ShouldMaintainContinuityAcrossFrames()
    {
        using var resampler = ResamplerFactory.Create(8000, 16000);

        // Two consecutive 20ms frames of sine wave
        short[] frame1 = new short[160];
        short[] frame2 = new short[160];
        for (int i = 0; i < 160; i++)
        {
            frame1[i] = (short)(10000 * Math.Sin(2 * Math.PI * 440 * i / 8000.0));
            frame2[i] = (short)(10000 * Math.Sin(2 * Math.PI * 440 * (i + 160) / 8000.0));
        }

        short[] out1 = new short[400];
        short[] out2 = new short[400];
        int n1 = resampler.Process(frame1.AsSpan(), out1.AsSpan());
        int n2 = resampler.Process(frame2.AsSpan(), out2.AsSpan());

        // Both frames should produce output
        n1.Should().BeGreaterThan(0);
        n2.Should().BeGreaterThan(0);

        // Last sample of frame1 and first sample of frame2 should not be wildly different
        // (continuity check -- no abrupt jumps from delay line corruption)
        Math.Abs((int)out1[n1 - 1] - out2[0]).Should().BeLessThan(2000);
    }

    [Fact]
    public void Process_ShouldThrow_AfterDispose()
    {
        var resampler = ResamplerFactory.Create(8000, 16000);
        resampler.Dispose();

        short[] input = new short[160];
        short[] output = new short[400];
        var act = () => resampler.Process(input.AsSpan(), output.AsSpan());
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Process_ShouldNotAllocate_OnHotPath()
    {
        using var resampler = ResamplerFactory.Create(8000, 16000);
        short[] input = new short[160];
        short[] output = new short[400];

        // Warm up (first call may trigger JIT or other one-time allocations)
        resampler.Process(input.AsSpan(), output.AsSpan());

        long before = GC.GetAllocatedBytesForCurrentThread();
        resampler.Process(input.AsSpan(), output.AsSpan());
        long after = GC.GetAllocatedBytesForCurrentThread();

        (after - before).Should().Be(0, "hot path must not allocate");
    }

    [Fact]
    public void Reset_ShouldClearDelayLine_WithoutAffectingSubsequentOutput()
    {
        using var resampler = ResamplerFactory.Create(8000, 16000);
        short[] input = new short[160];
        short[] output = new short[400];

        resampler.Process(input.AsSpan(), output.AsSpan());
        resampler.Reset(); // clear state

        // Should still produce output after reset
        int written = resampler.Process(input.AsSpan(), output.AsSpan());
        written.Should().BeGreaterThan(0);
    }

    [Fact]
    public void MaxOutputBytes_ShouldReturnSufficientBufferSize()
    {
        using var resampler = ResamplerFactory.Create(8000, 16000);
        int inputBytes = 160 * 2; // 160 short samples = 320 bytes
        int maxBytes = resampler.MaxOutputBytes(inputBytes);

        short[] input = new short[160];
        short[] output = new short[maxBytes / 2];
        int written = resampler.Process(input.AsSpan(), output.AsSpan());

        (written * 2).Should().BeLessThanOrEqualTo(maxBytes);
    }

    [Fact]
    public void Process_16kTo24k_ShouldProduceCorrectRatio()
    {
        using var resampler = ResamplerFactory.Create(16000, 24000);

        // 320 samples @ 16kHz -> ~480 samples @ 24kHz (ratio 3:2)
        short[] input = new short[320];
        for (int i = 0; i < input.Length; i++)
            input[i] = (short)(short.MaxValue * 0.5f * Math.Sin(2 * Math.PI * 440 * i / 16000.0));

        short[] output = new short[ResamplerFactory.CalculateOutputSize(320, 16000, 24000)];
        int written = resampler.Process(input.AsSpan(), output.AsSpan());

        // 320 * 24000/16000 = 480
        written.Should().BeGreaterThanOrEqualTo(480 - 4);
        written.Should().BeLessThanOrEqualTo(480 + 4);
    }

    [Fact]
    public void Process_ByteOverload_ShouldReturnCorrectByteCount()
    {
        using var resampler = ResamplerFactory.Create(8000, 16000);

        // 160 samples = 320 bytes
        short[] inputSamples = new short[160];
        for (int i = 0; i < inputSamples.Length; i++)
            inputSamples[i] = (short)(10000 * Math.Sin(2 * Math.PI * 440 * i / 8000.0));

        byte[] inputBytes = new byte[320];
        Buffer.BlockCopy(inputSamples, 0, inputBytes, 0, inputBytes.Length);

        byte[] outputBytes = new byte[resampler.MaxOutputBytes(inputBytes.Length)];
        int bytesWritten = resampler.Process(inputBytes.AsSpan(), outputBytes.AsSpan());

        // Should return bytes, not samples -- approximately 320*2 = 640 bytes
        bytesWritten.Should().BeGreaterThanOrEqualTo(320 * 2 - 8);
        bytesWritten.Should().BeLessThanOrEqualTo(320 * 2 + 8);
    }

    [Fact]
    public void InputFormat_ShouldMatchInputRate()
    {
        using var resampler = ResamplerFactory.Create(8000, 16000);
        resampler.InputFormat.SampleRate.Should().Be(8000);
        resampler.InputFormat.Channels.Should().Be(1);
        resampler.InputFormat.BitsPerSample.Should().Be(16);
        resampler.InputFormat.Encoding.Should().Be(AudioEncoding.LinearPcm);
    }

    [Fact]
    public void OutputFormat_ShouldMatchOutputRate()
    {
        using var resampler = ResamplerFactory.Create(8000, 16000);
        resampler.OutputFormat.SampleRate.Should().Be(16000);
        resampler.OutputFormat.Channels.Should().Be(1);
        resampler.OutputFormat.BitsPerSample.Should().Be(16);
        resampler.OutputFormat.Encoding.Should().Be(AudioEncoding.LinearPcm);
    }
}
