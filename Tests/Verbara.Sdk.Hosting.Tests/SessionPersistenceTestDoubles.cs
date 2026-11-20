using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks.Sources;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.Hosting.Tests;

// The test doubles of the persistence-token facts, shared by the hosted-service, token-service and multi-server
// host-stop tests. Moved unchanged from SessionManagerHostedServiceTests.

/// <summary>
/// A store that records the token each save was handed and keeps that save in flight until the
/// token is cancelled, when the save ends with an <see cref="OperationCanceledException"/>. The
/// delay-free shape of <c>CallSessionManagerTests.PendingSaveStore</c>: the registration ends the
/// save on the cancelling thread, so the persist has resumed and finished its catch before
/// <see cref="CancellationTokenSource.Cancel()"/> returns and the assertions read the result. No
/// wall-clock wait ever stands in for "it has finished reacting".
/// </summary>
internal sealed class PendingSaveStore : SessionStoreBase
{
    private readonly ConcurrentQueue<PendingSave> _saves = new();

    public IReadOnlyCollection<PendingSave> Saves => _saves.ToArray();

    public override ValueTask SaveAsync(CallSession session, CancellationToken ct)
    {
        var save = new PendingSave(ct);
        _saves.Enqueue(save);
        ct.Register(() => save.End(new OperationCanceledException(ct)));
        return new ValueTask(save, 0);
    }

    public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) =>
        ValueTask.FromResult<CallSession?>(null);
}

/// <summary>
/// One in-flight save, and the source of the <see cref="ValueTask"/> the manager awaits. The
/// manager's await leaves its continuation here, and <see cref="End"/> runs that continuation on
/// the calling thread instead of scheduling it, ignoring the await's scheduling flags on
/// purpose. So when <see cref="End"/> returns, the persist has resumed, read the exception and
/// finished its catch, whether or not its await captured a synchronization context.
/// </summary>
internal sealed class PendingSave : IValueTaskSource
{
    private readonly Lock _gate = new();
    private Action<object?>? _continuation;
    private object? _continuationState;
    private Exception? _failure;
    private bool _observed;

    public PendingSave(CancellationToken token) => Token = token;

    /// <summary>The token this save was handed, which is the token the manager persists under.</summary>
    public CancellationToken Token { get; }

    /// <summary>The exception the save ended with, or <see langword="null"/> while it is in flight.</summary>
    public Exception? Failure
    {
        get
        {
            lock (_gate)
                return _failure;
        }
    }

    /// <summary>True once the awaiting persist has read how the save ended.</summary>
    public bool IsObserved
    {
        get
        {
            lock (_gate)
                return _observed;
        }
    }

    public void End(Exception failure)
    {
        Action<object?>? continuation;
        object? state;
        lock (_gate)
        {
            if (_failure is not null)
                return;
            _failure = failure;
            continuation = _continuation;
            state = _continuationState;
            _continuation = null;
            _continuationState = null;
        }

        continuation?.Invoke(state);
    }

    public ValueTaskSourceStatus GetStatus(short token)
    {
        lock (_gate)
        {
            return _failure switch
            {
                null => ValueTaskSourceStatus.Pending,
                OperationCanceledException => ValueTaskSourceStatus.Canceled,
                _ => ValueTaskSourceStatus.Faulted,
            };
        }
    }

    public void OnCompleted(Action<object?> continuation, object? state, short token,
        ValueTaskSourceOnCompletedFlags flags)
    {
        lock (_gate)
        {
            if (_failure is null)
            {
                _continuation = continuation;
                _continuationState = state;
                return;
            }
        }

        continuation(state);
    }

    public void GetResult(short token)
    {
        Exception failure;
        lock (_gate)
        {
            failure = _failure ?? throw new InvalidOperationException("The save is still in flight.");
            _observed = true;
        }

        ExceptionDispatchInfo.Throw(failure);
    }
}

/// <summary>Keeps every entry written to it, so a test can assert on level, event and message.</summary>
internal sealed class RecordingLogger : ILogger<CallSessionManager>
{
    private readonly ConcurrentQueue<RecordedLogEntry> _entries = new();

    public IReadOnlyCollection<RecordedLogEntry> Entries => _entries.ToArray();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        _entries.Enqueue(new RecordedLogEntry(logLevel, eventId, formatter(state, exception), exception));
}

internal sealed record RecordedLogEntry(LogLevel Level, EventId EventId, string Message, Exception? Exception);

/// <summary>
/// A hosted service that parks the host's start until the token the host handed it is cancelled,
/// and then ends the start as aborted. Registered after the service under test, so the host has
/// started that one and its own start token is still live while the park holds.
/// </summary>
internal sealed class ParkedStartHostedService : IHostedService
{
    private readonly TaskCompletionSource _parked =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _entered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the host has reached this service's start.</summary>
    public Task Entered => _entered.Task;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.Register(() => _parked.TrySetCanceled(cancellationToken));
        _entered.TrySetResult();
        return _parked.Task;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
