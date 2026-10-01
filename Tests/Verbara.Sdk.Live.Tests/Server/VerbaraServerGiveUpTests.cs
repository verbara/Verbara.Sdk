using System.Collections.Concurrent;
using Verbara.Sdk.Ami;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Tests.Connection;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// A live server whose start returned while its AMI connection was reconnecting learns of the reconnect loop's give-up
/// from <see cref="IAmiConnection.StateChanged"/> on <see cref="VerbaraServer.Connection"/>: one change to
/// <see cref="AmiConnectionState.Disconnected"/>, announced as final, made by the connection and carrying the last
/// attempt's error. Before the event existed nothing told it: the give-up raises no second loss announcement.
/// </summary>
/// <remarks>
/// A real <see cref="AmiConnection"/> over the in-memory <see cref="PipedSocket"/> harness. The first peer closes the
/// session right after the connect, so the connection is reconnecting when the start begins; the reconnect's backoff is
/// the connection's own 1 s delay, and the test waits on the reconnect's log line, never on a clock. Every reconnect
/// attempt's login is rejected only once the start has returned, and the connection gives up after one. Every wait is
/// bounded by <see cref="Run.Bound"/> and ends on its signal. The test asserts the announcement, never how many connects
/// the loop made.
/// </remarks>
public sealed class VerbaraServerGiveUpTests
{
    [Fact]
    public async Task StateChanged_ShouldAnnounceTheGiveUpAsFinal_WhenTheStartReturnedWhileTheConnectionWasReconnecting()
    {
        using var peerCts = new CancellationTokenSource(Run.Bound * 3);
        var sockets = new PipedSocketFactory();
        var first = new BootingAsterisk { BootedAtLogin = true, Close = PeerClose.AfterConnect };
        var startReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peers = ServeAsync(sockets, first, startReturned.Task, peerCts.Token);
        var connectionLog = new SignalingLogger<AmiConnection>();
        var connection = new AmiConnection(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = true,
            MaxReconnectAttempts = 1,
            ReconnectInitialDelay = TimeSpan.FromSeconds(1),
            ReconnectMaxDelay = TimeSpan.FromSeconds(1),
            // Limits, never waits: nothing here runs until them.
            ConnectionTimeout = TimeSpan.FromMinutes(1),
            DefaultResponseTimeout = TimeSpan.FromMinutes(1),
            DefaultEventTimeout = TimeSpan.Zero,
        }), sockets, connectionLog);
        var server = new VerbaraServer(connection, new SignalingLogger<VerbaraServer>());
        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(Run.Bound);
            var reconnecting = await CompletesWithinBoundAsync(connectionLog.Logged("[AMI] Reconnecting"));
            var stateAtStart = connection.State;
            var outcome = await Record.ExceptionAsync(() => server.StartAsync().WaitAsync(Run.Bound));

            // Subscribed through the server, once its start has returned: what a consumer holding only the server does.
            var changes = new ConcurrentQueue<AmiConnectionStateChange>();
            var final = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.Connection.StateChanged += change =>
            {
                changes.Enqueue(change);
                if (change.IsFinal)
                    final.TrySetResult();
            };
            startReturned.TrySetResult();
            var gaveUp = await CompletesWithinBoundAsync(final.Task);

            var announced = changes.ToList();
            using (new AssertionScope())
            {
                reconnecting.Should().BeTrue("the connection started reconnecting once the peer closed the session");
                stateAtStart.Should().Be(AmiConnectionState.Reconnecting, "the start began on a connection already reconnecting");
                outcome.Should().BeNull("a start on a reconnecting connection returns and leaves the load to the reconnect");
                gaveUp.Should().BeTrue("the reconnect loop gives up once its attempt's login is rejected");
                announced.Should().ContainSingle(c => c.IsFinal, "the give-up is announced once, as final")
                    .Which.Should().Match<AmiConnectionStateChange>(c =>
                        c.Current == AmiConnectionState.Disconnected && !c.ByCaller && c.Cause is AmiAuthenticationException,
                        "the final change is the connection's own Disconnected, carrying the rejected login");
                first.Fault.Should().BeNull("the first peer served its session without failing");
            }
        }
        finally
        {
            startReturned.TrySetResult();
            await server.DisposeAsync().AsTask().WaitAsync(Run.Bound);
            await connection.DisposeAsync().AsTask().WaitAsync(Run.Bound);
            await peerCts.CancelAsync();
            await peers.WaitAsync(Run.Bound);
        }
    }

    /// <summary>
    /// Serves the first socket with <paramref name="first"/>; every later one, the reconnect's, waits for
    /// <paramref name="rejectFrom"/> and then rejects the login.
    /// </summary>
    private static async Task ServeAsync(PipedSocketFactory sockets, BootingAsterisk first, Task rejectFrom,
        CancellationToken ct)
    {
        var served = new List<Task>();
        try
        {
            served.Add(first.ServeAsync(await sockets.NextAsync(ct), ct));
            while (true)
            {
                var peer = await sockets.NextAsync(ct);
                served.Add(Task.Run(() => RejectLoginAsync(peer, rejectFrom, ct), CancellationToken.None));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The test is over.
        }

        await Task.WhenAll(served);
    }

    private static async Task RejectLoginAsync(PipedSocket peer, Task rejectFrom, CancellationToken ct)
    {
        try
        {
            await rejectFrom.WaitAsync(ct);
            await peer.WriteAsync("Asterisk Call Manager/6.0.0\r\n");
            var challenge = await peer.ReadActionAsync(ct) ?? throw new InvalidOperationException("closed");
            await peer.RespondAsync("Success", PipedSocket.ActionIdOf(challenge), [new("Challenge", "abc123")]);
            var login = await peer.ReadActionAsync(ct) ?? throw new InvalidOperationException("closed");
            await peer.RespondAsync("Error", PipedSocket.ActionIdOf(login), [new("Message", "Authentication failed")]);
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
            await task.WaitAsync(Run.Bound);
            return true;
        }
        catch (TimeoutException)
        {
            // Reaching the hang bound is the failure the caller asserts on, not an observation.
            return false;
        }
    }
}
