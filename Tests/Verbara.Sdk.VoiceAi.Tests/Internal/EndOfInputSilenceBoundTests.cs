using System.Globalization;
using FluentAssertions;
using Verbara.Sdk.VoiceAi.Internal;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tests.Internal;

/// <summary>
/// Every member of <see cref="EndOfInputSilenceBound"/>, the bound a streaming client puts on its wait
/// for the vendor once it has sent its end of input: when it starts, what restarts it, what holds it,
/// how it tells its own expiry from the caller's cancellation, and the failure it is reported as.
/// </summary>
/// <remarks>
/// Each test builds the bound on the manual clock, so nothing here waits: the limit passes only when a
/// test calls <see cref="FakeTimeProvider.Advance"/>, and the token is cancelled on that call. The
/// clock's <see cref="FakeTimeProvider.TimersArmed"/> says when the bound actually started counting.
/// </remarks>
public sealed class EndOfInputSilenceBoundTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OneMillisecond = TimeSpan.FromMilliseconds(1);

    [Fact]
    public void Token_ShouldNotBeCancelled_WhenTheBoundIsNeverArmed()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        using var bound = new EndOfInputSilenceBound(Limit, clock, CancellationToken.None);

        // Act
        clock.Advance(TimeSpan.FromHours(1));

        // Assert
        bound.Token.IsCancellationRequested.Should().BeFalse("the bound counts only once the end of input is out");
        bound.Expired.Should().BeFalse();
        clock.TimersArmed.TryRead(out _).Should().BeFalse("nothing armed the bound");
    }

    [Fact]
    public void Arm_ShouldCancelTheToken_WhenTheLimitPasses()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        using var bound = new EndOfInputSilenceBound(Limit, clock, CancellationToken.None);

        // Act
        bound.Arm();

        // Assert
        clock.TimersArmed.TryRead(out var due).Should().BeTrue("arming starts the count");
        due.Should().Be(Limit);

        clock.Advance(Limit - OneMillisecond);
        bound.Token.IsCancellationRequested.Should().BeFalse("the limit has not passed yet");
        bound.Expired.Should().BeFalse();

        clock.Advance(OneMillisecond);
        bound.Token.IsCancellationRequested.Should().BeTrue("the limit has passed");
        bound.Expired.Should().BeTrue("this bound, not the caller, ended the wait");
    }

    [Fact]
    public void Heard_ShouldRestartTheLimit_WhenArmed()
    {
        // Arrange — armed, and six of its ten seconds gone
        var clock = new FakeTimeProvider();
        using var bound = new EndOfInputSilenceBound(Limit, clock, CancellationToken.None);
        bound.Arm();
        clock.Advance(TimeSpan.FromSeconds(6));

        // Act
        bound.Heard();

        // Assert — the full limit again from the frame, not the four seconds that were left
        clock.Advance(Limit - OneMillisecond);
        bound.Token.IsCancellationRequested.Should().BeFalse("a frame from the vendor restarts the full limit");

        clock.Advance(OneMillisecond);
        bound.Token.IsCancellationRequested.Should().BeTrue("the restarted limit has passed");
        bound.Expired.Should().BeTrue();
    }

    [Fact]
    public void Heard_ShouldDoNothing_WhenNotArmed()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        using var bound = new EndOfInputSilenceBound(Limit, clock, CancellationToken.None);

        // Act — a frame while the client is still sending
        bound.Heard();
        clock.Advance(TimeSpan.FromHours(1));

        // Assert
        clock.TimersArmed.TryRead(out _).Should().BeFalse("a frame before the end of input starts nothing");
        bound.Token.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public void Pause_ShouldHoldAnArmedBound_UntilResume()
    {
        // Arrange — armed, and four of its ten seconds gone
        var clock = new FakeTimeProvider();
        using var bound = new EndOfInputSilenceBound(Limit, clock, CancellationToken.None);
        bound.Arm();
        clock.Advance(TimeSpan.FromSeconds(4));

        // Act
        bound.Pause();
        clock.Advance(Limit * 2);

        // Assert
        bound.Token.IsCancellationRequested.Should().BeFalse("a paused bound does not count, however long the pause");
        bound.Expired.Should().BeFalse();

        bound.Resume();
        clock.Advance(Limit);
        bound.Token.IsCancellationRequested.Should().BeTrue("the bound counts again once resumed");
    }

    [Fact]
    public void Resume_ShouldRestartTheFullLimit_WhenArmed()
    {
        // Arrange — armed, six of its ten seconds gone, then paused
        var clock = new FakeTimeProvider();
        using var bound = new EndOfInputSilenceBound(Limit, clock, CancellationToken.None);
        bound.Arm();
        clock.Advance(TimeSpan.FromSeconds(6));
        bound.Pause();

        // Act
        bound.Resume();

        // Assert — the full limit from the resume, not the four seconds left before the pause
        clock.TimersArmed.TryRead(out _).Should().BeTrue("the arm");
        clock.TimersArmed.TryRead(out var due).Should().BeTrue("the resume arms the bound again");
        due.Should().Be(Limit);

        clock.Advance(Limit - OneMillisecond);
        bound.Token.IsCancellationRequested.Should().BeFalse("the resume restarts the full limit");

        clock.Advance(OneMillisecond);
        bound.Token.IsCancellationRequested.Should().BeTrue("the restarted limit has passed");
        bound.Expired.Should().BeTrue();
    }

    [Fact]
    public void Arm_ShouldNotStartTheBound_WhenPaused()
    {
        // Arrange — paused before the end of input is out
        var clock = new FakeTimeProvider();
        using var bound = new EndOfInputSilenceBound(Limit, clock, CancellationToken.None);
        bound.Pause();

        // Act
        bound.Arm();
        clock.Advance(Limit * 2);

        // Assert
        clock.TimersArmed.TryRead(out _).Should().BeFalse("an arm while paused waits for the resume");
        bound.Token.IsCancellationRequested.Should().BeFalse();

        bound.Resume();
        clock.TimersArmed.TryRead(out var due).Should().BeTrue("the resume starts the armed bound");
        due.Should().Be(Limit);
        clock.Advance(Limit);
        bound.Token.IsCancellationRequested.Should().BeTrue("the full limit has passed since the resume");
        bound.Expired.Should().BeTrue();
    }

    [Fact]
    public void Expired_ShouldBeFalse_WhenTheOuterTokenWasCancelled()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        using var outer = new CancellationTokenSource();
        using var bound = new EndOfInputSilenceBound(Limit, clock, outer.Token);
        bound.Arm();

        // Act — the caller cancels while the bound is counting
        outer.Cancel();

        // Assert
        bound.Token.IsCancellationRequested.Should().BeTrue("the outer token still ends the receive");
        bound.Expired.Should().BeFalse("the caller ended the wait, not the bound");
    }

    [Fact]
    public void Arm_ShouldNotThrow_WhenTheBoundWasDisposed()
    {
        // Arrange — the session ended and released its bound
        var clock = new FakeTimeProvider();
        var bound = new EndOfInputSilenceBound(Limit, clock, CancellationToken.None);
        bound.Dispose();

        // Act
        var arm = bound.Invoking(b => b.Arm());

        // Assert
        arm.Should().NotThrow("there is nothing left to wait for");
    }

    [Fact]
    public void Dispose_ShouldUnlinkTheOuterToken_WhenTheOuterIsCancelledAfterwards()
    {
        // Arrange — a bound released while its caller's token lives on
        var clock = new FakeTimeProvider();
        using var outer = new CancellationTokenSource();
        var bound = new EndOfInputSilenceBound(Limit, clock, outer.Token);
        bound.Dispose();

        // Act
        var cancel = outer.Invoking(o => o.Cancel());

        // Assert
        cancel.Should().NotThrow("disposing the bound also removes its registration on the outer token");
    }

    [Fact]
    public void ToFailure_ShouldReportATransportFailureWithATimeout_WhenTheVendorStayedSilent()
    {
        // Arrange — a limit with a fraction, reported while the thread's culture writes a decimal comma
        using var bound = new EndOfInputSilenceBound(
            TimeSpan.FromMilliseconds(2_500), new FakeTimeProvider(), CancellationToken.None);
        var commaCulture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        commaCulture.NumberFormat.NumberDecimalSeparator = ",";
        var previous = CultureInfo.CurrentCulture;
        SpeechProviderFailureException failure;

        // Act
        try
        {
            CultureInfo.CurrentCulture = commaCulture;
            failure = bound.ToFailure("VendorUnderTest");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        // Assert
        failure.Signal.Should().Be(SpeechProviderFailureSignal.Transport, "a vendor that stopped answering left the result incomplete");
        failure.Provider.Should().Be("VendorUnderTest");
        failure.Code.Should().BeNull("the vendor gave no code");
        var timeout = failure.InnerException.Should().BeOfType<TimeoutException>().Subject;
        timeout.Message.Should().Contain("VendorUnderTest");
        timeout.Message.Should().Contain("2.5 s", "the limit is written in the invariant culture, whatever the thread's culture");
    }

    [Fact]
    public void Default_ShouldBeTenSeconds_WhenAClientTakesTheSharedLimit()
    {
        EndOfInputSilenceBound.Default.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Limit_ShouldBeTheConstructedLimit_WhenBuiltWithOrWithoutAClock()
    {
        // Arrange / Act — the system clock's constructor and the manual clock's
        using var onTheSystemClock = new EndOfInputSilenceBound(TimeSpan.FromSeconds(3), CancellationToken.None);
        using var onAManualClock = new EndOfInputSilenceBound(TimeSpan.FromSeconds(7), new FakeTimeProvider(), CancellationToken.None);

        // Assert
        onTheSystemClock.Limit.Should().Be(TimeSpan.FromSeconds(3));
        onAManualClock.Limit.Should().Be(TimeSpan.FromSeconds(7));
    }
}
