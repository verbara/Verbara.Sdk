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
