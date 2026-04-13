using Asterisk.Sdk.Sessions;
using Asterisk.Sdk.Sessions.Diagnostics;
using Asterisk.Sdk.Sessions.Manager;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.Hosting;

internal sealed partial class SessionReconciliationService : IHostedService, IDisposable
{
    private readonly ICallSessionManager _manager;
    private readonly SessionOptions _options;
    private readonly ILogger<SessionReconciliationService> _logger;
    private PeriodicTimer? _timer;
    private Task? _runningTask;
    private CancellationTokenSource? _cts;

    public SessionReconciliationService(
        ICallSessionManager manager,
        IOptions<SessionOptions> options,
        ILogger<SessionReconciliationService> logger)
    {
        _manager = manager;
        _options = options.Value;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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
                Sweep();
            }
            catch (Exception ex)
            {
                LogSweepError(ex);
            }
        }
    }

    internal void Sweep()
    {
        using var activity = SessionActivitySource.StartReconciliation();
        var now = DateTimeOffset.UtcNow;
        int timedOut = 0, orphaned = 0;

        foreach (var session in _manager.ActiveSessions.ToArray())
        {
            switch (session.State)
            {
                case CallSessionState.Dialing when session.DialingAt.HasValue
                    && (now - session.DialingAt.Value) > _options.DialingTimeout:
                    if (SessionReconciler.TryMarkTimedOut(session)) timedOut++;
                    break;

                case CallSessionState.Ringing when session.RingingAt.HasValue
                    && (now - session.RingingAt.Value) > _options.RingingTimeout:
                    if (SessionReconciler.TryMarkTimedOut(session)) timedOut++;
                    break;

                case CallSessionState.Created
                    when (now - session.CreatedAt) > _options.DialingTimeout:
                    if (SessionReconciler.TryMarkOrphaned(session)) orphaned++;
                    break;
            }
        }

        if (timedOut > 0 || orphaned > 0)
            LogSweepResult(timedOut, orphaned);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
            await _cts.CancelAsync();

        if (_runningTask is not null)
        {
            try
            {
                await _runningTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown
            }
        }

        _timer?.Dispose();
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _timer?.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Reconciliation sweep: {TimedOut} timed out, {Orphaned} orphaned")]
    private partial void LogSweepResult(int timedOut, int orphaned);

    [LoggerMessage(Level = LogLevel.Error, Message = "Reconciliation sweep error")]
    private partial void LogSweepError(Exception ex);
}
