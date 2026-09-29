using System.Text;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// What Asterisk's queue application puts on the wire, pinned on every committed queue capture: the premises
/// the SDK's rule for "the same queue visit" rests on. These are pins about Asterisk, not about the SDK; no
/// frame is delivered to it.
/// </summary>
/// <remarks>
/// <para>
/// A reload that reports a caller still waiting in the queue whose visit the SDK holds open is the same
/// visit only while Asterisk has not reported the caller leaving it. That reading holds only if app_queue
/// closes every visit with <c>QueueCallerLeave</c>, never reports a join while the caller's previous visit
/// is still open, and reports an answered visit's leave before its <c>AgentConnect</c>. The start of a
/// visit the SDK learns of from a snapshot is taken from the snapshot's <c>Wait</c>, so every snapshot
/// <c>QueueEntry</c> must carry it.
/// </para>
/// <para>
/// Frames are grouped by the <c>Uniqueid</c> of the channel app_queue reports: a visit opens at a
/// <c>QueueCallerJoin</c> and closes at the <c>QueueCallerLeave</c> for the same queue. Frames that carry an
/// <c>ActionID</c> are the capture tap's markers and the answers to its own actions, not live events; only
/// their <c>QueueEntry</c> frames are read, for <c>Wait</c>.
/// </para>
/// </remarks>
public sealed class QueueCapturePremiseTests
{
    /// <summary>
    /// Every committed queue capture, with the joins, <c>AgentConnect</c>s and snapshot <c>QueueEntry</c>s it
    /// holds. The queue-shape captures run fifteen queue shapes, one of which (the overflow) joins twice; the
    /// reload captures run four calls, two of which re-join the same queue and one of which overflows.
    /// </summary>
    public static TheoryData<string, int, int, int> Captures => new()
    {
        { "queue-shapes-asterisk-20.20.1.raw", 16, 10, 0 },
        { "queue-shapes-asterisk-22.9.0.raw", 16, 10, 0 },
        { "queue-shapes-asterisk-23.4.1.raw", 16, 10, 0 },
        { "queue-reload-asterisk-20.20.1.raw", 7, 4, 4 },
        { "queue-reload-asterisk-22.9.0.raw", 7, 4, 4 },
        { "queue-reload-asterisk-23.4.1.raw", 7, 4, 4 },
    };

    [Theory]
    [MemberData(nameof(Captures))]
    public async Task QueueCaptures_ShouldCloseEveryVisitWithItsLeaveBeforeAReJoinOrAConnection_WhenReadInWireOrder(
        string fixture, int joins, int connects, int entries)
    {
        var premise = QueueVisitPremise.Measure(await AmiCaptureReplay.ReadCaptureAsync(fixture));

        using var scope = new AssertionScope();
        scope.AddReportable("premise", premise.Describe());
        premise.Joins.Should().Be(joins, "{0} holds {1} QueueCallerJoin frames", fixture, joins);
        premise.Leaves.Should().Be(premise.Joins, "every visit ends with a QueueCallerLeave for its queue");
        premise.VisitsWithoutLeave.Should().Be(0, "every visit ends with a QueueCallerLeave for its queue");
        premise.JoinsWhileOpen.Should().Be(0, "app_queue never reports a join while the caller's previous visit has no leave");
        premise.Connects.Should().Be(connects, "{0} holds {1} AgentConnect frames", fixture, connects);
        premise.ConnectsAfterLeave.Should().Be(premise.Connects,
            "on an answer, app_queue reports the visit's QueueCallerLeave before its AgentConnect");
        premise.Entries.Should().Be(entries, "{0} holds {1} snapshot QueueEntry frames", fixture, entries);
        premise.EntriesWithoutWait.Should().Be(0, "every snapshot QueueEntry carries Wait");
        premise.Violations.Should().BeEmpty();
    }

    /// <summary>
    /// The negative control: the check above, on an in-memory copy of a capture with its first
    /// <c>QueueCallerLeave</c> frame removed, reports the visit that frame closed. Without it, a check that
    /// could not see a missing leave would pass on every capture.
    /// </summary>
    [Fact]
    public async Task QueueVisitPremise_ShouldReportTheVisitLeftOpen_WhenOneQueueCallerLeaveIsRemovedFromACapture()
    {
        const string fixture = "queue-shapes-asterisk-22.9.0.raw";
        var doctored = WithoutFirstFrameStartingWith(AmiCaptureReplay.ReadCaptureBytes(fixture), "Event: QueueCallerLeave");

        var premise = QueueVisitPremise.Measure(await AmiCaptureReplay.ReadCaptureAsync(doctored));

        using var scope = new AssertionScope();
        scope.AddReportable("premise", premise.Describe());
        premise.Leaves.Should().Be(15, "one of the capture's 16 leaves was removed");
        premise.VisitsWithoutLeave.Should().Be(1, "the visit whose leave was removed is never closed");
        premise.Violations.Should().NotBeEmpty("the check must report what it found wrong");
    }

    private static byte[] WithoutFirstFrameStartingWith(byte[] capture, string header)
    {
        var text = Encoding.UTF8.GetString(capture);
        var start = text.IndexOf("\r\n" + header, StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "premise: the capture holds a frame starting with {0}", header);

        // A frame is the header lines after one blank line and up to the next; drop it with its terminator.
        var frameStart = start + 2;
        var frameEnd = text.IndexOf("\r\n\r\n", frameStart, StringComparison.Ordinal) + 4;
        return Encoding.UTF8.GetBytes(text.Remove(frameStart, frameEnd - frameStart));
    }
}

/// <summary>
/// The counts behind the premise of <see cref="QueueCapturePremiseTests"/>, measured over one capture's
/// frames in wire order, and a line for each frame that breaks it.
/// </summary>
internal sealed record QueueVisitPremise(
    int Joins,
    int Leaves,
    int JoinsWhileOpen,
    int VisitsWithoutLeave,
    int Connects,
    int ConnectsAfterLeave,
    int Entries,
    int EntriesWithoutWait,
    IReadOnlyList<string> Violations)
{
    public static QueueVisitPremise Measure(IReadOnlyList<ManagerEvent> frames)
    {
        var open = new Dictionary<string, string>(StringComparer.Ordinal);
        var lastLeft = new Dictionary<string, string>(StringComparer.Ordinal);
        var violations = new List<string>();
        int joins = 0, leaves = 0, joinsWhileOpen = 0, withoutLeave = 0, connects = 0, connectsAfterLeave = 0;
        int entries = 0, entriesWithoutWait = 0;

        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            if (frame.RawFields is not { } fields)
                continue;

            if (string.Equals(frame.EventType, "QueueEntry", StringComparison.OrdinalIgnoreCase))
            {
                entries++;
                if (!fields.ContainsKey("Wait"))
                {
                    entriesWithoutWait++;
                    violations.Add($"#{i} QueueEntry for {fields.GetValueOrDefault("Uniqueid")} carries no Wait");
                }
            }

            if (fields.ContainsKey("ActionID"))
                continue;

            var caller = fields.GetValueOrDefault("Uniqueid") ?? "";
            var queue = fields.GetValueOrDefault("Queue") ?? "";
            switch (frame.EventType)
            {
                case "QueueCallerJoin":
                    joins++;
                    if (open.TryGetValue(caller, out var stillOpen))
                    {
                        joinsWhileOpen++;
                        withoutLeave++;
                        violations.Add($"#{i} QueueCallerJoin({queue}) for {caller} while its visit in {stillOpen} has no leave");
                    }

                    open[caller] = queue;
                    break;

                case "QueueCallerLeave":
                    leaves++;
                    if (open.TryGetValue(caller, out var visit) && string.Equals(visit, queue, StringComparison.Ordinal))
                    {
                        open.Remove(caller);
                        lastLeft[caller] = queue;
                    }
                    else
                    {
                        violations.Add($"#{i} QueueCallerLeave({queue}) for {caller}, which has no visit open in {queue}");
                    }

                    break;

                case "AgentConnect":
                    connects++;
                    if (!open.ContainsKey(caller) && lastLeft.TryGetValue(caller, out var left)
                        && string.Equals(left, queue, StringComparison.Ordinal))
                    {
                        connectsAfterLeave++;
                    }
                    else
                    {
                        violations.Add($"#{i} AgentConnect({queue}) for {caller} before its visit's QueueCallerLeave");
                    }

                    break;
            }
        }

        foreach (var (caller, queue) in open)
        {
            withoutLeave++;
            violations.Add($"the visit of {caller} in {queue} never ends with a QueueCallerLeave");
        }

        return new QueueVisitPremise(joins, leaves, joinsWhileOpen, withoutLeave, connects, connectsAfterLeave,
            entries, entriesWithoutWait, violations);
    }

    public string Describe() =>
        $"joins={Joins} leaves={Leaves} joinsWhileOpen={JoinsWhileOpen} visitsWithoutLeave={VisitsWithoutLeave} "
        + $"connects={Connects} connectsAfterLeave={ConnectsAfterLeave} entries={Entries} entriesWithoutWait={EntriesWithoutWait}"
        + (Violations.Count == 0 ? "" : Environment.NewLine + "   " + string.Join(Environment.NewLine + "   ", Violations));
}
