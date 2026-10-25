using Verbara.Sdk;
using Verbara.Sdk.Ami.Internal;
using FluentAssertions;
using FluentAssertions.Execution;

namespace Verbara.Sdk.Ami.Tests.Internal;

/// <summary>
/// A full buffer reports an overflow as an episode: every refusal says whether it opens one, and the episode is reported
/// once, with its count and its duration on the pump's clock, when the buffer next accepts an event or when the pump is
/// released while it is still open.
/// </summary>
public partial class AsyncEventPumpTests
{
    [Fact]
    public async Task TryEnqueue_ShouldReportTheEpisodeOnceWithItsCountAndDuration_WhenTheBufferAcceptsAgain()
    {
        var clock = new SteppedClock();
        var pump = new AsyncEventPump(capacity: 1) { Clock = clock };
        await using var _ = pump;
        var refusals = new List<(string Type, bool Opens)>();
        var episodes = new List<(long Count, TimeSpan Duration)>();
        var dropped = 0;
        pump.OnEventDropped = _ => dropped++;
        pump.OnEventRefused = (evt, opens) => refusals.Add((evt.EventType!, opens));
        pump.OnDropEpisodeEnded = (count, duration) => episodes.Add((count, duration));
        var firstDispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        pump.TryEnqueue(CreateEvent("A")).Should().BeTrue();
        pump.TryEnqueue(CreateEvent("B")).Should().BeFalse("premise: the full buffer refuses B");
        clock.Advance(TimeSpan.FromMilliseconds(250));
        pump.TryEnqueue(CreateEvent("C")).Should().BeFalse("premise: the full buffer refuses C");
        pump.TryEnqueue(CreateEvent("D")).Should().BeFalse("premise: the full buffer refuses D");
        episodes.Should().BeEmpty("the episode is open until the buffer accepts an event");

        pump.Start(evt =>
        {
            if (evt.EventType == "A")
                firstDispatched.TrySetResult();
            return ValueTask.CompletedTask;
        });
        // A's handler runs only once A has been read, so the buffer of one is empty from then on.
        await firstDispatched.Task.WaitAsync(Bound);
        pump.TryEnqueue(CreateEvent("E")).Should().BeTrue();

        using (new AssertionScope())
        {
            dropped.Should().Be(3, "every refused event still reaches OnEventDropped");
            refusals.Should().Equal([("B", true), ("C", false), ("D", false)],
                "only the first refusal after an accepted event opens an episode");
            episodes.Should().Equal([(3L, TimeSpan.FromMilliseconds(250))],
                "the accepted event ends the episode, reported once with its count and its duration on the pump's clock");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Release_ShouldReportTheOpenEpisodeOnce_WhenThePumpIsReleasedDuringIt(bool drain)
    {
        var clock = new SteppedClock();
        var pump = new AsyncEventPump(capacity: 1) { Clock = clock };
        var episodes = new List<(long Count, TimeSpan Duration)>();
        pump.OnDropEpisodeEnded = (count, duration) => episodes.Add((count, duration));

        pump.TryEnqueue(CreateEvent("A")).Should().BeTrue();
        pump.TryEnqueue(CreateEvent("B")).Should().BeFalse("premise: the full buffer refuses B");
        pump.TryEnqueue(CreateEvent("C")).Should().BeFalse("premise: the full buffer refuses C");
        clock.Advance(TimeSpan.FromSeconds(2));

        if (drain)
            await pump.DrainAndDisposeAsync();
        else
            await pump.DisposeAsync();

        episodes.Should().Equal([(2L, TimeSpan.FromSeconds(2))],
            "the episode still open at the release is reported there, once, with its count");
    }

    [Fact]
    public async Task Release_ShouldReportNoEpisode_WhenTheLastEpisodeEndedBeforeTheRelease()
    {
        var pump = new AsyncEventPump(capacity: 1);
        var episodes = 0;
        pump.OnDropEpisodeEnded = (_, _) => episodes++;
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        pump.TryEnqueue(CreateEvent("A")).Should().BeTrue();
        pump.TryEnqueue(CreateEvent("B")).Should().BeFalse("premise: the full buffer refuses B");
        pump.Start(_ =>
        {
            dispatched.TrySetResult();
            return ValueTask.CompletedTask;
        });
        await dispatched.Task.WaitAsync(Bound);
        pump.TryEnqueue(CreateEvent("C")).Should().BeTrue("premise: the buffer accepts again, which ends the episode");
        await pump.DrainAndDisposeAsync();

        episodes.Should().Be(1, "the episode was reported when the buffer accepted C, and the release reports nothing more");
    }

    /// <summary>A clock whose timestamp moves only on <see cref="Advance"/>.</summary>
    private sealed class SteppedClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan delta) => Interlocked.Add(ref _ticks, delta.Ticks);
    }
}
