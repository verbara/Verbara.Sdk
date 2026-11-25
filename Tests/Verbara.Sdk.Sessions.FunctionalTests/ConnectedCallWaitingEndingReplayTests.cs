using System.Globalization;
using System.Text;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// How a call that was connected ends when its caller hangs up while it waits in a later queue: a call answered in
/// <c>q-first</c> and blind-transferred into <c>q-second</c> (step <c>requeue-hangup-01</c>), and a call answered by a
/// plain dial and then queued (step <c>aq-hangup-01</c>), replayed from the connected-signal-matrix hang-up captures
/// (<c>Recordings/asterisk-ami/connected-signal-matrix-hangup-*</c>, the README's <i>The connected-signal-matrix
/// captures</i>). A normal clearing of the caller's own channel ends it completed and any other cause of that channel
/// ends it failed, whichever of the call's channels Asterisk reports hanging up last.
/// </summary>
/// <remarks>
/// <para>
/// The live arm delivers every live frame of the call; the reload arm reloads at the checkpoint the capture took while
/// the call waited (<c>wait2</c>, <c>wait</c>) and delivers the rest. In the <c>-reversed</c> capture the caller's
/// <c>Hangup</c> (<c>Cause: 16</c>) precedes app_queue's <c>Hangup</c> of the second queue's ringing member leg
/// (<c>Cause: 0</c>); in the three others the member leg's comes first.
/// </para>
/// <para>
/// No capture holds <c>aq-hangup-01</c> in the reversed order, so <see cref="Reordered"/> builds it: a copy of the
/// 22.12.0 capture's step in memory with the caller's <c>Hangup</c> frame moved before the ringing member leg's, and,
/// for the abnormal twin, the two frames' causes changed. Those frames are synthetic, parsed by the SDK's own reader.
/// The abnormal twin also shows the state its <see cref="CallEndedEvent"/> carries.
/// </para>
/// </remarks>
public sealed class ConnectedCallWaitingEndingReplayTests
{
    private const string Window22 = "connected-signal-matrix-hangup-asterisk-22.12.0.raw";
    private const string AnsweredThenQueued = "aq-hangup-01";

    /// <summary>Every hang-up capture, with each connected call that hangs up waiting and the checkpoint it waits at.</summary>
    public static TheoryData<string, string, string> WaitingCalls
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            foreach (var fixture in AmiCaptureReplay.ConnectedSignalMatrixHangupCaptures)
            {
                data.Add(fixture, "requeue-hangup-01", "wait2");
                data.Add(fixture, AnsweredThenQueued, "wait");
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(WaitingCalls))]
    public async Task CallSession_ShouldEndCompletedWithNormalClearing_WhenAConnectedCapturedCallHangsUpWaitingInALaterQueue(
        string fixture, string step, string checkpoint)
    {
        var run = await SignalMatrix.LiveAsync(fixture, step);

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        run.StateAt(checkpoint).Should().Be(CallSessionState.Queued, "premise: the call waits in the later queue");
        run.Session!.State.Should().Be(CallSessionState.Completed,
            "a call that was connected and whose caller hangs up normally while it waits took place and is over");
        run.Session.HangupCause.Should().Be(HangupCause.NormalClearing, "the call's cause is the caller's own channel's");
        run.Session.ConnectedAt.Should().NotBeNull("the call keeps its first connected time");
        run.Session.TalkTime.Should().NotBeNull("a completed call that connected has a talk time");
        run.Endings.Should().ContainSingle("the call ends once")
            .Which.Cause.Should().Be(HangupCause.NormalClearing, "the ending carries the caller's own cause");
    }

    [Theory]
    [MemberData(nameof(WaitingCalls))]
    public async Task CallSession_ShouldEndCompletedWithNormalClearing_WhenAReloadOpenedTheCapturedCallWhileItWaited(
        string fixture, string step, string checkpoint)
    {
        _ = checkpoint;
        var run = await SignalMatrix.ReloadAsync(fixture, step);

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        run.Session.Should().NotBeNull("premise: the reload opens the caller's call");
        run.Session!.State.Should().Be(CallSessionState.Completed,
            "a call a reload opened connected, whose caller hangs up normally while it waits, took place and is over");
        run.Session.HangupCause.Should().Be(HangupCause.NormalClearing, "the call's cause is the caller's own channel's");
        run.Session.ConnectedAt.Should().BeNull("no connection of the call's own was observed");
        run.Endings.Should().ContainSingle("the call ends once")
            .Which.Cause.Should().Be(HangupCause.NormalClearing, "the ending carries the caller's own cause");
    }

    [Theory]
    [MemberData(nameof(WaitingCalls))]
    public async Task CallSession_ShouldEndCompleted_WhenAReloadProvesAConnectedCapturedCallGoneWhileItWaits(
        string fixture, string step, string checkpoint)
    {
        var run = await SignalMatrix.GoneAtAsync(await SignalMatrix.ReadAsync(fixture, step), checkpoint);

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        run.StateAt(checkpoint).Should().Be(CallSessionState.Queued, "premise: the call waits in the later queue");
        run.Session!.State.Should().Be(CallSessionState.Completed,
            "a call that was connected, which a reload proves gone while it waits, took place and is over");
        run.Session.HangupCause.Should().BeNull("no hang-up was observed, so no cause is recorded");
        run.Endings.Should().ContainSingle("the call ends once");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallSession_ShouldEndCompletedWithNormalClearing_WhenTheCallerHangsUpBeforeTheRingingMemberLegOfAnAnsweredThenQueuedCall(
        bool reloadWhileWaiting)
    {
        var call = await SignalMatrix.ReadAsync(Reordered(callerCause: null, memberCause: null), AnsweredThenQueued);
        var run = reloadWhileWaiting ? await SignalMatrix.ReloadAsync(call) : await SignalMatrix.LiveAsync(call);

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        run.Session.Should().NotBeNull("premise: the replay opens the caller's call");
        run.Session!.State.Should().Be(CallSessionState.Completed,
            "the ringing member leg that hangs up after the caller decides nothing: the caller hung up normally");
        run.Session.HangupCause.Should().Be(HangupCause.NormalClearing,
            "the call's cause is the caller's own channel's, not the not-defined cause of the leg reported last");
        run.Endings.Should().ContainSingle("the call ends once")
            .Which.Cause.Should().Be(HangupCause.NormalClearing, "the ending carries the caller's own cause");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallSession_ShouldEndFailedWithTheCallersCause_WhenTheCallerHangsUpAbnormallyAndAMemberLegNormallyAfterIt(
        bool reloadWhileWaiting)
    {
        var call = await SignalMatrix.ReadAsync(
            Reordered(callerCause: HangupCause.UserBusy, memberCause: HangupCause.NormalClearing), AnsweredThenQueued);
        var run = reloadWhileWaiting ? await SignalMatrix.ReloadAsync(call) : await SignalMatrix.LiveAsync(call);

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        run.Session.Should().NotBeNull("premise: the replay opens the caller's call");
        run.Session!.State.Should().Be(CallSessionState.Failed,
            "the caller's own channel hung up with a cause other than normal clearing");
        run.Session.HangupCause.Should().Be(HangupCause.UserBusy,
            "the call's cause is the caller's own, not the normal clearing of the leg reported last");
        run.Endings.Should().ContainSingle("the call ends once")
            .Which.Cause.Should().Be(HangupCause.UserBusy, "the ending carries the caller's own cause");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallEndedEvent_ShouldCarryFailed_WhenTheCallerOfAConnectedCallWaitingInALaterQueueHangsUpAbnormally(
        bool reloadWhileWaiting)
    {
        var call = await SignalMatrix.ReadAsync(
            Reordered(callerCause: HangupCause.UserBusy, memberCause: HangupCause.NormalClearing), AnsweredThenQueued);
        var run = reloadWhileWaiting ? await SignalMatrix.ReloadAsync(call) : await SignalMatrix.LiveAsync(call);

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        run.Session.Should().NotBeNull("premise: the replay opens the caller's call");
        run.Endings.Select(e => e.FinalState).Should().Equal([CallSessionState.Failed],
            "the call ends once, failed, because its caller's own channel hung up abnormally, and its ending says so");
        run.StatesWhenEnded.Should().Equal([CallSessionState.Failed],
            "the call reads the state its ending carries when the ending is delivered");
    }

    /// <summary>
    /// A reconnect falls between the caller's own abnormal hang-up and the normal hang-up of the ringing member leg, and
    /// its reload still finds that leg. The reconnect may have lost events, so the call forgets the departures it had
    /// recorded, but not the cause Asterisk reported on the caller's own channel: a channel that hung up does not come
    /// back. The reload's <c>Status</c> row of the member leg is synthetic (no capture holds a reconnect at that point),
    /// written with the leg's own channel, uniqueid and linkedid from the capture.
    /// </summary>
    [Fact]
    public async Task CallSession_ShouldEndFailedWithTheCallersCause_WhenAReconnectFallsBetweenTheCallersAbnormalHangUpAndTheMemberLegs()
    {
        var call = await SignalMatrix.ReadAsync(
            Reordered(callerCause: HangupCause.UserBusy, memberCause: HangupCause.NormalClearing), AnsweredThenQueued);
        var member = RingingMemberLeg(call);
        var memberHangup = HangupIndex(call, member.UniqueId);
        var status = await SyntheticStatusAsync(member.Channel, member.UniqueId, call.CallerUniqueId);
        var run = await SignalMatrix.ReconnectingAsync(call,
            new SignalMatrix.Reconnect(memberHangup, status, QueueStatusEnd(call)));

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        run.StateAt("reconnected-1").Should().Be(CallSessionState.Queued,
            "premise: the caller hung up, and the call still waits in the later queue with its member leg ringing");
        run.Session!.State.Should().Be(CallSessionState.Failed,
            "the caller's own channel hung up with a cause other than normal clearing, before the reconnect");
        run.Session.HangupCause.Should().Be(HangupCause.UserBusy,
            "the reconnect does not erase the cause the caller's own channel reported");
        run.Endings.Should().ContainSingle("the call ends once")
            .Which.Cause.Should().Be(HangupCause.UserBusy, "the ending carries the caller's own cause");
    }

    /// <summary>
    /// A reconnect falls after the caller's own hang-up, and its completed reload no longer finds the ringing member
    /// leg: the reload proves the rest of the call gone. The caller's own hang-up was observed, so its cause decides
    /// the ending; a reload's ending with no cause is for a call whose caller's hang-up was not observed. The reload
    /// answers with the list-complete frames the capture took at <c>wait</c>.
    /// </summary>
    [Theory]
    [InlineData(HangupCause.UserBusy, CallSessionState.Failed)]
    [InlineData(HangupCause.NormalClearing, CallSessionState.Completed)]
    public async Task CallSession_ShouldEndOnTheCallersObservedCause_WhenAReloadThenProvesTheRestOfTheWaitingCallGone(
        HangupCause callerCause, CallSessionState expected)
    {
        var whole = await SignalMatrix.ReadAsync(Reordered(callerCause, memberCause: null), AnsweredThenQueued);
        var memberHangup = HangupIndex(whole, RingingMemberLeg(whole).UniqueId);
        var call = whole with { Frames = [.. whole.Frames.Take(memberHangup)] };
        var run = await SignalMatrix.ReconnectingAsync(call,
            new SignalMatrix.Reconnect(memberHangup, StatusEnd(whole), QueueStatusEnd(whole)));

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        run.StateAt("reconnected-1").Should().NotBeNull("premise: the replay opened the caller's call");
        run.Session!.State.Should().Be(expected, "the caller's own channel's cause decides the ending");
        run.Session.HangupCause.Should().Be(callerCause, "the call's cause is the one its caller's own channel reported");
        run.Endings.Should().ContainSingle("the call ends once")
            .Which.Cause.Should().Be(callerCause, "the ending carries the caller's own cause");
    }

    /// <summary>
    /// The same ending as <see cref="CallSession_ShouldEndOnTheCallersObservedCause_WhenAReloadThenProvesTheRestOfTheWaitingCallGone"/>:
    /// the caller's own cause decides it, but the last departure the call knows of, the ringing member leg's, was
    /// learned from the reload's snapshot, not from a <c>Hangup</c>. The session keeps the ending-provenance marker
    /// (<c>Metadata["cause"] = "reload"</c>) that says so, whichever cause decides the ending.
    /// </summary>
    [Theory]
    [InlineData(HangupCause.UserBusy)]
    [InlineData(HangupCause.NormalClearing)]
    public async Task CallSession_ShouldKeepTheReloadEndingMarker_WhenAReloadProvesTheRestOfAWaitingCallGoneAfterItsCallersHangUp(
        HangupCause callerCause)
    {
        var whole = await SignalMatrix.ReadAsync(Reordered(callerCause, memberCause: null), AnsweredThenQueued);
        var memberHangup = HangupIndex(whole, RingingMemberLeg(whole).UniqueId);
        var call = whole with { Frames = [.. whole.Frames.Take(memberHangup)] };
        var run = await SignalMatrix.ReconnectingAsync(call,
            new SignalMatrix.Reconnect(memberHangup, StatusEnd(whole), QueueStatusEnd(whole)));

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        run.Endings.Should().ContainSingle("premise: the reload ends the call once")
            .Which.Cause.Should().Be(callerCause, "premise: the caller's own cause decides the ending");
        run.Session!.Metadata.GetValueOrDefault("cause").Should().Be("reload",
            "the reload proved the call's remaining leg gone: its last departure was learned from a snapshot");
    }

    /// <summary>
    /// After the caller's own abnormal hang-up, a first reconnect's reload finds the caller's channel alive again, beside
    /// the ringing member leg (the capture's own <c>Status</c> rows and <c>QueueStatus</c> at <c>wait</c>, and the
    /// member leg's synthetic row), and a second one finds nothing. The cause recorded for a channel that is alive again
    /// no longer says how the caller left, so the second reload ends the call as a reload ends a connected call waiting
    /// in a queue: completed, with no cause. A caller's channel listed alive after its hang-up is synthetic: no capture
    /// holds it, and Asterisk does not reuse a uniqueid.
    /// </summary>
    [Fact]
    public async Task CallSession_ShouldForgetTheCallersCause_WhenAReloadFindsTheCallersChannelAliveAgain()
    {
        var whole = await SignalMatrix.ReadAsync(
            Reordered(callerCause: HangupCause.UserBusy, memberCause: null), AnsweredThenQueued);
        var memberHangup = HangupIndex(whole, RingingMemberLeg(whole).UniqueId);
        var member = RingingMemberLeg(whole);
        var wait = whole.Checkpoints.Single(c => c.Name == "wait");
        var callerAlive = OfAction(whole, wait.StatusId).Where(f => f.EventType == "Status").ToList();
        var status = await SyntheticStatusAsync(member.Channel, member.UniqueId, whole.CallerUniqueId,
            listed: callerAlive.Count + 1);
        var call = whole with { Frames = [.. whole.Frames.Take(memberHangup)] };
        var run = await SignalMatrix.ReconnectingAsync(call,
            new SignalMatrix.Reconnect(memberHangup, [.. callerAlive, .. status], OfAction(whole, wait.QueueStatusId)),
            new SignalMatrix.Reconnect(memberHangup, StatusEnd(whole), QueueStatusEnd(whole)));

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        run.StateAt("reconnected-1").Should().Be(CallSessionState.Queued,
            "premise: the first reload finds the caller waiting in the later queue");
        run.Session!.State.Should().Be(CallSessionState.Completed,
            "the second reload proves a connected call gone while it waits, and no cause of its caller stands");
        run.Session.HangupCause.Should().BeNull("the cause recorded before the caller's channel was found alive again is forgotten");
        run.Endings.Should().ContainSingle("the call ends once");
    }

    /// <summary>The ringing member leg of the call: the one <see cref="Reordered"/> moves the caller's hang-up before.</summary>
    private static (string Channel, string UniqueId) RingingMemberLeg(SignalMatrix.Call call)
    {
        var legs = call.Frames.Where(f => f.EventType == "Hangup"
                && Raw(f, "Uniqueid") != call.CallerUniqueId && Raw(f, "Linkedid") == call.CallerUniqueId
                && Raw(f, "ChannelStateDesc") == "Ringing")
            .Select(f => (Raw(f, "Channel")!, Raw(f, "Uniqueid")!)).ToList();
        return legs.Count == 1
            ? legs[0]
            : throw new InvalidOperationException($"premise: one ringing member leg hangs up, found {legs.Count}");
    }

    private static int HangupIndex(SignalMatrix.Call call, string uniqueId)
    {
        var found = call.Frames.Select((f, i) => (f, i))
            .Where(x => x.f.EventType == "Hangup" && Raw(x.f, "Uniqueid") == uniqueId).Select(x => x.i).ToList();
        return found.Count == 1
            ? found[0]
            : throw new InvalidOperationException($"premise: one Hangup frame of {uniqueId}, found {found.Count}");
    }

    /// <summary>The frames of the capture's answer to the action <paramref name="actionId"/>.</summary>
    private static List<ManagerEvent> OfAction(SignalMatrix.Call call, string? actionId) =>
        actionId is null
            ? throw new InvalidOperationException("premise: the checkpoint names its snapshot")
            : [.. call.Frames.Where(f => Raw(f, "ActionID") == actionId)];

    /// <summary>The <c>StatusComplete</c> the capture took at <c>wait</c>, and none of its rows.</summary>
    private static List<ManagerEvent> StatusEnd(SignalMatrix.Call call) =>
        [.. OfAction(call, call.Checkpoints.Single(c => c.Name == "wait").StatusId).Where(f => f.EventType == "StatusComplete")];

    /// <summary>The <c>QueueStatusComplete</c> the capture took at <c>wait</c>, and none of its rows.</summary>
    private static List<ManagerEvent> QueueStatusEnd(SignalMatrix.Call call) =>
        [.. OfAction(call, call.Checkpoints.Single(c => c.Name == "wait").QueueStatusId)
            .Where(f => f.EventType == "QueueStatusComplete")];

    /// <summary>
    /// A synthetic <c>Status</c> row of one ringing channel and the list's end, which counts <paramref name="listed"/>
    /// rows, parsed by the SDK's own reader: the row's fields are the ones Asterisk's <c>Status</c> rows carry for a
    /// channel's identity and state.
    /// </summary>
    private static async Task<IReadOnlyList<ManagerEvent>> SyntheticStatusAsync(string channel, string uniqueId, string linkedId,
        int listed = 1)
    {
        var items = listed.ToString(CultureInfo.InvariantCulture);
        var text = "Asterisk Call Manager/11.0.0\r\n"
            + "Event: Status\r\nPrivilege: Call\r\nChannel: " + channel + "\r\nChannelState: 5\r\nChannelStateDesc: Ringing\r\n"
            + "Uniqueid: " + uniqueId + "\r\nLinkedid: " + linkedId + "\r\nApplication: AppQueue\r\nData: (Outgoing Line)\r\n"
            + "ActionID: synthetic-status\r\n\r\n"
            + "Event: StatusComplete\r\nActionID: synthetic-status\r\nEventList: Complete\r\nListItems: " + items + "\r\nItems: " + items + "\r\n\r\n";
        return await AmiCaptureReplay.ReadCaptureAsync(Encoding.Latin1.GetBytes(text));
    }

    private static string? Raw(ManagerEvent f, string key) => f.RawFields?.GetValueOrDefault(key);

    /// <summary>
    /// The 22.12.0 window capture with <c>aq-hangup-01</c>'s tail reordered: the caller's <c>Hangup</c> frame is moved
    /// before app_queue's <c>Hangup</c> of the ringing <c>q-second</c> member leg, and the two frames trade their
    /// timestamps so the capture stays in time order. A non-null cause replaces that frame's <c>Cause</c> and
    /// <c>Cause-txt</c>.
    /// </summary>
    private static byte[] Reordered(HangupCause? callerCause, HangupCause? memberCause)
    {
        const string separator = "\r\n\r\n";
        var frames = Encoding.Latin1.GetString(AmiCaptureReplay.ReadCaptureBytes(Window22))
            .Split(separator).ToList();
        var stepStart = frames.FindIndex(f => f.Contains("UserEvent: CaptureMarker\r\n", StringComparison.Ordinal)
            && f.Contains($"Step: {AnsweredThenQueued}\r\n", StringComparison.Ordinal));
        var stepEnd = frames.FindIndex(stepStart + 1, f => f.Contains("UserEvent: CaptureMarker\r\n", StringComparison.Ordinal));
        if (stepStart < 0 || stepEnd < 0)
            throw new InvalidOperationException($"premise: {Window22} keeps {AnsweredThenQueued} followed by another step");

        var caller = ReadField(frames.Skip(stepStart).Take(stepEnd - stepStart)
            .First(f => f.Contains("UserEvent: CaptureOutcome\r\n", StringComparison.Ordinal)), "CallerUniqueid");
        var callerHangup = FindHangup(frames, stepStart, stepEnd, f => ReadField(f, "Uniqueid") == caller);
        var memberHangup = FindHangup(frames, stepStart, stepEnd, f => ReadField(f, "Uniqueid") != caller
            && ReadField(f, "Linkedid") == caller && ReadField(f, "ChannelStateDesc") == "Ringing");
        if (memberHangup > callerHangup || ReadField(frames[memberHangup], "Cause") != "0"
            || ReadField(frames[callerHangup], "Cause") != "16")
            throw new InvalidOperationException(
                "premise: the window's member leg hangs up first, with no cause, and the caller after it, normally");

        var callerFrame = WithCause(frames[callerHangup], callerCause);
        var memberFrame = WithCause(frames[memberHangup], memberCause);
        var callerStamp = ReadField(callerFrame, "Timestamp")!;
        var memberStamp = ReadField(memberFrame, "Timestamp")!;
        frames[memberHangup] = WithField(callerFrame, "Timestamp", memberStamp);
        frames.RemoveAt(callerHangup);
        frames.Insert(memberHangup + 1, WithField(memberFrame, "Timestamp", callerStamp));
        return Encoding.Latin1.GetBytes(string.Join(separator, frames));
    }

    private static int FindHangup(List<string> frames, int from, int to, Func<string, bool> leg)
    {
        var found = Enumerable.Range(from, to - from)
            .Where(i => frames[i].StartsWith("Event: Hangup\r\n", StringComparison.Ordinal) && leg(frames[i])).ToList();
        return found.Count == 1
            ? found[0]
            : throw new InvalidOperationException($"premise: one Hangup frame of that leg, found {found.Count}");
    }

    private static string? ReadField(string frame, string name) =>
        frame.Split("\r\n").Select(l => l.Split(": ", 2)).Where(kv => kv.Length == 2 && kv[0] == name)
            .Select(kv => kv[1]).FirstOrDefault();

    private static string WithField(string frame, string name, string value) =>
        string.Join("\r\n", frame.Split("\r\n").Select(l => l.StartsWith(name + ": ", StringComparison.Ordinal)
            ? $"{name}: {value}"
            : l));

    private static string WithCause(string frame, HangupCause? cause) =>
        cause is not { } c
            ? frame
            : WithField(WithField(frame, "Cause", ((int)c).ToString(CultureInfo.InvariantCulture)), "Cause-txt", c.ToString());
}
