using System.Net.WebSockets;
using System.Threading.Channels;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.Tests.Shared.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Verbara.Sdk.Ari.Tests.Audio;

/// <summary>
/// The token handed to the WebSocket audio server's start bounds the start, not the server's life. Cancelled once
/// the start returned, or already cancelled when it began, it neither stops the accept loop nor ends a connection
/// being served, and the server keeps reporting itself running. Only the server's own stop ends the loop; that
/// side is held by <see cref="WebSocketAudioServerTests.StopAsync_ShouldReportNoAcceptFailure_WhenARealListenerStopAbortsTheAccept"/>.
/// </summary>
public sealed class WebSocketAudioServerStartTokenTests
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>One 20 ms frame of 16 kHz signed linear audio.</summary>
    private const int AudioPayloadLength = 640;

    [Fact]
    public async Task StartAsync_ShouldKeepServing_WhenItsTokenIsCancelledAfterTheStart()
    {
        using var start = new CancellationTokenSource();
        var (server, port) = await StartOnLoopbackAsync(start.Token);
        await using var _ = server;
        using var announcements = new Announcements(server);

        await start.CancelAsync();
        using var peer = await TryUpgradeAsync(port, "ch-after-start");
        var announced = peer is null ? null : await announcements.NextWithinBoundAsync();

        announced?.ChannelId.Should().Be("ch-after-start");
        (announced is not null).Should().BeTrue(
            "a peer's WebSocket upgrade was not served after the start token was cancelled (upgrade answered: " +
            $"{peer is not null}): the accept loop runs on a source linked to that token, so the cancel ended it");
        server.IsRunning.Should().BeTrue("the server still reports itself running, and it must also be serving");
    }

    [Fact]
    public async Task StartAsync_ShouldServe_WhenItsTokenIsAlreadyCancelled()
    {
        var (server, port) = await StartOnLoopbackAsync(new CancellationToken(canceled: true));
        await using var _ = server;
        using var announcements = new Announcements(server);

        using var peer = await TryUpgradeAsync(port, "ch-already-cancelled");
        var announced = peer is null ? null : await announcements.NextWithinBoundAsync();

        announced?.ChannelId.Should().Be("ch-already-cancelled");
        (announced is not null).Should().BeTrue(
            "a peer's WebSocket upgrade was not served after a start whose token was already cancelled (upgrade " +
            $"answered: {peer is not null}): the accept loop runs on a source linked to that token, so it ended " +
            "before its first accept");
        server.IsRunning.Should().BeTrue("the server still reports itself running, and it must also be serving");
    }

    [Fact]
    public async Task StartAsync_ShouldKeepAConnectionServed_WhenItsTokenIsCancelledWhileTheConnectionIsLive()
    {
        using var start = new CancellationTokenSource();
        var (server, port) = await StartOnLoopbackAsync(start.Token);
        await using var _ = server;
        using var announcements = new Announcements(server);
        using var peer = await TryUpgradeAsync(port, "ch-live");
        peer.Should().NotBeNull("premise: the upgrade is answered before the start token is cancelled");
        var stream = await announcements.NextWithinBoundAsync();
        stream.Should().NotBeNull("premise: the connection is served before the start token is cancelled");

        await start.CancelAsync();
        await peer!.SendAsync(new byte[AudioPayloadLength], WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
        var frame = await ReadFrameWithinBoundAsync(stream!);

        frame.Length.Should().Be(
            AudioPayloadLength,
            "a frame the peer sent after the start token was cancelled was not served: the connection's handler " +
            "runs on a source linked to that token, so the cancel tore the session down");
        server.GetStream("ch-live").Should().BeSameAs(stream, "the session is still the one registered for its channel");
        server.IsRunning.Should().BeTrue();
    }

    // ------------------------------------------------------------------------------------ helpers

    /// <summary>
    /// Starts a server on a loopback port the test can dial, handing <paramref name="token"/> to its start.
    /// <see cref="LoopbackServerBind"/> retries on a fresh server when the probed port was taken in between.
    /// </summary>
    private static Task<(WebSocketAudioServer Server, int Port)> StartOnLoopbackAsync(CancellationToken token) =>
        LoopbackServerBind.StartAsync(
            port => new WebSocketAudioServer(
                new AudioServerOptions
                {
                    ListenAddress = "127.0.0.1",
                    WebSocketPort = port,
                    IdleTimeout = TimeSpan.FromSeconds(60),

                    // MediaStartTimeout = Zero: these are tests of the zero bound's exception (no wait). Their subject is the
                    // waiting list, the count, the limit, the stop, the restart or the ending, and their clients send no
                    // MEDIA_START, so each connection is announced at once under its path segment, as before the re-key;
                    // without it each would wait out the 2 s default before its announcement.
                    MediaStartTimeout = TimeSpan.Zero,
                },
                NullLogger<WebSocketAudioServer>.Instance),
            server => server.StartAsync(token));

    /// <summary>
    /// Opens a WebSocket to <paramref name="channelId"/>'s path on the IPv4 loopback literal; <see langword="null"/>
    /// when the upgrade is not answered within the bound. A listener nobody accepts on still completes the TCP
    /// handshake into its backlog, so only the upgrade tells a served connection from an unserved one.
    /// </summary>
    private static async Task<ClientWebSocket?> TryUpgradeAsync(int port, string channelId)
    {
        var client = new ClientWebSocket();
        using var hang = new CancellationTokenSource(SignalTimeout);
        try
        {
            await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws/{channelId}"), hang.Token);
            return client;
        }
        catch (OperationCanceledException) when (hang.IsCancellationRequested)
        {
            client.Dispose();
            return null;
        }
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

        public Announcements(WebSocketAudioServer server) =>
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
