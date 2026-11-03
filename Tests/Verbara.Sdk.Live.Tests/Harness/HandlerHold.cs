namespace Verbara.Sdk.Live.Tests.Harness;

/// <summary>
/// Holds a handler the connection or the load raises, the first time it runs, until the test releases it. The block is
/// bounded by <see cref="DispatchGate.HoldBound"/>, a hang bound a healthy run never reaches.
/// </summary>
internal sealed class HandlerHold : IDisposable
{
    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _released = new(initialState: false);
    private int _blocked;

    public Task Held => _held.Task;

    public void Block()
    {
        if (Interlocked.Exchange(ref _blocked, 1) == 1)
            return;

        _held.TrySetResult();
        _released.Wait(DispatchGate.HoldBound);
    }

    public void Release() => _released.Set();

    public void Dispose()
    {
        _released.Set();
        _released.Dispose();
    }
}
