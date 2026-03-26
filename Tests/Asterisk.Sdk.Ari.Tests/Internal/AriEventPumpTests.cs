using Asterisk.Sdk;
using Asterisk.Sdk.Ari.Internal;
using FluentAssertions;

namespace Asterisk.Sdk.Ari.Tests.Internal;

public class AriEventPumpTests
{
    [Fact]
    public async Task TryEnqueue_ShouldDispatchEvent()
    {
        await using var pump = new AriEventPump();
        var dispatched = new TaskCompletionSource<AriEvent>();

        pump.Start(evt =>
        {
            dispatched.TrySetResult(evt);
            return ValueTask.CompletedTask;
        });

        var ariEvent = new AriEvent { Type = "Test" };
        pump.TryEnqueue(ariEvent).Should().BeTrue();

        var result = await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(2));
        result.Type.Should().Be("Test");
        pump.ProcessedEvents.Should().BeGreaterOrEqualTo(1);
    }

    [Fact]
    public async Task TryEnqueue_ShouldAcceptEvents_WithBoundedCapacity()
    {
        await using var pump = new AriEventPump(capacity: 2);
        var dispatched = new List<AriEvent>();
        var allDispatched = new TaskCompletionSource();

        pump.Start(evt =>
        {
            dispatched.Add(evt);
            if (dispatched.Count >= 2)
                allDispatched.TrySetResult();
            return ValueTask.CompletedTask;
        });

        // Enqueue more than capacity — DropOldest means all TryWrite succeed
        // but oldest items are discarded silently by the channel
        for (var i = 0; i < 5; i++)
            pump.TryEnqueue(new AriEvent { Type = $"Event-{i}" });

        await allDispatched.Task.WaitAsync(TimeSpan.FromSeconds(2));
        pump.ProcessedEvents.Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public async Task DisposeAsync_ShouldComplete()
    {
        var pump = new AriEventPump();
        pump.Start(_ => ValueTask.CompletedTask);
        pump.TryEnqueue(new AriEvent { Type = "Test" });

        // Should complete without hanging or throwing
        await pump.DisposeAsync();
    }

    [Fact]
    public async Task PendingCount_ShouldReflectQueuedEvents()
    {
        // Don't start the consumer — events stay queued
        await using var pump = new AriEventPump(capacity: 10);

        pump.TryEnqueue(new AriEvent { Type = "A" });
        pump.TryEnqueue(new AriEvent { Type = "B" });
        pump.TryEnqueue(new AriEvent { Type = "C" });

        pump.PendingCount.Should().Be(3);
    }

    [Fact]
    public async Task ProcessedEvents_ShouldIncrement_WhenEventsDispatched()
    {
        await using var pump = new AriEventPump();
        var allDone = new TaskCompletionSource();

        pump.Start(evt =>
        {
            if (pump.ProcessedEvents >= 3)
                allDone.TrySetResult();
            return ValueTask.CompletedTask;
        });

        pump.TryEnqueue(new AriEvent { Type = "E1" });
        pump.TryEnqueue(new AriEvent { Type = "E2" });
        pump.TryEnqueue(new AriEvent { Type = "E3" });

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(2));
        pump.ProcessedEvents.Should().BeGreaterOrEqualTo(3);
    }

    [Fact]
    public async Task OnEventDropped_ShouldBeNullByDefault_AndSettable()
    {
        await using var pump = new AriEventPump();

        pump.OnEventDropped.Should().BeNull();

        Action<AriEvent> callback = _ => { };
        pump.OnEventDropped = callback;

        pump.OnEventDropped.Should().BeSameAs(callback);
    }

    [Fact]
    public void DefaultCapacity_ShouldBe20000()
    {
        AriEventPump.DefaultCapacity.Should().Be(20_000);
    }

    [Fact]
    public async Task DroppedEvents_ShouldBeZero_WhenNoDrops()
    {
        await using var pump = new AriEventPump();
        pump.DroppedEvents.Should().Be(0);
    }
}
