using Verbara.Sdk.Ami.Events;

namespace Verbara.Sdk.Live.Tests.Harness;

/// <summary>
/// A handler that holds the AMI connection's dispatch: subscribed on the connection, it blocks inside its
/// <c>OnNext</c> when it receives the peer's <c>UserEvent</c> named <see cref="Name"/>, until the test calls
/// <see cref="Release"/>. Every event the connection receives after that one waits in its pump, behind it, while the
/// reader loop goes on reading the socket and hands each list action's events straight to its collector.
/// </summary>
/// <remarks>
/// <para>
/// One clock: the hold starts on an event the peer writes, and ends on the test's <see cref="Release"/>, never on a
/// timer. <see cref="Held"/> completes once the dispatch is blocked, so whatever the peer writes after it is queued.
/// The block itself is bounded by <see cref="HoldBound"/>, a hang bound a healthy run never reaches, so a test that
/// forgets to release cannot stall the lane.
/// </para>
/// </remarks>
internal sealed class DispatchGate : IObserver<ManagerEvent>, IDisposable
{
    /// <summary>The <c>UserEvent</c> header of the event that blocks the dispatch.</summary>
    public const string Name = "held-dispatch";

    /// <summary>The longest the dispatch is held when the test never releases it: a hang bound.</summary>
    public static readonly TimeSpan HoldBound = TimeSpan.FromSeconds(30);

    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _released = new(initialState: false);

    /// <summary>Completes once the dispatch is blocked inside this handler.</summary>
    public Task Held => _held.Task;

    /// <summary>Whether the hold ran out on <see cref="HoldBound"/> instead of being released.</summary>
    public bool TimedOut { get; private set; }

    /// <summary>The frame the peer writes to block the dispatch.</summary>
    public static KeyValuePair<string, string>[] Frame() =>
        [new("Privilege", "user,all"), new("UserEvent", Name)];

    /// <summary>Lets the dispatch go on.</summary>
    public void Release() => _released.Set();

    public void OnNext(ManagerEvent value)
    {
        if (value is not UserEventEvent { UserEvent: Name })
            return;

        _held.TrySetResult();
        TimedOut = !_released.Wait(HoldBound);
    }

    public void OnError(Exception error)
    {
        // The connection never faults its observers in these tests; nothing to do.
    }

    public void OnCompleted()
    {
        // Completed when the connection is disposed; nothing to do.
    }

    public void Dispose()
    {
        _released.Set();
        _released.Dispose();
    }
}

/// <summary>
/// The frames a <see cref="BootingAsterisk"/> writes for a call's events, with the headers Asterisk 22.9.0 sent for each
/// (the queue-reload recording of <c>Verbara.Sdk.Sessions.FunctionalTests</c>), less the ones no reader here reads.
/// </summary>
internal static class AsteriskFrames
{
    public static KeyValuePair<string, string>[] Newchannel(string uniqueId, string channel, string linkedId,
        int channelState = 6) =>
    [
        new("Privilege", "call,all"), new("Channel", channel),
        new("ChannelState", channelState.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new("ChannelStateDesc", channelState == 6 ? "Up" : "Ring"), new("CallerIDNum", "5552101"),
        new("CallerIDName", "QRA"), new("Context", "from-pstn"), new("Exten", "4021"), new("Priority", "1"),
        new("Uniqueid", uniqueId), new("Linkedid", linkedId),
    ];

    public static KeyValuePair<string, string>[] Hangup(string uniqueId, string channel, string linkedId,
        int cause = 16, string causeText = "Normal Clearing") =>
    [
        new("Privilege", "call,all"), new("Channel", channel), new("ChannelState", "6"),
        new("ChannelStateDesc", "Up"), new("CallerIDNum", "5552101"), new("CallerIDName", "QRA"),
        new("Context", "from-pstn"), new("Exten", "4021"), new("Priority", "1"), new("Uniqueid", uniqueId),
        new("Linkedid", linkedId), new("Cause", cause.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new("Cause-txt", causeText),
    ];

    public static KeyValuePair<string, string>[] QueueCallerJoin(string uniqueId, string channel, string queue,
        int position, int count) => QueueCaller(uniqueId, channel, queue, position, count);

    public static KeyValuePair<string, string>[] QueueCallerLeave(string uniqueId, string channel, string queue,
        int position, int count) => QueueCaller(uniqueId, channel, queue, position, count);

    private static KeyValuePair<string, string>[] QueueCaller(string uniqueId, string channel, string queue,
        int position, int count) =>
    [
        new("Privilege", "agent,all"), new("Channel", channel), new("ChannelState", "4"),
        new("ChannelStateDesc", "Ring"), new("CallerIDNum", "5552101"), new("CallerIDName", "QRA"),
        new("Context", "from-pstn"), new("Exten", "4021"), new("Priority", "1"), new("Uniqueid", uniqueId),
        new("Linkedid", uniqueId), new("Queue", queue),
        new("Position", position.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new("Count", count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
    ];
}
