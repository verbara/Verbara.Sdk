using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using FluentAssertions;
using Verbara.Sdk.TestInfrastructure.WebSocket;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.Speechmatics;

/// <summary>
/// The fake's <c>TextFrameFragmentBytes</c> knob, read by a raw <see cref="ClientWebSocket"/> so that no
/// recognizer stands between the fake and the assertion: a test of the recognizer's reassembly is only
/// worth what these establish about the frames it is given.
/// </summary>
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class SpeechmaticsFakeServerTests : IAsyncLifetime
{
    private readonly SpeechmaticsFakeServer _server = new();

    [Fact]
    public async Task Session_ShouldDeliverOneTextMessageAsMoreThanOneRead_WhenTextFrameFragmentBytesIsSet()
    {
        // Arrange
        var message = SpeechmaticsFakeServer.BuildFinalTranscriptJson("hola mundo", 0.95f);
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(message);
        _server.TextFrameFragmentBytes = 16;
        _server.Start();
        using var client = await RawWebSocketReads.ConnectAsync(_server.Port);
        await RawWebSocketReads.SendTextAsync(client, """{"message":"StartRecognition"}""");
        var greeting = await RawWebSocketReads.ReadOneMessageAsync(client);
        RawWebSocketReads.Assemble(greeting).Should().Be(_server.RecognitionStartedJson, "the greeting precedes the transcript");

        // Act
        var reads = await RawWebSocketReads.ReadOneMessageAsync(client);

        // Assert
        reads.Count.Should().BeGreaterThanOrEqualTo(2, "a message sent in 16-byte fragments is not one read");
        reads[0].EndOfMessage.Should().BeFalse("the first read is a fragment, not the whole message");
        reads.Should().OnlyContain(r => r.Type == WebSocketMessageType.Text);
        RawWebSocketReads.Assemble(reads).Should().Be(message, "the fragments together are the message, byte for byte");
    }

    /// <summary>Control: with the knob at its default the fake sends each message in one frame, as before it existed.</summary>
    [Fact]
    public async Task Session_ShouldDeliverOneTextMessageAsOneRead_WhenTextFrameFragmentBytesIsZero()
    {
        // Arrange
        var message = SpeechmaticsFakeServer.BuildFinalTranscriptJson("hola mundo", 0.95f);
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(message);
        _server.Start();
        using var client = await RawWebSocketReads.ConnectAsync(_server.Port);
        await RawWebSocketReads.SendTextAsync(client, """{"message":"StartRecognition"}""");
        var greeting = await RawWebSocketReads.ReadOneMessageAsync(client);
        RawWebSocketReads.Assemble(greeting).Should().Be(_server.RecognitionStartedJson, "the greeting precedes the transcript");

        // Act
        var reads = await RawWebSocketReads.ReadOneMessageAsync(client);

        // Assert
        reads.Should().ContainSingle().Which.EndOfMessage.Should().BeTrue();
        RawWebSocketReads.Assemble(reads).Should().Be(message);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _server.DisposeAsync();
}
