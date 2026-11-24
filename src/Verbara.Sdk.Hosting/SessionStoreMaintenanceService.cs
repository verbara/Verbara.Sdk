using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Verbara.Sdk.Hosting;

/// <summary>
/// Runs the session store's maintenance: arms the store before any hosted service starts, then hands it, at once and on
/// every <see cref="SessionStoreBase.MaintenanceInterval"/>, the sessions the manager holds. Registered once by every
/// sessions registration, single- and multi-server alike; it attaches and detaches no server and never touches the
/// session engine.
/// </summary>
/// <remarks>
/// <para>
/// A store whose interval is <see langword="null"/> — the in-memory default, a store of a host's own, and an SDK store
/// whose option for it is unset — is never armed and never called: the service stays asleep for the whole process.
/// </para>
/// <para>
/// The store is armed in <see cref="StartingAsync"/>, which the host runs for every hosted service before any service's
/// <c>StartAsync</c>, so no save of a call the host attaches can go out unarmed. A caller that drives this service as a
/// plain <see cref="IHostedService"/> arms it in <see cref="StartAsync"/>. The start does no I/O and never fails on an
/// unreachable store: the first run goes out on the loop. Runs never overlap. A run that throws logs one Warning
/// (EventId 1) and the next tick runs as usual. An interval longer than <see cref="MaximumWait"/> waits that long
/// instead, and a wait the clock fails to make logs one Warning (EventId 2) and is made on the system clock, so no
/// interval and no clock ends the loop. <see cref="StopAsync"/> cancels the loop and waits for it.
/// </para>
/// </remarks>
internal sealed partial class SessionStoreMaintenanceService : IHostedLifecycleService, IDisposable
{
    private readonly SessionStoreBase _store;
    private readonly ICallSessionManager _manager;
    private readonly TimeProvider _time;
    private readonly ILogger<SessionStoreMaintenanceService> _logger;
    private readonly ILogger _storeLogger;

    /// <summary>
    /// The longest wait <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> accepts (4,294,967,294 ms,
    /// about 49.7 days): a longer interval waits this long, so its runs go out early, never not at all.
    /// </summary>
    internal static readonly TimeSpan MaximumWait = TimeSpan.FromMilliseconds(uint.MaxValue - 1d);

    /// <summary>Serializes the start, the stop and the disposal.</summary>
    private readonly Lock _gate = new();

    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private bool _started;
    private bool _disposed;

    /// <summary>Built by the sessions registrations through an explicit factory.</summary>
    public SessionStoreMaintenanceService(
        SessionStoreBase store,
        ICallSessionManager manager,
        TimeProvider time,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _store = store;
        _manager = manager;
        _time = time;
        _logger = loggerFactory.CreateLogger<SessionStoreMaintenanceService>();
        // The store reports under its own type's name, as its other logs would.
        _storeLogger = loggerFactory.CreateLogger(store.GetType().FullName ?? store.GetType().Name);
    }

    /// <summary>Over the store and the manager alone: the system clock, no logger.</summary>
    public SessionStoreMaintenanceService(SessionStoreBase store, ICallSessionManager manager)
        : this(store, manager, TimeProvider.System, NullLoggerFactory.Instance)
    {
    }

    /// <summary>Arms the store and starts the loop, before any hosted service starts.</summary>
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        Start();
        return Task.CompletedTask;
    }

    /// <summary>Arms the store and starts the loop for a plain driver; on a lifecycle host it already ran.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Start();
        return Task.CompletedTask;
    }

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Cancels the loop and waits for the run in progress, if any, to see the cancellation.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? loop;
        lock (_gate)
        {
            loop = _loop;
            _loopCts?.Cancel();
        }

        if (loop is not null)
            await loop.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
                return;
            _started = true;

            if (_store.MaintenanceInterval is not { } interval || interval <= TimeSpan.Zero)
                return;

            _store.MaintenanceLogger = _storeLogger;
            _store.ArmMaintenance();
            _loopCts = new CancellationTokenSource();
            var token = _loopCts.Token;
            _loop = Task.Run(() => RunAsync(interval, token), CancellationToken.None);
        }
    }

    /// <summary>One run at once, then one per interval after the previous run returned: runs never overlap.</summary>
    private async Task RunAsync(TimeSpan interval, CancellationToken ct)
    {
        var wait = interval > MaximumWait ? MaximumWait : interval;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await RunOnceAsync(ct).ConfigureAwait(false);
                await WaitAsync(wait, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped.
        }
    }

    /// <summary>Waits on the host's clock; a wait that clock fails to make is logged and made on the system clock.</summary>
    private async Task WaitAsync(TimeSpan wait, CancellationToken ct)
    {
        try
        {
            await Task.Delay(wait, _time, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogWaitFailed(_logger, ex, _store.GetType().Name);
            await Task.Delay(wait, TimeProvider.System, ct).ConfigureAwait(false);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            // What the manager holds at the start of this run.
            var held = _manager.ActiveSessions.ToList();
            await _store.MaintainAsync(held, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogRunFailed(_logger, ex, _store.GetType().Name);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "A maintenance run of the session store {StoreType} failed; the next run goes out on schedule.")]
    private static partial void LogRunFailed(ILogger logger, Exception exception, string storeType);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "The wait before the next maintenance run of the session store {StoreType} failed on the host's clock; it is made on the system clock.")]
    private static partial void LogWaitFailed(ILogger logger, Exception exception, string storeType);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _loopCts?.Cancel();
            _loopCts?.Dispose();
        }
    }
}
