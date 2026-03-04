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
}
