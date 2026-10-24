using System.Collections.Concurrent;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Tests.Shared.Sessions;

/// <summary>
/// Stops one handler of the session manager at one <see cref="HandlerStep"/> — the first time a handler of the named
/// server (or a release of that server id, at <see cref="HandlerStep.ReleaseWalkStarted"/>) reaches that step, and,
/// when an id is given, for that channel id only — and holds it there until the test lets it go. A test parks a
/// handler inside the window it wants a second operation to land in, instead of sleeping and hoping.
/// </summary>
/// <remarks>
/// <para>Installed on the manager's internal <c>StepHook</c> (<see cref="Install"/>), alongside any other hook; removed
/// by <see cref="Dispose"/>. Only the first matching step parks; every later one, and every other step, passes
/// through untouched.</para>
/// <para><b>Every wait is bounded.</b> <see cref="WaitUntilParked"/> and <see cref="WaitUntilResumed"/> throw
/// <see cref="TimeoutException"/> when their bound runs out. A parked handler that is never let go is released by
/// the parker itself once its bound runs out, and that is recorded as a fault: <see cref="ThrowIfFaulted"/> and
/// <see cref="Dispose"/> throw it, so a forgotten let-go is a red test, never a hung lane. The default bound is far
/// above the release's own limits (5 s drain, 1 s per call lock), so a scenario that parks a handler past them on
/// purpose keeps it parked.</para>
/// <para><b>What else a park holds up (Live's event outbox).</b> Live raises a server's channel and queue
/// notifications from its outbox, one at a time, waiting at most 1 s for the one before to return. A channel or queue
/// handler parked here therefore holds that server's later notifications for at most 1 s; after that they run
/// alongside it, on their own thread. A test that parks such a handler must not send a later notification of the same
/// server and expect it to wait behind the park.</para>
/// <para>A hook that throws — the <see cref="WhenReached"/> action — is recorded as a fault and rethrown into the
/// handler, as any exception at that point of the handler would be. Linked into each suite that needs it
/// (<c>&lt;Compile Include=… Link=…&gt;</c>); one definition.</para>
/// </remarks>
internal sealed class StepParker : IDisposable
{
    /// <summary>The bound on every wait when none is given.</summary>
    public static readonly TimeSpan DefaultBound = TimeSpan.FromSeconds(30);

    private readonly HandlerStep _step;
    private readonly string _serverId;
    private readonly string? _id;
    private readonly TimeSpan _bound;
    private readonly Action<HandlerStep, string, string> _hook;
    private readonly TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _letGo = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<Exception> _faults = new();
    private CallSessionManager? _manager;
    private int _claimed;
    private bool _disposed;

    /// <summary>
    /// A parker for the first time a handler of <paramref name="serverId"/> reaches <paramref name="step"/> (for the
    /// channel id <paramref name="id"/> only, when given), every wait bounded by <paramref name="bound"/>
    /// (<see cref="DefaultBound"/> when null). Not installed until <see cref="Install"/>.
    /// </summary>
    public StepParker(HandlerStep step, string serverId, string? id = null, TimeSpan? bound = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverId);
        _step = step;
        _serverId = serverId;
        _id = id;
        _bound = bound ?? DefaultBound;
        _hook = OnStep;
    }

    /// <summary>Run on the handler's thread when the step is reached, before the handler parks.</summary>
    public Action? WhenReached { get; init; }

    /// <summary>The managed thread id of the parked handler, or 0 before it parked.</summary>
    public int ParkedThreadId { get; private set; }

    /// <summary>Whether a handler has reached the step and parked (it may have been let go since).</summary>
    public bool HasParked => _parked.Task.IsCompleted;

    /// <summary>The faults recorded so far: a missed let-go, or what <see cref="WhenReached"/> threw.</summary>
    public IReadOnlyCollection<Exception> Faults => [.. _faults];

    /// <summary>Builds a parker and adds it to <paramref name="manager"/>'s step hook.</summary>
    public static StepParker Install(CallSessionManager manager, HandlerStep step, string serverId, string? id = null,
        TimeSpan? bound = null)
    {
        var parker = new StepParker(step, serverId, id, bound);
        parker.Install(manager);
        return parker;
    }

    /// <summary>Adds this parker to <paramref name="manager"/>'s step hook, next to any hook already there.</summary>
    public void Install(CallSessionManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (_manager is not null)
            throw new InvalidOperationException("The parker is already installed.");

        _manager = manager;
        manager.StepHook += _hook;
    }

    /// <summary>Waits until a handler has parked at the step; throws <see cref="TimeoutException"/> past the bound.</summary>
    public void WaitUntilParked(TimeSpan? bound = null)
    {
        var limit = bound ?? _bound;
        if (!_parked.Task.Wait(limit))
        {
            throw new TimeoutException(
                $"No handler of server '{_serverId}' reached {_step}{IdText} within {limit.TotalMilliseconds:0} ms.");
        }
    }

    /// <summary>Lets the parked handler go on (or, before it parks, lets it pass without stopping).</summary>
    public void LetGo() => _letGo.TrySetResult();

    /// <summary>
    /// Waits until the parked handler has left the park point — let go, or released by the parker's own bound;
    /// throws <see cref="TimeoutException"/> past the bound.
    /// </summary>
    public void WaitUntilResumed(TimeSpan? bound = null)
    {
        var limit = bound ?? _bound;
        if (!_resumed.Task.Wait(limit))
        {
            throw new TimeoutException(
                $"The handler parked at {_step} for server '{_serverId}'{IdText} did not resume within "
                + $"{limit.TotalMilliseconds:0} ms.");
        }
    }

    /// <summary>Throws the first recorded fault, if any, wrapped with what the parker was waiting for.</summary>
    public void ThrowIfFaulted()
    {
        if (_faults.TryPeek(out var first))
        {
            throw first is TimeoutException
                ? new TimeoutException(first.Message, first)
                : new InvalidOperationException(
                    $"The step hook parked at {_step} for server '{_serverId}'{IdText} threw: {first.Message}", first);
        }
    }

    /// <summary>
    /// Takes the recorded faults off the parker, so a test that expected them (the parker's own tests) can assert
    /// them and still dispose the parker cleanly.
    /// </summary>
    public IReadOnlyList<Exception> TakeFaults()
    {
        var taken = new List<Exception>();
        while (_faults.TryDequeue(out var fault))
            taken.Add(fault);
        return taken;
    }

    /// <summary>
    /// Lets any parked handler go, removes the parker from the manager's step hook, and throws the first recorded
    /// fault that was not taken — so a test that forgot to let its handler go, or whose hook threw, ends red.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        LetGo();
        if (_manager is not null)
            _manager.StepHook -= _hook;
        ThrowIfFaulted();
    }

    private string IdText => _id is null ? string.Empty : $" for '{_id}'";

    private void OnStep(HandlerStep step, string serverId, string id)
    {
        if (step != _step
            || !string.Equals(serverId, _serverId, StringComparison.Ordinal)
            || (_id is not null && !string.Equals(id, _id, StringComparison.Ordinal)))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0)
            return;

        try
        {
            WhenReached?.Invoke();
        }
        catch (Exception ex)
        {
            _faults.Enqueue(ex);
            _parked.TrySetResult();
            _resumed.TrySetResult();
            throw;
        }

        ParkedThreadId = Environment.CurrentManagedThreadId;
        _parked.TrySetResult();
        try
        {
            if (!_letGo.Task.Wait(_bound))
            {
                _faults.Enqueue(new TimeoutException(
                    $"The handler parked at {_step} for server '{_serverId}'{IdText} was never let go; the parker "
                    + $"released it after {_bound.TotalMilliseconds:0} ms."));
            }
        }
        finally
        {
            _resumed.TrySetResult();
        }
    }
}
