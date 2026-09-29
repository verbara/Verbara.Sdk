using System.Net.WebSockets;
using Verbara.Sdk.TestInfrastructure.WebSocket;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.Helpers;

/// <summary>
/// <see cref="StalledUpgradeListener"/> against a raw <see cref="ClientWebSocket"/>: an upgrade it holds
/// stays in flight until something other than the listener ends it, and an upgrade the test answers
/// opens and runs the handler given at construction.
/// </summary>
/// <remarks>
/// The listener lives in <c>Verbara.Sdk.TestInfrastructure</c>, which has no test project of its own,
/// so its tests live beside <see cref="EndOfInputPeerTests"/>. Nothing here waits on the clock.
/// </remarks>
public sealed class StalledUpgradeListenerTests
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ConnectAsync_ShouldStayInFlightUntilTheCallerCancels_WhenTheUpgradeIsHeld()
    {
        // Arrange
        await using var listener = new StalledUpgradeListener();
        listener.Start();
        using var client = new ClientWebSocket();
        using var cts = new CancellationTokenSource();

        var connect = client.ConnectAsync(new Uri($"ws://127.0.0.1:{listener.Port}/"), cts.Token);
        await listener.RequestReceived.WaitAsync(SignalTimeout);
        var heldAfterRequest = !connect.IsCompleted;

        // Act
        await cts.CancelAsync();
        var fault = await Record.ExceptionAsync(() => connect.WaitAsync(SignalTimeout));

        // Assert
        using (new AssertionScope())
        {
            heldAfterRequest.Should().BeTrue("nothing answered the upgrade");
            fault.Should().BeAssignableTo<OperationCanceledException>("only the caller's token ended the connect");
            listener.RequestCount.Should().Be(1);
        }
    }

    [Fact]
    public async Task AnswerUpgradeAsync_ShouldOpenTheHeldConnectAndRunTheHandler_WhenTheTestAnswers()
    {
        // Arrange — the answered connection is served by an end-of-input peer that greets first
        await using var peer = new EndOfInputPeer(EndOfInputPeerMode.AnswerOnRequest)
        {
            Preamble = [PeerFrame.Text("hello")],
            IsEndOfInput = static t => t == "eof",
        };
        await using var listener = new StalledUpgradeListener(peer.HandleSessionAsync);
        listener.Start();
        using var client = new ClientWebSocket();

        var connect = client.ConnectAsync(new Uri($"ws://127.0.0.1:{listener.Port}/"), CancellationToken.None);
        await listener.RequestReceived.WaitAsync(SignalTimeout);
        var heldBeforeAnswer = !connect.IsCompleted;

        // Act
        await listener.AnswerUpgradeAsync().WaitAsync(SignalTimeout);
        await connect.WaitAsync(SignalTimeout);
        var greeting = await EndOfInputPeerTests.ReceiveAsync(client);

        // Assert
        using (new AssertionScope())
        {
            heldBeforeAnswer.Should().BeTrue("the upgrade is held until the test answers it");
            client.State.Should().Be(WebSocketState.Open);
            greeting.Should().Be(new EndOfInputPeerTests.Received(WebSocketMessageType.Text, "hello"));
            listener.RequestCount.Should().Be(1);
        }
    }

    [Fact]
    public async Task AnswerUpgradeAsync_ShouldThrow_WhenNoUpgradeIsHeld()
    {
        await using var listener = new StalledUpgradeListener();
        listener.Start();

        await FluentActions.Awaiting(listener.AnswerUpgradeAsync).Should().ThrowAsync<InvalidOperationException>();
    }
}
