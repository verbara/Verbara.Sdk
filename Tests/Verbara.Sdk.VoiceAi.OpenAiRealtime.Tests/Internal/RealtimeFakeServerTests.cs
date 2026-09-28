using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using FluentAssertions;
using Xunit;

namespace Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Internal;

/// <summary>
/// The fake's own knobs, each driven from a plain client rather than through the bridge, so a
/// bridge test that leans on one of them stands on behaviour shown here first.
/// </summary>
/// <remarks>
/// Every wait is on a signal the fake or the socket raises, bounded by <see cref="SignalTimeout"/>;
/// nothing here paces itself on a clock.
/// </remarks>
public sealed class RealtimeFakeServerTests
{
    /// <summary>Upper bound on any single wait below. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The frame the fake waits for before it answers, as the bridge's first frame is.</summary>
    private const string SessionUpdate = """{"type":"session.update","session":{}}""";

    private const string TranscriptDelta = """{"type":"response.output_audio_transcript.delta","delta":"Hello"}""";

    [Fact]
    public async Task UpgradeHeaders_ShouldHoldTheHeadersAndTargetOfTheUpgrade_WhenAClientConnects()
    {
        // Arrange
        await using var fake = new RealtimeFakeServer { HoldOpenUntilDisposed = true };
        fake.Start();

        using var client = new ClientWebSocket();
        client.Options.SetRequestHeader("Authorization", "Bearer test-key");
        client.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

        // Act
        await client.ConnectAsync(
            new Uri($"ws://127.0.0.1:{fake.Port}/v1/realtime?model=gpt-realtime"),
            CancellationToken.None).WaitAsync(SignalTimeout);
        await SendTextAsync(client, SessionUpdate);
        await fake.SessionUpdateReceived.WaitAsync(SignalTimeout);

        // Assert — lookups by any casing, because header names are case-insensitive (RFC 9110 §5.1)
        fake.UpgradeRequestUri.Should().Be("/v1/realtime?model=gpt-realtime");
        fake.UpgradeHeaders.Should().ContainKey("authorization")
            .WhoseValue.Should().Be("Bearer test-key");
        fake.UpgradeHeaders["OPENAI-BETA"].Should().Be("realtime=v1");
    }

    [Fact]
    public async Task SendCloseAsync_ShouldArriveWithTheGivenCodeAndReason_WhenTheCodeIsAFailureCode()
    {
        // Arrange
        await using var fake = new RealtimeFakeServer { HoldOpenUntilDisposed = true };
        fake.Start();
        using var client = await ConnectAsync(fake);

        // Act
        await fake.SendCloseAsync((WebSocketCloseStatus)4004, "x");
        var received = await ReceiveAsync(client);

        // Assert
        received.MessageType.Should().Be(WebSocketMessageType.Close);
        client.CloseStatus.Should().Be((WebSocketCloseStatus)4004);
        client.CloseStatusDescription.Should().Be("x");
    }

    /// <summary>
    /// A close that carries no code, read off the wire rather than through a .NET client, because a
    /// .NET client cannot tell it from <c>1000</c> with no reason (see the next test).
    /// </summary>
    /// <remarks>
    /// The raw client speaks just enough of RFC 6455 to reach the close: the upgrade, then one masked
    /// text frame whose mask key is zero, so its payload goes out as written.
    /// </remarks>
    [Fact]
    public async Task SendCloseAsync_ShouldSendACloseFrameWithNoPayload_WhenTheStatusIsEmpty()
    {
        // Arrange
        await using var fake = new RealtimeFakeServer { HoldOpenUntilDisposed = true };
        fake.Start();

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, fake.Port).WaitAsync(SignalTimeout);
        var stream = tcp.GetStream();

        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "GET /v1/realtime HTTP/1.1\r\n" +
            "Host: 127.0.0.1\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
            "Sec-WebSocket-Version: 13\r\n\r\n"));
        await ReadUpgradeResponseAsync(stream);

        var payload = Encoding.UTF8.GetBytes(SessionUpdate);
        await stream.WriteAsync((byte[])[0x81, (byte)(0x80 | payload.Length), 0, 0, 0, 0, .. payload]);
        await fake.SessionUpdateReceived.WaitAsync(SignalTimeout);

        var created = await ReadFrameAsync(stream);
        created[0].Should().Be(0x81, "the fake greets with session.created, one final text frame");

        // Act
        await fake.SendCloseAsync(WebSocketCloseStatus.Empty, null);
        var close = await ReadFrameAsync(stream);

        // Assert — FIN + opcode 0x8, then a payload length of zero: no code, no reason
        close.Should().Equal(0x88, 0x00);
    }

    /// <summary>
    /// What a .NET client — the bridge's <see cref="ClientWebSocket"/> among them — reads from a close
    /// that carries no code: <c>NormalClosure</c> with an empty reason, not
    /// <see cref="WebSocketCloseStatus.Empty"/>.
    /// </summary>
    [Fact]
    public async Task SendCloseAsync_ShouldReadAsANormalClosureWithNoReason_WhenTheStatusIsEmpty()
    {
        // Arrange
        await using var fake = new RealtimeFakeServer { HoldOpenUntilDisposed = true };
        fake.Start();
        using var client = await ConnectAsync(fake);

        // Act
        await fake.SendCloseAsync(WebSocketCloseStatus.Empty, null);
        var received = await ReceiveAsync(client);

        // Assert
        received.MessageType.Should().Be(WebSocketMessageType.Close);
        client.CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure);
        client.CloseStatusDescription.Should().BeEmpty();
    }

    [Fact]
    public async Task SendCloseAsync_ShouldRefuse_WhenEventsToSendIsNotEmpty()
    {
        // Arrange
        await using var fake = new RealtimeFakeServer();
        fake.EventsToSend.Add(TranscriptDelta);

        // Act
        var act = () => fake.SendCloseAsync(WebSocketCloseStatus.NormalClosure, "done");

        // Assert — the burst would be a second writer on the same socket
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task HoldOpenUntilClientCloses_ShouldAnswerTheClientsCloseAtOnce_WhenTheClientCloses()
    {
        // Arrange
        await using var fake = new RealtimeFakeServer { HoldOpenUntilClientCloses = true };
        fake.Start();
        using var client = await ConnectAsync(fake);

        // Act
        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "caller hung up", CancellationToken.None)
            .WaitAsync(SignalTimeout);
        var clientClose = await fake.ClientCloseReceived.WaitAsync(SignalTimeout);
        var received = await ReceiveAsync(client);

        // Assert
        clientClose.Should().Be(WebSocketCloseStatus.NormalClosure);
        received.MessageType.Should().Be(WebSocketMessageType.Close);
        client.CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure);
        client.State.Should().Be(WebSocketState.Closed);
    }

    /// <summary>
    /// The order on the wire is the proof that nothing answered early: one socket delivers in order,
    /// so a close sent on the client's close would reach the client before the frame sent after it,
    /// and would also claim the one close this session may send, so the answer below would be refused.
    /// </summary>
    [Fact]
    public async Task AnswerClientCloseOnRequest_ShouldKeepSendingAndAnswerOnlyWhenAsked_WhenTheClientCloses()
    {
        // Arrange
        await using var fake = new RealtimeFakeServer { AnswerClientCloseOnRequest = true };
        fake.Start();
        using var client = await ConnectAsync(fake);

        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
            .WaitAsync(SignalTimeout);
        (await fake.ClientCloseReceived.WaitAsync(SignalTimeout)).Should().Be(WebSocketCloseStatus.NormalClosure);

        // Act — a vendor that keeps talking after the client's close
        await fake.SendEventAsync(TranscriptDelta);
        var afterClose = await ReceiveAsync(client);

        await fake.AnswerClientCloseAsync((WebSocketCloseStatus)4000, "x");
        var answer = await ReceiveAsync(client);

        // Assert
        afterClose.MessageType.Should().Be(
            WebSocketMessageType.Text, "the frame sent after the client's close arrives, and no close came first");
        afterClose.Text.Should().Be(TranscriptDelta);
        answer.MessageType.Should().Be(WebSocketMessageType.Close);
        client.CloseStatus.Should().Be((WebSocketCloseStatus)4000);
        client.CloseStatusDescription.Should().Be("x");

        var secondAnswer = () => fake.AnswerClientCloseAsync(WebSocketCloseStatus.NormalClosure, "again");
        await secondAnswer.Should().ThrowAsync<InvalidOperationException>("a session sends one close");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Connects, sends the frame the fake answers on, and reads its greeting, leaving the session
    /// where every knob above takes over: past the (here empty) burst.
    /// </summary>
    private static async Task<ClientWebSocket> ConnectAsync(RealtimeFakeServer fake)
    {
        var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{fake.Port}/v1/realtime"), CancellationToken.None)
            .WaitAsync(SignalTimeout);
        await SendTextAsync(client, SessionUpdate);
        await fake.SessionUpdateReceived.WaitAsync(SignalTimeout);

        var created = await ReceiveAsync(client);
        created.Text.Should().Contain("\"session.created\"");
        return client;
    }

    private static Task SendTextAsync(ClientWebSocket client, string json) =>
        client.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None)
            .WaitAsync(SignalTimeout);

    private static async Task<(WebSocketMessageType MessageType, string Text)> ReceiveAsync(ClientWebSocket client)
    {
        var buffer = new byte[4096];
        var result = await client.ReceiveAsync(buffer, CancellationToken.None).WaitAsync(SignalTimeout);
        return (result.MessageType, Encoding.UTF8.GetString(buffer, 0, result.Count));
    }

    /// <summary>Reads the upgrade response one byte at a time, so no frame byte after it is consumed.</summary>
    private static async Task ReadUpgradeResponseAsync(NetworkStream stream)
    {
        var response = new StringBuilder();
        var one = new byte[1];
        while (!response.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            await stream.ReadExactlyAsync(one).AsTask().WaitAsync(SignalTimeout);
            response.Append((char)one[0]);
        }

        response.ToString().Should().StartWith("HTTP/1.1 101");
    }

    /// <summary>
    /// Reads one unmasked server frame with a payload under 126 bytes — header and payload, as sent.
    /// </summary>
    private static async Task<byte[]> ReadFrameAsync(NetworkStream stream)
    {
        var header = new byte[2];
        await stream.ReadExactlyAsync(header).AsTask().WaitAsync(SignalTimeout);

        var length = header[1] & 0x7F;
        length.Should().BeLessThan(126, "every frame this reader meets is short");

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload).AsTask().WaitAsync(SignalTimeout);
        return [.. header, .. payload];
    }
}
