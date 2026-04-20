using System.Buffers;
using Asterisk.Sdk.Ari.Audio;
using FluentAssertions;

namespace Asterisk.Sdk.Ari.Tests.Audio;

public class AudioSocketSessionTests
{
    /// <summary>Build a complete AudioSocket frame in memory.</summary>
    private static byte[] BuildFrame(AudioFrameType type, byte[] payload)
    {
        var frame = new byte[4 + payload.Length];
        frame[0] = (byte)type;
        frame[1] = (byte)(payload.Length >> 16);
        frame[2] = (byte)(payload.Length >> 8);
        frame[3] = (byte)(payload.Length);
        payload.CopyTo(frame.AsSpan(4));
        return frame;
    }

    [Fact]
    public async Task Session_ShouldParseUuidAndReadAudio()
    {
        var uuid = Guid.NewGuid();
        var uuidFrame = BuildFrame(AudioFrameType.Uuid, uuid.ToByteArray());
        var audioData = new byte[320];
        Random.Shared.NextBytes(audioData);
        var audioFrame = BuildFrame(AudioFrameType.Audio, audioData);
        var hangupFrame = BuildFrame(AudioFrameType.Hangup, []);

        // Concatenate all frames
        var allData = new byte[uuidFrame.Length + audioFrame.Length + hangupFrame.Length];
        uuidFrame.CopyTo(allData, 0);
        audioFrame.CopyTo(allData, uuidFrame.Length);
        hangupFrame.CopyTo(allData, uuidFrame.Length + audioFrame.Length);

        await using var memStream = new MemoryStream(allData);
        var session = new AudioSocketSession(memStream, "slin16");
        session.Start();

        // Wait for UUID to be parsed
        var timeout = Task.Delay(TimeSpan.FromSeconds(2));
        while (string.IsNullOrEmpty(session.ChannelId) && !timeout.IsCompleted)
            await Task.Delay(10);

        session.ChannelId.Should().Be(uuid.ToString());
        session.Format.Should().Be("slin16");
        session.SampleRate.Should().Be(16000);

        // Read audio frame
        var frame = await session.ReadFrameAsync();
        frame.Length.Should().Be(320);
        frame.ToArray().Should().BeEquivalentTo(audioData);
    }

    [Fact]
    public async Task Session_ShouldReportDisconnectedOnHangup()
    {
        var uuidFrame = BuildFrame(AudioFrameType.Uuid, Guid.NewGuid().ToByteArray());
        var hangupFrame = BuildFrame(AudioFrameType.Hangup, []);

        var allData = new byte[uuidFrame.Length + hangupFrame.Length];
        uuidFrame.CopyTo(allData, 0);
        hangupFrame.CopyTo(allData, uuidFrame.Length);

        var states = new List<AudioStreamState>();
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var memStream = new MemoryStream(allData);
        var session = new AudioSocketSession(memStream, "ulaw");
        using var sub = session.StateChanges.Subscribe(s =>
        {
            states.Add(s);
            if (s == AudioStreamState.Disconnected)
                disconnected.TrySetResult();
        });
        session.Start();

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(2));

        states.Should().Contain(AudioStreamState.Connected);
        states.Should().Contain(AudioStreamState.Disconnected);
        session.SampleRate.Should().Be(8000);
    }

    [Fact]
    public async Task Session_ShouldHandleErrorFrame()
    {
        var uuidFrame = BuildFrame(AudioFrameType.Uuid, Guid.NewGuid().ToByteArray());
        var errorFrame = BuildFrame(AudioFrameType.Error, "test error"u8.ToArray());

        var allData = new byte[uuidFrame.Length + errorFrame.Length];
        uuidFrame.CopyTo(allData, 0);
        errorFrame.CopyTo(allData, uuidFrame.Length);

        var states = new List<AudioStreamState>();
        var errored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var memStream = new MemoryStream(allData);
        var session = new AudioSocketSession(memStream, "slin16");
        using var sub = session.StateChanges.Subscribe(s =>
        {
            states.Add(s);
            if (s == AudioStreamState.Error)
                errored.TrySetResult();
        });
        session.Start();

        await errored.Task.WaitAsync(TimeSpan.FromSeconds(2));

        states.Should().Contain(AudioStreamState.Error);
    }

    [Fact]
    public async Task Session_DisposeAsync_ShouldCompleteCleanly()
    {
        var uuidFrame = BuildFrame(AudioFrameType.Uuid, Guid.NewGuid().ToByteArray());
        await using var memStream = new MemoryStream(uuidFrame);
        var session = new AudioSocketSession(memStream, "slin16");
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = session.StateChanges.Subscribe(s =>
        {
            if (s == AudioStreamState.Connected)
                connected.TrySetResult();
        });
        session.Start();

        await connected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await session.DisposeAsync();

        session.IsConnected.Should().BeFalse();
    }
}
