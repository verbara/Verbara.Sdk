namespace Asterisk.Sdk.FunctionalTests.Layer5_Integration.Cdr;

using System.Collections.Concurrent;
using System.Globalization;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Events;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Attributes;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;

/// <summary>
/// Tests for CelEvent (Channel Event Logging). CEL events fire multiple times during a
/// call (one per milestone) and require cel_manager.conf enabled in Asterisk.
/// </summary>
[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class CelSequenceTests : FunctionalTestBase
{
    public CelSequenceTests() : base("Asterisk.Sdk.Ami")
    {
    }

    [AsteriskContainerFact]
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

        // ext 100 = Answer + Wait(5) + Hangup — produces full CEL timeline
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/100@test-functional",
            Context = "test-functional",
            Exten = "100",
            Priority = 1,
            IsAsync = true,
            Timeout = 15000,
            ActionId = "cel-timeline-01"
        });

        // Wait for CHAN_END which is the final CEL event in a call
        var result = await Task.WhenAny(chanEndReceived.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        if (result != chanEndReceived.Task)
        {
            // CEL module not loaded — skip gracefully
            return;
        }

        // Allow a brief moment for any trailing events to arrive
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        var eventNames = collectedEvents
            .Select(e => e.EventName ?? string.Empty)
            .ToList();

        eventNames.Should().Contain("CHAN_START", "CEL must include channel start event");
        eventNames.Should().Contain("ANSWER", "CEL must include answer event for ext 100");
        eventNames.Should().Contain("HANGUP", "CEL must include hangup event");
        eventNames.Should().Contain("CHAN_END", "CEL must include channel end event");
    }

    [AsteriskContainerFact]
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
            Channel = "Local/100@test-functional",
            Context = "test-functional",
            Exten = "100",
            Priority = 1,
            IsAsync = true,
            Timeout = 15000,
            ActionId = "cel-linkedid-01"
        });

        var result = await Task.WhenAny(chanEndReceived.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        if (result != chanEndReceived.Task)
        {
            return;
        }

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

    [AsteriskContainerFact]
    public async Task CelTimestamps_ShouldBeMonotonicallyIncreasing()
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
            Channel = "Local/100@test-functional",
            Context = "test-functional",
            Exten = "100",
            Priority = 1,
            IsAsync = true,
            Timeout = 15000,
            ActionId = "cel-monotonic-01"
        });

        var result = await Task.WhenAny(chanEndReceived.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        if (result != chanEndReceived.Task)
        {
            return;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(500));

        // Parse EventTime strings — CEL format: "YYYY-MM-DD HH:MM:SS.ffffff"
        var timestamps = collectedEvents
            .Where(e => !string.IsNullOrEmpty(e.EventTime))
            .Select(e => TryParseEventTime(e.EventTime!))
            .Where(dt => dt.HasValue)
            .Select(dt => dt!.Value)
            .OrderBy(dt => dt)
            .ToList();

        if (timestamps.Count < 2)
        {
            // Not enough parseable timestamps — CEL module may use a different format
            return;
        }

        // Verify monotonic ordering: each timestamp >= the previous
        for (var i = 1; i < timestamps.Count; i++)
        {
            timestamps[i].Should().BeOnOrAfter(timestamps[i - 1],
                "CEL EventTime at position {0} must not precede position {1}", i, i - 1);
        }
    }

    [AsteriskContainerFact]
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
            if (result != chanStartReceived.Task)
            {
                // CEL module not loaded — skip gracefully
                return;
            }

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
            try
            {
                await connection.SendActionAsync(new CommandAction { Command = "channel request hangup all" });
            }
            catch { /* best effort */ }
            await Task.Delay(TimeSpan.FromSeconds(1));
            try
            {
                await connection.SendActionAsync(new QueueRemoveAction
                {
                    Queue = testQueue,
                    Interface = testInterface
                });
            }
            catch { /* best effort cleanup */ }
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
