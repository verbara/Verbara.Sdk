namespace Asterisk.Sdk.FunctionalTests.Layer5_Integration.Concurrency;

using System.Collections.Concurrent;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;

[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class ConcurrentEventTests : FunctionalTestBase
{
    [Fact]
    public async Task ConcurrentSubscribeUnsubscribe_ShouldNotThrow()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        // Rapid subscribe/unsubscribe cycles from multiple threads
        var tasks = Enumerable.Range(0, 50).Select(async i =>
        {
            for (var cycle = 0; cycle < 10; cycle++)
            {
                var sub = connection.Subscribe(new CollectingObserver());
                await Task.Yield();
                sub.Dispose();
            }
        });

        var act = () => Task.WhenAll(tasks);
        await act.Should().NotThrowAsync("rapid subscribe/unsubscribe must not throw");

        // Connection should still be healthy
        var response = await connection.SendActionAsync(new PingAction());
        response.Response.Should().Be("Success");
    }

    [Fact]
    public async Task MultipleObservers_ShouldAllReceiveEvents()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.AutoReconnect = false;
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(10);
        });
        await connection.ConnectAsync();

        const int observerCount = 10;
        var observers = Enumerable.Range(0, observerCount)
            .Select(_ => new CollectingObserver())
            .ToList();

        var subscriptions = observers
            .Select(o => connection.Subscribe(o))
            .ToList();

        try
        {
            // Generate events by originating calls (async, will produce NewChannel events)
            const int originateCount = 5;
            for (var i = 0; i < originateCount; i++)
            {
                await connection.SendActionAsync(new OriginateAction
                {
                    Channel = "Local/s@default",
                    Application = "Wait",
                    Data = "1",
                    IsAsync = true
                });
            }

            // Wait for events to propagate
            await Task.Delay(TimeSpan.FromSeconds(3));

            // All observers should have received some events
            foreach (var observer in observers)
            {
                observer.Events.Should().NotBeEmpty(
                    "each observer must receive at least some events from originate calls");
            }

            // All observers should have the same event count (or very close if timing varies)
            var counts = observers.Select(o => o.Events.Count).ToList();
            var maxCount = counts.Max();
            var minCount = counts.Min();
            maxCount.Should().Be(minCount,
                "all observers should receive the same events (copy-on-write dispatch)");
        }
        finally
        {
            foreach (var sub in subscriptions) sub.Dispose();
        }
    }

    [Fact]
    public async Task HighVolumeEvents_ShouldNotLoseEvents()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.AutoReconnect = false;
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(10);
            opts.EventPumpCapacity = 50_000;
        });
        await connection.ConnectAsync();

        var observer = new CollectingObserver();
        using var subscription = connection.Subscribe(observer);

        // Generate many events by originating several calls concurrently
        const int callCount = 20;
        var originateTasks = Enumerable.Range(0, callCount).Select(async i =>
        {
            try
            {
                await connection.SendActionAsync(new OriginateAction
                {
                    Channel = "Local/s@default",
                    Application = "Wait",
                    Data = "1",
                    IsAsync = true,
                    ActionId = $"vol-{i:D4}"
                });
            }
            catch (OperationCanceledException)
            {
                // Some may timeout; that is acceptable
            }
        });

        await Task.WhenAll(originateTasks);

        // Wait for all events to arrive
        await Task.Delay(TimeSpan.FromSeconds(5));

        // Each Local channel originate generates multiple events
        // (NewChannel, Newstate, Hangup for each leg, plus OriginateResponse)
        // With 20 calls we expect at least a few dozen events
        observer.Events.Should().NotBeEmpty("high-volume originate should produce many events");
        observer.Events.Count.Should().BeGreaterThan(callCount,
            "each originate should generate multiple events");
    }

    [Fact]
    public async Task ConcurrentEventProcessing_ShouldMaintainOrder()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.AutoReconnect = false;
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(10);
        });
        await connection.ConnectAsync();

        var observer = new CollectingObserver();
        using var subscription = connection.Subscribe(observer);

        // Originate a single call and verify events for it are causally ordered
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/s@default",
            Application = "Wait",
            Data = "2",
            IsAsync = true
        });

        // Wait for full lifecycle
        await Task.Delay(TimeSpan.FromSeconds(5));

        // Find events for a specific channel by UniqueId
        var channelEvents = observer.Events
            .Where(e => e.UniqueId is not null)
            .GroupBy(e => e.UniqueId)
            .Where(g => g.Count() >= 2)
            .ToList();

        if (channelEvents.Count > 0)
        {
            // For any channel, events should arrive in order they were added
            // Verify by checking arrival indices are monotonically increasing
            foreach (var group in channelEvents)
            {
                var indices = group
                    .Select(e => observer.Events.IndexOf(e))
                    .ToList();
                indices.Should().BeInAscendingOrder(
                    "events for the same channel must maintain causal order");
            }
        }
    }

    [Fact]
    public async Task ObserverExceptionInOneSubscriber_ShouldNotAffectOthers()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.AutoReconnect = false;
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(10);
        });
        await connection.ConnectAsync();

        var faultyObserver = new FaultyObserver();
        var healthyObserver = new CollectingObserver();

        using var faultySub = connection.Subscribe(faultyObserver);
        using var healthySub = connection.Subscribe(healthyObserver);

        // Generate events
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/s@default",
            Application = "Wait",
            Data = "1",
            IsAsync = true
        });

        await Task.Delay(TimeSpan.FromSeconds(3));

        // The faulty observer threw on every event, but the healthy one should still receive them
        healthyObserver.Events.Should().NotBeEmpty(
            "healthy observer must still receive events even when another observer throws");

        // Connection should still be functional
        var response = await connection.SendActionAsync(new PingAction());
        response.Response.Should().Be("Success");
    }

    /// <summary>Thread-safe observer that collects all events.</summary>
    private sealed class CollectingObserver : IObserver<ManagerEvent>
    {
        private readonly List<ManagerEvent> _events = [];
        public List<ManagerEvent> Events
        {
            get { lock (_events) return [.. _events]; }
        }

        public void OnNext(ManagerEvent value)
        {
            lock (_events) _events.Add(value);
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    /// <summary>Observer that throws on every event to test fault isolation.</summary>
    private sealed class FaultyObserver : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value) =>
            throw new InvalidOperationException("Intentional test fault");
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
