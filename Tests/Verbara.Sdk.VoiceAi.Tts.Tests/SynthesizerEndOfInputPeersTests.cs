using Verbara.Sdk.Audio;
using Verbara.Sdk.TestInfrastructure.WebSocket;
using FluentAssertions;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tts.Tests;

/// <summary>
/// Each <see cref="SynthesizerEndOfInputPeers"/> profile against the real synthesizer it speaks for:
/// the peer sees the client's end of input, the audio sent on it and a progress round reach the caller,
/// and the stream ends on the vendor's terminal frame. A profile that fails here would make a bound
/// test fail for the wrong reason.
/// </summary>
public sealed class SynthesizerEndOfInputPeersTests
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    public static TheoryData<string> Clients => SynthesizerEndOfInputPeers.Clients;

    [Theory]
    [MemberData(nameof(Clients))]
    public async Task SynthesizeAsync_ShouldDeliverTheAudioAndComplete_WhenThePeerAnswersAfterAProgressFrame(string client)
    {
        // Arrange
        await using var peer = SynthesizerEndOfInputPeers.Create(client, EndOfInputPeerMode.FrameOnRequest);
        peer.Start();
        var synthesizer = SynthesizerEndOfInputPeers.CreateSynthesizer(client, peer.Port);
        var chunks = 0;
        var run = Task.Run(async () =>
        {
            await foreach (var _ in synthesizer.SynthesizeAsync("hello world", AudioFormat.Slin16Mono8kHz, CancellationToken.None))
            {
                chunks++;
            }
        });

        // Act
        await peer.EndOfInputSeen.WaitAsync(SignalTimeout);
        await peer.SendFrameAsync().WaitAsync(SignalTimeout);
        await peer.AnswerAsync().WaitAsync(SignalTimeout);
        await run.WaitAsync(SignalTimeout);

        // Assert — the frame sent on the end of input and the progress round
        chunks.Should().Be(2);
    }
}
