using Microsoft.Extensions.Logging;
using Verbara.Sdk.Live.Channels;
using Verbara.Sdk.Sessions.Diagnostics;
using Verbara.Sdk.Sessions.Internal;

namespace Verbara.Sdk.Sessions.Manager;

public sealed partial class CallSessionManager
{
    /// <summary>Admits the calls already held by the instance attached under <paramref name="serverId"/>.</summary>
    /// <remarks>
    /// Call after attaching and registering reconstructed calls. Only an incomplete channel table requests a Status
    /// snapshot. Existing channel owners are preserved, and repeating the admission adds nothing. A refused snapshot
    /// leaves calls unverified until a later completed snapshot; release them explicitly if Status stays refused.
    /// The snapshot wait and each visit are bound to this attachment; detaching stops the admission normally.
    /// The walk always runs on the thread pool. A session-event subscriber must return without waiting for this
    /// operation: notifications hold a call lock, while the walk takes channel locks before call locks.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The id has no attached instance.</exception>
    public ValueTask AdmitExistingCalls(string serverId, CancellationToken cancellationToken = default)
    {
        if (!_serverSubs.TryGetValue(serverId, out var attachment))
            throw new InvalidOperationException($"Server '{serverId}' is not attached.");
        var generation = attachment.Generation;
        return new ValueTask(Task.Run(() => AdmitExistingCallsCoreAsync(serverId, attachment, generation, cancellationToken), CancellationToken.None));
    }

    private async Task AdmitExistingCallsCoreAsync(string serverId, ServerSubscriptions attachment, long generation,
        CancellationToken cancellationToken)
    {
        var admitted = new HashSet<string>(StringComparer.Ordinal);
        var stopped = false;
        bool Current() => attachment.Generation == generation
            && _serverSubs.TryGetValue(serverId, out var current) && ReferenceEquals(current, attachment);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Current()) throw new AdmissionStoppedException();
            if (!attachment.Server.ChannelTableComplete)
            {
                Task request;
                if (!attachment.Gate.TryEnter()) throw new AdmissionStoppedException();
                try
                {
                    if (!Current()) throw new AdmissionStoppedException();
                    request = attachment.Server.RequestChannelSnapshotAsync(cancellationToken).AsTask();
                }
                finally { attachment.Gate.Exit(); }
                // A detached or cancelled waiter must still observe a later failure of the already-sent read.
                _ = request.ContinueWith(static completed => { _ = completed.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                // Gate entries change ThreadStatic engine depth: none survives this await.
                var finished = await Task.WhenAny(request, attachment.Detached).WaitAsync(cancellationToken).ConfigureAwait(false);
                if (!attachment.Gate.TryEnter()) throw new AdmissionStoppedException();
                try
                {
                    if (!Current() || ReferenceEquals(finished, attachment.Detached)) throw new AdmissionStoppedException();
                }
                finally { attachment.Gate.Exit(); }
                if (!Current() || ReferenceEquals(finished, attachment.Detached))
                {
                    // A read already sent belongs to the server. Observe a later failure without keeping this
                    // admission waiting for a detached instance, or cancelling another consumer's read.
                    throw new AdmissionStoppedException();
                }
                await request.ConfigureAwait(false);
            }
            if (!Current()) throw new AdmissionStoppedException();
            var index = IndexOf(serverId);
            Step(HandlerStep.AdmissionWalkStarted, serverId, string.Empty);
            attachment.Server.Channels.VisitHeld(channel =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Step(HandlerStep.AdmissionVisit, serverId, channel.UniqueId);
                if (!attachment.Gate.TryEnter()) throw new AdmissionStoppedException();
                try
                {
                    if (!Current()) throw new AdmissionStoppedException();
                    EvictStaleCompleted();
                    ReportArrivalCollision(serverId, channel.UniqueId,
                        string.IsNullOrEmpty(channel.LinkedId) ? channel.UniqueId : channel.LinkedId);
                    var changed = AdmitHeldChannel(channel, serverId, index, attachment, Current);
                    if (changed is not null) admitted.Add(changed.SessionId);
                }
                finally { attachment.Gate.Exit(); }
            });
            if (!attachment.Gate.TryEnter()) throw new AdmissionStoppedException();
            try
            {
                if (!Current()) throw new AdmissionStoppedException();
                Step(HandlerStep.AdmissionClosing, serverId, string.Empty);
                DecideCompleteTable(serverId, attachment);
            }
            finally { attachment.Gate.Exit(); }
        }
        catch (AdmissionStoppedException) { stopped = true; }
        finally { LogAdmissionCompleted(serverId, admitted.Count, stopped); }
    }

    // VisitHeld owns the channel lock. Each attempt below owns at most one call lock; a failed claim drops that
    // lock before retrying another owner. Registration need not take the channel lock, so TryAdd is the arbiter.
    private CallSession? AdmitHeldChannel(AsteriskChannel channel, string serverId, ServerIndex index,
        ServerSubscriptions attachment, Func<bool> current)
    {
        var linkedId = string.IsNullOrEmpty(channel.LinkedId) ? channel.UniqueId : channel.LinkedId;
        while (true)
        {
            if (!current()) throw new AdmissionStoppedException();
            if (index.ByChannelId.TryGetValue(channel.UniqueId, out var owner))
            {
                lock (owner.SyncRoot)
                {
                    if (!current()) throw new AdmissionStoppedException();
                    if (!index.ByChannelId.TryGetValue(channel.UniqueId, out var confirmed) || !ReferenceEquals(confirmed, owner))
                        continue;
                    if (IsHeld(owner) && !HasEnded(owner) && !owner.EndingDelivered)
                    {
                        ApplyPendingDemotion(owner, attachment);
                        if (FindPresentParticipant(owner, channel.UniqueId) is not null) return null;
                    }
                    index.ByChannelId.TryRemove(new KeyValuePair<string, CallSession>(channel.UniqueId, owner));
                }
                continue;
            }
            Step(HandlerStep.AdmissionNoOwner, serverId, channel.UniqueId);
            var found = index.ByLinkedId.TryGetValue(linkedId, out var target);
            if (found)
            {
                var joined = false;
                var changed = false;
                lock (target!.SyncRoot)
                {
                    if (!current()) throw new AdmissionStoppedException();
                    if (IsHeld(target) && !HasEnded(target) && !target.EndingDelivered)
                    {
                        ApplyPendingDemotion(target, attachment);
                        Step(HandlerStep.AdmissionClaimReady, serverId, channel.UniqueId);
                        if (!current()) throw new AdmissionStoppedException();
                        if (!index.ByChannelId.TryAdd(channel.UniqueId, target)) continue;
                        joined = true;
                        if (FindPresentParticipant(target, channel.UniqueId) is null)
                        {
                            AddAdmissionParticipant(target, channel);
                            changed = true;
                        }
                    }
                }
                if (joined)
                {
                    if (changed) _ = PersistAsync(target);
                    return changed ? target : null;
                }
            }

            var candidate = NewAdmissionSession(channel, serverId, linkedId, attachment);
            lock (candidate.SyncRoot)
            {
                if (!current()) throw new AdmissionStoppedException();
                if (!_sessions.TryAdd(candidate.SessionId, candidate)) continue;
                try
                {
                    Step(HandlerStep.AdmissionCandidateHeld, serverId, channel.UniqueId);
                    if (!current() || !IsHeld(candidate)) throw new AdmissionStoppedException();
                    if (!index.ByChannelId.TryAdd(channel.UniqueId, candidate)) continue;
                    if (!current()) throw new AdmissionStoppedException();
                    var published = found
                        ? index.ByLinkedId.TryUpdate(linkedId, candidate, target!)
                        : index.ByLinkedId.TryAdd(linkedId, candidate);
                    if (!published) continue;
                    // Publication already owns this channel. Never overwrite its owner after a notification.
                    AddAdmissionParticipant(candidate, channel, joined: false);
                    SessionMetrics.SessionsCreated.Add(1);
                    Publish(new CallStartedEvent(candidate.SessionId, serverId, DateTimeOffset.UtcNow,
                        candidate.Direction, channel.CallerIdNum));
                }
                finally
                {
                    if (!index.ByLinkedId.TryGetValue(linkedId, out var published) || !ReferenceEquals(published, candidate))
                    {
                        index.ByChannelId.TryRemove(new KeyValuePair<string, CallSession>(channel.UniqueId, candidate));
                        _sessions.TryRemove(new KeyValuePair<string, CallSession>(candidate.SessionId, candidate));
                    }
                }
            }
            _ = PersistAsync(candidate);
            return candidate;
        }
    }

    private CallSession NewAdmissionSession(AsteriskChannel channel, string serverId, string linkedId, ServerSubscriptions attachment)
    {
        var call = new CallSession(Guid.NewGuid().ToString("N"), linkedId, serverId,
            _correlator.InferDirection(channel.Context, channel.Extension))
        {
            Unverified = true,
            AttachmentGeneration = attachment.Generation,
            Context = channel.Context,
            Extension = channel.Extension
        };
        call.SetMetadata(OriginKey, OriginReload);
        call.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow, CallSessionEventType.Created, channel.Name, null, null));
        if (ReportedSessionState(channel.State) is { } state)
        {
            call.OpenInReportedState(state);
            call.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                state is CallSessionState.Connected ? CallSessionEventType.Connected : CallSessionEventType.Ringing,
                channel.Name, null, OriginReload));
        }
        return call;
    }

    private static void AddAdmissionParticipant(CallSession call, AsteriskChannel channel, bool joined = true)
    {
        call.SetParticipantMark(channel.UniqueId, channel.AdmissionMark);
        var role = SessionCorrelator.InferRole(channel.Name, call.Participants.Count);
        call.AddParticipant(new SessionParticipant
        {
            UniqueId = channel.UniqueId, Channel = channel.Name,
            Technology = SessionCorrelator.ExtractTechnology(channel.Name), Role = role,
            CallerIdNum = channel.CallerIdNum, CallerIdName = channel.CallerIdName, JoinedAt = DateTimeOffset.UtcNow
        });
        if (joined)
            call.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow, CallSessionEventType.ParticipantJoined,
                channel.Name, null, role.ToString()));
    }

    [LoggerMessage(EventId = 15, Level = LogLevel.Debug,
        Message = "Existing-call admission for server {ServerId} admitted {Calls} calls; stopped because its attachment changed: {Stopped}")]
    private partial void LogAdmissionCompleted(string serverId, int calls, bool stopped);

    private sealed class AdmissionStoppedException : Exception { }
}
