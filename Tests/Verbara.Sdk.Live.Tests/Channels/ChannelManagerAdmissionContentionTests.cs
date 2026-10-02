using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Verbara.Sdk.Live.Tests.Channels;

/// <summary>
/// The two admission races of the channel table that have no ordering point a test can step into:
/// nothing a test can inject runs between the reconciliation's "is it held?" and its admission, nor
/// between an admission's insert and its announcement. Each is proven by contention instead: a round
/// releases the two threads together through a <see cref="Barrier"/>, each round is joined under a
/// bound so a deadlock fails as a timeout, and the counts of every round are checked. A round whose
/// counts are wrong is a red round; the test asserts there were none.
/// </summary>
public sealed class ChannelManagerAdmissionContentionTests
{
    private const int Rounds = 2_000;

    /// <summary>Failure bound for one round: two threads meeting at the barrier and running one step each.</summary>
    private static readonly TimeSpan RoundBound = TimeSpan.FromSeconds(10);

    private const string UniqueId = "1700000000.1";
    private const string Name = "PJSIP/2000-0001";

    [Fact]
    public async Task ReconcileWithSnapshot_ShouldAdmitTheChannelOnce_WhenItsLiveArrivalIsHandledAtTheSameTime()
    {
        var redRounds = 0;
        string? firstRed = null;

        for (var round = 0; round < Rounds; round++)
        {
            var manager = new ChannelManager(NullLogger.Instance);
            var announced = 0;
            manager.ChannelAdded += channel =>
            {
                if (channel.UniqueId == UniqueId)
                    Interlocked.Increment(ref announced);
            };

            // The read began before the channel existed on either route.
            using (var window = manager.OpenReadWindow())
            {
                await RaceAsync(
                    () => manager.ReconcileWithSnapshot([new ChannelSnapshotEntry(UniqueId, Name, ChannelState.Up)], window),
                    () => manager.OnNewChannel(UniqueId, Name, ChannelState.Up));
            }

            var announcements = Volatile.Read(ref announced);
            if (announcements != 1 || manager.ChannelCount != 1)
            {
                redRounds++;
                firstRed ??= $"round {round}: {announcements} announcement(s), {manager.ChannelCount} held";
            }
        }

        redRounds.Should().Be(0,
            $"the snapshot and the live arrival report one channel, so whichever admits it first is the only "
            + $"admission and the other announces nothing ({Rounds} rounds, both routes released together)"
            + $"; first red round: {firstRed}");
    }

    [Fact]
    public async Task OnHangup_ShouldNeverAnnounceTheRemovalBeforeTheAdmission_WhenTheChannelHangsUpWhileItIsAdmitted()
    {
        var redRounds = 0;
        string? firstRed = null;

        for (var round = 0; round < Rounds; round++)
        {
            var manager = new ChannelManager(NullLogger.Instance);
            var order = new List<string>(2);
            var gate = new Lock();
            manager.ChannelAdded += _ =>
            {
                lock (gate)
                {
                    order.Add("added");
                }
            };
            manager.ChannelRemoved += _ =>
            {
                lock (gate)
                {
                    order.Add("removed");
                }
            };

            await RaceAsync(
                () => manager.OnNewChannel(UniqueId, Name, ChannelState.Up),
                () => manager.OnHangup(UniqueId, HangupCause.NormalClearing));

            string[] seen;
            lock (gate)
            {
                seen = [.. order];
            }

            // Either the hangup found nothing to remove (the channel is then held and was announced once),
            // or it removed the channel after its announcement (nothing is held, by either index).
            var held = manager.GetByUniqueId(UniqueId) is not null;
            var named = manager.GetByName(Name) is not null;
            var consistent = held
                ? seen is ["added"] && named
                : seen is ["added", "removed"] && !named;
            if (!consistent)
            {
                redRounds++;
                firstRed ??= $"round {round}: announced [{string.Join(", ", seen)}], held {held}, held by name {named}";
            }
        }

        redRounds.Should().Be(0,
            $"a channel's removal is never announced before its admission, and the table never keeps by one index "
            + $"what it dropped by the other ({Rounds} rounds, admission and hangup released together)"
            + $"; first red round: {firstRed}");
    }

    /// <summary>
    /// Run <paramref name="first"/> and <paramref name="second"/> on two thread-pool threads released together
    /// by a barrier, and wait for both under <see cref="RoundBound"/>.
    /// </summary>
    private static async Task RaceAsync(Action first, Action second)
    {
        using var barrier = new Barrier(2);
        var one = Task.Run(() => RunAfter(barrier, first));
        var two = Task.Run(() => RunAfter(barrier, second));
        await Task.WhenAll(one, two).WaitAsync(RoundBound);
    }

    private static void RunAfter(Barrier barrier, Action step)
    {
        if (!barrier.SignalAndWait(RoundBound))
            throw new TimeoutException("the other thread of the round never reached the barrier");
        step();
    }
}
