using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// The state a captured call's <see cref="CallEndedEvent"/> carries: the state the call ended in, which the call
/// itself reads when the ending is delivered. Replayed from the connected-signal-matrix captures
/// (<c>Recordings/asterisk-ami/connected-signal-matrix-*</c>, the README's <i>The connected-signal-matrix captures</i>):
/// connected calls whose channels hang up with normal clearing, and the same calls ended instead by a reload that finds
/// none of their channels at a checkpoint the capture took while they were connected or waiting after a connection.
/// </summary>
/// <remarks>
/// The abnormal ending of a captured call, whose caller hangs up with another cause, is checked with the reordered
/// copies <see cref="ConnectedCallWaitingEndingReplayTests"/> builds.
/// </remarks>
public sealed class EndingFinalStateReplayTests
{
    /// <summary>Every capture, with each connected call it keeps that ends with its channels' normal clearing.</summary>
    public static TheoryData<string, string> NormallyClearedCalls
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var fixture in AmiCaptureReplay.ConnectedSignalMatrixCaptures)
                data.Add(fixture, "plain-01");
            foreach (var fixture in AmiCaptureReplay.ConnectedSignalMatrixHangupCaptures)
            {
                data.Add(fixture, "queue-txdial-01");
                data.Add(fixture, "requeue-hangup-01");
                data.Add(fixture, "aq-hangup-01");
            }

            return data;
        }
    }

    /// <summary>Every capture, with each call it keeps and a checkpoint taken while the call was connected or waiting after a connection.</summary>
    public static TheoryData<string, string, string> ProvenGoneCalls
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            foreach (var fixture in AmiCaptureReplay.ConnectedSignalMatrixCaptures)
                data.Add(fixture, "plain-01", "up");
            foreach (var fixture in AmiCaptureReplay.ConnectedSignalMatrixHangupCaptures)
            {
                data.Add(fixture, "queue-txdial-01", "up");
                data.Add(fixture, "requeue-hangup-01", "wait2");
                data.Add(fixture, "aq-hangup-01", "wait");
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(NormallyClearedCalls))]
    public async Task CallEndedEvent_ShouldCarryCompleted_WhenAConnectedCapturedCallHangsUpWithNormalClearing(
        string fixture, string step)
    {
        var run = await SignalMatrix.LiveAsync(fixture, step);

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        run.Session.Should().NotBeNull("premise: the replay opens the caller's call");
        run.Session!.HangupCause.Should().Be(HangupCause.NormalClearing, "premise: the call ends with normal clearing");
        run.Endings.Select(e => e.FinalState).Should().Equal([CallSessionState.Completed],
            "the call ends once, completed, and its ending carries that state");
        run.StatesWhenEnded.Should().Equal([CallSessionState.Completed],
            "the call reads the state its ending carries when the ending is delivered");
    }

    [Theory]
    [MemberData(nameof(ProvenGoneCalls))]
    public async Task CallEndedEvent_ShouldCarryCompleted_WhenAReloadProvesAConnectedCapturedCallGone(
        string fixture, string step, string checkpoint)
    {
        var run = await SignalMatrix.GoneAtAsync(await SignalMatrix.ReadAsync(fixture, step), checkpoint);

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        run.StateAt(checkpoint).Should().BeOneOf([CallSessionState.Connected, CallSessionState.Queued],
            "premise: the call is connected, or waits in a later queue after a connection, when the reload finds it gone");
        run.Session!.HangupCause.Should().BeNull("premise: no hang-up was observed");
        run.Endings.Select(e => e.FinalState).Should().Equal([CallSessionState.Completed],
            "a call that connected, which a reload proves gone, took place and is over, and its ending says so");
        run.StatesWhenEnded.Should().Equal([CallSessionState.Completed],
            "the call reads the state its ending carries when the ending is delivered");
    }
}
