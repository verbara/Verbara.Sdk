using System.Globalization;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using Verbara.Sdk.Sessions.Manager;
using Xunit.Abstractions;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// The committed queue-shape and queue-loop captures of Asterisk 20.20.1, 22.9.0 and 23.4.1, replayed through a
/// session manager built with an <see cref="IWrapUpDurationProvider"/> that answers a different duration for each
/// queue: every answered queue leg's <see cref="CallWrapUpEvent"/> runs its own queue's duration, the provider is
/// asked once per <c>AgentComplete</c> with the queue that frame names, and the same replay with no provider raises
/// the same wrap-ups as before.
/// </summary>
/// <remarks>
/// The clock is moved past the longest answer after the last frame and before the manager is disposed. It does not
/// move while frames are delivered, so a wrap-up the member's next connect closes early is closed early with or
/// without a provider, and the two replays are compared leg by leg.
/// </remarks>
[Collection(WaitTimeHistogramGroup.Name)]
public sealed class QueueWrapUpDurationCaptureReplayTests(ITestOutputHelper output)
{
    private const string ServerId = "replay";

    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// One distinct duration per queue the captures name. The last three are joined in the queue-shape captures but
    /// never answered, so the provider must never be asked for them.
    /// </summary>
    private static readonly Dictionary<string, TimeSpan> Durations = new(StringComparer.Ordinal)
    {
        ["q-local"] = TimeSpan.FromSeconds(7),
        ["q-pjsip"] = TimeSpan.FromSeconds(11),
        ["q-ringall"] = TimeSpan.FromSeconds(13),
        ["q-agent"] = TimeSpan.FromSeconds(17),
        ["q-agent-opt"] = TimeSpan.FromSeconds(19),
        ["q-freepbx"] = TimeSpan.FromSeconds(23),
        ["q-pjsip3"] = TimeSpan.FromSeconds(29),
        ["q-loop"] = TimeSpan.FromSeconds(37),
        ["q-confirm"] = TimeSpan.FromSeconds(41),
        ["q-agent-ack"] = TimeSpan.FromSeconds(43),
        ["q-noans"] = TimeSpan.FromSeconds(47),
    };

    /// <summary>Past the longest answer and the default, so every wrap-up still open after the last frame elapses.</summary>
    private static readonly TimeSpan PastTheLongest =
        Durations.Values.Append(new SessionOptions().WrapUpDuration).Max() + TimeSpan.FromSeconds(1);

    public static TheoryData<string> QueueShapeCaptures => new(AmiCaptureReplay.QueueShapeCaptures);

    public static TheoryData<string> QueueLoopCaptures => new(AmiCaptureReplay.QueueLoopCaptures);

    [Theory]
    [MemberData(nameof(QueueShapeCaptures))]
    public async Task Replay_ShouldRunEachAnsweredLegsWrapUpForItsQueuesDuration_WhenAProviderAnswersPerQueue(
        string fixture)
    {
        var completeQueues = await AgentCompleteQueuesAsync(fixture);
        var provider = new PerQueueProvider();

        var without = await AmiCaptureReplay.ReplayQueueShapesAsync(fixture, clock: new ManualClock(T0),
            afterLastFrame: c => c.Advance(PastTheLongest));
        var with = await AmiCaptureReplay.ReplayQueueShapesAsync(fixture, clock: new ManualClock(T0),
            afterLastFrame: c => c.Advance(PastTheLongest), wrapUpDurations: provider);

        var configured = new SessionOptions().WrapUpDuration;
        var rows = with.Shapes.Select(s => string.Create(CultureInfo.InvariantCulture,
            $"{s.Shape.Id,-4} complete={s.AsteriskCompletes} wrapUps={s.WrapUps.Count} [{string.Join(", ", s.WrapUps.Select(w => $"{w.QueueName}={w.WrapUpDuration.TotalSeconds}s"))}]")).ToList();
        output.WriteLine($"{fixture}: AgentComplete {completeQueues.Count}, asked {provider.Asks.Count}, wrap-ups with provider {with.Shapes.Sum(s => s.WrapUps.Count)}, without {without.Shapes.Sum(s => s.WrapUps.Count)}");
        foreach (var row in rows)
            output.WriteLine(row);

        using var scope = new AssertionScope();
        scope.AddReportable("shapes", string.Join(Environment.NewLine, rows));
        completeQueues.Should().NotBeEmpty("premise: the capture answers some visits");
        provider.Asks.Should().Equal(completeQueues.Select(q => (ServerId, q)),
            "the provider is asked once per AgentComplete, in order, for the server that delivered it and the queue it names");

        foreach (var (shapeWith, shapeWithout) in with.Shapes.Zip(without.Shapes))
        {
            shapeWithout.WrapUps.Should().HaveCount(shapeWithout.AsteriskCompletes,
                "with no provider, shape {0} raises one wrap-up per answered queue leg, as before", shapeWithout.Shape.Id);
            shapeWithout.WrapUps.Should().NotContain(w => w.WrapUpDuration > configured,
                "with no provider, no wrap-up of shape {0} outlasts the configured duration", shapeWithout.Shape.Id);
            shapeWith.WrapUps.Should().HaveCount(shapeWithout.WrapUps.Count,
                "shape {0} raises as many wrap-ups with a provider as without", shapeWith.Shape.Id);

            foreach (var (w, wo) in shapeWith.WrapUps.Zip(shapeWithout.WrapUps))
            {
                w.ServerId.Should().Be(ServerId);
                w.QueueName.Should().Be(wo.QueueName, "shape {0}: the provider does not change the queue a wrap-up is booked to", shapeWith.Shape.Id);
                var answer = Durations[w.QueueName!];
                if (wo.WrapUpDuration == configured)
                    w.WrapUpDuration.Should().Be(answer,
                        "shape {0}: a wrap-up its timer closes runs its queue's duration ({1})", shapeWith.Shape.Id, w.QueueName);
                else
                    w.WrapUpDuration.Should().Be(wo.WrapUpDuration,
                        "shape {0}: a wrap-up the member's next connect closes early closes at that connect", shapeWith.Shape.Id);
            }
        }

        with.UnattributedWrapUps.Should().Be(0);
        without.UnattributedWrapUps.Should().Be(0);
        with.ObserverExceptions.Should().BeEmpty();
        without.ObserverExceptions.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(QueueLoopCaptures))]
    public async Task Replay_ShouldRunTheLoopedCallersWrapUpForItsQueuesDuration_WhenAProviderAnswersPerQueue(
        string fixture)
    {
        var completeQueues = await AgentCompleteQueuesAsync(fixture);
        var provider = new PerQueueProvider();

        var without = await AmiCaptureReplay.ReplayAsync(fixture, clock: new ManualClock(T0),
            afterLastFrame: c => c.Advance(PastTheLongest));
        var with = await AmiCaptureReplay.ReplayAsync(fixture, clock: new ManualClock(T0),
            afterLastFrame: c => c.Advance(PastTheLongest), wrapUpDurations: provider);

        var wrapUpsWithout = without.Calls.SelectMany(c => c.DomainEvents.OfType<CallWrapUpEvent>()).ToList();
        var wrapUpsWith = with.Calls.SelectMany(c => c.DomainEvents.OfType<CallWrapUpEvent>()).ToList();
        output.WriteLine($"{fixture}: AgentComplete {completeQueues.Count}, asked {provider.Asks.Count}, "
            + $"with provider [{string.Join(", ", wrapUpsWith.Select(w => $"{w.QueueName}={w.WrapUpDuration.TotalSeconds}s"))}], "
            + $"without [{string.Join(", ", wrapUpsWithout.Select(w => $"{w.QueueName}={w.WrapUpDuration.TotalSeconds}s"))}]");

        using var scope = new AssertionScope();
        scope.AddReportable("replay", with.Describe());
        completeQueues.Should().Equal(["q-loop"], "premise: the looped caller is answered once, in q-loop");
        provider.Asks.Should().Equal([(ServerId, "q-loop")], "the provider is asked once, for the queue the AgentComplete names");
        wrapUpsWithout.Should().ContainSingle("with no provider, one answered queue leg is one wrap-up, as before")
            .Which.WrapUpDuration.Should().Be(new SessionOptions().WrapUpDuration);
        var wrapUp = wrapUpsWith.Should().ContainSingle("one answered queue leg is one wrap-up").Subject;
        wrapUp.QueueName.Should().Be("q-loop");
        wrapUp.ServerId.Should().Be(ServerId);
        wrapUp.WrapUpDuration.Should().Be(Durations["q-loop"], "the wrap-up runs its queue's duration");
        with.ObserverExceptions.Should().BeEmpty();
        without.ObserverExceptions.Should().BeEmpty();
    }

    /// <summary>The <c>Queue</c> header of every <c>AgentComplete</c> in the capture, in capture order.</summary>
    private static async Task<List<string>> AgentCompleteQueuesAsync(string fixture) =>
    [
        .. (await AmiCaptureReplay.ReadCaptureAsync(fixture))
            .Where(e => string.Equals(e.EventType, "AgentComplete", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.RawFields?.GetValueOrDefault("Queue") ?? ""),
    ];

    /// <summary>Answers <see cref="Durations"/> and <see langword="null"/> for any other queue, recording every ask.</summary>
    private sealed class PerQueueProvider : IWrapUpDurationProvider
    {
        private readonly Lock _gate = new();
        private readonly List<(string ServerId, string QueueName)> _asks = [];

        public IReadOnlyList<(string ServerId, string QueueName)> Asks
        {
            get
            {
                lock (_gate)
                    return [.. _asks];
            }
        }

        public TimeSpan? GetWrapUpDuration(string serverId, string queueName)
        {
            lock (_gate)
                _asks.Add((serverId, queueName));
            return Durations.TryGetValue(queueName, out var duration) ? duration : null;
        }
    }
}
