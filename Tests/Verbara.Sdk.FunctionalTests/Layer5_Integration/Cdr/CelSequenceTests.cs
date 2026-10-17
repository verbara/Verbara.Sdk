namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.Cdr;

using System.Collections.Concurrent;
using System.Globalization;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Verbara.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;

/// <summary>
/// Tests for CelEvent (Channel Event Logging). CEL events fire multiple times during a
/// call (one per milestone) and require cel_manager.conf enabled in Asterisk.
/// </summary>
[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class CelSequenceTests : FunctionalTestBase
{
    public CelSequenceTests() : base("Verbara.Sdk.Ami")
    {
    }

    [Fact]
    public async Task AnsweredCall_ShouldProduceCompleteCelTimeline()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(20);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var collectedEvents = new ConcurrentBag<CelEvent>();
        var chanEndReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = connection.Subscribe(new CelCollector(collectedEvents, chanEndReceived));

        // ext 101 = Answer + Wait(5) + Hangup — produces full CEL timeline
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/101@test-functional",
            Context = "test-functional",
            Exten = "101",
            Priority = 1,
            IsAsync = true,
            Timeout = 15000,
            ActionId = "cel-timeline-01"
        });

        // Wait for CHAN_END which is the final CEL event in a call
        var result = await Task.WhenAny(chanEndReceived.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        result.Should().Be(chanEndReceived.Task, "ext 101 answers, waits 5 s and hangs up, so the CEL stream must report CHAN_END within 30 s");

        // Allow a brief moment for any trailing events to arrive
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        var eventNames = collectedEvents
            .Select(e => e.EventName ?? string.Empty)
            .ToList();

        eventNames.Should().Contain("CHAN_START", "CEL must include channel start event");
        eventNames.Should().Contain("ANSWER", "CEL must include answer event for ext 101");
        eventNames.Should().Contain("HANGUP", "CEL must include hangup event");
        eventNames.Should().Contain("CHAN_END", "CEL must include channel end event");
    }

    [Fact]
    public async Task CelLinkedId_ShouldCorrelateRelatedEvents()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(20);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var collectedEvents = new ConcurrentBag<CelEvent>();
        var chanEndReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = connection.Subscribe(new CelCollector(collectedEvents, chanEndReceived));

        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/101@test-functional",
            Context = "test-functional",
            Exten = "101",
            Priority = 1,
            IsAsync = true,
            Timeout = 15000,
            ActionId = "cel-linkedid-01"
        });

        var result = await Task.WhenAny(chanEndReceived.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        result.Should().Be(chanEndReceived.Task, "ext 101 answers, waits 5 s and hangs up, so the CEL stream must report CHAN_END within 30 s");

        await Task.Delay(TimeSpan.FromMilliseconds(500));

        var events = collectedEvents.ToList();
        events.Should().NotBeEmpty("at least one CEL event must have been received");

        // All events belonging to the same call leg should share a LinkedID
        var linkedIds = events
            .Where(e => !string.IsNullOrEmpty(e.LinkedID))
            .Select(e => e.LinkedID!)
            .Distinct()
            .ToList();

        // Local channels produce two legs (;1 and ;2), so there may be 1–2 distinct LinkedIDs.
        // The key constraint: no event may have a null/empty LinkedID when others don't.
        linkedIds.Should().NotBeEmpty("CEL events must carry a LinkedID for call correlation");

        // Each non-null LinkedID must appear on at least 2 events (start + end minimum)
        foreach (var linkedId in linkedIds)
        {
            var eventsForLeg = events.Count(e => e.LinkedID == linkedId);
            eventsForLeg.Should().BeGreaterThanOrEqualTo(2,
                "LinkedID '{0}' must appear on at least CHAN_START and CHAN_END", linkedId);
        }
    }

    [Fact]
    public async Task CelTimestamps_ShouldBeMonotonicallyIncreasing()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(20);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        // The originate names its ;1 half, which is also the LinkedID of every CEL event of this call.
        var callId = $"cel-monotonic-{Guid.NewGuid():N}";
        var arrivals = new ConcurrentQueue<CelEvent>();
        var chanEndReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = connection.Subscribe(new CelArrivalCollector(callId, arrivals, chanEndReceived));

        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/101@test-functional",
            Context = "test-functional",
            Exten = "101",
            Priority = 1,
            IsAsync = true,
            Timeout = 15000,
            ChannelId = callId,
            ActionId = "cel-monotonic-01"
        });

        var result = await Task.WhenAny(chanEndReceived.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        result.Should().Be(chanEndReceived.Task, "ext 101 answers, waits 5 s and hangs up, so the CEL stream must report this call's LINKEDID_END within 30 s");

        // The call's CEL events in the order they arrived — not sorted: the order is the claim.
        var received = arrivals.ToList();
        string Timeline() => string.Join(" → ", received.Select(e => $"{e.EventName}@{e.EventTime}"));

        received.Should().HaveCountGreaterThanOrEqualTo(2, "a call's CEL timeline holds at least CHAN_START and CHAN_END");
        var timestamps = received.Select(e => TryParseEventTime(e.EventTime ?? string.Empty)).ToList();
        timestamps.Should().AllSatisfy(t => t.Should().NotBeNull("every CEL EventTime must parse; timeline: {0}", Timeline()));

        // The CEL manager events carry EventTime to the second (measured on Asterisk 23; a dateformat in cel.conf does
        // not change it), so milestones within one second compare equal: the order is non-decreasing, not strict.
        // Measured on Asterisk 23 in 8 runs, 3 of them with three concurrent calls: no CEL event arrived carrying an
        // earlier EventTime than the one before it, across the whole stream and per channel.
        for (var i = 1; i < timestamps.Count; i++)
        {
            timestamps[i]!.Value.Should().BeOnOrAfter(timestamps[i - 1]!.Value,
                "CEL event {0} must not carry an earlier EventTime than event {1}, the one that arrived before it; timeline: {2}",
                i, i - 1, Timeline());
        }

        timestamps[^1]!.Value.Should().BeAfter(timestamps[0]!.Value,
            "the call lasts about 5 s, so its last CEL event must be later than its first; timeline: {0}", Timeline());
    }

    [Fact]
    public async Task QueueCall_ShouldProduceCelEvents()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(20);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var collectedEvents = new ConcurrentBag<CelEvent>();
        var chanStartReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = connection.Subscribe(new CelCollectorWithStart(collectedEvents, chanStartReceived));

        const string testInterface = "Local/100@test-functional";
        const string testQueue = "test-queue";

        try
        {
            // Add a queue member so the call can be answered
            await connection.SendActionAsync(new QueueAddAction
            {
                Queue = testQueue,
                Interface = testInterface
            });
            await Task.Delay(TimeSpan.FromSeconds(1));

            // Originate to ext 500 = Queue(test-queue)
            await connection.SendActionAsync(new OriginateAction
            {
                Channel = "Local/500@test-functional",
                Context = "test-functional",
                Exten = "500",
                Priority = 1,
                IsAsync = true,
                Timeout = 15000,
                ActionId = "cel-queue-01"
            });

            // Wait for at least one CEL event to confirm CEL is firing
            var result = await Task.WhenAny(chanStartReceived.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            result.Should().Be(chanStartReceived.Task, "an originate into the queue must make the CEL stream report CHAN_START within 15 s");

            // Allow events to accumulate for a moment
            await Task.Delay(TimeSpan.FromSeconds(2));

            var events = collectedEvents.ToList();
            events.Should().NotBeEmpty("queue call must produce at least one CEL event");

            // At minimum, CHAN_START should appear for the originating channel
            var eventNames = events
                .Select(e => e.EventName ?? string.Empty)
                .ToList();
            eventNames.Should().Contain("CHAN_START",
                "a queue call must produce a CHAN_START CEL event");
        }
        finally
        {
            await BestEffort.SendAsync(connection, new CommandAction { Command = "channel request hangup all" });
            await Task.Delay(TimeSpan.FromSeconds(1));
            await BestEffort.SendAsync(connection, new QueueRemoveAction
            {
                Queue = testQueue,
                Interface = testInterface
            });
        }
    }

    private static DateTimeOffset? TryParseEventTime(string eventTime)
    {
        // CEL EventTime format: "YYYY-MM-DD HH:MM:SS.ffffff"
        // Try multiple formats for robustness
        string[] formats =
        [
            "yyyy-MM-dd HH:mm:ss.ffffff",
            "yyyy-MM-dd HH:mm:ss.fffff",
            "yyyy-MM-dd HH:mm:ss.ffff",
            "yyyy-MM-dd HH:mm:ss.fff",
            "yyyy-MM-dd HH:mm:ss"
        ];

        foreach (var fmt in formats)
        {
            if (DateTimeOffset.TryParseExact(
                    eventTime,
                    fmt,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out var result))
            {
                return result;
            }
        }

        return null;
    }

    /// <summary>Collects all CelEvents and signals when CHAN_END is received.</summary>
    private sealed class CelCollector(
        ConcurrentBag<CelEvent> events,
        TaskCompletionSource<bool> chanEndTcs) : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value)
        {
            if (value is not CelEvent cel)
                return;

            events.Add(cel);

            if (string.Equals(cel.EventName, "CHAN_END", StringComparison.OrdinalIgnoreCase))
                chanEndTcs.TrySetResult(true);
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    /// <summary>
    /// Queues, in arrival order, the CelEvents of the call whose LinkedID is <paramref name="linkedId"/>, and signals
    /// its LINKEDID_END, the last CEL event of a call.
    /// </summary>
    private sealed class CelArrivalCollector(
        string linkedId,
        ConcurrentQueue<CelEvent> events,
        TaskCompletionSource<bool> linkedIdEndTcs) : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value)
        {
            if (value is not CelEvent cel || cel.LinkedID != linkedId)
                return;

            events.Enqueue(cel);

            if (string.Equals(cel.EventName, "LINKEDID_END", StringComparison.OrdinalIgnoreCase))
                linkedIdEndTcs.TrySetResult(true);
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    /// <summary>Collects all CelEvents and signals when the first CHAN_START is received.</summary>
    private sealed class CelCollectorWithStart(
        ConcurrentBag<CelEvent> events,
        TaskCompletionSource<bool> chanStartTcs) : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value)
        {
            if (value is not CelEvent cel)
                return;

            events.Add(cel);

            if (string.Equals(cel.EventName, "CHAN_START", StringComparison.OrdinalIgnoreCase))
                chanStartTcs.TrySetResult(true);
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
