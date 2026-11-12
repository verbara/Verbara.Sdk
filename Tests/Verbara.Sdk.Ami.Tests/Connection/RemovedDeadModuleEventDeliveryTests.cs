using System.Collections.Concurrent;
using Verbara.Sdk.Ami.Actions;
using FluentAssertions;
using FluentAssertions.Execution;
using static Verbara.Sdk.Ami.Tests.Connection.AmiDispatchTestKit;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// The event names that dead Asterisk modules sent, or that Asterisk 10 or 12 renamed or dropped, have no typed class
/// from 2.10.0 on, and still reach every subscriber: each arrives as a base <see cref="ManagerEvent"/> that keeps the
/// name it was sent under and every header. Nothing is dropped, and nothing is rewritten into the type of a different
/// wire name.
/// </summary>
/// <remarks>
/// Each name is written as Asterisk 11 or earlier spelled it, through a real connection over the in-memory transport,
/// followed by the sentinel; every wait ends on its signal, bounded by <see cref="Bound"/>. Types are named by string
/// only, so the file compiles with and without the removed types.
/// </remarks>
public sealed class RemovedDeadModuleEventDeliveryTests
{
    public static TheoryData<string> RemovedEventNames => new(
    [
        "Agentcallbacklogin",
        "Agentcallbacklogoff",
        "QueueMemberPaused",
        "MusicOnHold",
        "FaxReceived",
        "DTMF",
        "Transfer",
        "ChannelReload",
        "ChannelUpdate",
        "JabberEvent",
        "AGIExec",
        "AsyncAGI",
        "SendFAXStatus",
        "BridgeExec",
        "ModuleLoadReport",
    ]);

    [Theory]
    [MemberData(nameof(RemovedEventNames))]
    public async Task Subscribers_ShouldReceiveABaseManagerEvent_WhenARemovedEventNameArrives(string wireName)
    {
        var received = await DeliverAsync(wireName, [new("Channel", "SIP/100-00000001"), new("Queue", "q1")]);

        using (new AssertionScope())
        {
            received.Should().ContainSingle("the one event written before the sentinel reaches the subscriber");
            var evt = received.Count > 0 ? received[0] : null;
            evt?.GetType().Should().Be<ManagerEvent>("{0} has no typed class once its type is removed", wireName);
            evt?.EventType.Should().Be(wireName, "the event keeps the name Asterisk sent, spelled as sent");
            evt?.RawFields.Should().Contain("Channel", "SIP/100-00000001", "every header is kept");
            evt?.RawFields.Should().Contain("Queue", "q1", "every header is kept");
        }
    }

    /// <summary>
    /// A removed name is not manufactured into the type of the name that replaced it: the replacement name, written
    /// next, still arrives typed, and the removed one does not.
    /// </summary>
    [Theory]
    [InlineData("DTMF", "DTMFEnd", "Verbara.Sdk.Ami.Events.DtmfEndEvent")]
    [InlineData("Transfer", "BlindTransfer", "Verbara.Sdk.Ami.Events.BlindTransferEvent")]
    [InlineData("MusicOnHold", "MusicOnHoldStart", "Verbara.Sdk.Ami.Events.MusicOnHoldStartEvent")]
    [InlineData("QueueMemberPaused", "QueueMemberPause", "Verbara.Sdk.Ami.Events.QueueMemberPauseEvent")]
    public async Task Subscribers_ShouldNotReceiveTheReplacementType_WhenTheRemovedNameArrives(
        string removedName, string replacementName, string replacementType)
    {
        var received = await DeliverAsync([removedName, replacementName], [new("Channel", "SIP/100-00000001")]);

        using (new AssertionScope())
        {
            received.Select(static e => e.EventType).Should().Equal([removedName, replacementName],
                "both events reach the subscriber, in the order they were sent");
            received.Select(static e => e.GetType().FullName).Should().Equal(
                [typeof(ManagerEvent).FullName, replacementType],
                "only the replacement name maps to the replacement type");
        }
    }

    [Fact]
    public async Task Subscribers_ShouldKeepTheQueuePauseHeaders_WhenQueueMemberPausedArrives()
    {
        // What Live used to read from it: queue, member location, paused flag and reason, as Asterisk 11 sent them.
        var received = await DeliverAsync("QueueMemberPaused",
            [new("Queue", "support"), new("Location", "SIP/2000"), new("MemberName", "Agent 2000"), new("Paused", "1"), new("Reason", "lunch")]);

        using (new AssertionScope())
        {
            received.Should().ContainSingle();
            var evt = received.Count > 0 ? received[0] : null;
            evt?.GetType().Should().Be<ManagerEvent>();
            evt?.RawFields.Should().Contain("Queue", "support");
            evt?.RawFields.Should().Contain("Location", "SIP/2000");
            evt?.RawFields.Should().Contain("MemberName", "Agent 2000");
            evt?.RawFields.Should().Contain("Paused", "1");
            evt?.RawFields.Should().Contain("Reason", "lunch");
        }
    }

    /// <summary>
    /// <c>AsyncAGI</c> was a response event. Routing to a pending action keys on the <c>ActionID</c>, not on the type,
    /// so an <c>AsyncAGI</c> carrying the <c>ActionID</c> of a pending event-generating action is still yielded by that
    /// action's sequence, as a base <see cref="ManagerEvent"/>.
    /// </summary>
    [Fact]
    public async Task SendEventGeneratingActionAsync_ShouldYieldABaseManagerEvent_WhenAsyncAgiCarriesItsActionId()
    {
        using var peer = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory, new DispatchLogger());
        var socket = await ConnectAsync(connection, factory, peer);

        var ended = CollectAsync(connection.SendEventGeneratingActionAsync(new StatusAction()));
        var action = await socket.ReadActionAsync(peer.Token).WaitAsync(Bound)
            ?? throw new InvalidOperationException("The session ended before the connection sent its action.");
        var id = PipedSocket.ActionIdOf(action);

        (await socket.RespondAsync("Success", id, [new("EventList", "start")])).Should().BeTrue();
        (await socket.WriteEventAsync("AsyncAGI",
            [new("ActionID", id), new("SubEvent", "Exec"), new("Channel", "SIP/100-00000001")])).Should().BeTrue();
        (await socket.WriteEventAsync("StatusComplete", [new("ActionID", id), new("EventList", "Complete")])).Should().BeTrue();
        var yielded = await ended.WaitAsync(Bound);

        using (new AssertionScope())
        {
            yielded.Should().ContainSingle("the AsyncAGI carried the pending action's ActionID");
            var evt = yielded.Count > 0 ? yielded[0] : null;
            evt?.GetType().Should().Be<ManagerEvent>("AsyncAGI has no typed class, and no longer derives from a response event");
            evt?.EventType.Should().Be("AsyncAGI");
            evt?.RawFields.Should().Contain("SubEvent", "Exec");
        }
    }

    private static async Task<List<ManagerEvent>> CollectAsync(IAsyncEnumerable<ManagerEvent> sequence)
    {
        var events = new List<ManagerEvent>();
        await foreach (var evt in sequence)
            events.Add(evt);
        return events;
    }

    private static Task<IReadOnlyList<ManagerEvent>> DeliverAsync(string wireName, KeyValuePair<string, string>[] headers) =>
        DeliverAsync([wireName], headers);

    private static async Task<IReadOnlyList<ManagerEvent>> DeliverAsync(string[] wireNames, KeyValuePair<string, string>[] headers)
    {
        using var peer = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory, new DispatchLogger());
        var subscriber = new RecordingSubscriber();
        using var subscription = connection.Subscribe(subscriber);
        var socket = await ConnectAsync(connection, factory, peer);

        foreach (var wireName in wireNames)
            (await socket.WriteEventAsync(wireName, headers)).Should().BeTrue();
        await WriteSentinelAsync(socket);
        (await CompletesWithinBoundAsync(subscriber.SentinelReached)).Should().BeTrue(
            "the subscriber receives every event up to the sentinel written last");
        return subscriber.Received;
    }

    private sealed class RecordingSubscriber : IObserver<ManagerEvent>
    {
        private readonly ConcurrentQueue<ManagerEvent> _received = new();
        private readonly TaskCompletionSource _sentinel = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<ManagerEvent> Received => [.. _received];

        public Task SentinelReached => _sentinel.Task;

        public void OnNext(ManagerEvent value)
        {
            if (IsSentinel(value))
                _sentinel.TrySetResult();
            else
                _received.Enqueue(value);
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}
