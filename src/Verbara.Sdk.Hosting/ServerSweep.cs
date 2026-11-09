using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Diagnostics;
using Verbara.Sdk.Sessions.Manager;
using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.Hosting;

/// <summary>
/// One server's reconciliation pass, shared by the single-server and pool services. A complete current table first
/// decides pending calls without Status. Otherwise pending calls waiting for completeness, and old calls whose
/// channel ids or linkedid the table holds, cause one channel verification when its connection can be trusted.
/// </summary>
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
    /// Decides calls against this instance, then asks for Status at most once. A pool pass also restricts calls to
    /// its exact pool id; a single-server pass follows each call's own attachment identity. Custom managers retain
    /// their existing participant/age verification behavior, without assuming they expose the built-in oracle.
    /// </summary>
    public async Task SweepAsync(VerbaraServer server, string? serverId, CancellationToken cancellationToken)
    {
        using var activity = SessionActivitySource.StartReconciliation();
        var now = _timeProvider.GetUtcNow();
        var selected = _manager.ActiveSessions
            .Where(call => serverId is null || string.Equals(call.ServerId, serverId, StringComparison.Ordinal))
            .Where(call => _manager is not CallSessionManager scopedManager
                || scopedManager.IsSweepServer(call.ServerId, server, serverId is not null))
            .ToArray();
        var customHeld = _manager is CallSessionManager ? null : new HashSet<CallSession>(selected, ReferenceEqualityComparer.Instance);
        var decided = new List<CallSession>();
        if (_manager is CallSessionManager engine && server.ChannelTableComplete && !server.IsReconnectPending)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var call in selected)
            {
                if (engine.TryGetSweepState(call, server, serverId is not null, out var pending, out _) && pending)
                {
                    decided.Add(call);
                    ids.Add(call.ServerId);
                }
            }
            foreach (var id in ids)
            {
                if (!server.IsReconnectPending)
                    engine.DecideCompleteTable(id, server);
            }
        }

        var candidates = new List<CallSession>();
        var legacyParticipantCandidates = new HashSet<CallSession>(ReferenceEqualityComparer.Instance);
        foreach (var call in selected)
        {
            lock (call.SyncRoot)
            {
                if (!InScope(call, server, serverId, customHeld, out var pending, out var attached)) continue;
                var old = now - call.CreatedAt > _options.DialingTimeout;
                var holdsParticipant = old && HoldsParticipantChannel(server, call);
                if (old && holdsParticipant) legacyParticipantCandidates.Add(call);
                if ((old && (holdsParticipant || HoldsLinkedChannel(server, call, attached))) || (pending && !server.ChannelTableComplete))
                    candidates.Add(call);
            }
        }

        var verification = SkipReason(server, candidates.Count) is { } reason ? $"skipped:{reason}" : "run";
        if (serverId is not null) activity?.SetTag("server.id", serverId);
        activity?.SetTag("sessions.candidates", candidates.Count);
        activity?.SetTag("verification", verification);
        try
        {
            if (verification == "run")
                await server.ReconcileChannelsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Count only calls still held after both decision paths. A successful read may clear refusal even when
            // its application was deferred; completeness and the last refusal therefore remain separate inputs.
            if (_manager is not CallSessionManager)
                customHeld = new HashSet<CallSession>(_manager.ActiveSessions, ReferenceEqualityComparer.Instance);
            var unverifiable = 0;
            foreach (var call in selected)
            {
                lock (call.SyncRoot)
                {
                    if (!InScope(call, server, serverId, customHeld, out var pending, out var attached)) continue;
                    // A historical attachment has no live decision oracle. Preserve the pre-Status legacy
                    // participant classification, including a detach during this read; pending/linkedid-only
                    // candidates must not acquire legacy participant membership merely by requesting Status.
                    if (!attached)
                    {
                        if (now - call.CreatedAt > _options.DialingTimeout && !legacyParticipantCandidates.Contains(call)) unverifiable++;
                        continue;
                    }
                    if (HoldsParticipantChannel(server, call) || HoldsLinkedChannel(server, call, attached)) continue;
                    if (pending)
                    {
                        if (!server.ChannelTableComplete && server.LastStatusRefusal is not null) unverifiable++;
                    }
                    else if (now - call.CreatedAt > _options.DialingTimeout) unverifiable++;
                }
            }
            var ended = candidates.Concat(decided).Distinct().Count(HasEnded);
            activity?.SetTag("sessions.unverifiable", unverifiable);
            activity?.SetTag("sessions.ended", ended);
            if (serverId is null) LogSweepResult(candidates.Count, unverifiable, ended, verification);
            else LogServerSweepResult(serverId, candidates.Count, unverifiable, ended, verification);
        }
    }

    // The caller holds the call lock while inspecting participants and reading the scope/pending snapshot.
    private bool InScope(CallSession call, VerbaraServer server, string? serverId, HashSet<CallSession>? customHeld, out bool pending, out bool attached)
    {
        pending = false;
        attached = false;
        if (HasEnded(call) || (serverId is not null && !string.Equals(call.ServerId, serverId, StringComparison.Ordinal))) return false;
        return _manager is CallSessionManager engine
            ? engine.TryGetSweepState(call, server, serverId is not null, out pending, out attached)
            : customHeld!.Contains(call) && ReferenceEquals(_manager.GetById(call.SessionId), call);
    }

    private static string? SkipReason(VerbaraServer server, int candidates)
    {
        if (candidates == 0) return "no-candidate";
        var connection = server.Connection;
        if (!connection.ReportsEventActionOutcome) return "outcome-not-reported";
        if (connection.State != AmiConnectionState.Connected) return "not-connected";
        if (server.IsReconnectPending) return "reconnect-pending";
        return server.IsLoadInFlight ? "load-in-flight" : null;
    }

    private static bool HoldsParticipantChannel(VerbaraServer server, CallSession call)
    {
        foreach (var participant in call.Participants)
            if (server.Channels.GetByUniqueId(participant.UniqueId) is not null) return true;
        return false;
    }

    private static bool HoldsLinkedChannel(VerbaraServer server, CallSession call, bool attached)
    {
        // The linked index also retains departing objects for the decision oracle. Status candidacy needs only
        // objects still held in the table, including the same-id readmission check. Custom managers keep their
        // original participant-only source, without an inferred attachment oracle.
        if (attached)
        {
            var channels = server.Channels.ChannelsOfLinkedId(call.LinkedId);
            for (var i = 0; i < channels.Length; i++)
            {
                var channel = channels[i];
                if (ReferenceEquals(server.Channels.GetByUniqueId(channel.UniqueId), channel)) return true;
            }
        }
        return false;
    }

    private static bool HasEnded(CallSession call) => call.EndingDelivered
        || call.State is CallSessionState.Completed or CallSessionState.Failed or CallSessionState.TimedOut;

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Reconciliation sweep: candidates={Candidates} unverifiable={Unverifiable} ended={Ended} verification={Verification}")]
    private partial void LogSweepResult(int candidates, int unverifiable, int ended, string verification);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Reconciliation sweep: server={ServerId} candidates={Candidates} unverifiable={Unverifiable} ended={Ended} verification={Verification}")]
    private partial void LogServerSweepResult(string serverId, int candidates, int unverifiable, int ended, string verification);
}
