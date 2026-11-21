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

/// <summary>
/// A pass-through decorator over a session manager, the shape a host writes for logging or metrics. It records the
/// attach and detach calls it forwards, and forwards <see cref="DisposeAsync"/> only when it owns the manager it wraps.
/// It implements nothing but <see cref="ICallSessionManager"/>: no SDK seam lets the SDK see through it. A test that
/// needs another forwarding shape passes <c>attachVia</c>, which receives the forward of the attach and decides how,
/// and on which thread, it runs; without it the attach is forwarded inline.
/// </summary>
internal sealed class ForwardingManager(ICallSessionManager inner, bool ownsInner, Action<Action>? attachVia = null)
    : ICallSessionManager
{
    private readonly ConcurrentQueue<string> _calls = new();

    /// <summary>The attach and detach calls forwarded, in order, as <c>attach:{id}</c> and <c>detach:{id}</c>.</summary>
    public IReadOnlyList<string> Calls => [.. _calls];

    public CallSession? GetById(string sessionId) => inner.GetById(sessionId);

    public CallSession? GetByLinkedId(string linkedId) => inner.GetByLinkedId(linkedId);

    public CallSession? GetByChannelId(string uniqueId) => inner.GetByChannelId(uniqueId);

    public CallSession? GetByBridgeId(string bridgeId) => inner.GetByBridgeId(bridgeId);

    public CallSession? GetByLinkedId(string serverId, string linkedId) => inner.GetByLinkedId(serverId, linkedId);

    public CallSession? GetByChannelId(string serverId, string uniqueId) => inner.GetByChannelId(serverId, uniqueId);

    public CallSession? GetByBridgeId(string serverId, string bridgeId) => inner.GetByBridgeId(serverId, bridgeId);

    public IEnumerable<CallSession> ActiveSessions => inner.ActiveSessions;

    public IEnumerable<CallSession> GetRecentCompleted(int count = 100) => inner.GetRecentCompleted(count);

    public IObservable<SessionDomainEvent> Events => inner.Events;

    public void AttachToServer(Verbara.Sdk.Live.Server.VerbaraServer server, string serverId)
    {
        _calls.Enqueue($"attach:{serverId}");
        if (attachVia is null)
            inner.AttachToServer(server, serverId);
        else
            attachVia(() => inner.AttachToServer(server, serverId));
    }

    public void DetachFromServer(string serverId)
    {
        _calls.Enqueue($"detach:{serverId}");
        inner.DetachFromServer(serverId);
    }

    public IReadOnlyList<CallSession> DetachFromServer(string serverId, DetachedCalls calls)
    {
        _calls.Enqueue($"detach:{serverId}");
        return inner.DetachFromServer(serverId, calls);
    }

    public ValueTask AdmitExistingCalls(string serverId, CancellationToken cancellationToken = default) =>
        inner.AdmitExistingCalls(serverId, cancellationToken);

    public bool RegisterReconstructedSession(CallSession session) => inner.RegisterReconstructedSession(session);

    public ValueTask DisposeAsync() => ownsInner ? inner.DisposeAsync() : ValueTask.CompletedTask;
}

/// <summary>A host's own session manager, or a test fake, that wraps no engine and opens no session.</summary>
internal sealed class ForeignManager : ICallSessionManager
{
    private readonly System.Reactive.Subjects.Subject<SessionDomainEvent> _events = new();

    public CallSession? GetById(string sessionId) => null;

    public CallSession? GetByLinkedId(string linkedId) => null;

    public CallSession? GetByChannelId(string uniqueId) => null;

    public CallSession? GetByBridgeId(string bridgeId) => null;

    public IEnumerable<CallSession> ActiveSessions => [];

    public IEnumerable<CallSession> GetRecentCompleted(int count = 100) => [];

    public IObservable<SessionDomainEvent> Events => _events;

    public void AttachToServer(Verbara.Sdk.Live.Server.VerbaraServer server, string serverId)
    {
        // Opens no session: this manager wraps no engine.
    }

    public void DetachFromServer(string serverId)
    {
        // Nothing attached.
    }

    public bool RegisterReconstructedSession(CallSession session) => false;

    public ValueTask DisposeAsync()
    {
        _events.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// One log entry with its category, as <see cref="CategoryLogCapture"/> keeps it. <see cref="Values"/> holds the entry's
/// named arguments, so a test checks what a message carries without matching its prose.
/// </summary>
internal sealed record CategorizedLogEntry(
    string Category, LogLevel Level, EventId EventId, string Message, IReadOnlyDictionary<string, object?> Values);

/// <summary>Keeps every log entry of every category and level a host writes.</summary>
internal sealed class CategoryLogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<CategorizedLogEntry> _entries = new();

    public IReadOnlyList<CategorizedLogEntry> Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
        // Nothing to release.
    }

    private sealed class Logger(CategoryLogCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            // A later pair of the same name wins, so a state that repeats a name cannot fail the capture.
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                    values[pair.Key] = pair.Value;
            }

            owner._entries.Enqueue(
                new CategorizedLogEntry(category, logLevel, eventId, formatter(state, exception), values));
        }
    }
}
