using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Live.Queues;
using Verbara.Sdk.Live.Server;

namespace Verbara.Sdk.Live.Tests.Queues;

/// <summary>
/// The queue table's count of held <c>Uniqueid</c>s, which a <c>Hangup</c> reads before it walks the queues: after every
/// way an entry enters or leaves the table it equals a recount of the table, so a <c>Hangup</c> it does not hold has
/// nobody to remove.
/// </summary>
public sealed class QueueHeldUniqueIdIndexTests
{
    private static readonly EventLossEpoch Epoch = new(0, 1, 0);

    private readonly QueueManager _queues = new(NullLogger.Instance);

    [Fact]
    public void OnChannelHungUp_ShouldRemoveTheCallerFromEveryQueue_WhenTwoQueuesHoldItsUniqueId()
    {
        // A caller taken out of q-a by a bridge stays held there, and the same call later waits in q-b.
        _queues.OnCallerJoined("q-a", "PJSIP/a-1", "100", 1, "u1", Epoch);
        _queues.OnCallerJoined("q-b", "PJSIP/a-1", "100", 1, "u1", Epoch);

        _queues.OnChannelHungUp("u1");

        _queues.Queues.Sum(queue => queue.EntryCount).Should().Be(0, "the Hangup is the last of the call in both queues");
    }
}
