using System.Buffers;
using Asterisk.Sdk.Ari.Audio;
using BenchmarkDotNet.Attributes;

namespace Asterisk.Sdk.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class AudioSocketBenchmark
{
    private byte[] _singleAudioFrame = null!;
    private byte[] _batch100Frames = null!;
    private byte[] _incompleteFrame = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Single 640-byte audio frame (20ms slin16 @ 16kHz)
        var payload = new byte[640];
        Random.Shared.NextBytes(payload);
        _singleAudioFrame = BuildFrame(AudioFrameType.Audio, payload);

        // 100 audio frames back-to-back
        var writer = new ArrayBufferWriter<byte>(100 * (4 + 640));
        for (int i = 0; i < 100; i++)
            AudioSocketProtocol.WriteFrame(writer, AudioFrameType.Audio, payload);
        _batch100Frames = writer.WrittenSpan.ToArray();

        // Incomplete frame: header says 640 bytes but only 100 present
        _incompleteFrame = new byte[4 + 100];
        _incompleteFrame[0] = (byte)AudioFrameType.Audio;
        _incompleteFrame[1] = 0; // 640 >> 16
        _incompleteFrame[2] = 2; // 640 >> 8
        _incompleteFrame[3] = 128; // 640 & 0xFF
        // Only 100 bytes of payload follow — parser should return false + rewind
    }

    private static byte[] BuildFrame(AudioFrameType type, ReadOnlySpan<byte> payload)
    {
        var writer = new ArrayBufferWriter<byte>(4 + payload.Length);
        AudioSocketProtocol.WriteFrame(writer, type, payload);
        return writer.WrittenSpan.ToArray();
    }

    [Benchmark(Baseline = true)]
    public bool ParseSingleAudioFrame()
    {
        var seq = new ReadOnlySequence<byte>(_singleAudioFrame);
        var reader = new SequenceReader<byte>(seq);
        return AudioSocketProtocol.TryParseFrame(ref reader, out _, out _);
    }

    [Benchmark]
    public int Parse100AudioFrames()
    {
        var seq = new ReadOnlySequence<byte>(_batch100Frames);
        var reader = new SequenceReader<byte>(seq);
        int count = 0;
        while (AudioSocketProtocol.TryParseFrame(ref reader, out _, out _))
            count++;
        return count;
    }

    [Benchmark]
    public bool ParseIncompleteFrame_ShouldRewind()
    {
        var seq = new ReadOnlySequence<byte>(_incompleteFrame);
        var reader = new SequenceReader<byte>(seq);
        // Should return false and rewind
        return AudioSocketProtocol.TryParseFrame(ref reader, out _, out _);
    }

    [Benchmark]
    public void WriteAudioFrame()
    {
        var writer = new ArrayBufferWriter<byte>(644);
        var payload = (stackalloc byte[640]);
        AudioSocketProtocol.WriteFrame(writer, AudioFrameType.Audio, payload);
    }
}
