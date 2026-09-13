using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Verbara.Sdk.Ari.Audio;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Verbara.Sdk.Ari.Tests.Audio;

public class WebSocketAudioServerTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ReadUpgradeRequestAsync_ShouldParseWsKeyAndChannelId()
    {
        const string request = "GET /ws/ch-12345 HTTP/1.1\r\n" +
                               "Host: localhost:9093\r\n" +
                               "Upgrade: websocket\r\n" +
                               "Connection: Upgrade\r\n" +
                               "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
                               "Sec-WebSocket-Version: 13\r\n\r\n";

        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(request));
        var (wsKey, channelId) = await WebSocketAudioServer.ReadUpgradeRequestAsync(stream, CancellationToken.None);

        wsKey.Should().Be("dGhlIHNhbXBsZSBub25jZQ==");
        channelId.Should().Be("ch-12345");
    }

    [Fact]
    public async Task ReadUpgradeRequestAsync_ShouldHandleSimplePath()
    {
        const string request = "GET /my-channel-id HTTP/1.1\r\n" +
                               "Sec-WebSocket-Key: abc123==\r\n\r\n";

        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(request));
        var (wsKey, channelId) = await WebSocketAudioServer.ReadUpgradeRequestAsync(stream, CancellationToken.None);

        wsKey.Should().Be("abc123==");
        channelId.Should().Be("my-channel-id");
    }

    [Fact]
    public async Task ReadUpgradeRequestAsync_ShouldReturnNull_ForEmptyStream()
    {
        using var stream = new MemoryStream([]);
        var (wsKey, channelId) = await WebSocketAudioServer.ReadUpgradeRequestAsync(stream, CancellationToken.None);

        wsKey.Should().BeNull();
        channelId.Should().BeNull();
    }

    [Fact]
    public async Task SendUpgradeResponseAsync_ShouldWriteHttp101()
    {
        using var stream = new MemoryStream();

        await WebSocketAudioServer.SendUpgradeResponseAsync(stream, "dGhlIHNhbXBsZSBub25jZQ==", CancellationToken.None);

        stream.Position = 0;
        var response = Encoding.ASCII.GetString(stream.ToArray());
        response.Should().StartWith("HTTP/1.1 101 Switching Protocols");
        response.Should().Contain("Upgrade: websocket");
        response.Should().Contain("Connection: Upgrade");
        response.Should().Contain("Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=");
    }

    [Fact]
    public void AudioServerOptions_ShouldHaveCorrectDefaults()
    {
        var options = new AudioServerOptions();

        options.AudioSocketPort.Should().Be(9092);
        options.WebSocketPort.Should().Be(9093);
        options.ListenAddress.Should().Be("0.0.0.0");
        options.MaxConcurrentStreams.Should().Be(1000);
        options.DefaultFormat.Should().Be("slin16");
        options.IdleTimeout.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldRemoveAndDisposeSession_WhenClientCloses()
    {
        var port = GetFreePort();
        await using var server = await StartServerAsync(port);
        var disposedSignal = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var tracking = server.OnStreamConnected.Subscribe(stream => disposedSignal.TrySetResult(WhenDisposed(stream)));

        using var client = new ClientWebSocket();
        await client.ConnectAsync(ChannelUri(port, "ch-close"), CancellationToken.None);
        var disposed = await disposedSignal.Task.WaitAsync(WaitLimit);
        server.ActiveStreamCount.Should().Be(1);

        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await disposed.WaitAsync(WaitLimit);

        server.ActiveStreamCount.Should().Be(0);
        server.GetStream("ch-close").Should().BeNull();
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldRemoveAndDisposeSession_WhenStreamConnectedSubscriberThrows()
    {
        var port = GetFreePort();
        await using var server = await StartServerAsync(port);
        var disposedSignal = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Observers run in subscription order: this one sees the session before the next one throws.
        using var tracking = server.OnStreamConnected.Subscribe(stream => disposedSignal.TrySetResult(WhenDisposed(stream)));
        using var throwing = server.OnStreamConnected.Subscribe(static _ => throw new InvalidOperationException("subscriber failure"));

        using var client = new ClientWebSocket();
        await client.ConnectAsync(ChannelUri(port, "ch-throw"), CancellationToken.None);
        var disposed = await disposedSignal.Task.WaitAsync(WaitLimit);

        // The client stays open, so only the failed connection's own cleanup can dispose the session.
        await disposed.WaitAsync(WaitLimit);

        server.ActiveStreamCount.Should().Be(0);
        server.GetStream("ch-throw").Should().BeNull();
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldKeepFirstSession_WhenDuplicateChannelConnectionCloses()
    {
        var port = GetFreePort();
        await using var server = await StartServerAsync(port);
        var firstSignal = new TaskCompletionSource<IAudioStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDisposedSignal = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var tracking = server.OnStreamConnected.Subscribe(stream =>
        {
            if (!firstSignal.TrySetResult(stream))
                secondDisposedSignal.TrySetResult(WhenDisposed(stream));
        });

        using var firstClient = new ClientWebSocket();
        await firstClient.ConnectAsync(ChannelUri(port, "ch-shared"), CancellationToken.None);
        var first = await firstSignal.Task.WaitAsync(WaitLimit);

        // Same channel id: this session loses TryAdd and is never registered.
        using var secondClient = new ClientWebSocket();
        await secondClient.ConnectAsync(ChannelUri(port, "ch-shared"), CancellationToken.None);
        var secondDisposed = await secondDisposedSignal.Task.WaitAsync(WaitLimit);
        await secondClient.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await secondDisposed.WaitAsync(WaitLimit);

        server.ActiveStreamCount.Should().Be(1);
        server.GetStream("ch-shared").Should().BeSameAs(first);
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldCloseClient_WhenUpgradeRequestHasNoKey()
    {
        var port = GetFreePort();
        await using var server = await StartServerAsync(port);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("GET /ws/ch-nokey HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"));

        // No Sec-WebSocket-Key: the server answers nothing and closes the connection.
        var read = await stream.ReadAsync(new byte[64]).AsTask().WaitAsync(WaitLimit);

        read.Should().Be(0);
        server.ActiveStreamCount.Should().Be(0);
    }

    [Fact]
    public async Task StopAsync_ShouldDisposeSession_WhenClientStillConnected()
    {
        var port = GetFreePort();
        await using var server = await StartServerAsync(port);
        var disposedSignal = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var tracking = server.OnStreamConnected.Subscribe(stream => disposedSignal.TrySetResult(WhenDisposed(stream)));

        using var client = new ClientWebSocket();
        await client.ConnectAsync(ChannelUri(port, "ch-stop"), CancellationToken.None);
        var disposed = await disposedSignal.Task.WaitAsync(WaitLimit);

        // The client stays open, so the session's own connection handler disposes it once
        // cancellation reaches the handler. StopAsync has to wait for that handler, so the
        // session is already disposed when StopAsync returns, not merely on its way there.
        await server.StopAsync();

        disposed.IsCompleted.Should().BeTrue("StopAsync returns only after the connected session is disposed");
        server.ActiveStreamCount.Should().Be(0);
        server.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task StopAsync_ShouldReturnWithSessionDisposed_WhenTokenIsCancelledWhileSubscriberBlocks()
    {
        var port = GetFreePort();
        await using var server = await StartServerAsync(port);
        var disposedSignal = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriberEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriberLeft = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        // Observers run in subscription order: the first sees the session, the second parks the
        // connection's handler inside OnNext until the test releases it.
        using var tracking = server.OnStreamConnected.Subscribe(stream => disposedSignal.TrySetResult(WhenDisposed(stream)));
        using var blocking = server.OnStreamConnected.Subscribe(_ =>
        {
            try
            {
                subscriberEntered.TrySetResult();
                release.Wait();
            }
            finally
            {
                subscriberLeft.TrySetResult();
            }
        });

        try
        {
            using var client = new ClientWebSocket();
            await client.ConnectAsync(ChannelUri(port, "ch-blocked"), CancellationToken.None);
            await subscriberEntered.Task.WaitAsync(WaitLimit);
            var disposed = await disposedSignal.Task.WaitAsync(WaitLimit);

            using var stopToken = new CancellationTokenSource();
            var stop = server.StopAsync(stopToken.Token).AsTask();
            await stopToken.CancelAsync();
            // Bounded, so a StopAsync that ignores its token fails here instead of hanging the run.
            await stop.WaitAsync(WaitLimit);

            subscriberLeft.Task.IsCompleted.Should().BeFalse("the subscriber is still blocked when StopAsync returns");
            disposed.IsCompleted.Should().BeTrue("StopAsync disposes the registered session once its wait is cancelled");
            server.ActiveStreamCount.Should().Be(0);
            server.IsRunning.Should().BeFalse();
        }
        finally
        {
            release.Set();
            if (subscriberEntered.Task.IsCompleted)
                await subscriberLeft.Task.WaitAsync(WaitLimit);
        }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<WebSocketAudioServer> StartServerAsync(int port)
    {
        var server = new WebSocketAudioServer(
            new AudioServerOptions { ListenAddress = "127.0.0.1", WebSocketPort = port },
            NullLogger<WebSocketAudioServer>.Instance);
        await server.StartAsync();
        return server;
    }

    private static Uri ChannelUri(int port, string channelId) => new($"ws://127.0.0.1:{port}/ws/{channelId}");

    /// <summary>
    /// Completes when the session behind <paramref name="stream"/> is disposed: its state subject
    /// completes only from DisposeAsync. Call it from inside the OnStreamConnected emission, before
    /// the connection's handler can have disposed the session.
    /// </summary>
    private static Task WhenDisposed(IAudioStream stream)
    {
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stream.StateChanges.Subscribe(static _ => { }, () => disposed.TrySetResult());
        return disposed.Task;
    }
}
