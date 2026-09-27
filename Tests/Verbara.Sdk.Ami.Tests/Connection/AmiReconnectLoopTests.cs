using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// What the reconnect loop leaves behind when it gives up at
/// <see cref="AmiConnectionOptions.MaxReconnectAttempts"/>: every socket the connection created released
/// exactly once, the last one included, and <see cref="AmiConnectionState.Disconnected"/> reported only
/// after that release.
/// </summary>
/// <remarks>
/// <para>
/// How many connects the loop makes before it gives up is deliberately not asserted. The limit is checked
/// after the delay and before the connect, so today <c>MaxReconnectAttempts = N</c> makes N − 1 connects
/// (3 makes two, 1 makes none). Whether N should mean N attempts is ruling C3 of the 2026-09-26 decision
/// audit (ADR-0008 addendum), still open, and this test must hold under either answer: it reads the
/// sockets the factory actually created instead of predicting how many there are.
/// </para>
/// <para>
/// Each peer is an in-memory <see cref="PipedSocket"/>. The first accepts its connect and logs in; every
/// later one refuses, as an Asterisk that is down does. Every wait is bounded by <see cref="Bound"/> and
/// ends on the signal it asserts; none of them sleeps.
/// </para>
/// </remarks>
public sealed class AmiReconnectLoopTests
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Theory]
    // Gives up before any connect of its own: what is left is the socket the peer closed.
    [InlineData(1)]
    // Gives up after refused connects: what is left is the last socket it created.
    [InlineData(3)]
    public async Task ReconnectLoop_ShouldDisposeEverySocket_WhenItGivesUpAtMaxReconnectAttempts(int maxReconnectAttempts)
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory { ConnectsAccepted = 1 };
        var logger = new SignalingLogger<AmiConnection>();
        var connection = new AmiConnection(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            // Only the peer's close ends the connection.
            EnableHeartbeat = false,
            AutoReconnect = true,
            MaxReconnectAttempts = maxReconnectAttempts,
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(10),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(10),
        }), factory, logger);
        // Nothing else ends this connection, so the line is the loop giving up; it is written once the
        // loop has released what it held.
        var gaveUp = logger.Logged("[AMI] Disconnected");

        var loggedIn = Task.Run(async () =>
        {
            var peer = await factory.NextAsync(peerCts.Token);
            await peer.CompleteLoginAsync(peerCts.Token);
            return peer;
        }, peerCts.Token);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        var firstPeer = await loggedIn.WaitAsync(Bound);

        firstPeer.CloseFromPeer();
        var ended = await CompletesWithinBoundAsync(gaveUp);

        var sockets = factory.Created;
        using (new AssertionScope())
        {
            Unreleased(sockets).Should().BeEmpty(
                $"the connection created {sockets.Count} socket(s) and must release every one exactly once, the last included, " +
                $"when the reconnect loop gives up at MaxReconnectAttempts = {maxReconnectAttempts}");
            ended.Should().BeTrue("the loop gives up and reports Disconnected once it has released what it held");
            connection.State.Should().Be(AmiConnectionState.Disconnected);
        }

        (await CompletesWithinBoundAsync(connection.DisposeAsync().AsTask())).Should().BeTrue(
            "a DisposeAsync after the give-up completes");
        Unreleased(sockets).Should().BeEmpty(
            "a DisposeAsync that finds the connection already Disconnected disposes no socket a second time");
    }

    /// <summary>Each socket not disposed exactly once, described by its place in creation order.</summary>
    private static List<string> Unreleased(IReadOnlyList<PipedSocket> sockets) =>
        [.. sockets
            .Select((socket, index) => (socket.DisposeCount, Place: index + 1))
            .Where(s => s.DisposeCount != 1)
            .Select(s => $"socket {s.Place} of {sockets.Count}: disposed {s.DisposeCount} time(s)")];

    private static async Task<bool> CompletesWithinBoundAsync(Task task)
    {
        try
        {
            await task.WaitAsync(Bound);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
