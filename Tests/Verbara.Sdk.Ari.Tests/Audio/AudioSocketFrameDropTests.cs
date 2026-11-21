using System.Buffers.Binary;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.Ari.Tests.TestSupport;

namespace Verbara.Sdk.Ari.Tests.Audio;

/// <summary>
/// An AudioSocket stream whose consumer falls behind: each frame its full receive buffer discards is counted on
/// <c>audio.frames.dropped</c>, tagged <c>audio.transport=audiosocket</c>, the counter the WebSocket stream records its
/// drops on.
/// </summary>
/// <remarks>
/// <para>
/// The session reads a scripted connection: its identification, the audio, then a hangup frame. The read pump handles
/// frames in the order they arrive, so once the hangup has ended the session every audio frame before it was counted
/// and written to the buffer. No wait is a delay.
/// </para>
/// <para>
/// The instruments are process-wide, so the class runs in <see cref="AudioStreamMetricsGroup"/>, alone.
/// </para>
/// </remarks>
[Collection(AudioStreamMetricsGroup.Name)]
public sealed class AudioSocketFrameDropTests
{
    /// <summary>Upper bound on any wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    // The published names: the contract an exporter sees. Literals on purpose, so a rename is a red here.
    private const string TransportTag = "audio.transport";
    private const string AudioSocketTransport = "audiosocket";
    private const string FramesDropped = "audio.frames.dropped";
    private const string FramesReceived = "audio.frames.received";

    /// <summary>The session's receive buffer: the frames it keeps for a consumer that reads nothing.</summary>
    private const int BufferCapacity = 1000;

    private const int AudioFrameBytes = 320;

    [Theory]
    [InlineData(5)]
    [InlineData(37)]
    public async Task ReadFrameAsync_ShouldCountEachDroppedFrameTaggedAudiosocketAndReadTheNewest_WhenFramesArriveOnAFullBuffer(
        int overflow)
    {
        // Arrange — a session whose consumer reads nothing until the connection has delivered everything
        using var recorder = new AudioMeasurementRecorder(FramesDropped, FramesReceived);
        var sent = BufferCapacity + overflow;
        await using var session = new AudioSocketSession(new MemoryStream(Script(sent)), "slin16", NullLogger.Instance);

        // Act — the buffer fills, then `overflow` more frames arrive, then the far end hangs up
        session.Start();
        await session.Ended.WaitAsync(SignalTimeout);
        var read = new List<int>(BufferCapacity);
        using var bound = new CancellationTokenSource(SignalTimeout);
        while (true)
        {
            var frame = await session.ReadFrameAsync(bound.Token);
            if (frame.IsEmpty)
                break;
            read.Add(BinaryPrimitives.ReadInt32LittleEndian(frame.Span));
        }

        // Assert
        using (new AssertionScope(
                   $"{FramesDropped}={recorder.Total(FramesDropped)} in {recorder.Of(FramesDropped).Count} measurements, "
                   + $"{FramesReceived}={recorder.Total(FramesReceived)}"))
        {
            recorder.Total(FramesDropped).Should().Be(overflow, "each frame the full buffer discards is counted once");
            recorder.Of(FramesDropped).Should().HaveCount(overflow, "one measurement per discarded frame");
            recorder.Of(FramesDropped).Should().OnlyContain(
                m => Equals(m.Tag(TransportTag), AudioSocketTransport), "a drop carries the transport it came through");
            recorder.Total(FramesReceived).Should().Be(sent, "every frame that arrived is counted as received, dropped or not");
            read.Should().Equal(Enumerable.Range(overflow, BufferCapacity),
                "the buffer discards its oldest frame, so the consumer reads the 1000 newest, in order");
        }
    }

    [Fact]
    public async Task ReadFrameAsync_ShouldNotMoveTheDropCount_WhenTheBufferNeverFills()
    {
        // Arrange — the negative control: exactly as many frames as the buffer holds
        using var recorder = new AudioMeasurementRecorder(FramesDropped, FramesReceived);
        await using var session = new AudioSocketSession(new MemoryStream(Script(BufferCapacity)), "slin16", NullLogger.Instance);

        // Act
        session.Start();
        await session.Ended.WaitAsync(SignalTimeout);

        // Assert
        using (new AssertionScope())
        {
            recorder.Of(FramesDropped).Should().BeEmpty("a buffer that never overflows discards nothing");
            recorder.Total(FramesReceived).Should().Be(BufferCapacity);
        }
    }

    /// <summary>The far end's bytes: one identification frame, <paramref name="frames"/> numbered audio frames, a hangup.</summary>
    private static byte[] Script(int frames)
    {
        using var script = new MemoryStream();
        Write(script, AudioFrameType.Uuid, Guid.NewGuid().ToByteArray(bigEndian: true));
        for (var index = 0; index < frames; index++)
        {
            var payload = new byte[AudioFrameBytes];
            BinaryPrimitives.WriteInt32LittleEndian(payload, index);
            Write(script, AudioFrameType.Audio, payload);
        }

        Write(script, AudioFrameType.Hangup, []);
        return script.ToArray();
    }

    private static void Write(MemoryStream script, AudioFrameType type, byte[] payload)
    {
        script.WriteByte((byte)type);
        script.WriteByte((byte)(payload.Length >> 8));
        script.WriteByte((byte)payload.Length);
        script.Write(payload);
    }
}
