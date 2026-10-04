using System.Text;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using Verbara.Sdk.Tests.Shared.Metrics;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// Live's queue table, loaded from the <c>QueueStatus</c> snapshots Asterisk 20.20.1, 22.9.0 and 23.4.1 returned
/// (<c>Recordings/asterisk-ami/queue-reload-*</c>, four calls each), holds each caller with the calling number
/// the <c>QueueEntry</c> carried in <c>CallerIDNum</c>, and records for a caller that leaves at once a
/// <c>live.queue.wait_time</c> sample of at least the <c>Wait</c> Asterisk reported and less than one second
/// more.
/// </summary>
/// <remarks>
/// <para>
/// Each snapshot is the answer frames that carry the snapshot's <c>QueueStatus</c> <c>ActionID</c>
/// (<c>w2qr-&lt;id&gt;-qs</c>), read through the replay's own AMI reader and deserializer, and handed to a real
/// <see cref="VerbaraServer"/> as the answer to the <c>QueueStatus</c> of its load.
/// </para>
/// <para>
/// Two cases alter one captured <c>QueueEntry</c> frame in memory and read it through the same reader: the header
/// lookup is case-insensitive, so a <c>CallerID</c> header is what fills <c>QueueEntryEvent.CallerId</c>, which a
/// test that sets the typed member never exercises.
/// </para>
/// </remarks>
public sealed class QueueSnapshotWireReplayTests
{
    private const string EntryFrameStart = "Event: QueueEntry\r\n";
    private const string FrameEnd = "\r\n\r\n";

    public static TheoryData<string> QueueReloadCaptures => new(AmiCaptureReplay.QueueReloadCaptures);

    [Theory]
    [MemberData(nameof(QueueReloadCaptures))]
    public async Task StartAsync_ShouldHoldEveryCallerWithTheCallerIdNumItsEntryCarried_WhenLoadedFromACapturedSnapshot(
        string fixture)
    {
        var snapshots = Snapshots(await AmiCaptureReplay.ReadCaptureAsync(fixture));

        var held = new List<(string Channel, string? Sent, string? Held)>();
        foreach (var snapshot in snapshots)
        {
            await using var server = await LoadAsync(snapshot);
            foreach (var entry in snapshot.OfType<QueueEntryEvent>())
                held.Add((entry.Channel!, entry.CallerIDNum, server.Queues.GetByName(entry.Queue!)!.Entries[entry.Channel!].CallerId));
        }

        using var scope = new AssertionScope();
        held.Should().HaveCount(4, "the capture's snapshots report its four calls waiting, one entry each");
        held.Select(h => h.Sent).Should().Equal(["5552101", "5552102", "5552103", "5552104"],
            "the premise: Asterisk sent each caller's number in CallerIDNum");
        foreach (var (channel, sent, number) in held)
            number.Should().Be(sent, "the entry for {0} is held with the CallerIDNum Asterisk sent for it", channel);
    }

    [Theory]
    [MemberData(nameof(QueueReloadCaptures))]
    public async Task OnCallerLeft_ShouldRecordAtLeastTheReportedWait_WhenACallerLoadedFromACapturedSnapshotLeavesAtOnce(
        string fixture)
    {
        var snapshots = Snapshots(await AmiCaptureReplay.ReadCaptureAsync(fixture));
        using var recorder = new LiveQueueWaitSamples();

        var recorded = new List<(string Channel, long Wait, IReadOnlyList<double> Samples)>();
        foreach (var snapshot in snapshots)
        {
            await using var server = await LoadAsync(snapshot);
            foreach (var entry in snapshot.OfType<QueueEntryEvent>())
            {
                var samples = recorder.During(() => server.Queues.OnCallerLeft(entry.Queue!, entry.Channel!));
                recorded.Add((entry.Channel!, entry.Wait!.Value, samples));
            }
        }

        using var scope = new AssertionScope();
        recorded.Select(r => r.Wait).Should().Equal([3L, 2L, 2L, 1L],
            "the premise: the snapshots reported waits of 3, 2, 2 and 1 seconds");
        foreach (var (channel, wait, samples) in recorded)
        {
            samples.Should().ContainSingle("one leave of {0} records one sample", channel)
                .Which.Should().BeGreaterThanOrEqualTo(wait * 1000.0,
                    "Asterisk reported {0} had waited {1} s when the snapshot was taken", channel, wait)
                .And.BeLessThan((wait + 1) * 1000.0, "{0} left as soon as the load finished", channel);
        }
    }

    [Theory]
    [MemberData(nameof(QueueReloadCaptures))]
    public async Task StartAsync_ShouldHoldTheCallerIdHeader_WhenACapturedEntryCarriesCallerIdInsteadOfCallerIdNum(
        string fixture)
    {
        var entry = await AlteredEntryAsync(fixture, frame => frame.Replace(
            "\r\nCallerIDNum: 5552101\r\n", "\r\nCallerID: 5552100\r\n", StringComparison.Ordinal));
        entry.CallerIDNum.Should().BeNull("the premise: the altered frame carries no CallerIDNum");

        await using var server = await LoadAsync([entry]);

        server.Queues.GetByName(entry.Queue!)!.Entries[entry.Channel!].CallerId.Should().Be("5552100",
            "with no CallerIDNum, the caller number is read from a CallerID header");
    }

    [Theory]
    [MemberData(nameof(QueueReloadCaptures))]
    public async Task StartAsync_ShouldHoldTheCallerIdNum_WhenACapturedEntryAlsoCarriesACallerIdHeader(string fixture)
    {
        var entry = await AlteredEntryAsync(fixture, frame => frame.Replace(
            "\r\nCallerIDNum: 5552101\r\n", "\r\nCallerIDNum: 5552101\r\nCallerID: 999\r\n", StringComparison.Ordinal));
        entry.CallerId.Should().Be("999", "the premise: the altered frame carries a CallerID header too");

        await using var server = await LoadAsync([entry]);

        server.Queues.GetByName(entry.Queue!)!.Entries[entry.Channel!].CallerId.Should().Be("5552101",
            "CallerIDNum, the header Asterisk sends, is preferred over a CallerID header");
    }

    /// <summary>The capture's <c>QueueStatus</c> answers that report a caller waiting, each as its frames in order.</summary>
    private static List<ManagerEvent[]> Snapshots(IReadOnlyList<ManagerEvent> frames) =>
        [.. frames
            .Where(f => f.RawFields?.GetValueOrDefault("ActionID") is { } id && id.EndsWith("-qs", StringComparison.Ordinal))
            .GroupBy(f => f.RawFields!["ActionID"], StringComparer.Ordinal)
            .Select(g => g.ToArray())
            .Where(answer => answer.OfType<QueueEntryEvent>().Any())];

    /// <summary>
    /// The capture's first <c>QueueEntry</c> frame (the caller <c>5552101</c>), altered by <paramref name="alter"/>
    /// and read through the replay's own reader and deserializer.
    /// </summary>
    private static async Task<QueueEntryEvent> AlteredEntryAsync(string fixture, Func<string, string> alter)
    {
        var capture = Encoding.Latin1.GetString(AmiCaptureReplay.ReadCaptureBytes(fixture));
        var start = capture.IndexOf(EntryFrameStart, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the premise: the capture holds a QueueEntry frame");
        var end = capture.IndexOf(FrameEnd, start, StringComparison.Ordinal);
        var frame = capture[start..end];
        var altered = alter(frame);
        altered.Should().NotBe(frame, "the premise: the alteration applies to the captured frame");

        var frames = await AmiCaptureReplay.ReadCaptureAsync(
            Encoding.Latin1.GetBytes(string.Concat(capture.AsSpan(0, start), altered, capture.AsSpan(end))));
        return frames.OfType<QueueEntryEvent>().First();
    }

    private static async Task<VerbaraServer> LoadAsync(ManagerEvent[] queueStatus)
    {
        var connection = Substitute.For<IAmiConnection>();
        connection.Subscribe(Arg.Any<IObserver<ManagerEvent>>()).Returns(Substitute.For<IDisposable>());
        connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(call => Answer(call.Arg<ManagerAction>() is QueueStatusAction ? queueStatus : []));
        var server = new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance);
        await server.StartAsync();
        return server;
    }

    private static async IAsyncEnumerable<ManagerEvent> Answer(ManagerEvent[] events)
    {
        await Task.CompletedTask;
        foreach (var evt in events)
            yield return evt;
    }
}
