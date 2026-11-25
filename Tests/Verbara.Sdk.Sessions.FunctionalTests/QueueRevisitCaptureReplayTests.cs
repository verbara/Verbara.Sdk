using System.Collections.Concurrent;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// Replays the queue-revisit captures (<c>Recordings/asterisk-ami/queue-revisit-*</c>, the README's
/// <i>The queue-revisit captures</i>): a call answered in <c>q-first</c> by <c>PJSIP/agent1</c>, blind-transferred by
/// that member into <c>q-second</c> and answered there by <c>PJSIP/agent2</c> (step <c>revisit-01</c>), and a call
/// answered once in <c>q-first</c> (step <c>once-01</c>, the negative control).
/// </summary>
/// <remarks>
/// The members each call-connected event names are checked against the capture's own <c>AgentConnect</c> frames,
/// and those against a literal list, so an edit of a capture and an edit of the code cannot agree silently.
/// </remarks>
public sealed class QueueRevisitCaptureReplayTests
{
    private static readonly string[] Steps = ["revisit-01", "once-01"];

    public static TheoryData<string> QueueRevisitCaptures => new(AmiCaptureReplay.QueueRevisitCaptures);

    [Theory]
    [MemberData(nameof(QueueRevisitCaptures))]
    public async Task CallConnectedEvent_ShouldNameEachVisitsMember_WhenAQueueRevisitCaptureIsReplayed(string fixture)
    {
        var connects = (await AmiCaptureReplay.ReadCaptureAsync(fixture))
            .Where(f => f.RawFields?.ContainsKey("ActionID") != true
                && string.Equals(f.EventType, "AgentConnect", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.RawFields!)
            .ToList();
        var connected = new ConcurrentQueue<CallConnectedEvent>();
        var sessions = new Dictionary<string, CallSession>(StringComparer.Ordinal);
        IDisposable? subscription = null;

        var swallowed = await AmiCaptureReplay.ReplayStepsAsync(fixture, Steps,
            probe => subscription = probe.Manager.Events.Subscribe(evt =>
            {
                if (evt is CallConnectedEvent c)
                    connected.Enqueue(c);
            }),
            probe =>
            {
                foreach (var s in connected.Select(e => probe.Manager.GetById(e.SessionId)).OfType<CallSession>())
                    sessions[s.SessionId] = s;
            });
        subscription?.Dispose();

        using var scope = new AssertionScope();
        scope.AddReportable("connected", () => string.Join(Environment.NewLine,
            connected.Select(e => $"{e.SessionId} {e.QueueName} {e.AgentInterface} {e.AgentId}")));
        swallowed.Should().BeEmpty("no observer throws on {0}", fixture);
        (string? Caller, string? Queue, string? Member)[] wire =
        [
            ("9101", "q-first", "PJSIP/agent1"),
            ("9101", "q-second", "PJSIP/agent2"),
            ("9201", "q-first", "PJSIP/agent1"),
        ];
        connects.Select(f => (f.GetValueOrDefault("CallerIDNum"), f.GetValueOrDefault("Queue"), f.GetValueOrDefault("Interface")))
            .Should().Equal(wire,
                "premise: app_queue connected the revisit caller twice, to each queue's member, and the control once");
        connects.Should().OnlyContain(f => !f.ContainsKey("Agent"), "premise: no AgentConnect names an agent");

        var byCaller = connected.GroupBy(e => sessions[e.SessionId].CallerIdNum)
            .ToDictionary(g => g.Key ?? "", g => g.ToList());
        byCaller.Keys.Should().BeEquivalentTo(["9101", "9201"], "the two calls are each announced");
        (string? Queue, string? Member, string? Agent)[] revisit =
            [("q-first", "PJSIP/agent1", null), ("q-second", "PJSIP/agent2", null)];
        (string? Queue, string? Member, string? Agent)[] once = [("q-first", "PJSIP/agent1", null)];
        byCaller["9101"].Select(e => (e.QueueName, e.AgentInterface, e.AgentId)).Should().Equal(revisit,
            "each visit is announced once and names the member app_queue reported connecting it");
        sessions[byCaller["9101"][0].SessionId].AgentInterface.Should().Be("PJSIP/agent2",
            "the call records the member of its most recent connection, the second visit's");
        byCaller["9201"].Select(e => (e.QueueName, e.AgentInterface, e.AgentId)).Should().Equal(once,
            "a call answered once is announced once, naming its member");
    }
}
