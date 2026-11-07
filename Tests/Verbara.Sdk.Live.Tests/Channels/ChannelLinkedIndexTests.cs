using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Channels;
using Verbara.Sdk.Live.Tests.Harness;

namespace Verbara.Sdk.Live.Tests.Channels;

public sealed class ChannelLinkedIndexTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("call")]
    public void LinkedIndex_ShouldUseEngineKeyAndClearAllEntries_WhenChannelsAreAdmitted(string? linkedId)
    {
        var manager = NewManager();
        Add(manager, "x", linkedId);
        var key = string.IsNullOrEmpty(linkedId) ? "x" : linkedId;
        manager.HoldsLinkedId(key).Should().BeTrue();
        manager.IsHeldOrDeparting("x").Should().BeTrue();
        var channels = manager.ChannelsOfLinkedId(key);
        channels.Length.Should().Be(1);
        channels[0].AdmissionMark.Should().BeGreaterThan(0);
        ChannelManager.IsDeparting(channels[0]).Should().BeFalse();
        manager.Clear();
        manager.HoldsLinkedId(key).Should().BeFalse();
        manager.IsHeldOrDeparting("x").Should().BeFalse();
        manager.ChannelsOfLinkedId(key).Length.Should().Be(0);
    }

    [Fact]
    public void LinkedIndex_ShouldRetainEveryOtherInstance_WhenAGroupGrowsAndShrinks()
    {
        var manager = NewManager();
        Add(manager, "x", "call");
        Add(manager, "y", "call");
        Add(manager, "z", "call");
        var retained = manager.ChannelsOfLinkedId("call");
        retained.Length.Should().Be(3);
        manager.OnHangup("y");
        manager.ChannelsOfLinkedId("call").Length.Should().Be(2);
        manager.ChannelsOfLinkedId("call")[0].UniqueId.Should().Be("x");
        manager.ChannelsOfLinkedId("call")[1].UniqueId.Should().Be("z");
        retained.Length.Should().Be(3, "a retained immutable group remains safe while the index changes");
        manager.OnHangup("x");
        manager.ChannelsOfLinkedId("call").Length.Should().Be(1);
        manager.ChannelsOfLinkedId("call")[0].UniqueId.Should().Be("z");
        manager.OnHangup("z");
        manager.ChannelsOfLinkedId("call").Length.Should().Be(0);
        manager.HoldsLinkedId("call").Should().BeFalse();
    }

    [Fact]
    public async Task OnHangup_ShouldCountDepartureBeforeTakingChannelLock_WhenItsSyncRootIsHeld()
    {
        var manager = NewManager();
        Add(manager, "x", "call");
        var channel = manager.GetByUniqueId("x")!;
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            lock (channel.SyncRoot)
            {
                held.Set();
                release.Wait(Run.Bound).Should().BeTrue();
            }
        });
        Task? hangup = null;
        try
        {
            held.Wait(Run.Bound).Should().BeTrue();
            hangup = Task.Run(() => manager.OnHangup("x"));
            // fence-allow: GUARD-TIMEOUT — observe actual table removal while the channel lock prevents notification.
            SpinWait.SpinUntil(() => manager.GetByUniqueId("x") is null, Run.Bound).Should().BeTrue();
            ChannelManager.IsDeparting(channel).Should().BeTrue();
            manager.GetByUniqueId("x").Should().BeNull();
            hangup.IsCompleted.Should().BeFalse();
            manager.HoldsLinkedId("call").Should().BeTrue();
            manager.IsHeldOrDeparting("x").Should().BeTrue();
            var channels = manager.ChannelsOfLinkedId("call");
            channels.Length.Should().Be(1);
            channels[0].Should().BeSameAs(channel);
            ChannelManager.IsDeparting(channels[0]).Should().BeTrue();
        }
        finally
        {
            release.Set();
            await holder.WaitAsync(Run.Bound);
            if (hangup is not null) await hangup.WaitAsync(Run.Bound);
        }
        manager.HoldsLinkedId("call").Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChannelRemoved_ShouldKeepOldMarkUntilOwnLastHandlerReturns_WhenTheIdIsAdmittedAgain(bool reload)
    {
        var manager = NewManager();
        Add(manager, "x", "call");
        var old = manager.GetByUniqueId("x")!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var lastRan = false;
        manager.ChannelRemoved += c =>
        {
            c.Should().BeSameAs(old);
            manager.ChannelsOfLinkedId("call")[0].AdmissionMark.Should().Be(old.AdmissionMark);
            entered.Set();
            release.Wait(Run.Bound).Should().BeTrue();
        };
        manager.ChannelRemoved += _ =>
        {
            manager.HoldsLinkedId("call").Should().BeTrue("departure lasts through the last handler");
            lastRan = true;
        };
        var removal = Task.Run(() =>
        {
            if (!reload) manager.OnHangup("x");
            else
            {
                using var window = manager.OpenReadWindow();
                manager.ReconcileWithSnapshot([], window);
            }
        });
        try
        {
            entered.Wait(Run.Bound).Should().BeTrue();
            Add(manager, "x", "call");
            var current = manager.GetByUniqueId("x")!;
            current.Should().NotBeSameAs(old);
            current.AdmissionMark.Should().BeGreaterThan(old.AdmissionMark);
            var channels = manager.ChannelsOfLinkedId("call");
            channels.Length.Should().Be(2);
            ChannelManager.IsDeparting(channels[0]).Should().BeTrue();
            ChannelManager.IsDeparting(channels[1]).Should().BeFalse();
            manager.IsHeldOrDeparting("x").Should().BeTrue();
        }
        finally
        {
            release.Set();
            await removal.WaitAsync(Run.Bound);
        }
        lastRan.Should().BeTrue();
        manager.ChannelsOfLinkedId("call").Length.Should().Be(1);
        manager.ChannelsOfLinkedId("call")[0].Should().BeSameAs(manager.GetByUniqueId("x"));
        manager.IsHeldOrDeparting("x").Should().BeTrue();
    }

    [Fact]
    public async Task PostedRemoval_ShouldRemainDepartingAfterNestedDrainReturns_WhenAnEarlierHandlerIsBlocked()
    {
        var manager = NewManager();
        Add(manager, "x", "call");
        var old = manager.GetByUniqueId("x")!;
        using var posted = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var removals = 0;
        manager.ChannelRemoved += _ => Interlocked.Increment(ref removals);
        manager.ChannelStateChanged += _ =>
        {
            manager.OnHangup("x"); // Posts; nested Drain returns without raising the removal.
            posted.Set();
            release.Wait(Run.Bound).Should().BeTrue();
        };
        var earlier = Task.Run(() => manager.OnNewState("x", ChannelState.Ring));
        try
        {
            posted.Wait(Run.Bound).Should().BeTrue();
            removals.Should().Be(0);
            manager.GetByUniqueId("x").Should().BeNull();
            manager.HoldsLinkedId("call").Should().BeTrue();
            manager.IsHeldOrDeparting("x").Should().BeTrue();
            manager.ChannelsOfLinkedId("call")[0].Should().BeSameAs(old);
            ChannelManager.IsDeparting(old).Should().BeTrue();
        }
        finally
        {
            release.Set();
            await earlier.WaitAsync(Run.Bound);
        }
        removals.Should().Be(1);
        manager.HoldsLinkedId("call").Should().BeFalse();
        manager.IsHeldOrDeparting("x").Should().BeFalse();
    }

    [Fact]
    public async Task Departures_ShouldKeepUidHeldUntilBothIncarnationsFinish_WhenTheirNotificationsOverlap()
    {
        var manager = NewManager();
        Add(manager, "x", "old-call");
        using var oldEntered = new ManualResetEventSlim();
        using var newEntered = new ManualResetEventSlim();
        using var releaseOld = new ManualResetEventSlim();
        using var releaseNew = new ManualResetEventSlim();
        manager.ChannelRemoved += c =>
        {
            var old = c.LinkedId == "old-call";
            (old ? oldEntered : newEntered).Set();
            (old ? releaseOld : releaseNew).Wait(Run.Bound).Should().BeTrue();
        };
        var first = Task.Run(() => manager.OnHangup("x"));
        Task? second = null;
        try
        {
            oldEntered.Wait(Run.Bound).Should().BeTrue();
            Add(manager, "x", "new-call");
            second = Task.Run(() => manager.OnHangup("x"));
            newEntered.Wait(Run.Bound).Should().BeTrue("the bounded outbox allows this notification alongside the old one");
            releaseOld.Set();
            await first.WaitAsync(Run.Bound);
            manager.HoldsLinkedId("old-call").Should().BeFalse();
            manager.HoldsLinkedId("new-call").Should().BeTrue();
            manager.IsHeldOrDeparting("x").Should().BeTrue("the newer departing incarnation still holds the uid");
        }
        finally
        {
            releaseOld.Set();
            releaseNew.Set();
            await first.WaitAsync(Run.Bound);
            if (second is not null) await second.WaitAsync(Run.Bound);
        }
        manager.IsHeldOrDeparting("x").Should().BeFalse();
        manager.HoldsLinkedId("new-call").Should().BeFalse();
    }

    [Fact]
    public async Task Clear_ShouldPreserveEachDepartureUntilOwnHandlersReturn_WhenTheUidIsReusedAfterClear()
    {
        var manager = NewManager();
        Add(manager, "x", "old-call");
        var old = manager.GetByUniqueId("x")!;
        using var oldEntered = new ManualResetEventSlim();
        using var newEntered = new ManualResetEventSlim();
        using var releaseOld = new ManualResetEventSlim();
        using var releaseNew = new ManualResetEventSlim();
        manager.ChannelRemoved += c =>
        {
            var isOld = ReferenceEquals(c, old);
            (isOld ? oldEntered : newEntered).Set();
            (isOld ? releaseOld : releaseNew).Wait(Run.Bound).Should().BeTrue();
        };
        var oldRemoval = Task.Run(() => manager.OnHangup("x"));
        Task? newRemoval = null;
        var oldListedAfterClear = false;
        var uidHeldAfterClear = false;
        var oldListedAlongsideNew = false;
        var newUidHeldAfterOldReturned = false;
        var newListedAfterOldReturned = false;
        try
        {
            oldEntered.Wait(Run.Bound).Should().BeTrue();
            Add(manager, "held", "held-call");
            manager.Clear();
            manager.ChannelCount.Should().Be(0);
            manager.HoldsLinkedId("held-call").Should().BeFalse("Clear still drops channels that were held");
            oldListedAfterClear = manager.ChannelsOfLinkedId("old-call").Length == 1
                && ReferenceEquals(manager.ChannelsOfLinkedId("old-call")[0], old);
            uidHeldAfterClear = manager.IsHeldOrDeparting("x");
            Add(manager, "x", "new-call");
            var current = manager.GetByUniqueId("x")!;
            current.AdmissionMark.Should().BeGreaterThan(old.AdmissionMark);
            newRemoval = Task.Run(() => manager.OnHangup("x"));
            newEntered.Wait(Run.Bound).Should().BeTrue();
            oldListedAlongsideNew = manager.HoldsLinkedId("old-call") && manager.HoldsLinkedId("new-call");
            releaseOld.Set();
            await oldRemoval.WaitAsync(Run.Bound);
            newUidHeldAfterOldReturned = manager.IsHeldOrDeparting("x");
            newListedAfterOldReturned = manager.ChannelsOfLinkedId("new-call").Length == 1
                && ChannelManager.IsDeparting(manager.ChannelsOfLinkedId("new-call")[0]);
        }
        finally
        {
            releaseOld.Set();
            releaseNew.Set();
            await oldRemoval.WaitAsync(Run.Bound);
            if (newRemoval is not null) await newRemoval.WaitAsync(Run.Bound);
        }
        new
        {
            OldListedAfterClear = oldListedAfterClear,
            UidHeldAfterClear = uidHeldAfterClear,
            OldListedAlongsideNew = oldListedAlongsideNew,
            NewUidHeldAfterOldReturned = newUidHeldAfterOldReturned,
            NewListedAfterOldReturned = newListedAfterOldReturned,
        }.Should().BeEquivalentTo(new
        {
            OldListedAfterClear = true,
            UidHeldAfterClear = true,
            OldListedAlongsideNew = true,
            NewUidHeldAfterOldReturned = true,
            NewListedAfterOldReturned = true,
        }, "Clear cannot end a departure before that incarnation's own ChannelRemoved handlers return");
        manager.HoldsLinkedId("old-call").Should().BeFalse();
        manager.HoldsLinkedId("new-call").Should().BeFalse();
        manager.IsHeldOrDeparting("x").Should().BeFalse();
    }

    [Fact]
    public void ChannelRemoved_ShouldReleaseDepartureInFinally_WhenAHandlerThrows()
    {
        var manager = NewManager();
        Add(manager, "x", "call");
        var heard = false;
        manager.ChannelRemoved += _ =>
        {
            heard = true;
            throw new InvalidOperationException("subscriber");
        };
        var act = () => manager.OnHangup("x");
        act.Should().NotThrow("the channel table isolates each subscriber's failure");
        heard.Should().BeTrue("the cleanup is exercised after the throwing handler actually ran");
        manager.HoldsLinkedId("call").Should().BeFalse();
        manager.IsHeldOrDeparting("x").Should().BeFalse();
    }

    private static ChannelManager NewManager() => new(NullLogger.Instance);
    private static void Add(ChannelManager manager, string id, string? linkedId) =>
        manager.OnNewChannel(id, "PJSIP/" + id, ChannelState.Up, linkedId: linkedId);
}
