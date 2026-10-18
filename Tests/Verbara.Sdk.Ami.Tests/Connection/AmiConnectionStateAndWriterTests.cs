using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// <c>AmiConnection.StateAndWriter</c> reads the state with who wrote it: the caller's own <c>ConnectAsync</c> or
/// <c>DisconnectAsync</c> (<see langword="true"/>), or the reader and the reconnect loop (<see langword="false"/>). Live
/// reads it when a load is cut short, to tell a reconnect that will reload from a caller's connect that will not.
/// </summary>
/// <remarks>
/// A real <see cref="AmiConnection"/> over the in-memory <see cref="PipedSocket"/> harness, with a 1 ms reconnect backoff.
/// Each read is taken while the connection is parked where the test holds it — a connect attempt waits for its banner
/// until the test plays the peer — so the state cannot move under the read. Every wait is bounded by
/// <see cref="Bound"/>, a hang bound a healthy run never reaches.
/// </remarks>
public sealed class AmiConnectionStateAndWriterTests
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task StateAndWriter_ShouldNameTheWriterOfEachState_AcrossTheCallersConnectTheReconnectLoopAndTheCallersEnding()
    {
        using var peerCts = new CancellationTokenSource(Bound * 3);
        var sockets = new PipedSocketFactory();
        await using var connection = new AmiConnection(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = true,
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(1),
            DefaultEventTimeout = TimeSpan.Zero,
        }), sockets, NullLogger<AmiConnection>.Instance);
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Reconnected += () => reconnected.TrySetResult();
        var reads = new List<(string Step, AmiConnectionState State, bool WrittenByCaller)>();
        void Read(string step)
        {
            var (state, byCaller) = connection.StateAndWriter;
            reads.Add((step, state, byCaller));
        }

        // The caller's connect, parked on its banner, then logged in.
        var connect = connection.ConnectAsync().AsTask();
        var first = await sockets.NextAsync(peerCts.Token).AsTask().WaitAsync(Bound);
        Read("caller connecting");
        await first.CompleteLoginAsync(peerCts.Token).WaitAsync(Bound);
        await connect.WaitAsync(Bound);
        Read("caller connected");

        // The peer ends the session; the reconnect loop's attempt is parked on its banner, then logged in.
        first.CloseFromPeer();
        var second = await sockets.NextAsync(peerCts.Token).AsTask().WaitAsync(Bound);
        Read("loop connecting");
        await second.CompleteLoginAsync(peerCts.Token).WaitAsync(Bound);
        await reconnected.Task.WaitAsync(Bound);
        Read("loop connected");

        // The caller ends the connection.
        await connection.DisconnectAsync().AsTask().WaitAsync(Bound);
        Read("caller disconnected");

        reads.Should().Equal(
            [
                ("caller connecting", AmiConnectionState.Connecting, true),
                ("caller connected", AmiConnectionState.Connected, true),
                ("loop connecting", AmiConnectionState.Connecting, false),
                ("loop connected", AmiConnectionState.Connected, false),
                ("caller disconnected", AmiConnectionState.Disconnected, true),
            ],
            "each read names the state and who wrote it: the reconnect loop's Connecting and Connected are not the "
            + "caller's, so a load cut short then still reads that a reload will follow");
    }
}
