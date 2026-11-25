using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// Replays the connected-signal-matrix captures one call at a time and checks the member and the agent the call
/// records (<see cref="CallSession.AgentInterface"/>, <see cref="CallSession.AgentId"/>): those of its most recent
/// connection, already when that connection is announced, in the save made after it, and once the call has ended.
/// </summary>
/// <remarks>
/// app_queue's connect report in these captures carries the member's interface and no <c>Agent</c> header, so no
/// connection here names an agent. The case of an agent known by name, which no capture reaches, is covered on typed
/// frames by the session manager's unit tests.
/// </remarks>
public sealed class CallMemberReplayTests
{
    public static TheoryData<string> MatrixCaptures => new(AmiCaptureReplay.ConnectedSignalMatrixCaptures);

    public static TheoryData<string> HangupCaptures => new(AmiCaptureReplay.ConnectedSignalMatrixHangupCaptures);

    [Theory]
    [MemberData(nameof(MatrixCaptures))]
    public async Task AgentInterface_ShouldBeTheSecondMember_WhenACapturedCallIsRequeuedAndTakenByAnotherMember(
        string fixture)
    {
        var store = new RecordingSessionStore();
        var run = await SignalMatrix.LiveAsync(await SignalMatrix.ReadAsync(fixture, "requeue-01"), store);

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        scope.AddReportable("members", () => Describe(run));
        run.Announcements.Select(a => a.Event.AgentInterface).Should().Equal(["PJSIP/agent1", "PJSIP/agent2"],
            "premise: each queue connected the call to its own member");
        if (run.Announcements.Count != 2)
            return;

        var first = run.Announcements[0];
        var second = run.Announcements[1];
        (first.AgentId, first.AgentInterface).Should().Be(((string?)null, (string?)"PJSIP/agent1"),
            "the first visit's member is recorded before that connection is announced");
        (second.AgentId, second.AgentInterface).Should().Be(((string?)null, (string?)"PJSIP/agent2"),
            "the second visit's connection is the call's most recent: its member is recorded before it is announced");
        var save = store.Saves.Skip(second.SavesBefore).FirstOrDefault(s => s.SessionId == run.Session!.SessionId);
        save.Should().NotBeNull("the second connection is saved");
        (save?.State, save?.AgentInterface).Should().Be(((CallSessionState?)CallSessionState.Connected, (string?)"PJSIP/agent2"),
            "the save made after the second connection carries its member");
        (run.Session!.AgentId, run.Session.AgentInterface).Should().Be(((string?)null, (string?)"PJSIP/agent2"),
            "the call keeps the second member once it has ended");
    }

    [Theory]
    [MemberData(nameof(HangupCaptures))]
    public async Task AgentInterface_ShouldBeCleared_WhenACapturedQueuedCallReturnsFromABlindTransfer(string fixture)
    {
        var run = await SignalMatrix.LiveAsync(await SignalMatrix.ReadAsync(fixture, "queue-txdial-01"));

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        scope.AddReportable("members", () => Describe(run));
        run.Announcements.Should().HaveCount(2, "premise: the queue's connection and the transfer's return are announced");
        if (run.Announcements.Count != 2)
            return;

        run.Announcements[0].AgentInterface.Should().Be("PJSIP/agent1", "premise: the queue's member took the call");
        (run.Announcements[1].AgentId, run.Announcements[1].AgentInterface).Should().Be(((string?)null, (string?)null),
            "the transfer target's answer made the call's most recent connection, which names no member");
        (run.Session!.AgentId, run.Session.AgentInterface).Should().Be(((string?)null, (string?)null),
            "the call records no member once it has ended");
    }

    private static string Describe(SignalMatrix.Run run) =>
        $"[{string.Join(", ", run.Announcements.Select(a =>
            $"{a.Event.QueueName}|event {a.Event.AgentInterface}|call {a.AgentId}/{a.AgentInterface}|saves {a.SavesBefore}"))}] "
        + $"end={run.Session?.AgentId}/{run.Session?.AgentInterface}";
}
