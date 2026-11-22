using System.Collections.Concurrent;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// Replays the queue-ring-no-answer captures (<c>Recordings/asterisk-ami/queue-ring-no-answer-*</c>, the README's
/// <i>The queue-ring-no-answer captures</i>): a caller in the linear queue <c>q-rna</c> whose first member
/// <c>PJSIP/agent1</c> rings out unanswered before <c>PJSIP/agent2</c> answers (step <c>rna-01</c>), one in
/// <c>q-busy</c> whose first member <c>PJSIP/agent3</c> answers busy (step <c>busy-01</c>), and one in <c>q-answer</c>
/// answered at once (step <c>answer-01</c>, the negative control).
/// </summary>
/// <remarks>
/// The missed rings each notification names are checked against the capture's own <c>AgentRingNoAnswer</c> frames,
/// and those against a literal list, so an edit of a capture and an edit of the code cannot agree silently.
/// </remarks>
public sealed class QueueRingNoAnswerCaptureReplayTests
{
    private static readonly string[] Steps = ["rna-01", "busy-01", "answer-01"];

    public static TheoryData<string> QueueRingNoAnswerCaptures => new(AmiCaptureReplay.QueueRingNoAnswerCaptures);

    [Theory]
    [MemberData(nameof(QueueRingNoAnswerCaptures))]
    public async Task CallRingNoAnswerEvent_ShouldBeRaisedOncePerMissedRing_WhenAQueueRingNoAnswerCaptureIsReplayed(
        string fixture)
    {
        var frames = (await AmiCaptureReplay.ReadCaptureAsync(fixture))
            .Where(f => f.RawFields?.ContainsKey("ActionID") != true)
            .ToList();
        var missedOnWire = frames
            .Where(f => string.Equals(f.EventType, "AgentRingNoAnswer", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.RawFields!)
            .ToList();
        var raised = new ConcurrentQueue<SessionDomainEvent>();
        var callers = new Dictionary<string, string?>(StringComparer.Ordinal);
        IDisposable? subscription = null;

        var swallowed = await AmiCaptureReplay.ReplayStepsAsync(fixture, Steps,
            probe => subscription = probe.Manager.Events.Subscribe(evt =>
            {
                if (evt is CallRingNoAnswerEvent or CallConnectedEvent)
                    raised.Enqueue(evt);
            }),
            probe =>
            {
                foreach (var id in raised.Select(e => e.SessionId).Distinct())
                    callers[id] = probe.Manager.GetById(id)?.CallerIdNum;
            });
        subscription?.Dispose();

        using var scope = new AssertionScope();
        scope.AddReportable("raised", () => string.Join(Environment.NewLine,
            raised.Select(e => $"{e.GetType().Name} {e.SessionId} {callers.GetValueOrDefault(e.SessionId)}")));
        swallowed.Should().BeEmpty("no observer throws on {0}", fixture);

        (string? Caller, string? Queue, string? Member)[] wire =
        [
            ("9301", "q-rna", "PJSIP/agent1"),
            ("9401", "q-busy", "PJSIP/agent3"),
        ];
        missedOnWire.Select(f => (f.GetValueOrDefault("CallerIDNum"), f.GetValueOrDefault("Queue"), f.GetValueOrDefault("Interface")))
            .Should().Equal(wire,
                "premise: app_queue reported one missed ring for the ring-out caller and one for the busy caller, none "
                + "for the control");
        missedOnWire.Should().OnlyContain(f => !f.ContainsKey("Agent"), "premise: no AgentRingNoAnswer names an agent");

        var missed = raised.OfType<CallRingNoAnswerEvent>()
            .Select(e => (Caller: callers.GetValueOrDefault(e.SessionId), e.QueueName, e.AgentInterface, e.AgentId))
            .ToList();
        (string? Caller, string? QueueName, string? AgentInterface, string AgentId)[] expected =
        [
            ("9301", "q-rna", "PJSIP/agent1", "PJSIP/agent1"),
            ("9401", "q-busy", "PJSIP/agent3", "PJSIP/agent3"),
        ];
        missed.Should().Equal(expected,
            "each missed ring is raised once for its call, naming the frame's queue and member; with no agent named, "
            + "the member is its interface");

        foreach (var caller in new[] { "9301", "9401" })
        {
            raised.Where(e => callers.GetValueOrDefault(e.SessionId) == caller).Select(e => e.GetType().Name)
                .Should().Equal([nameof(CallRingNoAnswerEvent), nameof(CallConnectedEvent)],
                    "caller {0}'s missed ring is raised before the next member's connect is announced", caller);
        }
    }
}
