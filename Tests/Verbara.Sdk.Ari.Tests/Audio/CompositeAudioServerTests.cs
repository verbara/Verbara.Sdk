using System.Reactive.Linq;
using System.Reactive.Subjects;
using Verbara.Sdk.Ari.Audio;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace Verbara.Sdk.Ari.Tests.Audio;

public sealed class CompositeAudioServerTests
{
    [Fact]
    public async Task MaxConcurrentStreams_ShouldBoundBothServers_WhenTheyShareOneOptionsInstance()
    {
        // Arrange — one options instance for both servers, as AddVerbara registers them, with a limit
        // of one; an AudioSocket call holds that place
        var options = new AudioServerOptions
        {
            ListenAddress = "127.0.0.1",
            AudioSocketPort = FreePort(),
            WebSocketPort = FreePort(),
            MaxConcurrentStreams = 1,
        };
        await using var audioSocket = new AudioSocketServer(options, Microsoft.Extensions.Logging.Abstractions.NullLogger<AudioSocketServer>.Instance);
        await using var webSocket = new WebSocketAudioServer(options, Microsoft.Extensions.Logging.Abstractions.NullLogger<WebSocketAudioServer>.Instance);
        await audioSocket.StartAsync();
        await webSocket.StartAsync();

        var announced = new TaskCompletionSource<IAudioStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var announcement = audioSocket.OnStreamConnected.Subscribe(stream => announced.TrySetResult(stream));
        using var call = new System.Net.Sockets.TcpClient();
        await call.ConnectAsync(System.Net.IPAddress.Loopback, options.AudioSocketPort);
        var uuid = Guid.NewGuid();
        var frame = new byte[19];
        frame[0] = (byte)AudioFrameType.Uuid;
        frame[2] = 16;
        uuid.TryWriteBytes(frame.AsSpan(3), bigEndian: true, out _);
        await call.GetStream().WriteAsync(frame);
        (await announced.Task.WaitAsync(TimeSpan.FromSeconds(10))).ChannelId.Should().Be(uuid.ToString());

        // Act — a WebSocket call arrives while the AudioSocket call is live
        using var second = new System.Net.WebSockets.ClientWebSocket();
        var connect = async () => await second
            .ConnectAsync(new Uri($"ws://127.0.0.1:{options.WebSocketPort}/ws/ch-second"), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        await connect.Should().ThrowAsync<System.Net.WebSockets.WebSocketException>(
            "MaxConcurrentStreams is documented as the limit across both protocols, so the one place is taken");
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public void GetStream_ShouldReturnStreamFromFirstMatchingServer()
    {
        var mockStream = Substitute.For<IAudioStream>();
        var server1 = Substitute.For<IAudioServer>();
        var server2 = Substitute.For<IAudioServer>();
        server1.GetStream("ch-1").ReturnsNull();
        server2.GetStream("ch-1").Returns(mockStream);

        var composite = new CompositeAudioServer([server1, server2]);

        composite.GetStream("ch-1").Should().BeSameAs(mockStream);
    }

    [Fact]
    public void GetStream_ShouldReturnNull_WhenNoServerHasStream()
    {
        var server1 = Substitute.For<IAudioServer>();
        server1.GetStream("ch-1").ReturnsNull();

        var composite = new CompositeAudioServer([server1]);

        composite.GetStream("ch-1").Should().BeNull();
    }

    [Fact]
    public void ActiveStreamCount_ShouldSumAllServers()
    {
        var server1 = Substitute.For<IAudioServer>();
        var server2 = Substitute.For<IAudioServer>();
        server1.ActiveStreamCount.Returns(3);
        server2.ActiveStreamCount.Returns(5);

        var composite = new CompositeAudioServer([server1, server2]);

        composite.ActiveStreamCount.Should().Be(8);
    }

    [Fact]
    public void ActiveStreams_ShouldConcatenateAllServers()
    {
        var stream1 = Substitute.For<IAudioStream>();
        var stream2 = Substitute.For<IAudioStream>();
        var server1 = Substitute.For<IAudioServer>();
        var server2 = Substitute.For<IAudioServer>();
        server1.ActiveStreams.Returns([stream1]);
        server2.ActiveStreams.Returns([stream2]);

        var composite = new CompositeAudioServer([server1, server2]);

        composite.ActiveStreams.Should().Contain(stream1).And.Contain(stream2);
    }

    [Fact]
    public void OnStreamConnected_ShouldMergeAllServerStreams()
    {
        using var subject1 = new Subject<IAudioStream>();
        using var subject2 = new Subject<IAudioStream>();
        var server1 = Substitute.For<IAudioServer>();
        var server2 = Substitute.For<IAudioServer>();
        server1.OnStreamConnected.Returns(subject1);
        server2.OnStreamConnected.Returns(subject2);

        var composite = new CompositeAudioServer([server1, server2]);
        var received = new List<IAudioStream>();
        composite.OnStreamConnected.Subscribe(s => received.Add(s));

        var mockStream = Substitute.For<IAudioStream>();
        subject1.OnNext(mockStream);

        received.Should().ContainSingle().Which.Should().BeSameAs(mockStream);
    }
}
