using FluentAssertions;
using Verbara.Sdk.Ari.Audio;

namespace Verbara.Sdk.Ari.Tests.Audio;

/// <summary>
/// The members <see cref="IChanWebSocketSession"/> gained after 2.8.0 are default interface members: an implementation
/// written against 2.8.0 compiles unchanged and gets a default that reports nothing or throws naming the method.
/// </summary>
public class ChanWebSocketSessionDefaultMembersTests
{
    [Fact]
    public void MediaStart_ShouldReadNull_WhenAnImplementationHasOnlyThe280Members()
    {
        // Arrange — an implementation written against 2.8.0 compiles unchanged: MediaStart is a default member
        IChanWebSocketSession session = new SessionWrittenAgainst280();

        // Act
        var mediaStart = session.MediaStart;

        // Assert — the default never reports a MEDIA_START
        mediaStart.Should().BeNull();
    }

    [Theory]
    [InlineData(nameof(IChanWebSocketSession.SendFlushMediaAsync))]
    [InlineData(nameof(IChanWebSocketSession.SendPauseMediaAsync))]
    [InlineData(nameof(IChanWebSocketSession.SendContinueMediaAsync))]
    [InlineData(nameof(IChanWebSocketSession.SendGetStatusAsync))]
    [InlineData(nameof(IChanWebSocketSession.SendStartMediaBufferingAsync))]
    [InlineData(nameof(IChanWebSocketSession.SendStopMediaBufferingAsync))]
    [InlineData(nameof(IChanWebSocketSession.SendReportQueueDrainedAsync))]
    public void AddedCommands_ShouldThrowNotSupportedExceptionNamingTheMethod_WhenAnImplementationHasOnlyThe280Members(string method)
    {
        // Arrange — an implementation written against 2.8.0 compiles unchanged: every added command is a default member
        IChanWebSocketSession session = new SessionWrittenAgainst280();
        Func<ValueTask> call = method switch
        {
            nameof(IChanWebSocketSession.SendFlushMediaAsync) => () => session.SendFlushMediaAsync(),
            nameof(IChanWebSocketSession.SendPauseMediaAsync) => () => session.SendPauseMediaAsync(),
            nameof(IChanWebSocketSession.SendContinueMediaAsync) => () => session.SendContinueMediaAsync(),
            nameof(IChanWebSocketSession.SendGetStatusAsync) => () => session.SendGetStatusAsync(),
            nameof(IChanWebSocketSession.SendStartMediaBufferingAsync) => () => session.SendStartMediaBufferingAsync(),
            nameof(IChanWebSocketSession.SendStopMediaBufferingAsync) => () => session.SendStopMediaBufferingAsync("b4-0"),
            _ => () => session.SendReportQueueDrainedAsync(),
        };

        // Act — never awaited: the default body throws before any ValueTask exists, so nothing can be written
#pragma warning disable CA2012 // the discarded ValueTask is never created: the throw is the point
        var act = () => { _ = call(); };
#pragma warning restore CA2012

        // Assert
        act.Should().Throw<NotSupportedException>().Which.Message.Should().Contain(method);
    }

    /// <summary>
    /// The members <see cref="IChanWebSocketSession"/> and <see cref="IAudioStream"/> had in 2.8.0, less the two
    /// flow-control sends 2.11.0 removed.
    /// </summary>
    private sealed class SessionWrittenAgainst280 : IChanWebSocketSession
    {
        public string ChannelId => "c";

        public string Format => "slin16";

        public int SampleRate => 16000;

        public bool IsConnected => false;

        public IObservable<AudioStreamState> StateChanges => throw new NotSupportedException();

        public IObservable<ChanWebSocketControlMessage> ControlMessages => throw new NotSupportedException();

        public ValueTask<ReadOnlyMemory<byte>> ReadFrameAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask WriteFrameAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask SendMarkAsync(string mark, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask SendSetMediaDirectionAsync(
            ChanWebSocketMediaDirection direction,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
