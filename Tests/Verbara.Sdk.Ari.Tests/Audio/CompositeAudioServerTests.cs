using System.Reactive.Linq;
using System.Reactive.Subjects;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.Tests.Shared.Sockets;
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
        var (servers, audioSocketPort, webSocketPort) = await LoopbackServerBind.StartAsync(
            (audioSocketPort, webSocketPort) => new ServersSharingOneOptions(new AudioServerOptions
            {
                ListenAddress = "127.0.0.1",
                AudioSocketPort = audioSocketPort,
                WebSocketPort = webSocketPort,
                MaxConcurrentStreams = 1,
            }),
            pair => pair.StartAsync());
        await using var bothServers = servers;
        var audioSocket = servers.AudioSocket;

        var announced = new TaskCompletionSource<IAudioStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var announcement = audioSocket.OnStreamConnected.Subscribe(stream => announced.TrySetResult(stream));
        using var call = new System.Net.Sockets.TcpClient();
        await call.ConnectAsync(System.Net.IPAddress.Loopback, audioSocketPort);
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
            .ConnectAsync(new Uri($"ws://127.0.0.1:{webSocketPort}/ws/ch-second"), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        await connect.Should().ThrowAsync<System.Net.WebSockets.WebSocketException>(
            "MaxConcurrentStreams is documented as the limit across both protocols, so the one place is taken");
    }

    /// <summary>
    /// An AudioSocket server and a WebSocket server built on one options instance, started and
    /// disposed as one, so <see cref="LoopbackServerBind"/> can retry the pair on fresh servers when
    /// either bind finds its probed port in use.
    /// </summary>
    private sealed class ServersSharingOneOptions(AudioServerOptions options) : IAsyncDisposable
    {
        public AudioSocketServer AudioSocket { get; } =
            new(options, Microsoft.Extensions.Logging.Abstractions.NullLogger<AudioSocketServer>.Instance);

        public WebSocketAudioServer WebSocket { get; } =
            new(options, Microsoft.Extensions.Logging.Abstractions.NullLogger<WebSocketAudioServer>.Instance);

        public async ValueTask StartAsync()
        {
            await AudioSocket.StartAsync();
            await WebSocket.StartAsync();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await WebSocket.DisposeAsync();
            }
            finally
            {
                await AudioSocket.DisposeAsync();
            }
        }
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

    [Fact]
    public void OnStreamConnected_ShouldCompleteOnceAfterTheLastServerCompletes_WhenEveryServerCompletes()
    {
        using var subject1 = new Subject<IAudioStream>();
        using var subject2 = new Subject<IAudioStream>();
        var composite = new CompositeAudioServer([ServerOn(subject1), ServerOn(subject2)]);
        var completions = 0;
        using var subscription = composite.OnStreamConnected.Subscribe(_ => { }, () => completions++);

        subject1.OnCompleted();
        var afterFirst = completions;
        subject2.OnCompleted();

        afterFirst.Should().Be(0, "one server completing does not end the aggregate");
        completions.Should().Be(1, "the subscriber is completed once, after the last server completes");
    }

    [Fact]
    public void OnStreamConnected_ShouldCompleteAtOnce_WhenTheAggregateHoldsNoServer()
    {
        var composite = new CompositeAudioServer([]);
        var completions = 0;

        using var subscription = composite.OnStreamConnected.Subscribe(_ => { }, () => completions++);

        completions.Should().Be(1);
    }

    [Fact]
    public void OnStreamConnected_ShouldForwardTheFirstErrorAndUnsubscribeFromTheOtherServers_WhenOneServerFails()
    {
        using var subject1 = new Subject<IAudioStream>();
        using var subject2 = new Subject<IAudioStream>();
        var composite = new CompositeAudioServer([ServerOn(subject1), ServerOn(subject2)]);
        var received = new List<IAudioStream>();
        var errors = new List<Exception>();
        var completions = 0;
        using var subscription = composite.OnStreamConnected.Subscribe(received.Add, errors.Add, () => completions++);
        var failure = new InvalidOperationException("first");

        subject1.OnError(failure);
        var subscribedAfterError = subject2.HasObservers;
        subject2.OnNext(Substitute.For<IAudioStream>());
        subject2.OnError(new InvalidOperationException("second"));

        errors.Should().ContainSingle().Which.Should().BeSameAs(failure, "the first error wins");
        subscribedAfterError.Should().BeFalse("the first error unsubscribes the subscriber from the other servers");
        received.Should().BeEmpty("nothing is delivered after the error");
        completions.Should().Be(0);
    }

    [Fact]
    public void OnStreamConnected_ShouldNotSubscribeToLaterServers_WhenAnEarlierServerHasAlreadyFailed()
    {
        using var failed = new Subject<IAudioStream>();
        using var subject2 = new Subject<IAudioStream>();
        var failure = new InvalidOperationException("failed before the subscription");
        failed.OnError(failure);
        var composite = new CompositeAudioServer([ServerOn(failed), ServerOn(subject2)]);
        var errors = new List<Exception>();

        using var subscription = composite.OnStreamConnected.Subscribe(_ => { }, errors.Add);

        errors.Should().ContainSingle().Which.Should().BeSameAs(failure);
        subject2.HasObservers.Should().BeFalse("a subscription that has already ended subscribes to no further server");
    }

    [Fact]
    public void OnStreamConnected_ShouldUnsubscribeFromEveryServer_WhenTheSubscriptionIsDisposed()
    {
        using var subject1 = new Subject<IAudioStream>();
        using var subject2 = new Subject<IAudioStream>();
        var composite = new CompositeAudioServer([ServerOn(subject1), ServerOn(subject2)]);
        var received = new List<IAudioStream>();
        var subscription = composite.OnStreamConnected.Subscribe(received.Add);
        var subscribedBefore = subject1.HasObservers && subject2.HasObservers;

        subscription.Dispose();
        subscription.Dispose();
        subject1.OnNext(Substitute.For<IAudioStream>());
        subject2.OnNext(Substitute.For<IAudioStream>());

        subscribedBefore.Should().BeTrue();
        subject1.HasObservers.Should().BeFalse();
        subject2.HasObservers.Should().BeFalse();
        received.Should().BeEmpty();
    }

    [Fact]
    public void OnStreamConnected_ShouldKeepTheObserverSubscribed_WhenItsOnNextThrows()
    {
        using var subject1 = new Subject<IAudioStream>();
        using var subject2 = new Subject<IAudioStream>();
        var composite = new CompositeAudioServer([ServerOn(subject1), ServerOn(subject2)]);
        var calls = 0;
        using var subscription = composite.OnStreamConnected.Subscribe(_ =>
        {
            if (++calls == 1)
                throw new InvalidOperationException("observer failure");
        });

        var announce = () => subject1.OnNext(Substitute.For<IAudioStream>());
        announce.Should().Throw<InvalidOperationException>("the exception reaches the announcing server, as from a direct subscriber");
        subject1.OnNext(Substitute.For<IAudioStream>());
        subject2.OnNext(Substitute.For<IAudioStream>());

        calls.Should().Be(3, "the observer that threw stays subscribed to every server");
    }

    [Fact]
    public void OnStreamConnected_ShouldThrowArgumentNullException_WhenTheObserverIsNull()
    {
        var composite = new CompositeAudioServer([]);

        var subscribe = () => composite.OnStreamConnected.Subscribe(null!);

        subscribe.Should().Throw<ArgumentNullException>();
    }

    private static IAudioServer ServerOn(IObservable<IAudioStream> announcements)
    {
        var server = Substitute.For<IAudioServer>();
        server.OnStreamConnected.Returns(announcements);
        return server;
    }
}
