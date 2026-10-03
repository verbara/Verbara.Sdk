using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Diagnostics;
using Verbara.Sdk.Sessions.Manager;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Hosting;

/// <summary>
/// The session engine's periodic reconciliation sweep. Its clock only decides when to ask Asterisk: at each tick,
/// when a held call is older than <see cref="SessionOptions.DialingTimeout"/> and the channel table holds one of its
/// channels, the sweep reconciles the server's channels against Asterisk's <c>Status</c> snapshot
/// (<see cref="VerbaraServer.ReconcileChannelsAsync"/>), once for all such calls. A call whose channels the completed
/// snapshot omits ends as a reload ends it; the sweep itself changes no session.
/// </summary>
/// <remarks>
/// <para>
/// The sweep asks nothing when no call qualifies, when the connection does not report how an action ended (it could
/// not tell a refused <c>Status</c> from an empty one), when the connection is not established, or while a load of the
/// same server is running; it does not wait for that load. A held call none of whose channels the table holds cannot
/// be proved gone by a snapshot: it is left alone and reported as unverifiable.
/// </para>
/// <para>
/// <see cref="SessionOptions.ReconciliationInterval"/> set to <see cref="Timeout.InfiniteTimeSpan"/> switches the
/// sweep off: no timer is started and nothing is sent. Any other interval of zero or less fails the start with
/// <see cref="ArgumentOutOfRangeException"/>.
/// </para>
/// </remarks>
internal sealed partial class SessionReconciliationService : IHostedService, IDisposable
{
    private readonly ICallSessionManager _manager;
    private readonly VerbaraServer _server;
    private readonly SessionOptions _options;
    private readonly ILogger<SessionReconciliationService> _logger;
    private readonly TimeProvider _timeProvider;
    private PeriodicTimer? _timer;
    private Task? _runningTask;
    private CancellationTokenSource? _cts;

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
        _manager = manager;
        _server = server;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.ReconciliationInterval == Timeout.InfiniteTimeSpan)
        {
            LogSweepSwitchedOff();
            return Task.CompletedTask;
        }

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
    /// trusted, reconcile the server's channels once.
    /// </summary>
    internal async Task SweepAsync(CancellationToken cancellationToken)
    {
        using var activity = SessionActivitySource.StartReconciliation();
        var now = _timeProvider.GetUtcNow();
        var candidates = new List<CallSession>();
        var unverifiable = 0;

        var old = _manager.ActiveSessions.Where(session => now - session.CreatedAt > _options.DialingTimeout).ToArray();
        foreach (var session in old)
        {
            if (HoldsAChannel(session))
                candidates.Add(session);
            else
                unverifiable++;
        }

        var verification = SkipReason(candidates.Count) is { } reason ? $"skipped:{reason}" : "run";
        activity?.SetTag("sessions.candidates", candidates.Count);
        activity?.SetTag("sessions.unverifiable", unverifiable);
        activity?.SetTag("verification", verification);

        try
        {
            if (verification == "run")
                await _server.ReconcileChannelsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Counted however the verification ended: one that threw after it had ended some calls (a subscriber to
            // an ending that threw) reports those.
            var ended = candidates.Count(HasEnded);
            activity?.SetTag("sessions.ended", ended);
            LogSweepResult(candidates.Count, unverifiable, ended, verification);
        }
    }

    /// <summary>Why this sweep does not verify, or <see langword="null"/> when it does.</summary>
    private string? SkipReason(int candidates)
    {
        if (candidates == 0)
            return "no-candidate";

        var connection = _server.Connection;
        if (!connection.ReportsEventActionOutcome)
            return "outcome-not-reported";

        if (connection.State != AmiConnectionState.Connected)
            return "not-connected";

        return _server.IsLoadInFlight ? "load-in-flight" : null;
    }

    /// <summary>Whether the server's channel table holds any of <paramref name="session"/>'s channels.</summary>
    private bool HoldsAChannel(CallSession session)
    {
        string[] uniqueIds;
        lock (session.SyncRoot)
        {
            uniqueIds = [.. session.Participants.Select(participant => participant.UniqueId)];
        }

        foreach (var uniqueId in uniqueIds)
        {
            if (_server.Channels.GetByUniqueId(uniqueId) is not null)
                return true;
        }

        return false;
    }

    private static bool HasEnded(CallSession session) =>
        session.State is CallSessionState.Completed or CallSessionState.Failed or CallSessionState.TimedOut;

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

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Reconciliation sweep: candidates={Candidates} unverifiable={Unverifiable} ended={Ended} verification={Verification}")]
    private partial void LogSweepResult(int candidates, int unverifiable, int ended, string verification);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Reconciliation sweep switched off: the reconciliation interval is infinite")]
    private partial void LogSweepSwitchedOff();

    [LoggerMessage(Level = LogLevel.Error, Message = "Reconciliation sweep error")]
    private partial void LogSweepError(Exception ex);
}
