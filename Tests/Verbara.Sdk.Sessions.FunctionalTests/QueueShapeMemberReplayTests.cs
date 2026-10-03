using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using Verbara.Sdk.Sessions.Serialization;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// Which queue member a call records as having taken it (<see cref="CallSession.AgentInterface"/>), replayed from
/// the AMI captures of Asterisk 20.20.1, 22.9.0 and 23.4.1.
/// </summary>
/// <remarks>
/// <para>
/// app_queue's <c>AgentConnect</c> names the member by its <c>Interface</c> and <c>MemberName</c> and carries no
/// <c>Agent</c> header in any capture, so the connect of an agent the SDK knows by name never runs for these calls.
/// The member a call records is app_queue's own report: the <c>Interface</c> of the <c>AgentConnect</c> on the
/// call's caller channel, exactly as sent. For a <c>Local</c> member that is the <c>Local</c> interface, not the
/// device it reaches.
/// </para>
/// <para>
/// The expected member of each queue shape is read from the capture itself and checked against a literal table
/// (<see cref="MemberByShape"/>), so an edit of a capture and an edit of the code cannot agree silently.
/// </para>
/// </remarks>
public sealed class QueueShapeMemberReplayTests
{
    /// <summary>
    /// The member app_queue reports it connected each taken queue shape's caller to, identical on all three
    /// captures.
    /// </summary>
    private static readonly Dictionary<string, string> MemberByShape = new(StringComparer.Ordinal)
    {
        ["L"] = "Local/agent@agents/n",
        ["P"] = "PJSIP/agent1",
        ["P2"] = "PJSIP/agent1",
        ["PI"] = "PJSIP/agent1",
        ["PD"] = "PJSIP/agent1",
        ["A"] = "Local/1001@agent-request/n",
        ["AO"] = "Local/1001@agent-request",
        ["F"] = "Local/agent1@from-queue/n",
        ["T"] = "PJSIP/agent3",
        ["O"] = "PJSIP/agent3",
    };

    /// <summary>The queue shapes whose caller no member took.</summary>
    private static readonly string[] Untaken = ["XC", "AX", "X1", "X2", "X3"];

    /// <summary>The shapes whose call never joins a queue.</summary>
    private static readonly string[] NeverQueued = ["D", "I"];

    public static TheoryData<string> QueueShapeCaptures => new(AmiCaptureReplay.QueueShapeCaptures);

    /// <summary>The captures, other than the queue shapes, that hold queue calls a member took.</summary>
    public static TheoryData<string> OtherQueueCaptures => new(
        AmiCaptureReplay.CallShapeCaptures.Concat(AmiCaptureReplay.QueueExitCaptures).Concat(AmiCaptureReplay.QueueLoopCaptures));

    [Theory]
    [MemberData(nameof(QueueShapeCaptures))]
    public async Task AgentInterface_ShouldBeTheMemberAppQueueReported_WhenAQueueShapeCaptureIsReplayed(string fixture)
    {
        var replay = await AmiCaptureReplay.ReplayQueueShapesAsync(fixture);
        using var scope = new AssertionScope();
        scope.AddReportable("members", DescribeMembers(replay));

        foreach (var shape in replay.Shapes)
        {
            shape.AsteriskConnectsNamingAnAgent.Should().Be(0,
                "premise: no AgentConnect on {0}'s caller names an agent, so a known agent's connect never runs", shape.Shape.Name);
            shape.Sessions.Should().NotContain(s => s.AgentId != null, "{0}: no known agent took the call", shape.Shape.Name);
            shape.ConnectedEvents.Should().NotContain(e => e.AgentId != null,
                "{0}: the call-connected event names no agent, as before", shape.Shape.Name);

            if (MemberByShape.TryGetValue(shape.Shape.Id, out var member))
            {
                shape.AsteriskConnectInterfaces.Should().Equal([member],
                    "premise: app_queue connected {0}'s caller once, to {1}", shape.Shape.Name, member);
                shape.Sessions.Where(s => s.AgentInterface == member).Should().ContainSingle(
                    "{0}: the call app_queue connected records the member it reported", shape.Shape.Name);

                var atDelivery = shape.ConnectedEvents
                    .Select((e, i) => (e.QueueName, AgentInterface: shape.AgentInterfacesAtConnected[i]))
                    .Where(e => e.QueueName is not null)
                    .ToList();
                atDelivery.Should().NotBeEmpty("premise: {0}'s queue visit is announced connected", shape.Shape.Name);
                atDelivery.Should().OnlyContain(e => e.AgentInterface == member,
                    "{0}: the member is recorded when the call is announced connected, so a consumer reads it on that delivery",
                    shape.Shape.Name);
            }
            else
            {
                (Untaken.Contains(shape.Shape.Id) || NeverQueued.Contains(shape.Shape.Id)).Should().BeTrue(
                    "premise: {0} is a taken, an untaken or a never-queued shape", shape.Shape.Name);
                shape.AsteriskConnectInterfaces.Should().BeEmpty("premise: app_queue connected {0}'s caller to no member", shape.Shape.Name);
                shape.Sessions.Should().NotBeEmpty("premise: {0} opened a call", shape.Shape.Name);
                shape.Sessions.Should().OnlyContain(s => s.AgentInterface == null,
                    "{0}: no member took the call, so it records none", shape.Shape.Name);
            }
        }
    }

    /// <summary>
    /// A convenience check that the member survives the persisted copy of the call; the snapshot's own round trip
    /// is pinned where it is defined.
    /// </summary>
    [Theory]
    [MemberData(nameof(QueueShapeCaptures))]
    public async Task AgentInterface_ShouldSurviveAPersistedRoundTrip_WhenAQueueShapeCallRecordedItsMember(string fixture)
    {
        var replay = await AmiCaptureReplay.ReplayQueueShapesAsync(fixture);
        var call = replay.Shape("P").Sessions.Should().ContainSingle("P is one call").Subject;

        var json = JsonSerializer.Serialize(CallSessionSnapshot.FromSession(call), SessionJsonContext.Default.CallSessionSnapshot);
        var restored = JsonSerializer.Deserialize(json, SessionJsonContext.Default.CallSessionSnapshot)!.ToSession();

        restored.AgentInterface.Should().Be("PJSIP/agent1", "the persisted copy carries the member the call recorded");
    }

    /// <summary>
    /// The call shapes, the queue exits and the queue loop: every <c>AgentConnect</c> in them lands on a call the
    /// manager saw join a queue, and that call records the frame's <c>Interface</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(OtherQueueCaptures))]
    public async Task AgentInterface_ShouldBeEveryConnectsMember_WhenACaptureWithQueueCallsIsReplayed(string fixture)
    {
        var connects = (await AmiCaptureReplay.ReadCaptureAsync(fixture))
            .Where(f => f.RawFields?.ContainsKey("ActionID") != true
                && string.Equals(f.EventType, "AgentConnect", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.RawFields!)
            .ToList();
        var replay = await AmiCaptureReplay.ReplayAsync(fixture);

        using var scope = new AssertionScope();
        scope.AddReportable("calls", () => DescribeCalls(replay.Calls.Select(c => c.Session)));
        connects.Should().NotBeEmpty("premise: {0} holds a queue call a member took", fixture);

        foreach (var fields in connects)
        {
            var callerUniqueId = fields.GetValueOrDefault("Uniqueid");
            var member = fields.GetValueOrDefault("Interface");
            fields.ContainsKey("Agent").Should().BeFalse("premise: the AgentConnect on {0} names no agent", callerUniqueId);

            var call = replay.Calls.Select(c => c.Session)
                .Where(s => s.Participants.Any(p => p.UniqueId == callerUniqueId))
                .Should().ContainSingle("premise: one call holds the caller channel {0}", callerUniqueId).Subject;
            call.QueueName.Should().NotBeNull("premise: the manager saw {0} join a queue", callerUniqueId);
            call.AgentInterface.Should().Be(member, "app_queue connected {0} to {1}", callerUniqueId, member);
        }
    }

    /// <summary>
    /// The queue-reload captures, each call replayed whole and across the reconnect reloads whose snapshot reopens
    /// or reports its visit: the call app_queue connected records the member, including a visit the manager first
    /// learned of from a <c>QueueStatus</c> snapshot.
    /// </summary>
    [Theory]
    [MemberData(nameof(QueueReloadCases))]
    public async Task AgentInterface_ShouldBeTheMember_WhenAQueueReloadCallIsReplayed(string fixture, string caller, string? snapshot,
        string? outageAfter, int outageOrdinal)
    {
        var frames = await AmiCaptureReplay.ReadCaptureAsync(fixture);
        var callerUniqueId = frames.First(f => string.Equals(f.EventType, "Newchannel", StringComparison.OrdinalIgnoreCase)
            && f.RawFields?.ContainsKey("ActionID") != true
            && f.RawFields?.GetValueOrDefault("CallerIDNum") == caller
            && f.RawFields?.GetValueOrDefault("Uniqueid") == f.RawFields?.GetValueOrDefault("Linkedid")).UniqueId;
        var connect = frames.Single(f => f.RawFields?.ContainsKey("ActionID") != true
            && string.Equals(f.EventType, "AgentConnect", StringComparison.OrdinalIgnoreCase)
            && f.RawFields?.GetValueOrDefault("Uniqueid") == callerUniqueId);
        var member = connect.RawFields!.GetValueOrDefault("Interface");

        var outage = outageAfter switch
        {
            null => null,
            "" => Outage.FromTheCallsFirstFrame,
            _ => Outage.After(outageAfter, outageOrdinal),
        };
        var replay = await AmiCaptureReplay.ReplayQueueReloadAsync(fixture, caller,
            snapshot is null ? null : CapturedSnapshot.Named(snapshot), outage, new ManualClock(DateTimeOffset.UnixEpoch));

        using var scope = new AssertionScope();
        scope.AddReportable("replay", replay.Describe());
        scope.AddReportable("calls", () => DescribeCalls(replay.Sessions));
        connect.RawFields!.ContainsKey("Agent").Should().BeFalse("premise: the AgentConnect names no agent");
        member.Should().Be("Local/late@agents-late/n", "premise: q-late's one member is a Local channel");

        var call = replay.Sessions.Where(s => s.Participants.Any(p => p.UniqueId == callerUniqueId))
            .Should().ContainSingle("premise: one call holds the caller channel").Subject;
        call.QueueName.Should().Be("q-late", "premise: the manager holds the visit app_queue connected, live or reopened from the snapshot");
        call.AgentInterface.Should().Be(member, "app_queue connected the caller to {0}", member);
        call.AgentId.Should().BeNull("no known agent took the call");
        replay.Connected.Should().NotContain(e => e.AgentId != null, "the call-connected event names no agent, as before");
    }

    /// <summary>
    /// (capture, caller, snapshot, outage start, ordinal): every call whole with no reload, then the reloads whose
    /// snapshot reports the visit app_queue later connects. An empty outage start withholds the call's frames from
    /// its first, so the manager first learns of the call from the snapshot.
    /// </summary>
    public static TheoryData<string, string, string?, string?, int> QueueReloadCases()
    {
        var cases = new TheoryData<string, string, string?, string?, int>();
        foreach (var fixture in AmiCaptureReplay.QueueReloadCaptures)
        {
            foreach (var caller in new[] { "5552101", "5552102", "5552103", "5552104" })
                cases.Add(fixture, caller, null, null, 0);

            cases.Add(fixture, "5552101", "a1", "", 0);
            cases.Add(fixture, "5552102", "b2", "QueueCallerLeave", 1);
            cases.Add(fixture, "5552102", "b2", "QueueCallerJoin", 1);
            cases.Add(fixture, "5552103", "c1", "QueueCallerJoin", 1);
            cases.Add(fixture, "5552104", "d1", "QueueCallerLeave", 1);
        }

        return cases;
    }

    private static string DescribeMembers(QueueShapeReplay replay) =>
        replay.Fixture + Environment.NewLine + string.Join(Environment.NewLine, replay.Shapes.Select(shape => string.Create(
            CultureInfo.InvariantCulture,
            $"   {shape.Shape.Name,-42} asterisk=[{string.Join(",", shape.AsteriskConnectInterfaces)}] "
            + $"sessions=[{string.Join(",", shape.Sessions.Select(s => $"{s.AgentInterface ?? "<null>"}/{s.AgentId ?? "<null>"}"))}] "
            + $"atConnected=[{string.Join(",", shape.AgentInterfacesAtConnected.Select(i => i ?? "<null>"))}]")));

    private static string DescribeCalls(IEnumerable<CallSession> sessions) =>
        string.Join(Environment.NewLine, sessions.Select(s => string.Create(CultureInfo.InvariantCulture,
            $"   {(s.Participants.Count > 0 ? s.Participants[0].UniqueId : "-")}: queue={s.QueueName ?? "<null>"} agentInterface={s.AgentInterface ?? "<null>"} agentId={s.AgentId ?? "<null>"} state={s.State}")));
}
