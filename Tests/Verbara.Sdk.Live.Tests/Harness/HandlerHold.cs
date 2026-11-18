namespace Verbara.Sdk.Live.Tests.Harness;

/// <summary>
/// Holds a handler the connection or the load raises, the first time it runs, until the test releases it: a seam the
/// product awaits holds with <see cref="BlockAsync"/>, a synchronous event with <see cref="Block"/>. The hold is a
/// <see cref="ThreadHold"/> bounded by <see cref="DispatchGate.HoldBound"/>, a hang bound a healthy run never reaches;
/// <see cref="Release"/> fails when the hold ran out on it.
/// </summary>
internal sealed class HandlerHold : IDisposable
{
    private readonly ThreadHold _hold = new();
    private int _blocked;

    public Task Held => _hold.Held;

    /// <summary>Whether the hold ran out on <see cref="DispatchGate.HoldBound"/> instead of being released.</summary>
    public bool TimedOut => _hold.TimedOut;

    /// <summary>Blocks the raising thread: for a synchronous event, which the product cannot await.</summary>
    public void Block()
    {
        if (Interlocked.Exchange(ref _blocked, 1) == 1)
            return;

        _hold.Hold(DispatchGate.HoldBound);
    }

    /// <summary>Holds an awaitable seam without blocking the thread that raised it.</summary>
    public ValueTask BlockAsync()
    {
        if (Interlocked.Exchange(ref _blocked, 1) == 1)
            return ValueTask.CompletedTask;

        return Wait(_hold);

        static async ValueTask Wait(ThreadHold hold) => await hold.HoldAsync(DispatchGate.HoldBound);
    }

    /// <summary>Lets the handler go on; fails when the hold had already run out.</summary>
    public void Release() => _hold.Release();

    public void Dispose() => _hold.Free();
}
