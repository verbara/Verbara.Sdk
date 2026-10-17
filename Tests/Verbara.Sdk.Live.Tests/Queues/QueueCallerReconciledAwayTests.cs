using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Live.Queues;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Live.Tests.Queues;

/// <summary>
/// A caller waiting in a queue whose channel a channel reconciliation removes
/// (<see cref="Live.Server.VerbaraServer.ReconcileChannelsAsync"/> over a completed snapshot that no longer lists it, the
/// route the sweep's verification takes) leaves Live's queue table the same way: the entry is gone,
/// <c>live.queue.calls.left</c> advances and <see cref="QueueManager.CallerLeft"/> is raised; and, since no
/// <c>QueueCallerLeave</c> was received, neither <c>CallerLeaveReported</c> nor the session layer's <c>VisitLeft</c> is.
/// </summary>
/// <remarks>
/// <para>
/// A real <see cref="Verbara.Sdk.Ami.Connection.AmiConnection"/> over <see cref="Run"/>: the peer writes the caller's
/// <c>Newchannel</c> and <c>QueueCallerJoin</c> as Asterisk does, and a <see cref="CallSessionManager"/> is attached, so
/// the call is open and queued when the reconciliation runs.
/// </para>
/// <para>
/// <c>VisitLeft</c> is internal to <c>Verbara.Sdk.Sessions</c>, which does not expose its internals to this assembly; the
/// test subscribes to it through reflection, by its name.
/// </para>
/// </remarks>
[Collection(LiveCountersGroup.Name)]
public sealed class QueueCallerReconciledAwayTests
{
    private const string Queue = BootingAsterisk.QueueName;
    private const string CallerUid = "1790633830.0";
    private const string CallerChannel = "PJSIP/pstn-00000000";

    [Fact]
    public async Task ReconcileChannelsAsync_ShouldTakeTheWaitingCallerOutOfTheQueueAsAnUnobservedLeave_WhenItRemovesTheCallersChannel()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.ConnectAsync(peer);
        await using var manager = new CallSessionManager(
            Options.Create(new SessionOptions()), NullLogger<CallSessionManager>.Instance, new NullStore());
        manager.AttachToServer(run.Server, "srv-1");
        await run.StartServerAsync();

        var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Server.Queues.CallerJoined += (_, _) => joined.TrySetResult();
        await peer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(CallerUid, CallerChannel, CallerUid, channelState: 4));
        await peer.SendEventAsync("QueueCallerJoin", AsteriskFrames.QueueCallerJoin(CallerUid, CallerChannel, Queue, 1, 1));
        await joined.Task.WaitAsync(Run.Bound);

        var callerLeft = new ConcurrentQueue<string>();
        var leaveReported = 0;
        var visitLeft = 0;
        run.Server.Queues.CallerLeft += (_, entry) => callerLeft.Enqueue(entry.Channel);
        run.Server.Queues.CallerLeaveReported += _ => Interlocked.Increment(ref leaveReported);
        SubscribeVisitLeft(manager, () => Interlocked.Increment(ref visitLeft));
        var queuedBefore = manager.GetByLinkedId(CallerUid)?.State;
        using var counters = new LiveCounters();

        peer.StatusChannels = [];
        await run.Server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound);

        new
        {
            QueuedBefore = queuedBefore,
            ChannelHeld = run.Server.Channels.GetByUniqueId(CallerUid) is not null,
            EntryHeld = run.Server.Queues.GetByName(Queue)?.Entries.ContainsKey(CallerChannel) ?? false,
            Left = counters.Left,
            CallerLeft = callerLeft.ToArray(),
            CallerLeaveReported = Volatile.Read(ref leaveReported),
            VisitLeft = Volatile.Read(ref visitLeft),
            PeerFault = peer.Fault?.Message,
        }.Should().BeEquivalentTo(
            new
            {
                QueuedBefore = (CallSessionState?)CallSessionState.Queued,
                ChannelHeld = false,
                EntryHeld = false,
                Left = 1L,
                CallerLeft = new[] { CallerChannel },
                CallerLeaveReported = 0,
                VisitLeft = 0,
                PeerFault = default(string),
            },
            "the reconciliation removed the waiting caller's channel, so the caller is no longer waiting: its entry is taken "
            + "out, counted left and announced with CallerLeft; no QueueCallerLeave was received, so neither "
            + $"CallerLeaveReported nor VisitLeft fires. Measured: {counters}");
    }

    /// <summary>Adds <paramref name="onVisitLeft"/> to the manager's internal <c>VisitLeft</c>, by its name.</summary>
    private static void SubscribeVisitLeft(CallSessionManager manager, Action onVisitLeft)
    {
        var visitLeft = typeof(CallSessionManager).GetEvent("VisitLeft", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("CallSessionManager has no internal event VisitLeft.");
        var handlerType = visitLeft.EventHandlerType
            ?? throw new InvalidOperationException("VisitLeft has no handler type.");
        var signal = Expression.Parameter(handlerType.GetGenericArguments()[0], "signal");
        var handler = Expression.Lambda(handlerType, Expression.Invoke(Expression.Constant(onVisitLeft)), signal).Compile();
        (visitLeft.GetAddMethod(nonPublic: true)
            ?? throw new InvalidOperationException("VisitLeft has no add accessor.")).Invoke(manager, [handler]);
    }

    /// <summary>A store that saves nothing; the test reads the manager and Live's tables.</summary>
    private sealed class NullStore : SessionStoreBase
    {
        public override ValueTask SaveAsync(CallSession session, CancellationToken ct) => ValueTask.CompletedTask;

        public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) =>
            ValueTask.FromResult<CallSession?>(null);
    }
}
