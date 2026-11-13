using System.Globalization;
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
    private const string Queue = "q-sales";
    private static readonly EventLossEpoch Epoch = new(0, 1, 0);

    private readonly QueueManager _queues = new(NullLogger.Instance);

    [Fact]
    public void OnCallerJoined_ShouldCountItsUniqueIdOnce_WhenTheSameCallerJoinsTwice()
    {
        _queues.OnCallerJoined(Queue, "PJSIP/a-1", "100", 1, "u1", Epoch);
        ShouldMatchTheTable().Should().Equal(Held(("u1", 1)));

        _queues.OnCallerJoined(Queue, "PJSIP/a-1", "100", 2, "u1", Epoch);

        ShouldMatchTheTable().Should().Equal(Held(("u1", 1)), "a duplicate join replaces nothing and inserts nothing");
    }

    [Fact]
    public void OnCallerJoined_ShouldKeepTheCount_WhenTheSameUniqueIdJoinsOnAnotherChannel()
    {
        _queues.OnCallerJoined(Queue, "PJSIP/a-1", "100", 1, "u1", Epoch);

        _queues.OnCallerJoined(Queue, "PJSIP/a-1<ZOMBIE>", "100", 1, "u1", Epoch);

        _queues.GetByName(Queue)!.Entries.Keys.Should().Equal(["PJSIP/a-1<ZOMBIE>"], "premise: the held caller was re-keyed");
        ShouldMatchTheTable().Should().Equal(Held(("u1", 1)), "a re-key takes the caller out and stores it again");
    }

    [Fact]
    public void OnCallerJoined_ShouldReleaseTheDisplacedCaller_WhenAnotherUniqueIdJoinsOnItsChannel()
    {
        _queues.OnCallerJoined(Queue, "PJSIP/a-1", "100", 1, "u1", Epoch);

        _queues.OnCallerJoined(Queue, "PJSIP/a-1", "200", 1, "u2", Epoch);

        ShouldMatchTheTable().Should().Equal(Held(("u2", 1)), "the caller held under the reused name is no longer there");
    }

    [Fact]
    public void OnCallerJoined_ShouldReleaseTheDisplacedCaller_WhenARenamedCallerTakesItsChannel()
    {
        _queues.OnCallerJoined(Queue, "PJSIP/a-1", "100", 1, "u1", Epoch);
        _queues.OnCallerJoined(Queue, "PJSIP/b-1", "200", 1, "u2", Epoch);

        // u1 is reported on b-1, where u2 is held: u1 is re-keyed there and u2 displaced.
        _queues.OnCallerJoined(Queue, "PJSIP/b-1", "100", 1, "u1", Epoch);

        ShouldMatchTheTable().Should().Equal(Held(("u1", 1)));
    }

    [Fact]
    public void OnCallerJoined_ShouldCountNothing_WhenTheCallerCarriesNoUniqueId()
    {
        _queues.OnCallerJoined(Queue, "PJSIP/a-1", "100", 1);

        _queues.GetByName(Queue)!.EntryCount.Should().Be(1, "premise: the caller is held");
        ShouldMatchTheTable().Should().BeEmpty("only a Uniqueid can be matched by a Hangup");
    }

    [Fact]
    public void OnCallerLeft_ShouldReleaseItsUniqueId_WhenTheCallerLeaves()
    {
        _queues.OnCallerJoined(Queue, "PJSIP/a-1", "100", 1, "u1", Epoch);

        _queues.OnCallerLeft(Queue, "PJSIP/a-1");

        ShouldMatchTheTable().Should().BeEmpty();
    }

    [Fact]
    public void OnChannelHungUp_ShouldReleaseItsUniqueId_WhenTheCallerHangsUp()
    {
        _queues.OnCallerJoined(Queue, "PJSIP/a-1", "100", 1, "u1", Epoch);
        _queues.OnCallerJoined(Queue, "PJSIP/a-2", "200", 1, "u2", Epoch);

        _queues.OnChannelHungUp("u1");

        ShouldMatchTheTable().Should().Equal(Held(("u2", 1)));
    }

    [Fact]
    public void OnChannelHungUp_ShouldRemoveTheCallerFromEveryQueue_WhenTwoQueuesHoldItsUniqueId()
    {
        // A caller taken out of q-a by a bridge stays held there, and the same call later waits in q-b.
        _queues.OnCallerJoined("q-a", "PJSIP/a-1", "100", 1, "u1", Epoch);
        _queues.OnCallerJoined("q-b", "PJSIP/a-1", "100", 1, "u1", Epoch);
        ShouldMatchTheTable().Should().Equal(Held(("u1", 2)), "premise: one Uniqueid, two entries");

        _queues.OnChannelHungUp("u1");

        _queues.Queues.Sum(queue => queue.EntryCount).Should().Be(0, "the Hangup is the last of the call in both queues");
        ShouldMatchTheTable().Should().BeEmpty();
    }

    [Fact]
    public void OnChannelRemovedByReconciliation_ShouldReleaseItsUniqueId_WhenAReloadRemovesTheChannel()
    {
        _queues.OnCallerJoined(Queue, "PJSIP/a-1", "100", 1, "u1", Epoch);

        _queues.OnChannelRemovedByReconciliation("u1", "PJSIP/a-1");

        ShouldMatchTheTable().Should().BeEmpty();
    }

    [Fact]
    public void CompleteSnapshot_ShouldReleaseTheUniqueId_WhenTheSnapshotNoLongerListsTheCaller()
    {
        _queues.OnCallerJoined(Queue, "PJSIP/a-1", "100", 1, "u1", Epoch);
        using var window = _queues.OpenSnapshotWindow();
        _queues.OnSnapshotQueueParams(window, Queue, 0, "ringall", 0, 0, 0, 0, 0);
        _queues.OnSnapshotEntry(window, Queue, "PJSIP/a-2", "u2", "200", 1, 3, Epoch);

        _queues.CompleteSnapshot(window);

        _queues.GetByName(Queue)!.Entries.Keys.Should().Equal(["PJSIP/a-2"], "premise: the unlisted caller was removed");
        ShouldMatchTheTable().Should().Equal(Held(("u2", 1)));
    }

    [Fact]
    public void RemoveQueue_ShouldReleaseEveryUniqueIdItHeld_WhenTheQueueIsRemoved()
    {
        _queues.OnCallerJoined(Queue, "PJSIP/a-1", "100", 1, "u1", Epoch);
        _queues.OnCallerJoined("q-other", "PJSIP/a-2", "200", 1, "u2", Epoch);

        _queues.RemoveQueue(Queue);

        ShouldMatchTheTable().Should().Equal(Held(("u2", 1)));
    }

    [Fact]
    public void Clear_ShouldReleaseEveryUniqueId_WhenTheTableIsEmptied()
    {
        _queues.OnCallerJoined(Queue, "PJSIP/a-1", "100", 1, "u1", Epoch);
        _queues.OnCallerJoined("q-other", "PJSIP/a-2", "200", 1, "u2", Epoch);

        _queues.Clear();

        ShouldMatchTheTable().Should().BeEmpty();
    }

    [Fact]
    public void Clear_ShouldForgetAUniqueIdLeftBehind_WhenAConsumerRemovedItsCallerDirectly()
    {
        _queues.OnCallerJoined(Queue, "PJSIP/a-1", "100", 1, "u1", Epoch);
        _queues.OnCallerJoined(Queue, "PJSIP/a-2", "200", 1, "u2", Epoch);
        // A consumer that breaks the read-only contract of Entries: the count no longer sees this removal.
        _queues.GetByName(Queue)!.Entries.TryRemove("PJSIP/a-1", out _).Should().BeTrue("premise: removed directly");
        _queues.HeldUniqueIdCounts().Should().Equal(Held(("u1", 1), ("u2", 1)), "premise: the count kept u1, stale");

        _queues.Clear();

        _queues.HeldUniqueIdCounts().Should().BeEmpty(
            "the table is empty after a Clear, so a key a direct removal left behind must not outlive it");
    }

    // Eight threads at once on a small pool of queues, channels and Uniqueids, so joins re-key, displace and land in
    // queues a snapshot, RemoveQueue or Clear is taking out. At rest the count equals a recount of the table. Bounded:
    // a fixed number of operations from fixed seeds, a few hundred milliseconds.
    [Fact]
    public void QueueTable_ShouldCountEveryHeldUniqueId_WhenJoinsLeavesHangupsAndReloadsRunConcurrently()
    {
        const int Rounds = 3;
        const int Threads = 8;
        const int Operations = 10_000;

        for (var round = 0; round < Rounds; round++)
        {
            var queues = new QueueManager(NullLogger.Instance);
            using var start = new Barrier(Threads);
            var workers = Enumerable.Range(0, Threads)
                .Select(t => new Thread(() => Churn(queues, start, seed: (uint)((round * 100) + t + 1), Operations)))
                .ToArray();
            foreach (var worker in workers)
                worker.Start();
            foreach (var worker in workers)
                worker.Join();

            queues.HeldUniqueIdCounts().Should().BeEquivalentTo(Recount(queues),
                string.Create(CultureInfo.InvariantCulture, $"round {round}: the count must equal the table at rest"));
        }
    }

    private static void Churn(QueueManager queues, Barrier start, uint seed, int operations)
    {
        var random = seed;
        uint Next(uint bound)
        {
            random ^= random << 13;
            random ^= random >> 17;
            random ^= random << 5;
            return random % bound;
        }

        string QueueName() => string.Create(CultureInfo.InvariantCulture, $"q-{Next(6)}");
        string Channel() => string.Create(CultureInfo.InvariantCulture, $"PJSIP/c-{Next(40)}");
        string UniqueId() => string.Create(CultureInfo.InvariantCulture, $"17.{Next(60)}");

        start.SignalAndWait();
        for (var i = 0; i < operations; i++)
        {
            switch (Next(100))
            {
                case < 40: queues.OnCallerJoined(QueueName(), Channel(), "1", 1, UniqueId(), Epoch); break;
                case < 45: queues.OnCallerJoined(QueueName(), Channel(), "1", 1); break;
                case < 65: queues.OnCallerLeft(QueueName(), Channel()); break;
                case < 85: queues.OnChannelHungUp(UniqueId()); break;
                case < 90: queues.OnChannelRemovedByReconciliation(UniqueId(), Channel()); break;
                case < 97:
                    using (var window = queues.OpenSnapshotWindow())
                    {
                        for (var k = 0; k < 5; k++)
                            queues.OnSnapshotEntry(window, QueueName(), Channel(), UniqueId(), "1", 1, 3, Epoch);
                        if (Next(2) == 0)
                            queues.CompleteSnapshot(window);
                    }
                    break;
                case < 99: queues.RemoveQueue(QueueName()); break;
                default: queues.Clear(); break;
            }
        }
    }

    // Asserts the count equals a recount of the table, and returns it.
    private Dictionary<string, int> ShouldMatchTheTable()
    {
        var counts = _queues.HeldUniqueIdCounts();
        counts.Should().BeEquivalentTo(Recount(_queues), "the count of held Uniqueids is a recount of the table");
        return counts;
    }

    private static Dictionary<string, int> Recount(QueueManager queues) =>
        queues.Queues
            .SelectMany(queue => queue.Entries.Values)
            .Where(entry => entry.UniqueId is not null)
            .GroupBy(entry => entry.UniqueId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    private static Dictionary<string, int> Held(params (string UniqueId, int Entries)[] held) =>
        held.ToDictionary(pair => pair.UniqueId, pair => pair.Entries, StringComparer.Ordinal);
}
