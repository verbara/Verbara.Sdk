using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.Manager;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Hosting;

/// <summary>
/// The session engine's periodic reconciliation. A complete current channel table first decides pending held calls
/// without Status, regardless of their age or connection state, except while a reconnect is pending. Status then
/// verifies old calls whose participant channel ids or linkedid are held, and pending calls on an incomplete table.
/// </summary>
/// <remarks>
/// <para>
/// Status is requested at most once per tick, and skipped without candidates, without event-action outcome reporting,
/// while disconnected, reconnecting, or loading. These skips do not suppress the independent complete-table decision.
/// Pending calls waiting without a held participant/linkedid are counted on each tick while actual Status refusal
/// persists; a successful read clears that state even when application is deferred. Older observed channel-less
/// calls retain their count, as do detached/custom legacy calls under their participant classification. Ended or
/// released calls are excluded. Instance checks use each call's server id; detached historical ownership supplies
/// legacy counting only, without a live table oracle.
/// </para>
/// <para>
/// <see cref="SessionOptions.ReconciliationInterval"/> set to <see cref="Timeout.InfiniteTimeSpan"/> switches the
/// sweep off. Any other interval of zero or less fails the start with <see cref="ArgumentOutOfRangeException"/>.
/// </para>
/// </remarks>
internal sealed partial class SessionReconciliationService : IHostedService, IDisposable
{
    private readonly VerbaraServer _server;
    private readonly SessionOptions _options;
    private readonly ILogger<SessionReconciliationService> _logger;
    private readonly ServerSweep _sweep;
    private PeriodicTimer? _timer;
    private Task? _runningTask;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Serializes <see cref="StopAsync"/> against <see cref="Dispose"/>: whichever takes <see cref="_cts"/> under it
    /// owns that source, so a stop never cancels a source the disposal released.
    /// </summary>
    private readonly Lock _lifecycleGate = new();

    /// <summary>Set by <see cref="Dispose"/> under <see cref="_lifecycleGate"/>; a stop that finds it set does nothing.</summary>
    private bool _disposed;

    public SessionReconciliationService(
        ICallSessionManager manager,
        VerbaraServer server,
        IOptions<SessionOptions> options,
        ILogger<SessionReconciliationService> logger)
        : this(manager, server, options, logger, TimeProvider.System)
    {
    }

    /// <summary>The same sweep, reading the time a call's age is measured at from <paramref name="timeProvider"/>.</summary>
    internal SessionReconciliationService(
        ICallSessionManager manager,
        VerbaraServer server,
        IOptions<SessionOptions> options,
        ILogger<SessionReconciliationService> logger,
        TimeProvider timeProvider)
    {
        _server = server;
        _options = options.Value;
        _logger = logger;
        _sweep = new ServerSweep(manager, _options, logger, timeProvider);
    }

    /// <summary>
    /// Starts the sweep loop. The sweep runs until <see cref="StopAsync"/> or <see cref="Dispose"/>.
    /// </summary>
    /// <param name="cancellationToken">Unused: this start begins nothing that can be aborted.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="SessionOptions.ReconciliationInterval"/> is zero or less and not <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </exception>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The parameter is deliberately unused: it means the start was aborted, and this start begins
        // nothing that can be aborted. The loop gets the token this service owns, which the service's own
        // stop cancels; a source linked to the start's token would end the sweep when that token is
        // cancelled after the start returned.
        if (_options.ReconciliationInterval == Timeout.InfiniteTimeSpan)
        {
            LogSweepSwitchedOff();
            return Task.CompletedTask;
        }

        _cts = new CancellationTokenSource();
        _timer = new PeriodicTimer(_options.ReconciliationInterval);
        _runningTask = RunLoop(_cts.Token);
        return Task.CompletedTask;
    }

    private async Task RunLoop(CancellationToken ct)
    {
        while (await _timer!.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await SweepAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Everything but the loop's own cancellation: a connection that is not established, a snapshot that
                // timed out on the connection's own event timeout, or a subscriber to an ending that threw. The next
                // tick verifies again.
                LogSweepError(ex);
            }
        }
    }

    /// <summary>
    /// One sweep: find the held calls old enough to verify and, when there is one and the verification can be
    /// trusted, reconcile the server's channels once. The rules are <see cref="ServerSweep"/>'s, with every held call a
    /// candidate.
    /// </summary>
    internal Task SweepAsync(CancellationToken cancellationToken) =>
        _sweep.SweepAsync(_server, serverId: null, cancellationToken);

    /// <summary>
    /// Ends the loop and waits for it. A stop after disposal does nothing: it cancels and awaits nothing the disposal
    /// released.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? cts;
        Task? runningTask;
        lock (_lifecycleGate)
        {
            if (_disposed)
                return;

            // Taken, not read: from here this stop owns the source and releases it, and a disposal that comes while
            // the loop is ending finds nothing of it to release.
            cts = _cts;
            _cts = null;
            runningTask = _runningTask;
        }

        if (cts is null)
            return;

        await cts.CancelAsync().ConfigureAwait(false);

        if (runningTask is not null)
        {
            try
            {
                await runningTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // The loop's own cancellation, which this stop requested.
            }
        }

        cts.Dispose();
        _timer?.Dispose();
    }

    public void Dispose()
    {
        CancellationTokenSource? cts;
        lock (_lifecycleGate)
        {
            if (_disposed)
                return;

            _disposed = true;
            cts = _cts;
            _cts = null;
        }

        cts?.Dispose();
        _timer?.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Reconciliation sweep switched off: the reconciliation interval is infinite")]
    private partial void LogSweepSwitchedOff();

    [LoggerMessage(Level = LogLevel.Error, Message = "Reconciliation sweep error")]
    private partial void LogSweepError(Exception ex);
}
