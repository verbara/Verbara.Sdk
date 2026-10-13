using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Push.Bus;
using Verbara.Sdk.Push.Diagnostics;
using Verbara.Sdk.Push.Events;
using Verbara.Sdk.Push.Topics;

namespace Verbara.Sdk.Push.Webhooks.Tests;

/// <summary>
/// The unobserved-task assertions read <see cref="TaskScheduler.UnobservedTaskException"/>, which is
/// process-wide: these tests never run alongside another test class.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WebhookProcessWideGroup
{
    public const string Name = "Webhook process-wide state";
}

/// <summary>
/// Spec <c>webhook-delivery-options</c>: a delivery whose backoff fails is dead-lettered and logged once,
/// later events still go out, and a dispatch failure is logged, never left in an unobserved task.
/// </summary>
[Collection(WebhookProcessWideGroup.Name)]
public sealed class WebhookDeliveryGuardTests
{
    private const int BackoffFailedEventId = 8;
    private const int RetriesExhaustedEventId = 5;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Delivery_ShouldDeadLetterWithOneBackoffError_WhenOptionsBecomeUnusableAfterConstruction()
    {
        using var unobserved = new UnobservedTaskExceptions();
        var options = GuardRun.ValidOptions();
        bool deadLettered;
        int calls;
        long deadLetterCount;
        CapturedLogEntry[] errors;
        await using (var run = await GuardRun.StartAsync(options, HttpStatusCode.InternalServerError))
        {
            // The service keeps the host's options object (WebhookDeliveryService.cs:48): a later change reaches it.
            options.InitialDelay = TimeSpan.FromSeconds(1);
            options.MaxDelay = TimeSpan.FromMilliseconds(500);

            await run.PublishAsync("fail.x");
            deadLettered = await run.DeadLetters.WaitForAsync(1, Bound);
            calls = run.Endpoint.Calls;
            deadLetterCount = run.Metrics.DeadLetter;
            errors = run.Log.Entries.Where(e => e.Level >= LogLevel.Error).ToArray();
        }
        var faulted = unobserved.Collect();

        using var scope = new AssertionScope();
        deadLettered.Should().BeTrue("the delivery whose backoff failed is dead-lettered");
        calls.Should().Be(1, "the first attempt fails and no retry can be waited");
        deadLetterCount.Should().Be(1, "the dead-letter counter records the delivery");
        errors.Should().ContainSingle("exactly one Error entry is written for the delivery");
        errors.Should().NotContain(e => e.EventId == RetriesExhaustedEventId,
            "no retry was exhausted, so the 'exhausted retries' entry would be false");
        if (errors.Length == 1)
        {
            errors[0].EventId.Should().Be(BackoffFailedEventId, "it is the new backoff-failure entry, not the exhausted-retries one");
            errors[0].Message.Should().Contain("s-fail", "the entry names the subscription");
            errors[0].Message.Should().Contain(GuardEvent.Type, "the entry names the event type");
            errors[0].Exception.Should().NotBeNull("the entry carries the failure");
        }
        faulted.Should().BeEmpty("no delivery task faults unobserved");
    }

    [Fact]
    public async Task Delivery_ShouldStillDeliverLaterEvents_WhenAnEarlierBackoffFailed()
    {
        var options = GuardRun.ValidOptions();
        await using var run = await GuardRun.StartAsync(options, HttpStatusCode.InternalServerError);
        options.InitialDelay = TimeSpan.FromSeconds(1);
        options.MaxDelay = TimeSpan.FromMilliseconds(500);

        await run.PublishAsync("fail.x");
        // Today nothing marks the end of the lost delivery; the bound is then only the wait for it.
        await run.DeadLetters.WaitForAsync(1, Bound);

        options.InitialDelay = TimeSpan.FromMilliseconds(20);
        options.MaxDelay = TimeSpan.FromMilliseconds(50);
        run.Endpoint.Status = HttpStatusCode.OK;
        await run.PublishAsync("fail.later");
        var delivered = await run.Succeeded.WaitForAsync(1, Bound);

        using var scope = new AssertionScope();
        delivered.Should().BeTrue("a later matching event is delivered");
        run.Metrics.Succeeded.Should().Be(1, "the later event is counted as succeeded");
    }

    [Fact]
    public async Task Dispatch_ShouldLogOneErrorAndKeepDelivering_WhenTopicPathDoesNotParse()
    {
        using var unobserved = new UnobservedTaskExceptions();
        bool delivered;
        int calls;
        CapturedLogEntry[] errors;
        await using (var run = await GuardRun.StartAsync(GuardRun.ValidOptions(), HttpStatusCode.OK, pattern: "queue.**"))
        {
            await run.PublishAsync("queue..x");
            await run.PublishAsync("queue.1.updated");
            delivered = await run.Succeeded.WaitForAsync(1, Bound);
            calls = run.Endpoint.Calls;
            errors = run.Log.Entries.Where(e => e.Level >= LogLevel.Error).ToArray();
        }
        var faulted = unobserved.Collect();

        using var scope = new AssertionScope();
        delivered.Should().BeTrue("queue.1.updated is delivered after the event that cannot be dispatched");
        calls.Should().Be(1, "only the valid event reaches the endpoint");
        errors.Should().ContainSingle("one Error entry names the dispatch failure");
        if (errors.Length == 1)
            errors[0].Exception.Should().BeAssignableTo<ArgumentException>("the entry carries the topic parse failure");
        faulted.Should().BeEmpty("no dispatch task faults unobserved");
    }
}

/// <summary>One captured log entry.</summary>
internal sealed record CapturedLogEntry(LogLevel Level, int EventId, string Message, Exception? Exception);

/// <summary>Captures every entry the delivery service writes.</summary>
internal sealed class CapturingLogger : ILogger<WebhookDeliveryService>
{
    private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();

    public IReadOnlyList<CapturedLogEntry> Entries => _entries.ToArray();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _entries.Enqueue(new CapturedLogEntry(logLevel, eventId.Id, formatter(state, exception), exception));
}

/// <summary>
/// Collects <see cref="TaskScheduler.UnobservedTaskException"/> raised while it is alive. Earlier tests'
/// garbage is finalized before it subscribes; <see cref="Collect"/> forces a collection before reading.
/// </summary>
internal sealed class UnobservedTaskExceptions : IDisposable
{
    private readonly ConcurrentQueue<string> _seen = new();

    public UnobservedTaskExceptions()
    {
        ForceCollection();
        TaskScheduler.UnobservedTaskException += OnUnobserved;
    }

    public IReadOnlyList<string> Collect()
    {
        ForceCollection();
        return _seen.ToArray();
    }

    public void Dispose() => TaskScheduler.UnobservedTaskException -= OnUnobserved;

    private void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        foreach (var inner in e.Exception.InnerExceptions)
            _seen.Enqueue(inner.GetType().Name + ": " + inner.Message);
        e.SetObserved();
    }

    private static void ForceCollection()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
    }
}

/// <summary>Counts up; a waiter learns whether the count was reached within its bound (time is only the failure bound).</summary>
internal sealed class CountSignal
{
    private readonly Lock _gate = new();
    private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = [];
    private int _count;

    public int Count
    {
        get
        {
            lock (_gate)
                return _count;
        }
    }

    public void Add(int value)
    {
        lock (_gate)
        {
            _count += value;
            _waiters.RemoveAll(w => _count >= w.Count && w.Signal.TrySetResult());
        }
    }

    public async Task<bool> WaitForAsync(int count, TimeSpan bound)
    {
        Task signal;
        lock (_gate)
        {
            if (_count >= count) return true;
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, tcs));
            signal = tcs.Task;
        }
        return await Task.WhenAny(signal, Task.Delay(bound)) == signal; // fence-allow: GUARD-TIMEOUT — failure bound on a counted outcome, never the winning arm of a green run
    }
}

/// <summary>
/// An <see cref="IPushEventBus"/> decorator that completes <see cref="Subscribed"/> on the first subscription and
/// counts every subscription in <see cref="Subscriptions"/>.
/// </summary>
internal sealed class SubscriptionSignallingBus(IPushEventBus inner) : IPushEventBus
{
    private readonly TaskCompletionSource _subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Subscribed => _subscribed.Task;

    public CountSignal Subscriptions { get; } = new();

    public ValueTask PublishAsync<TEvent>(TEvent pushEvent, CancellationToken ct = default)
        where TEvent : PushEvent => inner.PublishAsync(pushEvent, ct);

    public IObservable<PushEvent> AsObservable() => new Signalling<PushEvent>(inner.AsObservable(), this);

    public IObservable<TEvent> OfType<TEvent>() where TEvent : PushEvent
        => new Signalling<TEvent>(inner.OfType<TEvent>(), this);

    private sealed class Signalling<T>(IObservable<T> source, SubscriptionSignallingBus owner) : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer)
        {
            var subscription = source.Subscribe(observer);
            owner._subscribed.TrySetResult();
            owner.Subscriptions.Add(1);
            return subscription;
        }
    }
}
