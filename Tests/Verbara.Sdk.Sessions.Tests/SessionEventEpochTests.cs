using Verbara.Sdk.Sessions;
using FluentAssertions;
using FluentAssertions.Execution;

namespace Verbara.Sdk.Sessions.Tests;

/// <summary>
/// The public loss mark a session notification carries: <see cref="SessionDomainEvent.Epoch"/>, settable by a consumer
/// that builds a notification and part of the notification's value; and <see cref="SessionEventEpoch.LossBetween"/>,
/// which compares two marks by equality and answers <see cref="SessionEventLoss.Unknown"/> when either is absent, so two
/// absent marks never read as complete.
/// </summary>
public sealed class SessionEventEpochTests
{
    private static readonly DateTimeOffset Ts = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private static readonly SessionEventEpoch Mark = new(new Guid("6f1c2d4e-0000-4000-8000-000000000001"), 2, 3, 1);

    [Fact]
    public void Epoch_ShouldBeSettableByInit_WhenAConsumerBuildsANotification()
    {
        var withMark = new CallHeldEvent("s1", "srv1", Ts) { Epoch = Mark };
        var without = new CallHeldEvent("s1", "srv1", Ts);

        using (new AssertionScope())
        {
            withMark.Epoch.Should().Be(Mark, "a consumer builds a notification with a mark in its own tests");
            without.Epoch.Should().BeNull("a notification nobody stamped carries no mark, never a default that reads complete");
        }
    }

    [Fact]
    public void Equals_ShouldIncludeEpoch_WhenTwoNotificationsDiffer()
    {
        var first = new CallEndedEvent("s1", "srv1", Ts, null, TimeSpan.FromSeconds(5), null) { Epoch = Mark };
        var sameMark = new CallEndedEvent("s1", "srv1", Ts, null, TimeSpan.FromSeconds(5), null) { Epoch = Mark };
        var otherMark = first with { Epoch = Mark with { DroppedBefore = Mark.DroppedBefore + 1 } };
        var noMark = first with { Epoch = null };

        using (new AssertionScope())
        {
            first.Should().Be(sameMark, "premise: equal content and equal marks");
            first.Should().NotBe(otherMark, "the mark is part of the notification's value");
            first.Should().NotBe(noMark, "a notification with a mark is not equal to one without");
            first.GetHashCode().Should().Be(sameMark.GetHashCode());
            first.ToString().Should().Contain("Epoch = ", "the printed value includes the mark");
        }
    }

    [Fact]
    public void LossBetween_ShouldBeUnknown_WhenEitherEpochIsNull()
    {
        using (new AssertionScope())
        {
            SessionEventEpoch.LossBetween(null, null).Should().Be(SessionEventLoss.Unknown,
                "two absent marks are equal under lifted equality, and must not read as complete");
            SessionEventEpoch.LossBetween(Mark, null).Should().Be(SessionEventLoss.Unknown);
            SessionEventEpoch.LossBetween(null, Mark).Should().Be(SessionEventLoss.Unknown);
            default(SessionEventLoss).Should().Be(SessionEventLoss.Unknown,
                "a verdict nobody computed reads as cannot-tell, never as complete");
        }
    }

    [Fact]
    public void LossBetween_ShouldBeNoneKnown_WhenEqual()
    {
        var copy = new SessionEventEpoch(Mark.ConnectionId, Mark.AmiSession, Mark.DroppedBefore, Mark.Attachment);

        SessionEventEpoch.LossBetween(Mark, copy).Should().Be(SessionEventLoss.NoneKnown);
    }

    [Theory]
    [InlineData("ConnectionId")]
    [InlineData("AmiSession")]
    [InlineData("DroppedBefore")]
    [InlineData("Attachment")]
    public void LossBetween_ShouldBePossible_WhenAnyFieldDiffers(string field)
    {
        var later = field switch
        {
            "ConnectionId" => Mark with { ConnectionId = new Guid("6f1c2d4e-0000-4000-8000-000000000002") },
            "AmiSession" => Mark with { AmiSession = Mark.AmiSession + 1 },
            "DroppedBefore" => Mark with { DroppedBefore = Mark.DroppedBefore + 1 },
            "Attachment" => Mark with { Attachment = Mark.Attachment + 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        using (new AssertionScope())
        {
            SessionEventEpoch.LossBetween(Mark, later).Should().Be(SessionEventLoss.Possible, $"{field} differs");
            SessionEventEpoch.LossBetween(later, Mark).Should().Be(SessionEventLoss.Possible,
                "the comparison is equality, not an order");
        }
    }
}
