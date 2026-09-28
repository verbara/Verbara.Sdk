using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.VoiceAi.Internal;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tests.Internal;

/// <summary>
/// <see cref="WebSocketConnectBound"/> on its own, against a far end that takes the TCP connection and
/// never answers the upgrade: its expiry is a failed upgrade, the caller's cancellation stays a
/// cancellation, and the limit runs on the clock it is given.
/// </summary>
/// <remarks>
/// <para>
/// The far end is a started <see cref="TcpListener"/> that never accepts: the kernel completes the TCP
/// handshake into its backlog, the client writes its upgrade request, and nothing reads or answers it.
/// The limit's source is built on the manual clock before the dial starts, so a move of the clock right
/// after the call reaches it, wherever the dial has got to.
/// </para>
/// <para>
/// The recognizers' own tests pin each call site. These pin the helper they share, here because this is
/// the test assembly whose coverage run instruments <c>Verbara.Sdk.VoiceAi</c>.
/// </para>
/// </remarks>
public sealed class WebSocketConnectBoundTests : IDisposable
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(2);

    private readonly TcpListener _mute = new(IPAddress.Loopback, 0);

    public WebSocketConnectBoundTests() => _mute.Start();

    /// <summary>The mute far end. 127.0.0.1, never "localhost" (ADR-0044).</summary>
    private Uri MuteUri => new($"ws://127.0.0.1:{((IPEndPoint)_mute.LocalEndpoint).Port}/");

    [Fact]
    public async Task ConnectAsync_ShouldThrowWebSocketExceptionCarryingATimeout_WhenTheUpgradeIsNotAnsweredWithinTheLimit()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        using var ws = new ClientWebSocket();
        var connect = WebSocketConnectBound.ConnectAsync(ws, MuteUri, Limit, clock, CancellationToken.None);
        var due = await clock.TimersArmed.ReadAsync().AsTask().WaitAsync(SignalTimeout);

        // Act
        clock.Advance(Limit);
        var fault = await Record.ExceptionAsync(() => connect.WaitAsync(SignalTimeout));

        // Assert
        var failure = fault.Should().BeOfType<WebSocketException>(
            "an upgrade never answered takes the type a refused one takes, so every call site keeps its classification").Subject;
        using (new AssertionScope())
        {
            due.Should().Be(Limit, "the limit runs on the clock the helper is given");
            failure.WebSocketErrorCode.Should().Be(WebSocketError.Faulted);
            failure.Message.Should().Contain("127.0.0.1").And.Contain("2 s");
            failure.InnerException.Should().BeOfType<TimeoutException>()
                .Which.InnerException.Should().BeAssignableTo<OperationCanceledException>(
                    "the bound's own cancellation is kept as the cause");
        }
    }

    [Fact]
    public async Task ConnectAsync_ShouldThrowOperationCanceled_WhenTheCallerCancelsBeforeTheLimit()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        using var ws = new ClientWebSocket();
        using var cts = new CancellationTokenSource();
        var connect = WebSocketConnectBound.ConnectAsync(ws, MuteUri, Limit, clock, cts.Token);

        // Act
        await cts.CancelAsync();
        var fault = await Record.ExceptionAsync(() => connect.WaitAsync(SignalTimeout));

        // Assert
        fault.Should().BeAssignableTo<OperationCanceledException>(
            "a cancellation the caller asked for is never turned into a failed upgrade");
    }

    public void Dispose() => _mute.Stop();
}
