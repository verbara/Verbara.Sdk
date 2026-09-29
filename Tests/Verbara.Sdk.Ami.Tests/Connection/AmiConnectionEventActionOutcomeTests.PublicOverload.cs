using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// How an event-generating action ends, as the caller that enumerates it sees it: the events Asterisk sent, and
/// whether the enumeration ended with an error.
/// </summary>
/// <remarks>
/// <para>
/// The cases in this file drive only the public <see cref="AmiConnection.SendEventGeneratingActionAsync"/>. They pin
/// what every caller of that overload sees today, which must not move: an action Asterisk refuses with
/// <c>Response: Error</c> ends with no events and no error, and an action pending when its session ends completes
/// with the events already received and no error. Only the moment of that completion may move, from the reconnect's
/// cleanup to the session's end.
/// </para>
/// <para>
/// Each peer is an in-memory <see cref="PipedSocket"/>. Every wait is bounded by <see cref="Bound"/> and ends on the
/// signal it waits for. The reconnect's socket, when the connection reconnects, is never served, so a reconnect
/// attempt waits for its banner until the test disposes the connection.
/// </para>
/// </remarks>
public sealed partial class AmiConnectionEventActionOutcomeTests
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// <summary>What Asterisk answers for an action that no loaded module has registered.</summary>
    private const string UnknownCommand =
        "Invalid/unknown command: QueueStatus. Use Action: ListCommands to show available commands.";

    /// <summary>
    /// A reconnect backoff that never runs out while a test watches: the reconnect loop waits in it until the test
    /// disposes the connection, which stops the loop.
    /// </summary>
    private static readonly TimeSpan LongBackoff = TimeSpan.FromMinutes(1);

    /// <summary>A reconnect backoff short enough that the reconnect's cleanup runs at once after a session ends.</summary>
    private static readonly TimeSpan ShortBackoff = TimeSpan.FromMilliseconds(1);

    [Fact]
    public async Task SendEventGeneratingActionAsync_ShouldEndWithNoEventsAndNoError_WhenAsteriskRefusesTheAction()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory, autoReconnect: false, LongBackoff);
        var peer = await ConnectAsync(connection, factory, peerCts);

        var ended = ReadToEndAsync(connection.SendEventGeneratingActionAsync(new QueueStatusAction()));
        var id = await ReadActionIdAsync(peer, peerCts);
        (await peer.RespondAsync("Error", id, [new("Message", UnknownCommand)])).Should().BeTrue("the peer refuses the action");
        var result = await ended.WaitAsync(Bound);

        using (new AssertionScope())
        {
            result.Error.Should().BeNull("the public overload ends a refused action without an error, as it always has");
            result.Events.Should().BeEmpty("a refusal carries no events");
            connection.State.Should().Be(AmiConnectionState.Connected, "a refusal does not end the session");
        }
    }

    /// <summary>
    /// The peer answers the action, sends two of its events and ends the session before the list's completion event.
    /// The connection then completes the pending action: at the reconnect's cleanup, after its backoff, or at the
    /// release of a connection it will not reconnect. The events already received are the caller's, and nothing
    /// fails.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SendEventGeneratingActionAsync_ShouldCompleteWithTheEventsReceivedAndNoError_WhenTheSessionEndsWhileTheActionIsPending(
        bool autoReconnect)
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory, autoReconnect, ShortBackoff);
        var peer = await ConnectAsync(connection, factory, peerCts);

        var ended = ReadToEndAsync(connection.SendEventGeneratingActionAsync(new StatusAction()));
        var id = await ReadActionIdAsync(peer, peerCts);
        (await WriteStatusListAsync(peer, id, complete: false, "1700000000.1", "1700000000.2"))
            .Should().BeTrue("the peer lists two channels");
        peer.CloseFromPeer();
        var result = await ended.WaitAsync(Bound);

        using (new AssertionScope())
        {
            result.Error.Should().BeNull("an action pending when its session ends completes without an error");
            result.Events.OfType<StatusEvent>().Select(status => status.UniqueId)
                .Should().Equal(["1700000000.1", "1700000000.2"], "the events received before the end are the caller's");
        }
    }

    // ── Helpers, shared by both halves of this class ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A connection over <paramref name="factory"/>, with the heartbeat off and no event timeout, so that only the
    /// peer and the test end an action.
    /// </summary>
    private static AmiConnection Create(PipedSocketFactory factory, bool autoReconnect, TimeSpan backoff,
        ILogger<AmiConnection>? logger = null) =>
        new(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = autoReconnect,
            ReconnectInitialDelay = backoff,
            ReconnectMaxDelay = backoff,
            // Limits, never waits: a reconnect attempt the test holds must not give up while it holds it.
            ConnectionTimeout = TimeSpan.FromMinutes(1),
            DefaultResponseTimeout = TimeSpan.FromMinutes(1),
            DefaultEventTimeout = TimeSpan.Zero,
        }), factory, logger ?? NullLogger<AmiConnection>.Instance);

    /// <summary>Connects, with the next socket's peer completing the login; returns that socket.</summary>
    private static async Task<PipedSocket> ConnectAsync(AmiConnection connection, PipedSocketFactory factory,
        CancellationTokenSource peerCts)
    {
        var loggedIn = Task.Run(async () =>
        {
            var peer = await factory.NextAsync(peerCts.Token);
            await peer.CompleteLoginAsync(peerCts.Token);
            return peer;
        }, peerCts.Token);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        return await loggedIn.WaitAsync(Bound);
    }

    /// <summary>The ActionID of the next action the connection sends to <paramref name="peer"/>.</summary>
    private static async Task<string> ReadActionIdAsync(PipedSocket peer, CancellationTokenSource peerCts)
    {
        var action = await peer.ReadActionAsync(peerCts.Token).WaitAsync(Bound)
            ?? throw new InvalidOperationException("The session ended before the connection sent its action.");
        return PipedSocket.ActionIdOf(action);
    }

    /// <summary>
    /// Answers a <c>Status</c> action: the list's start, one <c>Status</c> event per unique id, and its
    /// <c>StatusComplete</c> when <paramref name="complete"/>. False once the session had ended.
    /// </summary>
    private static async Task<bool> WriteStatusListAsync(PipedSocket peer, string id, bool complete,
        params string[] uniqueIds)
    {
        var written = await peer.RespondAsync("Success", id,
            [new("EventList", "start"), new("Message", "Channel status will follow")]);
        foreach (var uniqueId in uniqueIds)
        {
            written &= await peer.WriteEventAsync("Status",
            [
                new("ActionID", id), new("Channel", "PJSIP/" + uniqueId), new("ChannelState", "6"),
                new("Uniqueid", uniqueId), new("Linkedid", uniqueId),
            ]);
        }

        if (complete)
        {
            written &= await peer.WriteEventAsync("StatusComplete",
                [new("ActionID", id), new("EventList", "Complete"), new("ListItems", uniqueIds.Length.ToString(
                    System.Globalization.CultureInfo.InvariantCulture))]);
        }

        return written;
    }

    /// <summary>
    /// Enumerates <paramref name="events"/> to its end and reports what the caller saw: every event, and the error
    /// the enumeration ended with, if any. It starts the enumeration, which sends the action, before it returns.
    /// </summary>
    private static async Task<Ended> ReadToEndAsync(IAsyncEnumerable<ManagerEvent> events)
    {
        var received = new List<ManagerEvent>();
        try
        {
            await foreach (var evt in events)
                received.Add(evt);

            return new Ended(received, Error: null);
        }
        catch (Exception ex)
        {
            // How the enumeration ended is what the tests assert: an error is recorded, not rethrown.
            return new Ended(received, ex);
        }
    }

    /// <summary>What the caller of an event-generating action saw: its events, and the error it ended with.</summary>
    private sealed record Ended(IReadOnlyList<ManagerEvent> Events, Exception? Error);
}
