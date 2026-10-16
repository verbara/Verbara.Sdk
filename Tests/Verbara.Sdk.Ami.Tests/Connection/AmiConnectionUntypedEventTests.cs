using System.Collections.Concurrent;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Events;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using static Verbara.Sdk.Ami.Tests.Connection.AmiDispatchTestKit;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// An AMI event whose name no SDK type maps is delivered as it arrived: a base <see cref="ManagerEvent"/> carrying the
/// name exactly as Asterisk spelled it and every distinct header, to every observer, every <c>OnEvent</c> handler and
/// the pending event-generating action whose <c>ActionID</c> it carries.
/// </summary>
/// <remarks>
/// <c>MeetmeJoin</c> is what app_meetme sends on an Asterisk that still builds it (20, or 22/23 built by hand). It is
/// the documented path for an event the SDK no longer types. Every wait ends on its signal, bounded by
/// <see cref="Bound"/>.
/// </remarks>
public sealed class AmiConnectionUntypedEventTests
{
    /// <summary>A list item no SDK type maps, before or after the removal of the obsolete event types.</summary>
    private const string UntypedListItem = "MeetmeList";

    /// <summary>
    /// The untyped <c>MeetmeJoin</c> and a typed <c>ConfbridgeJoin</c> travel the same connection, then the sentinel.
    /// The connection logs whatever it logs about an event before it reads past that event or before it delivers the
    /// next one, so once both subscribers have seen the sentinel the log holds every entry the two events produced.
    /// </summary>
    [Fact]
    public async Task Subscribers_ShouldReceiveABaseManagerEventWithEveryHeader_WhenAnEventWithNoTypedClassArrives()
    {
        using var peer = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var logger = new DispatchLogger();
        await using var connection = Create(factory, logger);
        var observer = new RecordingSubscriber();
        var handler = new RecordingSubscriber();
        using var subscription = connection.Subscribe(observer);
        connection.OnEvent += handler.HandleAsync;
        var socket = await ConnectAsync(connection, factory, peer);

        (await socket.WriteEventAsync("MeetmeJoin",
            [new("Meetme", "200"), new("Usernum", "1"), new("Channel", "PJSIP/a-1")])).Should().BeTrue();
        (await socket.WriteEventAsync("ConfbridgeJoin",
            [new("Conference", "300"), new("Channel", "PJSIP/b-1")])).Should().BeTrue();
        await WriteSentinelAsync(socket);
        var observerReached = await CompletesWithinBoundAsync(observer.SentinelReached);
        var handlerReached = await CompletesWithinBoundAsync(handler.SentinelReached);

        using (new AssertionScope())
        {
            observerReached.Should().BeTrue("the observer receives every event up to the sentinel written last");
            handlerReached.Should().BeTrue("the handler receives every event up to the sentinel written last");
            AssertMeetmeJoin(observer.Received, "the observer");
            AssertMeetmeJoin(handler.Received, "the OnEvent handler");
            AssertConfbridgeJoin(observer.Received, "the observer");
            AssertConfbridgeJoin(handler.Received, "the OnEvent handler");
            logger.Entries.Where(static e => e.Level >= LogLevel.Warning).Select(static e => $"{e.Level}: {e.Line}")
                .Should().BeEmpty("an event with no typed class is delivered, not logged as an error or warning");
        }
    }

    /// <summary>
    /// The action is only the vehicle: the connection routes a list item to its action by <c>ActionID</c>, never by
    /// the action's name or the item's.
    /// </summary>
    [Fact]
    public async Task EventGeneratingAction_ShouldYieldTheUntypedEventAndEnd_WhenItCarriesThePendingActionId()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory, new DispatchLogger());
        var peer = await ConnectAsync(connection, factory, peerCts);

        var ended = ReadToEndAsync(connection.SendEventGeneratingActionAsync(new IaxPeerListAction()));
        var action = await peer.ReadActionAsync(peerCts.Token).WaitAsync(Bound)
            ?? throw new InvalidOperationException("The session ended before the connection sent its action.");
        var id = PipedSocket.ActionIdOf(action);
        (await peer.RespondAsync("Success", id, [new("EventList", "start")])).Should().BeTrue();
        (await peer.WriteEventAsync(UntypedListItem, [new("ActionID", id), new("Conference", "200")])).Should().BeTrue();
        (await peer.WriteEventAsync(UntypedListItem + "Complete",
            [new("ActionID", id), new("EventList", "Complete"), new("ListItems", "1")])).Should().BeTrue();
        var events = await ended.WaitAsync(Bound);

        using (new AssertionScope())
        {
            events.Should().ContainSingle("the one list item is yielded and its …Complete ends the sequence")
                .Which.Should().BeOfType<ManagerEvent>("no SDK type maps the item's name");
            events.Single().EventType.Should().Be(UntypedListItem);
            events.Single().RawFields.Should().Contain(new KeyValuePair<string, string>("Conference", "200"));
        }
    }

    private static void AssertMeetmeJoin(IReadOnlyList<ManagerEvent> received, string who)
    {
        var named = received.Where(static e => e.EventType == "MeetmeJoin").ToList();
        named.Should().ContainSingle($"{who} receives the one MeetmeJoin written before the sentinel");
        if (named.Count != 1)
            return;

        var evt = named[0];
        evt.GetType().Should().Be<ManagerEvent>( $"{who} receives the event as a base ManagerEvent, no typed class");
        evt.RawFields.Should().Contain(
        [
            new KeyValuePair<string, string>("Meetme", "200"),
            new KeyValuePair<string, string>("Usernum", "1"),
            new KeyValuePair<string, string>("Channel", "PJSIP/a-1"),
        ], "every header of the message reaches {0}", who);
    }

    private static void AssertConfbridgeJoin(IReadOnlyList<ManagerEvent> received, string who)
    {
        var named = received.Where(static e => e.EventType == "ConfbridgeJoin").ToList();
        named.Should().ContainSingle($"{who} receives the one ConfbridgeJoin written before the sentinel");
        if (named.Count != 1)
            return;

        named[0].Should().BeOfType<ConfbridgeJoinEvent>($"a typed event keeps its type on its way to {who}")
            .Which.Conference.Should().Be("300");
        received.Should().HaveCount(2, $"{who} receives the two events written before the sentinel, and no other");
    }

    private static async Task<IReadOnlyList<ManagerEvent>> ReadToEndAsync(IAsyncEnumerable<ManagerEvent> events)
    {
        var received = new List<ManagerEvent>();
        await foreach (var evt in events)
            received.Add(evt);
        return received;
    }

    /// <summary>Keeps every event before the sentinel, as an observer and as an <c>OnEvent</c> handler.</summary>
    private sealed class RecordingSubscriber : IObserver<ManagerEvent>
    {
        private readonly ConcurrentQueue<ManagerEvent> _received = new();
        private readonly TaskCompletionSource _sentinel = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<ManagerEvent> Received => [.. _received];

        public Task SentinelReached => _sentinel.Task;

        public ValueTask HandleAsync(ManagerEvent evt)
        {
            OnNext(evt);
            return ValueTask.CompletedTask;
        }

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
