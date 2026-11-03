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
}
