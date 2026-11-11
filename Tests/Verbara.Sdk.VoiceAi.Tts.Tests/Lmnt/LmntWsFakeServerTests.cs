using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using FluentAssertions;
using Verbara.Sdk.TestInfrastructure.WebSocket;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tts.Tests.Lmnt;

/// <summary>
/// The fake's <c>TextFrameFragmentBytes</c> knob, read by a raw <see cref="ClientWebSocket"/> so that no
/// synthesizer stands between the fake and the assertion: a test of the synthesizer's reassembly is only
/// worth what these establish about the frames it is given.
/// </summary>
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class LmntWsFakeServerTests : IAsyncLifetime
{
    private readonly LmntWsFakeServer _server = new();

    [Fact]
    public async Task Session_ShouldDeliverOneTextMessageAsMoreThanOneRead_WhenTextFrameFragmentBytesIsSet()
    {
        // Arrange
        var message = LmntWsFakeServer.ReadFrame(LmntWsFakeServer.FinishFrame);
        var audio = _server.AudioFramesToSend.Select(f => f.Length).ToList();
        _server.TextFrameFragmentBytes = 16;
        _server.Start();
        using var client = await RawWebSocketReads.ConnectAsync(_server.Port);
        await RawWebSocketReads.SendTextAsync(client, """{"eof":true}""");

        // Act
        var reads = await RawWebSocketReads.ReadUntilCloseAsync(client);

        // Assert
        var text = reads.Where(r => r.Type == WebSocketMessageType.Text).ToList();
        text.Count.Should().BeGreaterThanOrEqualTo(2, "a message sent in 16-byte fragments is not one read");
        text[0].EndOfMessage.Should().BeFalse("the first read is a fragment, not the whole message");
        RawWebSocketReads.Assemble(text).Should().Be(message, "the fragments together are the message, byte for byte");
        var binary = reads.Where(r => r.Type == WebSocketMessageType.Binary).ToList();
        binary.Should().OnlyContain(r => r.EndOfMessage, "the knob fragments text messages, never audio");
        binary.Select(r => r.Bytes.Length).Should().Equal(audio, "each audio frame still arrives whole, in order");
    }

    /// <summary>Control: with the knob at its default the fake sends each message in one frame, as before it existed.</summary>
    [Fact]
    public async Task Session_ShouldDeliverOneTextMessageAsOneRead_WhenTextFrameFragmentBytesIsZero()
    {
        // Arrange
        var message = LmntWsFakeServer.ReadFrame(LmntWsFakeServer.FinishFrame);
        _server.Start();
        using var client = await RawWebSocketReads.ConnectAsync(_server.Port);
        await RawWebSocketReads.SendTextAsync(client, """{"eof":true}""");

        // Act
        var reads = await RawWebSocketReads.ReadUntilCloseAsync(client);

        // Assert
        reads.Where(r => r.Type == WebSocketMessageType.Text).Should().ContainSingle()
            .Which.Should().Match<RawWebSocketRead>(r => r.EndOfMessage && r.Text == message);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _server.DisposeAsync();
}
