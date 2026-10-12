using System.Net.WebSockets;
using System.Text;
using Verbara.Sdk.Ari.Outbound;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ari.Tests.Outbound;

/// <summary>
/// The token handed to the outbound listener's start bounds the start, not the listener's life. Cancelled once the
/// start returned, or already cancelled when it began, it neither stops the accept loop nor ends a connection
/// being served, and the listener keeps reporting itself running. Only the listener's own stop ends the loop;
/// that side is held by <see cref="AriOutboundListenerTests.StopAsync_ShouldEndTheAcceptLoopWithoutBackingOff_WhenTheListenerIsStopped"/>.
/// </summary>
public sealed class AriOutboundListenerStartTokenTests
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    private const string Application = "myapp";

    private const string StasisStartJson =
        """{"type":"StasisStart","application":"myapp","timestamp":"2026-04-19T10:00:00Z","args":["arg1"],"channel":{"id":"ch-1","name":"PJSIP/test","state":"Up"}}""";

    [Fact]
    public async Task StartAsync_ShouldKeepServing_WhenItsTokenIsCancelledAfterTheStart()
    {
        await using var listener = CreateListener();
        var accepted = Accepted(listener);
        using var start = new CancellationTokenSource();
        await listener.StartAsync(start.Token);

        await start.CancelAsync();
        using var peer = await TryUpgradeAsync(listener.BoundPort);
        var served = peer is not null && await WithinBoundAsync(accepted);

        served.Should().BeTrue(
            "an outbound WebSocket was not served after the start token was cancelled (upgrade answered: " +
            $"{peer is not null}): the accept loop runs on a source linked to that token, so the cancel ended it");
        listener.IsRunning.Should().BeTrue("the listener still reports itself running, and it must also be serving");
    }

    [Fact]
    public async Task StartAsync_ShouldServe_WhenItsTokenIsAlreadyCancelled()
    {
        await using var listener = CreateListener();
        var accepted = Accepted(listener);
        await listener.StartAsync(new CancellationToken(canceled: true));

        using var peer = await TryUpgradeAsync(listener.BoundPort);
        var served = peer is not null && await WithinBoundAsync(accepted);

        served.Should().BeTrue(
            "an outbound WebSocket was not served after a start whose token was already cancelled (upgrade " +
            $"answered: {peer is not null}): the accept loop runs on a source linked to that token, so it ended " +
            "before its first accept");
        listener.IsRunning.Should().BeTrue("the listener still reports itself running, and it must also be serving");
    }

    [Fact]
    public async Task StartAsync_ShouldKeepAConnectionServed_WhenItsTokenIsCancelledWhileTheConnectionIsLive()
    {
        await using var listener = CreateListener();
        var accepted = Accepted(listener);
        using var start = new CancellationTokenSource();
        await listener.StartAsync(start.Token);
        using var peer = await TryUpgradeAsync(listener.BoundPort);
        peer.Should().NotBeNull("premise: the upgrade is answered before the start token is cancelled");
        (await WithinBoundAsync(accepted)).Should().BeTrue("premise: the connection is accepted before the cancel");
        var connection = await accepted;
        var received = new TaskCompletionSource<AriEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var events = connection.Events.Subscribe(e => received.TrySetResult(e));

        await start.CancelAsync();
        await peer!.SendAsync(Encoding.UTF8.GetBytes(StasisStartJson), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        var answered = await WithinBoundAsync(received.Task);

        answered.Should().BeTrue(
            "a message the peer sent after the start token was cancelled was not served: the connection's read " +
            "pump runs on a source linked to that token, so the cancel ended it");
        listener.ActiveConnectionCount.Should().Be(1, "the live connection is still counted");
        listener.GetByApplication(Application).Should().ContainSingle()
            .Which.Should().BeSameAs(connection, "the live connection is still listed for its application");
    }

    // ------------------------------------------------------------------------------------ helpers

    private static AriOutboundListener CreateListener() =>
        new(
            Options.Create(new AriOutboundListenerOptions
            {
                ListenAddress = "127.0.0.1",
                Port = 0, // ephemeral; the test reads BoundPort
                Path = "/ari/events",
                ConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            }),
            NullLogger<AriOutboundListener>.Instance);

    /// <summary>The first connection the listener announces.</summary>
    private static Task<AriOutboundConnection> Accepted(AriOutboundListener listener)
    {
        var accepted = new TaskCompletionSource<AriOutboundConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.OnConnectionAccepted.Subscribe(connection => accepted.TrySetResult(connection));
        return accepted.Task;
    }

    /// <summary>
    /// Opens the outbound WebSocket Asterisk would dial, on the IPv4 loopback literal; <see langword="null"/> when
    /// the upgrade is not answered within the bound. A listener nobody accepts on still completes the TCP
    /// handshake into its backlog, so only the upgrade tells a served connection from an unserved one.
    /// </summary>
    private static async Task<ClientWebSocket?> TryUpgradeAsync(int port)
    {
        var client = new ClientWebSocket();
        using var hang = new CancellationTokenSource(SignalTimeout);
        try
        {
            await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ari/events?app={Application}"), hang.Token);
            return client;
        }
        catch (OperationCanceledException) when (hang.IsCancellationRequested)
        {
            client.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="signal"/> completes within the bound. Returning a bool rather than letting the timeout
    /// escape turns "never served" into an assertion failure carrying the reason instead of a bare timeout.
    /// </summary>
    private static async Task<bool> WithinBoundAsync(Task signal)
    {
        try
        {
            await signal.WaitAsync(SignalTimeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
