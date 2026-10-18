using FluentAssertions;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.Tests.Manager;

/// <summary>
/// The bounds of <see cref="SessionOptions.WrapUpDuration"/>, as the host's start-up validation sees them: the
/// generated <see cref="SessionOptionsValidator"/>, called exactly as <c>ValidateOnStart</c> calls it. A wrap-up
/// is timed by a one-shot timer of the manager's clock, so the duration may be neither negative (there is no
/// "never ends", and <see cref="Timeout.InfiniteTimeSpan"/> is negative) nor longer than the system timer accepts.
/// </summary>
public sealed class SessionOptionsWrapUpValidationTests
{
    /// <summary>The largest due time <see cref="TimeProvider.System"/>'s timer accepts, in milliseconds.</summary>
    private const long LargestDueTimeMs = 4_294_967_294;

    public static TheoryData<string, TimeSpan> Rejected => new()
    {
        { "minus one second", TimeSpan.FromSeconds(-1) },
        { "Timeout.InfiniteTimeSpan", Timeout.InfiniteTimeSpan },
        { "fifty days", TimeSpan.FromDays(50) },
    };

    public static TheoryData<string, TimeSpan> Accepted => new()
    {
        { "zero", TimeSpan.Zero },
        { "the largest due time the system timer accepts", TimeSpan.FromMilliseconds(LargestDueTimeMs) },
    };

    [Theory]
    [MemberData(nameof(Rejected))]
    public void Validate_ShouldFailNamingWrapUpDuration_WhenTheDurationIsOutOfBounds(string label, TimeSpan duration)
    {
        var result = new SessionOptionsValidator().Validate(null, new SessionOptions { WrapUpDuration = duration });

        new { result.Failed, NamesTheMember = result.FailureMessage?.Contains(nameof(SessionOptions.WrapUpDuration),
                StringComparison.Ordinal) == true }
            .Should().BeEquivalentTo(
                new { Failed = true, NamesTheMember = true },
                $"a wrap-up duration of {label} ({duration}) cannot time a wrap-up, so start-up validation fails and "
                + $"names {nameof(SessionOptions.WrapUpDuration)}. Measured: failed={result.Failed}, "
                + $"message={result.FailureMessage ?? "null"}");
    }

    [Theory]
    [MemberData(nameof(Accepted))]
    public void Validate_ShouldPass_WhenTheDurationIsWithinBounds(string label, TimeSpan duration)
    {
        var result = new SessionOptionsValidator().Validate(null, new SessionOptions { WrapUpDuration = duration });

        result.Failed.Should().BeFalse(
            $"Guard: a wrap-up duration of {label} ({duration}) is valid. Measured: {result.FailureMessage ?? "null"}");
    }

    [Fact]
    public void SystemTimer_ShouldAcceptTheLargestDueTimeAndRejectTheNext_WhenCreatedWithThem()
    {
        Exception? atLargest = null;
        try
        {
            using var timer = TimeProvider.System.CreateTimer(
                static _ => { }, null, TimeSpan.FromMilliseconds(LargestDueTimeMs), Timeout.InfiniteTimeSpan);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            atLargest = ex;
        }

        var aboveLargest = Record.Exception(() =>
        {
            using var timer = TimeProvider.System.CreateTimer(
                static _ => { }, null, TimeSpan.FromMilliseconds(LargestDueTimeMs + 1), Timeout.InfiniteTimeSpan);
        });

        new { AtLargest = atLargest?.GetType().Name, AboveLargest = aboveLargest?.GetType().Name }
            .Should().BeEquivalentTo(
                new { AtLargest = default(string), AboveLargest = nameof(ArgumentOutOfRangeException) },
                $"Guard: the upper bound of the duration is measured on the system timer, not remembered: it accepts "
                + $"{LargestDueTimeMs} ms as a due time and rejects one millisecond more");
    }
}
