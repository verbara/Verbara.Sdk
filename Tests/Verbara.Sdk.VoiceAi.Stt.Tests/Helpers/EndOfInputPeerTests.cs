using System.Net.WebSockets;
using System.Text;
using Verbara.Sdk.TestInfrastructure.WebSocket;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.Helpers;

/// <summary>
/// <see cref="EndOfInputPeer"/>, one knob at a time, against a raw <see cref="ClientWebSocket"/>: what
/// it sends before the end of input, what each mode does once the end of input has arrived, and the
/// two signals a test waits on.
/// </summary>
/// <remarks>
/// The peer lives in <c>Verbara.Sdk.TestInfrastructure</c>, which has no test project of its own. The
/// recognizer suites are its first users, so its tests live here. Nothing here waits on the clock:
/// every step ends on a frame the client receives or a signal the peer raises.
/// </remarks>
public sealed class EndOfInputPeerTests
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AnswerAsync_ShouldSendTheAnswerThenClose1000_WhenTheModeIsAnswerOnRequest()
    {
        // Arrange — a greeting, a reply to one client frame, a frame on the end of input, and an answer
        await using var peer = new EndOfInputPeer(EndOfInputPeerMode.AnswerOnRequest)
        {
            Preamble = [PeerFrame.Text("hello")],
            Replies = static t => t == "ping" ? [PeerFrame.Text("pong")] : [],
            IsEndOfInput = static t => t == "eof",
            OnEndOfInput = [PeerFrame.Text("chunk")],
            Answer = [PeerFrame.Text("final")],
        };
        peer.Start();
        using var client = new ClientWebSocket();
        await ConnectAsync(client, peer.Port);

        var greeting = await ReceiveAsync(client);
        await SendTextAsync(client, "ping");
        var reply = await ReceiveAsync(client);
        await client.SendAsync(new byte[320], WebSocketMessageType.Binary, true, CancellationToken.None);
        await SendTextAsync(client, "eof");
        await peer.EndOfInputSeen.WaitAsync(SignalTimeout);
        var onEndOfInput = await ReceiveAsync(client);

        // Act
        await peer.AnswerAsync().WaitAsync(SignalTimeout);
        var answer = await ReceiveAsync(client);
        var close = await ReceiveAsync(client);

        // Assert
        using (new AssertionScope())
        {
            greeting.Should().Be(Text("hello"));
            reply.Should().Be(Text("pong"));
            onEndOfInput.Should().Be(Text("chunk"));
            answer.Should().Be(Text("final"));
            close.Type.Should().Be(WebSocketMessageType.Close);
            client.CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure);
            peer.TextFramesReceived.Should().Be(2, "the ping and the end of input");
            peer.BinaryFramesReceived.Should().Be(1);
        }
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldSendNothingAfterTheEndOfInput_WhenTheModeIsNeverAnswer()
    {
        // Arrange
        await using var peer = new EndOfInputPeer(EndOfInputPeerMode.NeverAnswer)
        {
            IsEndOfInput = static t => t == "eof",
            OnEndOfInput = [PeerFrame.Text("chunk")],
            Answer = [PeerFrame.Text("final")],
        };
        peer.Start();
        using var client = new ClientWebSocket();
        await ConnectAsync(client, peer.Port);
        await SendTextAsync(client, "eof");
        await peer.EndOfInputSeen.WaitAsync(SignalTimeout);
        var onEndOfInput = await ReceiveAsync(client);

        // Act — the client gives up and closes; a vendor gone silent answers not even that
        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await peer.ClientCloseSeen.WaitAsync(SignalTimeout);
        var ending = await Record.ExceptionAsync(() => ReceiveAsync(client));

        // Assert — the only frame after the end of input was the one sent on it
        using (new AssertionScope())
        {
            onEndOfInput.Should().Be(Text("chunk"));
            ending.Should().BeOfType<WebSocketException>("the peer sent no frame and no close before the connection went");
        }
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldDropTheConnectionWithoutAClose_WhenTheModeIsDropTcp()
    {
        // Arrange
        await using var peer = new EndOfInputPeer(EndOfInputPeerMode.DropTcp)
        {
            IsEndOfInput = static t => t == "eof",
            OnEndOfInput = [PeerFrame.Text("chunk")],
        };
        peer.Start();
        using var client = new ClientWebSocket();
        await ConnectAsync(client, peer.Port);

        // Act
        await SendTextAsync(client, "eof");
        await peer.EndOfInputSeen.WaitAsync(SignalTimeout);
        var onEndOfInput = await ReceiveAsync(client);
        var ending = await Record.ExceptionAsync(() => ReceiveAsync(client));

        // Assert
        using (new AssertionScope())
        {
            onEndOfInput.Should().Be(Text("chunk"));
            ending.Should().BeOfType<WebSocketException>()
                .Which.WebSocketErrorCode.Should().Be(WebSocketError.ConnectionClosedPrematurely);
        }
    }

    [Fact]
    public async Task SendFrameAsync_ShouldSendOneRoundOfProgressPerCall_WhenTheModeIsFrameOnRequest()
    {
        // Arrange
        await using var peer = new EndOfInputPeer(EndOfInputPeerMode.FrameOnRequest)
        {
            IsEndOfInput = static t => t == "eof",
            OnEndOfInput = [PeerFrame.Text("chunk")],
            Progress = [PeerFrame.Binary(4)],
            Answer = [PeerFrame.Text("final")],
        };
        peer.Start();
        using var client = new ClientWebSocket();
        await ConnectAsync(client, peer.Port);
        await SendTextAsync(client, "eof");
        await peer.EndOfInputSeen.WaitAsync(SignalTimeout);
        var onEndOfInput = await ReceiveAsync(client);

        // Act
        await peer.SendFrameAsync().WaitAsync(SignalTimeout);
        var first = await ReceiveAsync(client);
        await peer.SendFrameAsync().WaitAsync(SignalTimeout);
        var second = await ReceiveAsync(client);
        await peer.AnswerAsync().WaitAsync(SignalTimeout);
        var answer = await ReceiveAsync(client);
        var close = await ReceiveAsync(client);

        // Assert
        using (new AssertionScope())
        {
            onEndOfInput.Should().Be(Text("chunk"));
            first.Should().Be(new Received(WebSocketMessageType.Binary, "\0\0\0\0"));
            second.Should().Be(new Received(WebSocketMessageType.Binary, "\0\0\0\0"));
            answer.Should().Be(Text("final"));
            close.Type.Should().Be(WebSocketMessageType.Close);
            client.CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure);
        }
    }

    [Fact]
    public async Task AnswerAsync_ShouldAnswerTheCloseAlone_WhenTheEndOfInputIsTheClientsCloseFrame()
    {
        // Arrange — no in-band matcher: the client's close frame is its end of input
        await using var peer = new EndOfInputPeer(EndOfInputPeerMode.AnswerOnRequest)
        {
            Answer = [PeerFrame.Text("final")],
        };
        peer.Start();
        using var client = new ClientWebSocket();
        await ConnectAsync(client, peer.Port);
        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await peer.EndOfInputSeen.WaitAsync(SignalTimeout);
        await peer.ClientCloseSeen.WaitAsync(SignalTimeout);

        // Act
        await peer.AnswerAsync().WaitAsync(SignalTimeout);
        var close = await ReceiveAsync(client);

        // Assert — the answer to a close is the close
        using (new AssertionScope())
        {
            close.Type.Should().Be(WebSocketMessageType.Close);
            client.CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure);
            client.State.Should().Be(WebSocketState.Closed);
        }
    }

    [Theory]
    [InlineData(EndOfInputPeerMode.NeverAnswer)]
    [InlineData(EndOfInputPeerMode.DropTcp)]
    public async Task AnswerAsync_ShouldThrow_WhenTheModeNeverAnswers(EndOfInputPeerMode mode)
    {
        await using var peer = new EndOfInputPeer(mode);

        await FluentActions.Awaiting(peer.AnswerAsync).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(peer.SendFrameAsync).Should().ThrowAsync<InvalidOperationException>();
    }

    internal readonly record struct Received(WebSocketMessageType Type, string Text);

    private static Received Text(string text) => new(WebSocketMessageType.Text, text);

    internal static Task ConnectAsync(ClientWebSocket client, int port)
        => client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), CancellationToken.None).WaitAsync(SignalTimeout);

    private static Task SendTextAsync(ClientWebSocket client, string text)
        => client.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>One whole message, or the close, bounded by <see cref="SignalTimeout"/>.</summary>
    internal static async Task<Received> ReceiveAsync(ClientWebSocket client)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await client.ReceiveAsync(buffer.AsMemory(), CancellationToken.None).AsTask().WaitAsync(SignalTimeout);
            if (result.MessageType == WebSocketMessageType.Close)
                return new Received(WebSocketMessageType.Close, "");

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return new Received(result.MessageType, Encoding.UTF8.GetString(message.ToArray()));
        }
    }
}
