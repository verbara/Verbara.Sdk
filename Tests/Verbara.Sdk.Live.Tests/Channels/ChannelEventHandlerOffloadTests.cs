using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Channels;

namespace Verbara.Sdk.Live.Tests.Channels;

/// <summary>
/// A channel's admission subscriber can wait for another thread changing that same channel. The
/// second admission subscriber records completion of the whole admission notification, so a later
/// event must follow every subscriber, including the one that offloaded the change.
/// </summary>
public sealed class ChannelEventHandlerOffloadTests
{
    private const string UniqueId = "1700000000.1";
    private const string Name = "PJSIP/2000-0001";

    [Theory]
    [InlineData("Hangup", false)]
    [InlineData("Hangup", true)]
    [InlineData("NewState", false)]
    [InlineData("NewState", true)]
    [InlineData("DialBegin", false)]
    [InlineData("DialBegin", true)]
    [InlineData("DialEnd", false)]
    [InlineData("DialEnd", true)]
    [InlineData("Hold", false)]
    [InlineData("Hold", true)]
    [InlineData("Unhold", false)]
    [InlineData("Unhold", true)]
    [InlineData("Rename", false)]
    [InlineData("Rename", true)]
    [InlineData("ReconcileRemoving", false)]
    [InlineData("ReconcileRemoving", true)]
    public void ChannelAdded_ShouldLetItsHandlerWaitForAnotherThread_WhenThatThreadChangesTheAdmittedChannel(
        string operation, bool fromSnapshot)
    {
        var manager = new ChannelManager(NullLogger.Instance);
        var probe = new HandlerOffloadProbe();
        var order = new ConcurrentQueue<string>();
        Thread? offloadedThread = null;

        manager.ChannelAdded += _ => probe.Offload(() =>
        {
            Volatile.Write(ref offloadedThread, Thread.CurrentThread);
            ChangeChannel(manager, operation);
        });
        manager.ChannelAdded += _ => order.Enqueue(nameof(ChannelManager.ChannelAdded));
        manager.ChannelRemoved += _ => order.Enqueue(nameof(ChannelManager.ChannelRemoved));
        manager.ChannelStateChanged += _ => order.Enqueue(nameof(ChannelManager.ChannelStateChanged));
        manager.ChannelDialBegin += _ => order.Enqueue(nameof(ChannelManager.ChannelDialBegin));
        manager.ChannelDialEnd += _ => order.Enqueue(nameof(ChannelManager.ChannelDialEnd));
        manager.ChannelHeld += _ => order.Enqueue(nameof(ChannelManager.ChannelHeld));
        manager.ChannelUnheld += _ => order.Enqueue(nameof(ChannelManager.ChannelUnheld));

        if (fromSnapshot)
        {
            using var window = manager.OpenReadWindow();
            manager.ReconcileWithSnapshot([new ChannelSnapshotEntry(UniqueId, Name, ChannelState.Ringing)], window);
        }
        else
        {
            manager.OnNewChannel(UniqueId, Name, ChannelState.Ringing);
        }

        try
        {
            probe.FinishedInsideTheHandler().Should().BeTrue(
                $"a ChannelAdded subscriber waiting for {operation} of the admitted channel must not deadlock");

            var laterEvent = operation switch
            {
                "Hangup" or "ReconcileRemoving" => nameof(ChannelManager.ChannelRemoved),
                "NewState" => nameof(ChannelManager.ChannelStateChanged),
                "DialBegin" => nameof(ChannelManager.ChannelDialBegin),
                "DialEnd" => nameof(ChannelManager.ChannelDialEnd),
                "Hold" => nameof(ChannelManager.ChannelHeld),
                "Unhold" => nameof(ChannelManager.ChannelUnheld),
                "Rename" => null,
                _ => throw new ArgumentOutOfRangeException(nameof(operation)),
            };
            string[] expected = laterEvent is null
                ? [nameof(ChannelManager.ChannelAdded)]
                : [nameof(ChannelManager.ChannelAdded), laterEvent];
            order.Should().Equal(expected,
                "a change reported by the admission subscriber must follow every admission subscriber");
        }
        finally
        {
            var worker = Volatile.Read(ref offloadedThread);
            if (worker is not null)
                worker.Join(HandlerOffloadProbe.Drain).Should().BeTrue("the offloaded worker must be reaped");
        }
    }

    private static void ChangeChannel(ChannelManager manager, string operation)
    {
        switch (operation)
        {
            case "Hangup":
                manager.OnHangup(UniqueId, HangupCause.NormalClearing);
                break;
            case "NewState":
                manager.OnNewState(UniqueId, ChannelState.Up);
                break;
            case "DialBegin":
                manager.OnDialBegin(UniqueId, "1700000000.2", "PJSIP/3000-0002", "PJSIP/3000");
                break;
            case "DialEnd":
                manager.OnDialEnd(UniqueId, "ANSWER");
                break;
            case "Hold":
                manager.OnHold(UniqueId, "default");
                break;
            case "Unhold":
                manager.OnUnhold(UniqueId);
                break;
            case "Rename":
                manager.OnRename(UniqueId, "PJSIP/2000-renamed");
                break;
            case "ReconcileRemoving":
                // Open after admission: this snapshot can prove that channel absent and remove it.
                using (var window = manager.OpenReadWindow())
                    manager.ReconcileWithSnapshot([], window);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }
}
