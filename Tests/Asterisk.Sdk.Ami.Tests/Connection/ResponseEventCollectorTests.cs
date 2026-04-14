using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Connection;
using FluentAssertions;

namespace Asterisk.Sdk.Ami.Tests.Connection;

public class ResponseEventCollectorTests
{
    private static ManagerEvent CreateEvent(string type) =>
        new() { EventType = type };

    [Fact]
    public void Add_ShouldBufferEvents()
    {
        var collector = new ResponseEventCollector();

        collector.Add(CreateEvent("Status"));
        collector.Add(CreateEvent("Status"));
        collector.Add(CreateEvent("Status"));
        collector.Complete();

        // Should not throw; events are buffered
    }

    [Fact]
    public async Task Complete_ShouldEndEnumeration()
    {
        var collector = new ResponseEventCollector();

        collector.Add(CreateEvent("Status"));
        collector.Complete();

        var events = new List<ManagerEvent>();
        await foreach (var evt in collector.ReadAllAsync())
        {
            events.Add(evt);
        }

        events.Should().HaveCount(1);
        events[0].EventType.Should().Be("Status");
    }

    [Fact]
    public async Task ReadAllAsync_ShouldYieldEventsInOrder()
    {
        var collector = new ResponseEventCollector();

        collector.Add(CreateEvent("First"));
        collector.Add(CreateEvent("Second"));
        collector.Add(CreateEvent("Third"));
        collector.Complete();

        var types = new List<string>();
        await foreach (var evt in collector.ReadAllAsync())
        {
            types.Add(evt.EventType!);
        }

        types.Should().ContainInOrder("First", "Second", "Third");
    }

    [Fact]
    public async Task ReadAllAsync_ShouldRespectCancellation()
    {
        var collector = new ResponseEventCollector();
        collector.Add(CreateEvent("A"));
        // Don't call Complete — simulate long-running event collection

        using var cts = new CancellationTokenSource();
        var events = new List<ManagerEvent>();

        var firstEventRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readTask = Task.Run(async () =>
        {
            await foreach (var evt in collector.ReadAllAsync(cts.Token))
            {
                events.Add(evt);
                firstEventRead.TrySetResult();
            }
        });

        // Wait until the first event is consumed before cancelling (avoids timing-based flakiness)
        await firstEventRead.Task;
        await cts.CancelAsync();

        var act = async () => await readTask;
        await act.Should().ThrowAsync<OperationCanceledException>();
        events.Should().HaveCount(1);
    }

    [Fact]
    public void Add_AfterComplete_ShouldNotThrow()
    {
        var collector = new ResponseEventCollector();
        collector.Complete();

        var act = () => collector.Add(CreateEvent("Late"));
        act.Should().NotThrow();
    }
}
