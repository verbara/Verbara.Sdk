using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;

namespace Verbara.Sdk.VoiceAi.AudioSocket.Tests;

[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class AudioSocketServerTests : IAsyncLifetime
{
    private readonly AudioSocketServer _server;

    public AudioSocketServerTests()
    {
        // Port = 0: OS assigns an available ephemeral port, eliminating collision risk
        var options = new AudioSocketOptions { Port = 0, ConnectionTimeout = TimeSpan.FromSeconds(5) };
        _server = new AudioSocketServer(options, NullLogger<AudioSocketServer>.Instance);
    }

    [Fact]
    public async Task Server_ShouldAcceptConnection_AndFireOnSessionStarted()
    {
        AudioSocketSession? receivedSession = null;
        _server.OnSessionStarted += session =>
        {
            receivedSession = session;
            return ValueTask.CompletedTask;
        };

        await _server.StartAsync(CancellationToken.None);

        var channelId = Guid.NewGuid();
        await using var client = new AudioSocketClient("127.0.0.1", _server.BoundPort, channelId);
        await client.ConnectAsync();

        // Give server time to process the UUID frame
        await Task.Delay(200);

        receivedSession.Should().NotBeNull();
        receivedSession!.ChannelId.Should().Be(channelId);
    }

    [Fact]
    public async Task Session_ReadAudioAsync_ShouldYieldAudioFrames()
    {
        AudioSocketSession? capturedSession = null;
        _server.OnSessionStarted += session =>
        {
            capturedSession = session;
            return ValueTask.CompletedTask;
        };

        await _server.StartAsync(CancellationToken.None);

        var channelId = Guid.NewGuid();
        await using var client = new AudioSocketClient("127.0.0.1", _server.BoundPort, channelId);
        await client.ConnectAsync();
        await Task.Delay(200);

        capturedSession.Should().NotBeNull();

        byte[] audioData = new byte[320];
        Random.Shared.NextBytes(audioData);
        await client.SendAudioAsync(audioData);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        ReadOnlyMemory<byte>? received = null;

        await foreach (var chunk in capturedSession!.ReadAudioAsync(cts.Token))
        {
            received = chunk;
            break;
        }

        received.Should().NotBeNull();
        received!.Value.Length.Should().Be(320);
        received!.Value.ToArray().Should().BeEquivalentTo(audioData);
    }

    [Fact]
    public async Task Session_ShouldDetectHangup_WhenClientSendsHangupFrame()
    {
        AudioSocketSession? capturedSession = null;
        _server.OnSessionStarted += session =>
        {
            capturedSession = session;
            return ValueTask.CompletedTask;
        };

        await _server.StartAsync(CancellationToken.None);

        var channelId = Guid.NewGuid();
        await using var client = new AudioSocketClient("127.0.0.1", _server.BoundPort, channelId);
        await client.ConnectAsync();
        await Task.Delay(200);

        bool hangupFired = false;
        capturedSession!.OnHangup += () => hangupFired = true;

        await client.SendHangupAsync();
        await Task.Delay(300);

        hangupFired.Should().BeTrue();
    }

    [Fact]
    public async Task Server_ActiveSessionCount_ShouldTrackConnections()
    {
        await _server.StartAsync(CancellationToken.None);

        await using var client1 = new AudioSocketClient("127.0.0.1", _server.BoundPort, Guid.NewGuid());
        await using var client2 = new AudioSocketClient("127.0.0.1", _server.BoundPort, Guid.NewGuid());

        await client1.ConnectAsync();
        await client2.ConnectAsync();
        await Task.Delay(300);

        _server.ActiveSessionCount.Should().Be(2);
    }

    [Fact]
    public async Task Session_WriteAudioAsync_ShouldSendFramedData_ToClient()
    {
        AudioSocketSession? capturedSession = null;
        _server.OnSessionStarted += session =>
        {
            capturedSession = session;
            return ValueTask.CompletedTask;
        };

        await _server.StartAsync(CancellationToken.None);

        var channelId = Guid.NewGuid();
        await using var client = new AudioSocketClient("127.0.0.1", _server.BoundPort, channelId);
        await client.ConnectAsync();
        await Task.Delay(200);

        byte[] responseAudio = new byte[160];
        for (int i = 0; i < responseAudio.Length; i++) responseAudio[i] = (byte)(i % 256);

        await capturedSession!.WriteAudioAsync(responseAudio);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        ReadOnlyMemory<byte>? received = null;
        await foreach (var chunk in client.ReadAudioAsync(cts.Token))
        {
            received = chunk;
            break;
        }

        received.Should().NotBeNull();
        received!.Value.ToArray().Should().BeEquivalentTo(responseAudio);
    }

    // ── The server's own stop ends live sessions with a hangup frame ─────────

    /// <summary>Upper bound on any single wait in the stop tests. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How the stop tests end the server.</summary>
    public enum ServerEnding
    {
        /// <summary><see cref="AudioSocketServer.StopAsync"/>.</summary>
        Stop,

        /// <summary><see cref="AudioSocketServer.DisposeAsync"/>, with no stop before it.</summary>
        Disposal,
    }

    /// <summary>
    /// The server's stop, and its disposal, end a session that is still live the way the broker and a
    /// refusal end one: one hangup frame, then the close. The session's hangup is raised once.
    /// </summary>
    [Theory]
    [InlineData(ServerEnding.Stop)]
    [InlineData(ServerEnding.Disposal)]
    public async Task StopAsync_ShouldWriteOneHangupFrameThenClose_WhenASessionIsLive(ServerEnding ending)
    {
        var probe = SessionStartedProbe.SubscribeTo(_server);
        await _server.StartAsync(CancellationToken.None);
        var channelId = Guid.NewGuid();
        using var peer = await FrameCountingPeer.ConnectAsync(_server.BoundPort, channelId);
        var session = await probe.Started(channelId).WaitAsync(SignalTimeout);
        var hangups = 0;
        session.OnHangup += () => Interlocked.Increment(ref hangups);

        if (ending == ServerEnding.Stop)
            await _server.StopAsync(CancellationToken.None).WaitAsync(SignalTimeout);
        else
            await _server.DisposeAsync().AsTask().WaitAsync(SignalTimeout);

        var read = await peer.ReadToEndAsync(SignalTimeout);
        using (new AssertionScope())
        {
            read.ReachedEnd.Should().BeTrue($"the {ending} closes the connection (read: {read.Describe()})");
            read.Frames.Count(f => f == AudioSocketFrameType.Hangup).Should().Be(
                1, $"the {ending} writes exactly one hangup frame to a live session (read: {read.Describe()})");
            if (read.Frames.Count > 0)
                read.Frames[^1].Should().Be(AudioSocketFrameType.Hangup, "the hangup frame is the last thing the far end reads");
            session.IsConnected.Should().BeFalse($"no session the server served is connected once its {ending} returns");
            Volatile.Read(ref hangups).Should().Be(1, "the session's hangup is raised once");
        }
    }

    /// <summary>
    /// A session that already ended gets nothing more from the server's stop: no frame is written to a
    /// connection the far end already hung up.
    /// </summary>
    [Fact]
    public async Task StopAsync_ShouldWriteNothing_WhenTheSessionAlreadyEnded()
    {
        var probe = SessionStartedProbe.SubscribeTo(_server);
        await _server.StartAsync(CancellationToken.None);
        var channelId = Guid.NewGuid();
        using var peer = await FrameCountingPeer.ConnectAsync(_server.BoundPort, channelId);
        var session = await probe.Started(channelId).WaitAsync(SignalTimeout);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnHangup += () => ended.TrySetResult();

        await peer.SendHangupAsync();
        await ended.Task.WaitAsync(SignalTimeout);
        await _server.StopAsync(CancellationToken.None).WaitAsync(SignalTimeout);

        var read = await peer.ReadToEndAsync(SignalTimeout);
        using (new AssertionScope())
        {
            read.ReachedEnd.Should().BeTrue("the session closed when the far end hung up");
            read.ByteCount.Should().Be(0, $"nothing is written to a session that already ended (read: {read.Describe()})");
        }
    }

    /// <summary>Bound on the class cleanup, so a hang there fails the test instead of stalling the lane.</summary>
    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(30);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ReleaseAsync().WaitAsync(CleanupBound);

    private async Task ReleaseAsync()
    {
        await _server.StopAsync(CancellationToken.None);
        await _server.DisposeAsync();
    }
}

/// <summary>
/// A far end that speaks the AudioSocket wire directly, as Asterisk does, and keeps every byte the server
/// writes, so a test can count the frames.
/// </summary>
file sealed class FrameCountingPeer : IDisposable
{
    private readonly System.Net.Sockets.TcpClient _client;
    private readonly System.Net.Sockets.NetworkStream _stream;
    private readonly List<byte> _received = [];
    private readonly Lock _gate = new();
    private readonly Task _reading;

    private FrameCountingPeer(System.Net.Sockets.TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
        _reading = ReadAllAsync();
    }

    public static async Task<FrameCountingPeer> ConnectAsync(int port, Guid channelId)
    {
        var client = new System.Net.Sockets.TcpClient();
        try
        {
            // 127.0.0.1, never "localhost": the name resolves ::1 first on this host.
            await client.ConnectAsync(System.Net.IPAddress.Loopback, port);
            var uuid = new byte[19];
            uuid[0] = (byte)AudioSocketFrameType.Uuid;
            uuid[2] = 16;
            channelId.TryWriteBytes(uuid.AsSpan(3), bigEndian: true, out _);
            await client.GetStream().WriteAsync(uuid);
            return new FrameCountingPeer(client);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            client.Dispose();
            throw;
        }
    }

    public async Task SendHangupAsync() =>
        await _stream.WriteAsync(new byte[] { (byte)AudioSocketFrameType.Hangup, 0, 0 });

    /// <summary>
    /// Waits until the server ends the stream or <paramref name="bound"/> runs out; what was read is
    /// returned either way, so a line left open fails on its assertion rather than on a timeout.
    /// </summary>
    public async Task<FrameRead> ReadToEndAsync(TimeSpan bound)
    {
        var reachedEnd = false;
        try
        {
            await _reading.WaitAsync(bound);
            reachedEnd = true;
        }
        catch (TimeoutException)
        {
            // The line is still open: reported by the caller's assertion, with what was read.
        }

        byte[] bytes;
        lock (_gate)
            bytes = [.. _received];

        var frames = new List<AudioSocketFrameType>();
        var offset = 0;
        while (bytes.Length - offset >= 3)
        {
            var length = (bytes[offset + 1] << 8) | bytes[offset + 2];
            if (bytes.Length - offset - 3 < length)
                break;
            frames.Add((AudioSocketFrameType)bytes[offset]);
            offset += 3 + length;
        }

        return new FrameRead(reachedEnd, frames, bytes.Length);
    }

    private async Task ReadAllAsync()
    {
        var buffer = new byte[4096];
        try
        {
            int n;
            while ((n = await _stream.ReadAsync(buffer)) > 0)
            {
                lock (_gate)
                    _received.AddRange(buffer.AsSpan(0, n).ToArray());
            }
        }
        catch (IOException)
        {
            // A reset is also an end of the connection; the bytes before it are kept.
        }
        catch (ObjectDisposedException)
        {
            // The peer was disposed while a read was still pending on a line left open.
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
        _client.Dispose();
    }
}

/// <summary>What a <see cref="FrameCountingPeer"/> read: whether the stream ended, the frames' types, the bytes.</summary>
file sealed record FrameRead(bool ReachedEnd, IReadOnlyList<AudioSocketFrameType> Frames, int ByteCount)
{
    public string Describe() =>
        ByteCount == 0 ? "no bytes" : $"{ByteCount} bytes: {string.Join(", ", Frames)}";
}
