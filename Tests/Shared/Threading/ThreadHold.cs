namespace Verbara.Sdk.Tests.Shared.Threading;

/// <summary>
/// Holds the code that raises a hook until the test releases it: the hook calls <see cref="HoldAsync"/> (an awaitable
/// seam) or <see cref="Hold"/> (a synchronous one), which completes <see cref="Held"/> and waits, and the test, once
/// <see cref="Held"/> completes, does what must happen while the hook is held and calls <see cref="Release"/>.
/// </summary>
/// <remarks>
/// <para>
/// Prefer <see cref="HoldAsync"/> wherever the product awaits the hook (the server's internal load seams): it holds the
/// load without holding a thread, so a saturated pool cannot starve the test's continuation past the bound. <see cref="Hold"/>
/// is for a hook the product raises synchronously (a public event, an observer's <c>OnNext</c>, a lock-held seam), where
/// the only way to hold is to block the thread.
/// </para>
/// <para>
/// That thread is usually a thread-pool worker, and blocking one starves the work it would have run next, the test's
/// own continuation included. Two rules keep that continuation runnable while a blocking hold is in force:
/// </para>
/// <list type="bullet">
/// <item><see cref="Held"/> is completed from the pool's global queue (<see cref="Signal"/>). Completed on the worker
/// itself, its continuation lands on that worker's local queue, which the other workers only steal from once the global
/// queue runs dry; under load it waits there for the very worker that is blocked, until the hold runs out.</item>
/// <item>The block is a <see cref="Task.Wait(TimeSpan)"/>, which tells the pool its worker is blocked, so the pool adds
/// a worker for it. A <see cref="ManualResetEventSlim"/> wait does not, and on a saturated pool the continuation can
/// wait for the next starvation injection, past the hold's bound.</item>
/// </list>
/// <para>
/// The bound is a hang bound: a healthy run never reaches it. A hold that ran out let the thread go on unheld, so
/// whatever the test then does no longer happens while it is held. <see cref="TimedOut"/> records it, and
/// <see cref="Release"/> fails on it, so such a run fails as a hold that ran out, not as the product's fault.
/// </para>
/// </remarks>
internal sealed class ThreadHold
{
    private const int Open = 0;
    private const int Released = 1;
    private const int RanOut = 2;

    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _state;

    /// <summary>Completes once a thread is blocked in <see cref="Hold"/>.</summary>
    public Task Held => _held.Task;

    /// <summary>Whether the hold ran out on its bound before the test released it.</summary>
    public bool TimedOut => Volatile.Read(ref _state) == RanOut;

    /// <summary>Whether the test has let the thread go (<see cref="Release"/> or <see cref="Free"/>).</summary>
    public bool IsReleased => _released.Task.IsCompleted;

    /// <summary>
    /// Completes <see cref="Held"/> and waits, without blocking a thread, until <see cref="Release"/> or <see cref="Free"/>,
    /// or until <paramref name="bound"/> runs out. For a seam the product awaits.
    /// </summary>
    /// <returns>Whether the hook was let go; <see langword="false"/> when the hold ran out.</returns>
    public async ValueTask<bool> HoldAsync(TimeSpan bound)
    {
        _held.TrySetResult();
        try
        {
            await _released.Task.WaitAsync(bound);
            return true;
        }
        catch (TimeoutException)
        {
            // A release that lands between the wait's end and this exchange still let the hook go first.
            return Interlocked.CompareExchange(ref _state, RanOut, Open) != Open;
        }
    }

    /// <summary>
    /// Completes <see cref="Held"/> and blocks the calling thread until <see cref="Release"/> or <see cref="Free"/>, or
    /// until <paramref name="bound"/> runs out.
    /// </summary>
    /// <returns>Whether the thread was let go; <see langword="false"/> when the hold ran out.</returns>
    public bool Hold(TimeSpan bound)
    {
        Signal(_held);
        if (Block(_released.Task, bound))
            return true;

        // A release that lands between the wait's end and this exchange still let the thread go first.
        return Interlocked.CompareExchange(ref _state, RanOut, Open) != Open;
    }

    /// <summary>The test's release: lets the held thread go on, and fails when the hold had already run out.</summary>
    /// <exception cref="TimeoutException">The hold ran out before this release, so the thread went on unheld.</exception>
    public void Release()
    {
        Free();
        if (TimedOut)
        {
            throw new TimeoutException(
                "The hold ran out on its bound before the test released it, so the held thread went on unheld and "
                + "what the test did since did not happen while it was held: a starved test continuation, not a product fault.");
        }
    }

    /// <summary>Lets the held thread go on without judging the hold: for cleanup that runs however far the test got.</summary>
    public void Free()
    {
        Interlocked.CompareExchange(ref _state, Released, Open);
        _released.TrySetResult();
    }

    /// <summary>
    /// Waits, without blocking a thread, until <paramref name="released"/> completes or <paramref name="bound"/> runs out,
    /// whichever is first: for an awaitable seam whose test only paces it and judges the outcome elsewhere.
    /// </summary>
    public static async ValueTask Until(Task released, TimeSpan bound)
    {
        try
        {
            await released.WaitAsync(bound);
        }
        catch (TimeoutException)
        {
            // A hang bound: the seam goes on, and the test's own assertions judge what followed.
        }
    }

    /// <summary>
    /// Completes <paramref name="signal"/> from the pool's global queue, for a hook that blocks its thread right after:
    /// the continuations then never wait on that thread's local queue.
    /// </summary>
    public static void Signal(TaskCompletionSource signal) =>
        ThreadPool.UnsafeQueueUserWorkItem(static s => s.TrySetResult(), signal, preferLocal: false);

    /// <summary>
    /// Completes <paramref name="signal"/> with <paramref name="result"/> from the pool's global queue, for a hook that
    /// blocks its thread right after (see <see cref="Signal(TaskCompletionSource)"/>).
    /// </summary>
    public static void Signal<T>(TaskCompletionSource<T> signal, T result) =>
        ThreadPool.UnsafeQueueUserWorkItem(static s => s.Signal.TrySetResult(s.Result), (Signal: signal, Result: result), preferLocal: false);

    /// <summary>
    /// Blocks the calling thread until <paramref name="released"/> completes or <paramref name="bound"/> runs out, in a
    /// wait the pool compensates with another worker.
    /// </summary>
    /// <returns>Whether <paramref name="released"/> completed within <paramref name="bound"/>.</returns>
    public static bool Block(Task released, TimeSpan bound)
    {
#pragma warning disable VSTHRD002 // blocking the hook's thread is what a hold is
        return released.Wait(bound);
#pragma warning restore VSTHRD002
    }
}
