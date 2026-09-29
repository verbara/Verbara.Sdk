using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// The internal overload of <c>SendEventGeneratingActionAsync</c> that takes an <see cref="EventActionOutcome"/>:
/// besides the events, the caller learns how the action ended. A <c>Response: Error</c> is reported as its
/// <c>Message</c>; an action the connection gave up because its session ended is reported as ended with its session.
/// The public overload reads both exactly like "Asterisk has none", which is how a load of live state took an
/// Asterisk that had not finished starting for one with no queues.
/// </summary>
public sealed partial class AmiConnectionEventActionOutcomeTests
{
    [Fact]
    public async Task SendEventGeneratingActionAsync_ShouldReportTheRefusalsMessage_WhenAsteriskAnswersResponseError()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory, autoReconnect: false, LongBackoff);
        var peer = await ConnectAsync(connection, factory, peerCts);
        var outcome = new EventActionOutcome();

        var ended = ReadToEndAsync(
            connection.SendEventGeneratingActionAsync(new QueueStatusAction(), outcome, CancellationToken.None));
        var id = await ReadActionIdAsync(peer, peerCts);
        (await peer.RespondAsync("Error", id, [new("Message", UnknownCommand)])).Should().BeTrue("the peer refuses the action");
        var result = await ended.WaitAsync(Bound);

        using (new AssertionScope())
        {
            result.Error.Should().BeNull("a refusal ends the enumeration quietly; the outcome is where it is reported");
            result.Events.Should().BeEmpty("a refusal carries no events");
            outcome.Rejection.Should().Be(UnknownCommand, "the outcome carries the Message of the Response: Error");
            outcome.SessionEnded.Should().BeFalse("the session is still up");
        }
    }

    /// <summary>
    /// The peer answers the action, sends one of its events and ends the session before the list's completion event.
    /// The reconnect's backoff is long, so the reconnect's cleanup cannot be what completes the action: the session's
    /// end does. The state read once the enumeration has ended is the one the ending chose.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SendEventGeneratingActionAsync_ShouldReportTheSessionEnded_WhenTheSessionEndsWhileTheActionIsPending(
        bool autoReconnect)
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory, autoReconnect, LongBackoff);
        var peer = await ConnectAsync(connection, factory, peerCts);
        var outcome = new EventActionOutcome();

        var ended = ReadToEndAsync(
            connection.SendEventGeneratingActionAsync(new StatusAction(), outcome, CancellationToken.None));
        var id = await ReadActionIdAsync(peer, peerCts);
        (await WriteStatusListAsync(peer, id, complete: false, "1700000000.1")).Should().BeTrue("the peer lists one channel");
        peer.CloseFromPeer();
        var result = await ended.WaitAsync(Bound);
        var stateWhenEnded = connection.State;

        using (new AssertionScope())
        {
            result.Error.Should().BeNull("an action whose session ended completes without an error");
            result.Events.OfType<StatusEvent>().Select(status => status.UniqueId)
                .Should().Equal(["1700000000.1"], "the event received before the end is the caller's");
            outcome.SessionEnded.Should().BeTrue("the action ended because its session did, not because Asterisk completed it");
            outcome.Rejection.Should().BeNull("Asterisk refused nothing");
            if (autoReconnect)
            {
                stateWhenEnded.Should().Be(AmiConnectionState.Reconnecting,
                    "the ending chose to reconnect before it ended the action, and the reconnect waits out its backoff");
            }
            else
            {
                stateWhenEnded.Should().BeOneOf([AmiConnectionState.Disconnecting, AmiConnectionState.Disconnected],
                    "the ending chose not to reconnect before it ended the action; its release may have finished since");
            }
        }
    }

    /// <summary>
    /// The caller disposes the connection while an action is pending. The action ends with its session, whichever
    /// part of the ending reaches it first: the reader loop's end, or the release's own sweep of the actions still
    /// pending, which covers an action registered after the reader loop had ended.
    /// </summary>
    [Fact]
    public async Task SendEventGeneratingActionAsync_ShouldReportTheSessionEnded_WhenTheConnectionIsDisposedWhileTheActionIsPending()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory, autoReconnect: true, LongBackoff);
        var peer = await ConnectAsync(connection, factory, peerCts);
        var outcome = new EventActionOutcome();

        var ended = ReadToEndAsync(
            connection.SendEventGeneratingActionAsync(new StatusAction(), outcome, CancellationToken.None));
        var id = await ReadActionIdAsync(peer, peerCts);
        (await WriteStatusListAsync(peer, id, complete: false, "1700000000.1")).Should().BeTrue("the peer lists one channel");
        // A Ping answered after the listed channel: once its answer is in, the connection has read that channel too.
        var ping = connection.SendActionAsync(new PingAction()).AsTask();
        (await peer.RespondAsync("Success", await ReadActionIdAsync(peer, peerCts))).Should().BeTrue();
        await ping.WaitAsync(Bound);
        await connection.DisposeAsync().AsTask().WaitAsync(Bound);
        var result = await ended.WaitAsync(Bound);

        using (new AssertionScope())
        {
            result.Error.Should().BeNull("an action whose connection was disposed completes without an error");
            result.Events.OfType<StatusEvent>().Select(status => status.UniqueId)
                .Should().Equal(["1700000000.1"], "the event received before the end is the caller's");
            outcome.SessionEnded.Should().BeTrue("the action ended because the connection ended, not because Asterisk completed it");
            outcome.Rejection.Should().BeNull("Asterisk refused nothing");
        }
    }

    [Fact]
    public async Task SendEventGeneratingActionAsync_ShouldReportNeitherARefusalNorAnEnding_WhenAsteriskCompletesTheAction()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory, autoReconnect: true, LongBackoff);
        var peer = await ConnectAsync(connection, factory, peerCts);
        var outcome = new EventActionOutcome();

        var ended = ReadToEndAsync(
            connection.SendEventGeneratingActionAsync(new StatusAction(), outcome, CancellationToken.None));
        var id = await ReadActionIdAsync(peer, peerCts);
        (await WriteStatusListAsync(peer, id, complete: true, "1700000000.1", "1700000000.2"))
            .Should().BeTrue("the peer lists two channels and completes the list");
        var result = await ended.WaitAsync(Bound);

        using (new AssertionScope())
        {
            result.Error.Should().BeNull();
            result.Events.OfType<StatusEvent>().Select(status => status.UniqueId)
                .Should().Equal(["1700000000.1", "1700000000.2"], "every event of the list is the caller's");
            outcome.Rejection.Should().BeNull("Asterisk refused nothing");
            outcome.SessionEnded.Should().BeFalse("Asterisk completed the list on a session that is still up");
            connection.State.Should().Be(AmiConnectionState.Connected);
        }
    }
}
