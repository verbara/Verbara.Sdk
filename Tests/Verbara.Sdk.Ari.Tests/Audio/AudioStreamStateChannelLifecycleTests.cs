using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.Ari.Tests.TestSupport;

namespace Verbara.Sdk.Ari.Tests.Audio;

/// <summary>
/// The lifecycle counts of <see cref="AudioStreamStateChannel"/> under concurrency: two parties released by
/// one gate on the same channel, many times over, and the <c>audio.streams.opened</c> /
/// <c>audio.streams.closed</c> measurements each channel produced.
/// </summary>
/// <remarks>
/// Each channel is opened with its own tag value, an <see cref="Iteration"/>, so every measurement is
/// attributed to the iteration that made it. A closed count recorded without an opening carries no such tag,
/// so those tests count every <c>audio.streams.closed</c> measurement instead; the class runs in
/// <see cref="AudioStreamMetricsGroup"/>, alone, so nothing else records meanwhile.
/// </remarks>
[Collection(AudioStreamMetricsGroup.Name)]
public sealed class AudioStreamStateChannelLifecycleTests
{
    private const int Iterations = 20_000;
    private static readonly TimeSpan RaceTimeout = TimeSpan.FromMinutes(1);

    [Fact]
    public void TryEnd_ShouldCountClosedExactlyOnce_WhenTwoThreadsEndAnOpenedStream()
    {
        // Arrange
        using var counts = new LifecycleCounts(Iterations);
        var channels = Channels(open: true);
        var wins = new int[Iterations];

        // Act
        var failures = Race(
            i => { if (channels[i].TryEnd(error: false)) Interlocked.Increment(ref wins[i]); },
            i => { if (channels[i].TryEnd(error: false)) Interlocked.Increment(ref wins[i]); });
        DisposeAll(channels);

        // Assert
        using (new AssertionScope())
        {
            failures.Should().BeEmpty("neither party may throw");
            Histogram(counts.Opened).Should().Equal(new Dictionary<int, int> { [1] = Iterations }, "each stream was opened once");
            Histogram(counts.Closed).Should().Equal(new Dictionary<int, int> { [1] = Iterations }, "two parties ending one opened stream close it once");
            Histogram(wins).Should().Equal(new Dictionary<int, int> { [1] = Iterations }, "exactly one TryEnd publishes the ending");
        }
    }

    [Fact]
    public void TryEnd_ShouldCountNoClosed_WhenTwoThreadsEndAStreamThatNeverOpened()
    {
        // Arrange
        using var counts = new LifecycleCounts(Iterations);
        var channels = Channels(open: false);

        // Act
        var failures = Race(i => channels[i].TryEnd(error: false), i => channels[i].TryEnd(error: false));
        DisposeAll(channels);

        // Assert
        using (new AssertionScope())
        {
            failures.Should().BeEmpty("neither party may throw");
            counts.AllOpened.Should().Be(0, "no stream was opened");
            counts.AllClosed.Should().Be(0, "a stream that never opened is never closed, however many parties end it");
        }
    }

    [Fact]
    public void RecordOpened_ShouldCountOpenedOnce_WhenTwoThreadsRecordIt()
    {
        // Arrange
        using var counts = new LifecycleCounts(Iterations);
        var channels = Channels(open: false);

        // Act
        var failures = Race(i => channels[i].RecordOpened(Tag(i)), i => channels[i].RecordOpened(Tag(i)));
        foreach (var channel in channels)
            channel.TryEnd(error: false);
        DisposeAll(channels);

        // Assert
        using (new AssertionScope())
        {
            failures.Should().BeEmpty("neither party may throw");
            Histogram(counts.Opened).Should().Equal(new Dictionary<int, int> { [1] = Iterations }, "two openings of one stream count it opened once");
            Histogram(counts.Closed).Should().Equal(new Dictionary<int, int> { [1] = Iterations }, "and its one ending closes it once");
        }
    }

    [Fact]
    public void RecordOpenedAndTryEnd_ShouldKeepOpenedEqualToClosed_WhenTheyRace()
    {
        // Arrange
        using var counts = new LifecycleCounts(Iterations);
        var channels = Channels(open: false);

        // Act — one party opens the stream while the other ends it
        var failures = Race(i => channels[i].RecordOpened(Tag(i)), i => channels[i].TryEnd(error: false));
        DisposeAll(channels);

        // Assert — an opening that won is closed by the ending; an ending that won leaves nothing to open
        var unbalanced = Enumerable.Range(0, Iterations).Where(i => counts.Opened[i] != counts.Closed[i]).ToList();
        using (new AssertionScope())
        {
            failures.Should().BeEmpty("neither party may throw");
            unbalanced.Should().BeEmpty(
                "every stream counts opened and closed together or neither; {0} of {1} did not (first: opened={2} closed={3})",
                unbalanced.Count, Iterations,
                unbalanced.Count > 0 ? counts.Opened[unbalanced[0]] : 0,
                unbalanced.Count > 0 ? counts.Closed[unbalanced[0]] : 0);
            counts.AllClosed.Should().Be(counts.AllOpened, "no closed count is ever recorded without its opening");
        }
    }

    /// <summary>The tag value one iteration opens its channel with.</summary>
    private sealed record Iteration(int Index);

    private static KeyValuePair<string, object?> Tag(int index) => new("audio.transport", new Iteration(index));

    private static AudioStreamStateChannel[] Channels(bool open)
    {
        var channels = new AudioStreamStateChannel[Iterations];
        for (var i = 0; i < Iterations; i++)
        {
            channels[i] = new AudioStreamStateChannel(AudioStreamState.Connected, NullLogger.Instance, static () => "lifecycle-test");
            if (open)
                channels[i].RecordOpened(Tag(i));
        }

        return channels;
    }

    private static void DisposeAll(AudioStreamStateChannel[] channels)
    {
        foreach (var channel in channels)
            channel.Dispose();
    }

    /// <summary>
    /// Runs <paramref name="first"/> and <paramref name="second"/> on two threads for every iteration, both
    /// released by one barrier per iteration so they start at the same moment. Returns what either threw.
    /// </summary>
    private static ConcurrentQueue<Exception> Race(Action<int> first, Action<int> second)
    {
        var failures = new ConcurrentQueue<Exception>();
        using var barrier = new Barrier(2);
        void Loop(Action<int> act)
        {
            try
            {
                for (var i = 0; i < Iterations; i++)
                {
                    barrier.SignalAndWait(RaceTimeout);
                    act(i);
                }
            }
            catch (Exception ex)
            {
                failures.Enqueue(ex);
                barrier.RemoveParticipant();
            }
        }

        var threads = new[] { new Thread(() => Loop(first)), new Thread(() => Loop(second)) };
        foreach (var thread in threads)
            thread.Start();
        foreach (var thread in threads)
        {
            if (!thread.Join(RaceTimeout))
                failures.Enqueue(new TimeoutException("A racing thread did not finish."));
        }

        return failures;
    }

    private static Dictionary<int, int> Histogram(int[] perIteration) =>
        perIteration.GroupBy(n => n).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>
    /// Counts the <c>audio.streams.opened</c> and <c>audio.streams.closed</c> measurements of the
    /// <c>Verbara.Sdk.Ari.Audio</c> meter: per iteration by the <see cref="Iteration"/> tag, and in total.
    /// </summary>
    private sealed class LifecycleCounts : IDisposable
    {
        private readonly MeterListener _listener = new();
        private int _allOpened;
        private int _allClosed;

        public LifecycleCounts(int iterations)
        {
            Opened = new int[iterations];
            Closed = new int[iterations];
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Verbara.Sdk.Ari.Audio"
                    && instrument.Name is "audio.streams.opened" or "audio.streams.closed")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                var opened = instrument.Name == "audio.streams.opened";
                Interlocked.Add(ref opened ? ref _allOpened : ref _allClosed, (int)value);
                foreach (var tag in tags)
                {
                    if (tag.Value is Iteration iteration)
                        Interlocked.Add(ref (opened ? Opened : Closed)[iteration.Index], (int)value);
                }
            });
            _listener.Start();
        }

        public int[] Opened { get; }

        public int[] Closed { get; }

        public int AllOpened => Volatile.Read(ref _allOpened);

        public int AllClosed => Volatile.Read(ref _allClosed);

        public void Dispose() => _listener.Dispose();
    }
}
