using System.Threading.Channels;
using Verbara.Sdk.TestInfrastructure.Channels;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.Helpers;

/// <summary>
/// The <see cref="HeldReadWindow{T}"/> the lost-cancel tests rely on: it holds exactly one wait, the
/// first one made after the configured number of items was read; it checks the token before holding and
/// not after; and the held wait reports what the real reader reports once released.
/// </summary>
public sealed class HeldReadWindowTests
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task WaitToReadAsync_ShouldHoldExactlyOneCall_WhenOneItemWasReadBeforeIt()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<int>();
        channel.Writer.TryWrite(1);
        channel.Writer.TryWrite(2);
        var window = new HeldReadWindow<int>(itemsBeforeTheHold: 1);
        var reader = window.Wrap(channel.Reader);

        // Act
        var first = await reader.WaitToReadAsync().AsTask().WaitAsync(SignalTimeout);
        reader.TryRead(out var item).Should().BeTrue();
        var held = reader.WaitToReadAsync().AsTask();
        await window.WindowEntered.WaitAsync(SignalTimeout);
        var completedBeforeRelease = held.IsCompleted;
        window.Release();
        var second = await held.WaitAsync(SignalTimeout);
        reader.TryRead(out var next).Should().BeTrue();
        channel.Writer.TryComplete();
        var third = await reader.WaitToReadAsync().AsTask().WaitAsync(SignalTimeout);

        // Assert
        using (new AssertionScope())
        {
            first.Should().BeTrue("no item had been read yet, so the first wait is not held");
            item.Should().Be(1);
            completedBeforeRelease.Should().BeFalse("the first wait after one item is held until released");
            second.Should().BeTrue("once released, the held wait reports the item still queued");
            next.Should().Be(2);
            third.Should().BeFalse("a later wait is not held and sees the completed channel");
            window.HeldCalls.Should().Be(1);
        }
    }

    [Fact]
    public async Task WaitToReadAsync_ShouldHoldTheFirstCall_WhenNoItemIsToBeReadBeforeIt()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<int>();
        var window = new HeldReadWindow<int>(itemsBeforeTheHold: 0);
        var reader = window.Wrap(channel.Reader);

        // Act
        var held = reader.WaitToReadAsync().AsTask();
        await window.WindowEntered.WaitAsync(SignalTimeout);
        var completedBeforeRelease = held.IsCompleted;
        channel.Writer.TryWrite(7);
        window.Release();
        var result = await held.WaitAsync(SignalTimeout);

        // Assert
        using (new AssertionScope())
        {
            completedBeforeRelease.Should().BeFalse();
            result.Should().BeTrue("the release delegates to the real reader, which has an item");
            window.HeldCalls.Should().Be(1);
        }
    }

    [Fact]
    public async Task WaitToReadAsync_ShouldReturnCancelledWithoutHolding_WhenTheTokenIsAlreadyCancelled()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<int>();
        var window = new HeldReadWindow<int>(itemsBeforeTheHold: 0);
        var reader = window.Wrap(channel.Reader);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act
        var cancelled = reader.WaitToReadAsync(cts.Token);
        var heldAfterwards = reader.WaitToReadAsync().AsTask();
        await window.WindowEntered.WaitAsync(SignalTimeout);
        window.Release();
        channel.Writer.TryComplete();
        var result = await heldAfterwards.WaitAsync(SignalTimeout);

        // Assert
        using (new AssertionScope())
        {
            cancelled.IsCanceled.Should().BeTrue("the token is checked before anything else, as the runtime's reader does");
            result.Should().BeFalse();
            window.HeldCalls.Should().Be(1, "the cancelled call did not use up the hold");
        }
    }

    [Fact]
    public async Task WaitToReadAsync_ShouldReportTheCompletedChannel_WhenTheTokenIsCancelledWhileHeld()
    {
        // Arrange — the window itself: past the token check, the channel completes cleanly.
        var channel = Channel.CreateUnbounded<int>();
        var window = new HeldReadWindow<int>(itemsBeforeTheHold: 0);
        var reader = window.Wrap(channel.Reader);
        using var cts = new CancellationTokenSource();

        // Act
        var held = reader.WaitToReadAsync(cts.Token).AsTask();
        await window.WindowEntered.WaitAsync(SignalTimeout);
        await cts.CancelAsync();
        channel.Writer.TryComplete();
        window.Release();
        var result = await held.WaitAsync(SignalTimeout);

        // Assert
        result.Should().BeFalse("past its token check the read reports the completed channel, not the cancel");
    }

    [Fact]
    public async Task Inner_ShouldBeTheWrappedReader_WhenWrapped()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<int>();
        var window = new HeldReadWindow<int>(itemsBeforeTheHold: 0);

        // Act
        var reader = window.Wrap(channel.Reader);
        var inner = await window.Inner.WaitAsync(SignalTimeout);
        channel.Writer.TryWrite(1);
        channel.Writer.TryComplete();

        // Assert
        using (new AssertionScope())
        {
            inner.Should().BeSameAs(channel.Reader);
            reader.Count.Should().Be(1);
            reader.Completion.Should().BeSameAs(channel.Reader.Completion);
            FluentActions.Invoking(() => window.Wrap(channel.Reader)).Should().Throw<InvalidOperationException>();
        }
    }
}
