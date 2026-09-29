using FluentAssertions;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tests.Internal;

/// <summary>
/// The manual clock's <see cref="FakeTimeProvider.TimersArmed"/> signal, on the primitive the
/// end-of-input bound is built from: a <see cref="CancellationTokenSource"/> made on this clock with no
/// delay, then armed, paused and re-armed through <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>.
/// </summary>
/// <remarks>
/// Nothing here waits: the clock moves only when a test calls <see cref="FakeTimeProvider.Advance"/>, and
/// the timer callbacks run on that call.
/// </remarks>
public sealed class FakeTimeProviderTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Fact]
    public void TimersArmed_ShouldPublishOneDueTimeOfTenSeconds_WhenATokenSourceIsArmedWithCancelAfter()
    {
        // Arrange — the source makes its timer now, with an infinite due time: not an arm
        var clock = new FakeTimeProvider();
        using var source = new CancellationTokenSource(Timeout.InfiniteTimeSpan, clock);
        clock.TimersArmed.TryRead(out _).Should().BeFalse("creating the source arms nothing");

        // Act
        source.CancelAfter(Limit);

        // Assert
        clock.TimersArmed.TryRead(out var due).Should().BeTrue();
        due.Should().Be(Limit);
        clock.TimersArmed.TryRead(out _).Should().BeFalse("one arm is one signal");
    }

    [Fact]
    public void Advance_ShouldCancelAnArmedTokenSource_WhenTheClockReachesItsDueTime()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        using var source = new CancellationTokenSource(Timeout.InfiniteTimeSpan, clock);
        source.CancelAfter(Limit);

        // Act / Assert
        clock.Advance(TimeSpan.FromMilliseconds(9_900));
        source.IsCancellationRequested.Should().BeFalse("9.9 s is short of the 10 s it was armed with");

        clock.Advance(TimeSpan.FromMilliseconds(100));
        source.IsCancellationRequested.Should().BeTrue("the clock has reached the due time");
    }

    [Fact]
    public void TimersArmed_ShouldPublishNothingAndTheSourceShouldHold_WhenAnArmedSourceIsSetToAnInfiniteDelay()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        using var source = new CancellationTokenSource(Timeout.InfiniteTimeSpan, clock);
        source.CancelAfter(Limit);
        clock.TimersArmed.TryRead(out _).Should().BeTrue();

        // Act — a pause
        source.CancelAfter(Timeout.InfiniteTimeSpan);
        clock.Advance(TimeSpan.FromSeconds(15));

        // Assert
        source.IsCancellationRequested.Should().BeFalse("an infinite delay holds the source");
        clock.TimersArmed.TryRead(out _).Should().BeFalse("an infinite delay is not an arm");
    }
}
