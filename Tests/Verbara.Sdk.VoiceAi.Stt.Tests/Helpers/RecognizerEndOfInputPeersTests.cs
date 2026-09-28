using Verbara.Sdk.Audio;
using Verbara.Sdk.TestInfrastructure.WebSocket;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.Helpers;

/// <summary>
/// Each <see cref="RecognizerEndOfInputPeers"/> profile against the real recognizer it speaks for: the
/// peer sees the client's end of input, a progress frame and the answer reach the caller as results,
/// and the stream ends on the vendor's own ending. A profile that fails here would make a bound test
/// fail for the wrong reason.
/// </summary>
public sealed class RecognizerEndOfInputPeersTests
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    public static TheoryData<string> Clients => RecognizerEndOfInputPeers.Clients;

    [Theory]
    [MemberData(nameof(Clients))]
    public async Task StreamAsync_ShouldDeliverTheProgressAndTheAnswer_WhenThePeerSendsThemAfterTheEndOfInput(string client)
    {
        // Arrange
        await using var peer = RecognizerEndOfInputPeers.Create(client, EndOfInputPeerMode.FrameOnRequest);
        peer.Start();
        var recognizer = RecognizerEndOfInputPeers.CreateRecognizer(client, peer.Port);
        var transcripts = new List<string>();
        var run = Task.Run(async () =>
        {
            await foreach (var result in recognizer.StreamAsync(
                RecognizerEndOfInputPeers.Frames(25, CancellationToken.None), AudioFormat.Slin16Mono8kHz, CancellationToken.None))
            {
                transcripts.Add(result.Transcript);
            }
        });

        // Act
        await peer.EndOfInputSeen.WaitAsync(SignalTimeout);
        await peer.SendFrameAsync().WaitAsync(SignalTimeout);
        await peer.AnswerAsync().WaitAsync(SignalTimeout);
        await run.WaitAsync(SignalTimeout);

        // Assert
        using (new AssertionScope())
        {
            transcripts.Should().Equal("hello", "hello", "hello world");
            peer.BinaryFramesReceived.Should().BePositive("the caller's audio reached the peer before the end of input");
        }
    }
}
