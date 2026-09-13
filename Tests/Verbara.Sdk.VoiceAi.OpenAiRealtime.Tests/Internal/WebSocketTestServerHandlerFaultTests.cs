using System.Net.WebSockets;
using Verbara.Sdk.TestInfrastructure.WebSocket;
using FluentAssertions;
using Xunit;

namespace Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Internal;

/// <summary>
/// <see cref="WebSocketTestServer.HandlerFault"/>: the per-connection boundary swallows what a fake's
/// handler throws, and this is where the first such fault stays readable.
/// </summary>
/// <remarks>
/// The server lives in <c>Verbara.Sdk.TestInfrastructure</c>, which has no test project of its own.
/// This suite references it directly and builds <see cref="RealtimeFakeServer"/> on it, so its tests
/// live here.
/// </remarks>
public sealed class WebSocketTestServerHandlerFaultTests
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task HandlerFault_ShouldHoldTheHandlersException_WhenTheHandlerThrows()
    {
        // Arrange
        var thrown = new InvalidOperationException("the fake's handler is broken");
        await using var server = new WebSocketTestServer(_ => Task.FromException(thrown));
        server.Start();

        // Act
        using var client = new ClientWebSocket();
        await client.ConnectAsync(ServerUri(server, "/"), CancellationToken.None);
        await server.SessionCompleted.WaitAsync(SignalTimeout);

        // Assert
        server.HandlerFault.Should().BeSameAs(thrown);
    }

    [Fact]
    public async Task HandlerFault_ShouldKeepTheFirstFault_WhenALaterSessionAlsoThrows()
    {
        // Arrange — each session throws its own exception, picked by the path it was opened on
        var first = new InvalidOperationException("first session");
        var second = new InvalidOperationException("second session");
        await using var server = new WebSocketTestServer(
            session => Task.FromException(session.RequestUri == "/second" ? second : first));
        server.Start();

        using (var firstClient = new ClientWebSocket())
        {
            await firstClient.ConnectAsync(ServerUri(server, "/first"), CancellationToken.None);
            await server.SessionCompleted.WaitAsync(SignalTimeout);
        }

        server.HandlerFault.Should().BeSameAs(first, "the first session has ended and its fault is recorded");

        // Act — SessionCompleted belongs to the first session only, so the second session's join
        // point is its client seeing the connection drop. The server releases the connection only
        // after the fault has been through the recording filter.
        using var secondClient = new ClientWebSocket();
        await secondClient.ConnectAsync(ServerUri(server, "/second"), CancellationToken.None);
        var dropped = await Record.ExceptionAsync(
            () => secondClient.ReceiveAsync(new byte[1], CancellationToken.None).WaitAsync(SignalTimeout));

        // Assert
        dropped.Should().BeOfType<WebSocketException>(
            "the server released the second connection without a close handshake");
        server.HandlerFault.Should().BeSameAs(first, "a later fault must not replace the first");
    }

    [Fact]
    public async Task HandlerFault_ShouldStayNull_WhenTheSessionEndsCleanly()
    {
        // Arrange — the handler ends its session with a close frame and returns
        await using var server = new WebSocketTestServer(session =>
            session.WebSocket.CloseOutputAsync(
                WebSocketCloseStatus.NormalClosure, "done", session.ServerCancellationToken));
        server.Start();

        // Act
        using var client = new ClientWebSocket();
        await client.ConnectAsync(ServerUri(server, "/"), CancellationToken.None);
        var received = await client.ReceiveAsync(new byte[16], CancellationToken.None).WaitAsync(SignalTimeout);
        await server.SessionCompleted.WaitAsync(SignalTimeout);

        // Assert
        received.MessageType.Should().Be(WebSocketMessageType.Close, "the handler ran to its own close");
        server.HandlerFault.Should().BeNull();
    }

    private static Uri ServerUri(WebSocketTestServer server, string path) =>
        new($"ws://127.0.0.1:{server.Port}{path}");
}
