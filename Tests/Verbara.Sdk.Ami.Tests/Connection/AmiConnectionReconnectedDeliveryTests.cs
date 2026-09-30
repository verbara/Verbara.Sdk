using Verbara.Sdk.Ami.Connection;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// <see cref="AmiConnection.Reconnected"/> reaches every handler, one after the other: a handler that throws is logged,
/// and the handlers subscribed after it still hear the reconnect. A consumer that subscribed before the live server, and
/// whose handler throws, must not keep the server's reload from running.
/// </summary>
/// <remarks>
/// A real <see cref="AmiConnection"/> over the in-memory <see cref="PipedSocket"/> harness; every socket's peer logs in
/// and answers Pings. Every wait is bounded by <see cref="Bound"/> and ends on the signal it asserts.
/// </remarks>
public sealed class AmiConnectionReconnectedDeliveryTests
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Reconnected_ShouldReachLaterHandlers_WhenAnEarlierHandlerThrows()
    {
        using var peerCts = new CancellationTokenSource(Bound * 3);
        var sockets = new PipedSocketFactory();
        var serving = ServeEverySocketAsync(sockets, peerCts.Token);
        var logger = new SignalingLogger<AmiConnection>();
        await using var connection = new AmiConnection(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = true,
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(50),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(50),
        }), sockets, logger);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        var later = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Reconnected += () => throw new InvalidOperationException("a consumer's handler that throws");
        connection.Reconnected += () => later.TrySetResult();
        var errorLogged = logger.Logged("[AMI] Reconnect handler error");

        sockets.Created[0].CloseFromPeer();
        var reached = await CompletesWithinBoundAsync(later.Task);
        var logged = await CompletesWithinBoundAsync(errorLogged);

        using (new AssertionScope())
        {
            reached.Should().BeTrue("a handler that throws does not keep the handlers after it from hearing the reconnect");
            logged.Should().BeTrue("the handler's exception is logged");
            logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Line.Contains("[AMI] Reconnect handler error"),
                "the throw is logged as an error");
        }

        await peerCts.CancelAsync();
        await serving;
    }

    /// <summary>Logs in every socket the connection creates and answers its Pings, until <paramref name="ct"/> is cancelled.</summary>
    private static async Task ServeEverySocketAsync(PipedSocketFactory sockets, CancellationToken ct)
    {
        var peers = new List<Task>();
        try
        {
            while (true)
            {
                var peer = await sockets.NextAsync(ct);
                peers.Add(Task.Run(() => ServeAsync(peer, ct), CancellationToken.None));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The test is over.
        }

        await Task.WhenAll(peers);
    }

    private static async Task ServeAsync(PipedSocket peer, CancellationToken ct)
    {
        try
        {
            await peer.CompleteLoginAsync(ct);
            await peer.AnswerPingsAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The test is over.
        }
        catch (InvalidOperationException)
        {
            // The connection closed this socket before the login finished.
        }
    }

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
