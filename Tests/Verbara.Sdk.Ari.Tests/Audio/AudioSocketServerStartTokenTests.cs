using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.Tests.Shared.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Verbara.Sdk.Ari.Tests.Audio;

/// <summary>
/// The token handed to the AudioSocket server's start bounds the start, not the server's life. Cancelled once the
/// start returned, or already cancelled when it began, it neither stops the accept loop nor ends a connection
/// being served, and the server keeps reporting itself running. Only the server's own stop ends the loop; that
/// side is held by <see cref="AudioSocketServerTests.StopAsync_ShouldReportNoAcceptFailure_WhenARealListenerStopAbortsTheAccept"/>.
/// </summary>
public sealed class AudioSocketServerStartTokenTests
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>One 20 ms frame of 8 kHz signed linear audio.</summary>
    private const int AudioPayloadLength = 320;

    [Fact]
    public async Task StartAsync_ShouldKeepServing_WhenItsTokenIsCancelledAfterTheStart()
    {
        using var start = new CancellationTokenSource();
        var (server, port) = await StartOnLoopbackAsync(start.Token);
        await using var _ = server;
        using var announcements = new Announcements(server);

        await start.CancelAsync();
        var uuid = Guid.NewGuid();
        using var peer = await ConnectAndIdentifyAsync(port, uuid);
        var announced = await announcements.NextWithinBoundAsync();

        announced?.ChannelId.Should().Be(uuid.ToString());
        (announced is not null).Should().BeTrue(
            "a peer that identified itself was not served after the start token was cancelled: the accept loop " +
            "runs on a source linked to that token, so the cancel ended it");
        server.IsRunning.Should().BeTrue("the server still reports itself running, and it must also be serving");
    }

    [Fact]
    public async Task StartAsync_ShouldServe_WhenItsTokenIsAlreadyCancelled()
    {
        var (server, port) = await StartOnLoopbackAsync(new CancellationToken(canceled: true));
        await using var _ = server;
        using var announcements = new Announcements(server);

        var uuid = Guid.NewGuid();
        using var peer = await ConnectAndIdentifyAsync(port, uuid);
        var announced = await announcements.NextWithinBoundAsync();

        announced?.ChannelId.Should().Be(uuid.ToString());
        (announced is not null).Should().BeTrue(
            "a peer that identified itself was not served after a start whose token was already cancelled: the " +
            "accept loop runs on a source linked to that token, so it ended before its first accept");
        server.IsRunning.Should().BeTrue("the server still reports itself running, and it must also be serving");
    }

    [Fact]
    public async Task StartAsync_ShouldKeepAConnectionServed_WhenItsTokenIsCancelledWhileTheConnectionIsLive()
    {
        using var start = new CancellationTokenSource();
        var (server, port) = await StartOnLoopbackAsync(start.Token);
        await using var _ = server;
        using var announcements = new Announcements(server);
        var uuid = Guid.NewGuid();
        using var peer = await ConnectAndIdentifyAsync(port, uuid);
        var stream = await announcements.NextWithinBoundAsync();
        stream.Should().NotBeNull("premise: the connection is served before the start token is cancelled");

        await start.CancelAsync();
        await peer.GetStream().WriteAsync(BuildFrame(AudioFrameType.Audio, new byte[AudioPayloadLength]));
        await peer.GetStream().FlushAsync();
        var frame = await ReadFrameWithinBoundAsync(stream!);

        frame.Length.Should().Be(
            AudioPayloadLength,
            "a frame the peer sent after the start token was cancelled was not served: the connection's handler " +
            "runs on a source linked to that token, so the cancel tore the session down");
        server.GetStream(uuid.ToString()).Should().BeSameAs(stream, "the session is still the one registered for its channel");
        server.IsRunning.Should().BeTrue();
    }

    // ------------------------------------------------------------------------------------ helpers

    /// <summary>
    /// Starts a server on a loopback port the test can dial, handing <paramref name="token"/> to its start.
    /// <see cref="LoopbackServerBind"/> retries on a fresh server when the probed port was taken in between.
    /// </summary>
    private static Task<(AudioSocketServer Server, int Port)> StartOnLoopbackAsync(CancellationToken token) =>
        LoopbackServerBind.StartAsync(
            port => new AudioSocketServer(
                new AudioServerOptions
                {
                    AudioSocketPort = port,
                    ListenAddress = "127.0.0.1",
                    DefaultFormat = "slin16",
                    IdleTimeout = TimeSpan.FromSeconds(5),
                },
                NullLogger<AudioSocketServer>.Instance),
            server => server.StartAsync(token));

    /// <summary>Connects on the IPv4 loopback literal and sends the identification frame for <paramref name="uuid"/>.</summary>
    private static async Task<TcpClient> ConnectAndIdentifyAsync(int port, Guid uuid)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SignalTimeout);
        await client.GetStream().WriteAsync(BuildFrame(AudioFrameType.Uuid, uuid.ToByteArray(bigEndian: true)));
        await client.GetStream().FlushAsync();
        return client;
    }

    /// <summary>One byte of type, two of big-endian length, then the payload.</summary>
    private static byte[] BuildFrame(AudioFrameType type, byte[] payload)
    {
        var frame = new byte[3 + payload.Length];
        frame[0] = (byte)type;
        frame[1] = (byte)(payload.Length >> 8);
        frame[2] = (byte)payload.Length;
        payload.CopyTo(frame.AsSpan(3));
        return frame;
    }

    /// <summary>The next frame on <paramref name="stream"/>, or an empty one when none arrives within the bound.</summary>
    private static async Task<ReadOnlyMemory<byte>> ReadFrameWithinBoundAsync(IAudioStream stream)
    {
        using var hang = new CancellationTokenSource(SignalTimeout);
        try
        {
            return await stream.ReadFrameAsync(hang.Token);
        }
        catch (OperationCanceledException) when (hang.IsCancellationRequested)
        {
            return ReadOnlyMemory<byte>.Empty;
        }
    }

    /// <summary>Every stream the server announces, in announcement order.</summary>
    private sealed class Announcements : IDisposable
    {
        private readonly Channel<IAudioStream> _announced = Channel.CreateUnbounded<IAudioStream>();
        private readonly IDisposable _subscription;

        public Announcements(AudioSocketServer server) =>
            _subscription = server.OnStreamConnected.Subscribe(stream => _announced.Writer.TryWrite(stream));

        /// <summary>The next announcement, or <see langword="null"/> when none comes within the bound.</summary>
        public async Task<IAudioStream?> NextWithinBoundAsync()
        {
            using var hang = new CancellationTokenSource(SignalTimeout);
            try
            {
                return await _announced.Reader.ReadAsync(hang.Token);
            }
            catch (OperationCanceledException) when (hang.IsCancellationRequested)
            {
                return null;
            }
        }

        public void Dispose() => _subscription.Dispose();
    }
}
