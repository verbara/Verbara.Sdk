using Asterisk.Sdk.Sessions;
using FluentAssertions;

namespace Asterisk.Sdk.Sessions.Tests;

public sealed class QueueSessionTests
{
    [Fact]
    public void ServiceLevel_ShouldReturn100_WhenNoCallsOffered()
    {
        var sut = new QueueSession("test-queue");

        sut.ServiceLevel.Should().Be(100.0);
    }

    [Fact]
    public void ServiceLevel_ShouldCalculateCorrectly_WhenCallsOffered()
    {
        var sut = new QueueSession("test-queue");
        sut.CallsOffered = 10;
        sut.CallsWithinSla = 8;

        sut.ServiceLevel.Should().Be(80.0);
    }

    [Fact]
    public void AvgWaitTime_ShouldReturnZero_WhenNoCallsAnswered()
    {
        var sut = new QueueSession("test-queue");

        sut.AvgWaitTime.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void AvgWaitTime_ShouldCalculateCorrectly_WhenCallsAnswered()
    {
        var sut = new QueueSession("test-queue");
        sut.CallsAnswered = 4;
        sut.TotalWaitTime = TimeSpan.FromSeconds(40);

        sut.AvgWaitTime.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void AbandonRate_ShouldCalculateCorrectly()
    {
        var sut = new QueueSession("test-queue");
        sut.CallsOffered = 20;
        sut.CallsAbandoned = 5;

        sut.AbandonRate.Should().Be(25.0);
    }

    [Fact]
    public void AbandonRate_ShouldReturnZero_WhenNoCallsOffered()
    {
        var sut = new QueueSession("test-queue");

        sut.AbandonRate.Should().Be(0.0);
    }

    [Fact]
    public void AnswerRate_ShouldCalculateCorrectly()
    {
        var sut = new QueueSession("test-queue");
        sut.CallsOffered = 10;
        sut.CallsAnswered = 7;

        sut.AnswerRate.Should().Be(70.0);
    }

    [Fact]
    public void AnswerRate_ShouldReturnZero_WhenNoCallsOffered()
    {
        var sut = new QueueSession("test-queue");

        sut.AnswerRate.Should().Be(0.0);
    }

    [Fact]
    public void ResetWindow_ShouldClearCounters_ButKeepCallsWaiting()
    {
        var sut = new QueueSession("test-queue");
        sut.CallsOffered = 10;
        sut.CallsAnswered = 8;
        sut.CallsAbandoned = 2;
        sut.CallsTimedOut = 1;
        sut.TotalWaitTime = TimeSpan.FromSeconds(100);
        sut.MaxWaitTime = TimeSpan.FromSeconds(30);
        sut.MinWaitTime = TimeSpan.FromSeconds(5);
        sut.CallsWithinSla = 6;
        sut.CallsWaiting = 3;

        sut.ResetWindow();

        sut.CallsOffered.Should().Be(0);
        sut.CallsAnswered.Should().Be(0);
        sut.CallsAbandoned.Should().Be(0);
        sut.CallsTimedOut.Should().Be(0);
        sut.TotalWaitTime.Should().Be(TimeSpan.Zero);
        sut.MaxWaitTime.Should().Be(TimeSpan.Zero);
        sut.MinWaitTime.Should().Be(TimeSpan.MaxValue);
        sut.CallsWithinSla.Should().Be(0);
        sut.CallsWaiting.Should().Be(3, "CallsWaiting is live state and must not be reset");
    }
}
