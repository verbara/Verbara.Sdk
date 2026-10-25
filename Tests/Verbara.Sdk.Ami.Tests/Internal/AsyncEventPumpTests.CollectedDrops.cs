using Verbara.Sdk;
using Verbara.Sdk.Ami.Internal;
using FluentAssertions;
using FluentAssertions.Execution;

namespace Verbara.Sdk.Ami.Tests.Internal;

/// <summary>
/// A refused event that an action had already received in its answer (enqueued as collected) is counted in
/// <see cref="AsyncEventPump.DroppedEvents"/>, which every refused event moves, and not in the drop count a later
/// event carries to its handler, which counts only what was lost.
/// </summary>
public partial class AsyncEventPumpTests
{
    [Fact]
    public async Task DroppedBeforeCurrentEvent_ShouldNotCountARefusedCollectedEvent_AndDroppedEventsShouldCountBoth()
    {
        var pump = new AsyncEventPump(capacity: 2);
        await using var _ = pump;
        var seen = new List<(string Type, long DroppedBefore)>();
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var last = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        pump.TryEnqueue(CreateEvent("E1"), collected: false).Should().BeTrue();
        pump.TryEnqueue(CreateEvent("E2"), collected: false).Should().BeTrue();
        pump.TryEnqueue(CreateEvent("C1"), collected: true).Should().BeFalse("premise: the full buffer refuses the collected copy");
        pump.TryEnqueue(CreateEvent("U1"), collected: false).Should().BeFalse("premise: the full buffer refuses the uncollected event");
        pump.TryEnqueue(CreateEvent("C2"), collected: true).Should().BeFalse("premise: the full buffer refuses the second collected copy");
        pump.Start(evt =>
        {
            lock (seen)
                seen.Add((evt.EventType!, pump.DroppedBeforeCurrentEvent));

            if (evt.EventType == "E2")
                second.TrySetResult();
            if (evt.EventType == "E3")
                last.TrySetResult();
            return ValueTask.CompletedTask;
        });
        await second.Task.WaitAsync(Bound);
        pump.TryEnqueue(CreateEvent("E3"), collected: false).Should().BeTrue();
        await last.Task.WaitAsync(Bound);

        using (new AssertionScope())
        {
            pump.DroppedEvents.Should().Be(3, "every refused event is counted, the collected copies included");
            lock (seen)
            {
                seen.Should().Equal([("E1", 0L), ("E2", 0L), ("E3", 1L)],
                    "of the three refused before E3, only U1 was lost; C1 and C2 were copies an action had received");
            }
        }
    }
}
