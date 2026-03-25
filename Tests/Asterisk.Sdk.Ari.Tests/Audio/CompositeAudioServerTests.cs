using System.Reactive.Linq;
using System.Reactive.Subjects;
using Asterisk.Sdk.Ari.Audio;
using FluentAssertions;
using NSubstitute;

namespace Asterisk.Sdk.Ari.Tests.Audio;

public sealed class CompositeAudioServerTests
{
    [Fact]
    public void GetStream_ShouldReturnStreamFromFirstMatchingServer()
    {
        var mockStream = Substitute.For<IAudioStream>();
        var server1 = Substitute.For<IAudioServer>();
        var server2 = Substitute.For<IAudioServer>();
        server1.GetStream("ch-1").Returns((IAudioStream?)null);
        server2.GetStream("ch-1").Returns(mockStream);

        var composite = new CompositeAudioServer([server1, server2]);

        composite.GetStream("ch-1").Should().BeSameAs(mockStream);
    }

    [Fact]
    public void GetStream_ShouldReturnNull_WhenNoServerHasStream()
    {
        var server1 = Substitute.For<IAudioServer>();
        server1.GetStream("ch-1").Returns((IAudioStream?)null);

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
        var subject1 = new Subject<IAudioStream>();
        var subject2 = new Subject<IAudioStream>();
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
