using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Diagnostics;
using Verbara.Sdk.Sessions.Manager;
using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.Hosting;

/// <summary>
/// One reconciliation pass over one server: the rules the single-server sweep and the pool sweep share. Its clock only
/// decides when to ask Asterisk: the held calls older than <see cref="SessionOptions.DialingTimeout"/> that hold a
/// channel the server's table holds are the candidates, and when there is one and the verification can be trusted, the
/// server's channels are reconciled once (<see cref="VerbaraServer.ReconcileChannelsAsync"/>). A call whose channels
/// the completed snapshot omits ends as a reload ends it; the pass itself changes no session.
/// </summary>
/// <remarks>
/// A pass asks nothing when no call qualifies, when the connection does not report how an action ended, when the
/// connection is not established, or while a load of the same server is running. A held call none of whose channels the
/// table holds cannot be proved gone by a snapshot: it is left alone and reported as unverifiable.
/// </remarks>
internal sealed partial class ServerSweep
{
    private readonly ICallSessionManager _manager;
    private readonly SessionOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    public ServerSweep(ICallSessionManager manager, SessionOptions options, ILogger logger, TimeProvider timeProvider)
    {
        _manager = manager;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// One pass over <paramref name="server"/>: find the held calls old enough to verify and, when there is one and the
    /// verification can be trusted, reconcile the server's channels once.
    /// </summary>
    /// <param name="server">The server to verify.</param>
    /// <param name="serverId">
    /// <see langword="null"/> for the single-server sweep, whose candidates are every held call; otherwise the server's
    /// id in the pool, and only the held calls whose <see cref="CallSession.ServerId"/> equals it are candidates.
    /// </param>
    /// <param name="cancellationToken">Cancels the verification.</param>
    public async Task SweepAsync(VerbaraServer server, string? serverId, CancellationToken cancellationToken)
    {
        using var activity = SessionActivitySource.StartReconciliation();
        var now = _timeProvider.GetUtcNow();
        var candidates = new List<CallSession>();
        var unverifiable = 0;

        var old = _manager.ActiveSessions
            .Where(session => serverId is null || string.Equals(session.ServerId, serverId, StringComparison.Ordinal))
            .Where(session => now - session.CreatedAt > _options.DialingTimeout)
            .ToArray();
        foreach (var session in old)
        {
            if (HoldsAChannel(server, session))
                candidates.Add(session);
            else
                unverifiable++;
        }

        var verification = SkipReason(server, candidates.Count) is { } reason ? $"skipped:{reason}" : "run";
        if (serverId is not null)
            activity?.SetTag("server.id", serverId);
        activity?.SetTag("sessions.candidates", candidates.Count);
        activity?.SetTag("sessions.unverifiable", unverifiable);
        activity?.SetTag("verification", verification);

        try
        {
            if (verification == "run")
                await server.ReconcileChannelsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Counted however the verification ended: one that threw after it had ended some calls (a subscriber to
            // an ending that threw) reports those.
            var ended = candidates.Count(HasEnded);
            activity?.SetTag("sessions.ended", ended);
            if (serverId is null)
                LogSweepResult(candidates.Count, unverifiable, ended, verification);
            else
                LogServerSweepResult(serverId, candidates.Count, unverifiable, ended, verification);
        }
    }

    /// <summary>Why this pass does not verify, or <see langword="null"/> when it does.</summary>
    private static string? SkipReason(VerbaraServer server, int candidates)
    {
        if (candidates == 0)
            return "no-candidate";

        var connection = server.Connection;
        if (!connection.ReportsEventActionOutcome)
            return "outcome-not-reported";

        if (connection.State != AmiConnectionState.Connected)
            return "not-connected";

        return server.IsLoadInFlight ? "load-in-flight" : null;
    }

    /// <summary>Whether the server's channel table holds any of <paramref name="session"/>'s channels.</summary>
    private static bool HoldsAChannel(VerbaraServer server, CallSession session)
    {
        string[] uniqueIds;
        lock (session.SyncRoot)
        {
            uniqueIds = [.. session.Participants.Select(participant => participant.UniqueId)];
        }

        foreach (var uniqueId in uniqueIds)
        {
            if (server.Channels.GetByUniqueId(uniqueId) is not null)
                return true;
        }

        return false;
    }

    private static bool HasEnded(CallSession session) =>
        session.State is CallSessionState.Completed or CallSessionState.Failed or CallSessionState.TimedOut;

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Reconciliation sweep: candidates={Candidates} unverifiable={Unverifiable} ended={Ended} verification={Verification}")]
    private partial void LogSweepResult(int candidates, int unverifiable, int ended, string verification);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Reconciliation sweep: server={ServerId} candidates={Candidates} unverifiable={Unverifiable} ended={Ended} verification={Verification}")]
    private partial void LogServerSweepResult(string serverId, int candidates, int unverifiable, int ended, string verification);
}
