using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace Verbara.Sdk.TestInfrastructure.Channels;

/// <summary>
/// Holds one <see cref="ChannelReader{T}.WaitToReadAsync"/> of a channel reader inside the window between
/// its token check and its read of the channel's state, until the test lets it go: the instant a cancel
/// can land after the reader has looked at its token and before it sees the channel completed.
/// </summary>
/// <remarks>
/// <para>
/// The code under test reads through the reader <see cref="Wrap"/> returns, which decorates the real
/// one. The held call is the first <see cref="ChannelReader{T}.WaitToReadAsync"/> made once
/// <c>itemsBeforeTheHold</c> items have been handed out through <see cref="ChannelReader{T}.TryRead"/>;
/// counting items, rather than arming from the test, puts the hold on the right call even when the
/// reader has already parked by the time the test acts. Every other call goes straight to the real
/// reader.
/// </para>
/// <para>
/// The held call follows the order of the runtime's unbounded reader: it checks its token first and
/// returns a cancelled result, without holding, if the token is already cancelled; it then completes
/// <see cref="WindowEntered"/>, waits for <see cref="Release"/>, and reads the channel's state through
/// the real reader without looking at its token again. Nothing in it waits on the clock.
/// </para>
/// </remarks>
/// <typeparam name="T">The channel's item type.</typeparam>
public sealed class HeldReadWindow<T>
{
    private readonly int _itemsBeforeTheHold;
    private readonly TaskCompletionSource _windowEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<ChannelReader<T>> _wrapped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _handedOut;
    private int _held;

    /// <summary>A window that holds the first wait made after <paramref name="itemsBeforeTheHold"/> items were read.</summary>
    public HeldReadWindow(int itemsBeforeTheHold)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(itemsBeforeTheHold);
        _itemsBeforeTheHold = itemsBeforeTheHold;
    }

    /// <summary>Completes when the held call has checked its token and is waiting for <see cref="Release"/>.</summary>
    public Task WindowEntered => _windowEntered.Task;

    /// <summary>How many calls have been held: zero or one.</summary>
    public int HeldCalls => Volatile.Read(ref _held);

    /// <summary>
    /// Completes with the real reader once <see cref="Wrap"/> has run, so a test can watch the real
    /// channel's <see cref="ChannelReader{T}.Completion"/> and <see cref="ChannelReader{T}.Count"/>.
    /// </summary>
    public Task<ChannelReader<T>> Inner => _wrapped.Task;

    /// <summary>The reader the code under test reads through, decorating <paramref name="inner"/>. One per window.</summary>
    public ChannelReader<T> Wrap(ChannelReader<T> inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!_wrapped.TrySetResult(inner))
            throw new InvalidOperationException("A held window wraps one reader.");

        return new HeldReader(this, inner);
    }

    /// <summary>Lets the held call read the channel's state.</summary>
    public void Release() => _released.TrySetResult();

    private sealed class HeldReader(HeldReadWindow<T> window, ChannelReader<T> inner) : ChannelReader<T>
    {
        public override Task Completion => inner.Completion;

        public override bool CanCount => inner.CanCount;

        public override int Count => inner.Count;

        public override bool CanPeek => inner.CanPeek;

        public override bool TryPeek([MaybeNullWhen(false)] out T item) => inner.TryPeek(out item);

        public override bool TryRead([MaybeNullWhen(false)] out T item)
        {
            if (!inner.TryRead(out item))
                return false;

            Interlocked.Increment(ref window._handedOut);
            return true;
        }

        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
        {
            // The runtime's reader looks at its token before anything else.
            if (cancellationToken.IsCancellationRequested)
                return ValueTask.FromCanceled<bool>(cancellationToken);

            if (Volatile.Read(ref window._handedOut) < window._itemsBeforeTheHold
                || Interlocked.CompareExchange(ref window._held, 1, 0) != 0)
            {
                return inner.WaitToReadAsync(cancellationToken);
            }

            return HoldAsync();
        }

        private async ValueTask<bool> HoldAsync()
        {
            window._windowEntered.TrySetResult();
            await window._released.Task.ConfigureAwait(false);

            // Past its token check, the runtime's reader reads the state without looking at the token again.
            return await inner.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
