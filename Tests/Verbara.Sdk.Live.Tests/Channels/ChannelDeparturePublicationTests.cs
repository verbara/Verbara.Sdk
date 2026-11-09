using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Channels;
using Verbara.Sdk.Live.Tests.Harness;

namespace Verbara.Sdk.Live.Tests.Channels;

public sealed class ChannelDeparturePublicationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Removal_ShouldPublishDepartureBeforeDroppingHeldEntry_WhenAReaderObservesItsBoundary(bool reload)
    {
        var manager = new ChannelManager(NullLogger.Instance);
        manager.OnNewChannel("X", "PJSIP/X", ChannelState.Up, linkedId: "L");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        manager.BeforeDeparturePublished = _ =>
        {
            entered.TrySetResult();
            // fence-allow: GUARD-TIMEOUT — bound cleanup if the observing test fails before releasing its gate.
            release.Wait(Run.Bound).Should().BeTrue("the observer releases the publication gate");
        };
        var removal = Task.Run(() =>
        {
            if (!reload)
                manager.OnHangup("X");
            else
            {
                using var window = manager.OpenReadWindow();
                manager.ReconcileWithSnapshot([], window);
            }
        });
        var heldOrDeparting = false;
        try
        {
            await entered.Task.WaitAsync(Run.Bound);
            heldOrDeparting = manager.IsHeldOrDeparting("X");
        }
        finally
        {
            release.Set();
            await removal.WaitAsync(Run.Bound);
        }
        heldOrDeparting.Should().BeTrue("a table reader must never see a departing channel in neither state");
        manager.IsHeldOrDeparting("X").Should().BeFalse("the removal still finishes after the observer releases it");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Removal_ShouldKeepDepartureUntilEverySubscriberReturnsAndThenClearIt_WhenAnEarlierSubscriberThrows(bool reload)
    {
        var manager = new ChannelManager(NullLogger.Instance);
        manager.OnNewChannel("X", "PJSIP/X", ChannelState.Up, linkedId: "L");
        var firstHeard = 0;
        var laterHeard = 0;
        var laterSawDeparting = false;
        var laterSawLinked = false;
        var laterSawNoHeldEntry = false;
        manager.ChannelRemoved += _ =>
        {
            firstHeard++;
            throw new InvalidOperationException("earlier-subscriber");
        };
        manager.ChannelRemoved += _ =>
        {
            laterHeard++;
            laterSawDeparting = manager.IsDeparting("X");
            laterSawLinked = manager.HoldsLinkedId("L");
            laterSawNoHeldEntry = manager.GetByUniqueId("X") is null;
        };

        var failure = Record.Exception(() =>
        {
            if (reload)
            {
                using var window = manager.OpenReadWindow();
                manager.ReconcileWithSnapshot([], window);
            }
            else
            {
                manager.OnHangup("X");
            }
        });

        failure.Should().BeNull("one subscriber's failure must not leave the removal");
        firstHeard.Should().Be(1);
        laterHeard.Should().Be(1, "each subscriber still receives the removal after an earlier one throws");
        laterSawNoHeldEntry.Should().BeTrue("the removal is already outside the held table during delivery");
        laterSawDeparting.Should().BeTrue("departure remains visible throughout the invocation list");
        laterSawLinked.Should().BeTrue("the ending oracle keeps the removing channel until every subscriber returned");
        manager.IsDeparting("X").Should().BeFalse("the finally completes departure after delivery");
        manager.HoldsLinkedId("L").Should().BeFalse("the same finally removes the linked-channel entry");
        manager.IsHeldOrDeparting("X").Should().BeFalse();
    }
}
