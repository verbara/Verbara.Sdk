using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.Manager;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Hosting;

/// <summary>
/// The reconciliation sweep of a multi-server host: on each <see cref="SessionOptions.ReconciliationInterval"/> tick it
/// takes the servers <see cref="VerbaraServerPool"/> holds and runs, for each one, the pass a single-server host runs
/// (<see cref="ServerSweep"/>), with the held calls whose <c>ServerId</c> is that server's pool id as its candidates.
/// </summary>
/// <remarks>
/// <para>
/// The passes of a tick run side by side, each on its own: one that throws, times out, or reaches a server removed
/// and disposed since the tick began is logged and skipped, and the other servers of the tick are verified. A server
/// added while a tick runs is first walked by the next tick. A held call whose <c>ServerId</c> names no server the pool
/// holds is never verified nor ended; each tick reports how many there are.
/// </para>
/// <para>
/// The loop runs on a source this service owns: the token handed to <see cref="StartAsync"/> is neither stored nor
/// linked. <see cref="Timeout.InfiniteTimeSpan"/> switches the sweep off; with no pool registered it starts nothing.
/// When the host also registered the single-server sweep, the pool server that is the single DI server is left to it.
/// </para>
/// </remarks>
internal sealed partial class PoolReconciliationService : IHostedService, IDisposable
{
    private readonly ICallSessionManager _manager;
    private readonly VerbaraServerPool? _pool;
    private readonly VerbaraServer? _excluded;
    private readonly SessionOptions _options;
    private readonly ILogger<PoolReconciliationService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ServerSweep _sweep;

    /// <summary>The source the loop runs on, owned by this service: cancelled by the stop, released by the disposal.</summary>
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>Serializes <see cref="StopAsync"/> against <see cref="Dispose"/>, so a stop never cancels a released source.</summary>
    private readonly Lock _lifecycleGate = new();

    /// <summary>Set by <see cref="Dispose"/> under <see cref="_lifecycleGate"/>; a stop that finds it set does nothing.</summary>
    private bool _disposed;

    private PeriodicTimer? _timer;
    private Task? _runningTask;

    /// <param name="manager">The session engine whose held calls are verified.</param>
    /// <param name="pool">The pool to walk; <see langword="null"/> when the host registered none.</param>
    /// <param name="excluded">The single DI server the single-server sweep verifies, or <see langword="null"/>.</param>
    /// <param name="options">The interval and the age a held call is verified after.</param>
    /// <param name="logger">The sweep's logger.</param>
    /// <param name="timeProvider">The clock the interval and a call's age are read on.</param>
    public PoolReconciliationService(
        ICallSessionManager manager,
        VerbaraServerPool? pool,
        VerbaraServer? excluded,
        IOptions<SessionOptions> options,
        ILogger<PoolReconciliationService> logger,
        TimeProvider timeProvider)
    {
        _manager = manager;
        _pool = pool;
        _excluded = excluded;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider;
        _sweep = new ServerSweep(manager, _options, logger, timeProvider);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_pool is null)
        {
            LogNoPool();
            return Task.CompletedTask;
        }

        if (_options.ReconciliationInterval == Timeout.InfiniteTimeSpan)
        {
            LogSweepSwitchedOff();
            return Task.CompletedTask;
        }

        _timer = new PeriodicTimer(_options.ReconciliationInterval, _timeProvider);
        _runningTask = RunLoop(_pool, _lifetime.Token);
        return Task.CompletedTask;
    }

    private async Task RunLoop(VerbaraServerPool pool, CancellationToken ct)
    {
        while (await _timer!.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await SweepAsync(pool, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Each pass absorbs its own failure; anything that still reaches here is logged, and the next tick runs.
                LogTickError(ex);
            }
        }
    }

    /// <summary>One tick: a pass per server the pool holds now, side by side, then the tick's own line.</summary>
    private async Task SweepAsync(VerbaraServerPool pool, CancellationToken ct)
    {
        var servers = pool.Servers.ToArray();
        var ids = new HashSet<string>(servers.Select(entry => entry.Key), StringComparer.Ordinal);
        var serverless = _manager.ActiveSessions.Count(session => !ids.Contains(session.ServerId));

        var passes = new List<Task>(servers.Length);
        foreach (var (id, server) in servers)
        {
            if (!ReferenceEquals(server, _excluded))
                passes.Add(PassAsync(pool, id, server, ct));
        }

        await Task.WhenAll(passes).ConfigureAwait(false);
        LogTick(servers.Length, serverless);
    }

    /// <summary>One server's pass; every failure but the loop's own cancellation is logged here and not thrown.</summary>
    private async Task PassAsync(VerbaraServerPool pool, string id, VerbaraServer server, CancellationToken ct)
    {
        try
        {
            await _sweep.SweepAsync(server, id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException
            && (ex is not OperationCanceledException || !ct.IsCancellationRequested))
        {
            if (ReferenceEquals(pool.GetServer(id), server))
                LogPassError(id, ex);
            else
                LogPassErrorAfterRemoval(id, ex);
        }
    }

    /// <summary>Ends the loop and waits for it. A stop after disposal does nothing.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? runningTask;
        lock (_lifecycleGate)
        {
            if (_disposed)
                return;

            _lifetime.Cancel();
            runningTask = _runningTask;
        }

        if (runningTask is not null)
        {
            try
            {
                await runningTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // The loop's own cancellation, which this stop requested.
            }
        }

        _timer?.Dispose();
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
        _timer?.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Pool reconciliation sweep: servers={Servers} serverless={Serverless}")]
    private partial void LogTick(int servers, int serverless);

    [LoggerMessage(Level = LogLevel.Error, Message = "Pool reconciliation sweep: the pass of server {ServerId} failed")]
    private partial void LogPassError(string serverId, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Pool reconciliation sweep: the pass of server {ServerId} failed after the server left the pool")]
    private partial void LogPassErrorAfterRemoval(string serverId, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Pool reconciliation sweep error")]
    private partial void LogTickError(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Pool reconciliation sweep switched off: the reconciliation interval is infinite")]
    private partial void LogSweepSwitchedOff();

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Pool reconciliation sweep not started: no VerbaraServerPool is registered")]
    private partial void LogNoPool();
}
