using System.Collections.Concurrent;
using System.Reactive.Subjects;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Live.Agents;
using Asterisk.Sdk.Live.Bridges;
using Asterisk.Sdk.Live.Channels;
using Asterisk.Sdk.Live.Queues;
using Asterisk.Sdk.Live.Server;
using Asterisk.Sdk.Sessions.Diagnostics;
using Asterisk.Sdk.Sessions.Extensions;
using Asterisk.Sdk.Sessions.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.Sessions.Manager;

public sealed partial class CallSessionManager : ICallSessionManager
{
    private readonly ConcurrentDictionary<string, CallSession> _sessions = new();
    private readonly ConcurrentDictionary<string, CallSession> _byLinkedId = new();
    private readonly ConcurrentDictionary<string, CallSession> _byChannelId = new();
    private readonly ConcurrentDictionary<string, string> _bridgeToSession = new();
    private readonly ConcurrentQueue<string> _completedOrder = new();
    private readonly ConcurrentDictionary<string, ServerSubscriptions> _serverSubs = new();
    private readonly Subject<SessionDomainEvent> _events = new();
    private readonly SessionCorrelator _correlator;
    private readonly SessionOptions _options;
    private readonly SessionStoreBase _store;
    private readonly ILogger<CallSessionManager> _logger;

    public CallSessionManager(
        IOptions<SessionOptions> options,
        ILogger<CallSessionManager> logger,
        SessionStoreBase store)
    {
        _options = options.Value;
        _logger = logger;
        _store = store;
        _correlator = new SessionCorrelator(_options);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to persist session {SessionId}")]
    private partial void LogPersistError(Exception ex, string sessionId);

    private async Task PersistAsync(CallSession session)
    {
        try
        {
            await _store.SaveAsync(session, CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogPersistError(ex, session.SessionId);
        }
    }

    public IObservable<SessionDomainEvent> Events => _events;

    public IEnumerable<CallSession> ActiveSessions => _sessions.Values
        .Where(s => s.State is not CallSessionState.Completed
            and not CallSessionState.Failed
            and not CallSessionState.TimedOut);

    public CallSession? GetById(string sessionId) => _sessions.GetValueOrDefault(sessionId);
    public CallSession? GetByLinkedId(string linkedId) => _byLinkedId.GetValueOrDefault(linkedId);
    public CallSession? GetByChannelId(string uniqueId) => _byChannelId.GetValueOrDefault(uniqueId);

    public CallSession? GetByBridgeId(string bridgeId) =>
        _bridgeToSession.TryGetValue(bridgeId, out var sessionId)
            ? _sessions.GetValueOrDefault(sessionId)
            : null;

    public IEnumerable<CallSession> GetRecentCompleted(int count = 100) =>
        _sessions.Values
            .Where(s => s.State is CallSessionState.Completed or CallSessionState.Failed or CallSessionState.TimedOut)
            .OrderByDescending(s => s.CompletedAt)
            .Take(count);

    public void AttachToServer(AsteriskServer server, string serverId)
    {
        // Create typed delegates so we can -= unsubscribe later
        Action<AsteriskChannel> onAdded = ch => OnChannelAdded(ch, serverId);
        Action<AsteriskChannel> onRemoved = OnChannelRemoved;
        Action<AsteriskChannel> onStateChanged = OnChannelStateChanged;
        Action<AsteriskChannel> onDialBegin = OnChannelDialBegin;
        Action<AsteriskChannel> onDialEnd = OnChannelDialEnd;
        Action<AsteriskChannel> onHeld = OnChannelHeld;
        Action<AsteriskChannel> onUnheld = OnChannelUnheld;
        Action<AsteriskBridge, string> onBridgeEntered = OnBridgeChannelEntered;
        Action<AsteriskBridge> onBridgeDestroyed = OnBridgeDestroyed;
        Action<BridgeTransferInfo> onTransfer = OnTransfer;
        Action<string, AsteriskQueueEntry> onCallerJoined = OnQueueCallerJoined;
        Action<string, string?, string?> onAgentConnected = OnAgentConnected;

        server.Channels.ChannelAdded += onAdded;
        server.Channels.ChannelRemoved += onRemoved;
        server.Channels.ChannelStateChanged += onStateChanged;
        server.Channels.ChannelDialBegin += onDialBegin;
        server.Channels.ChannelDialEnd += onDialEnd;
        server.Channels.ChannelHeld += onHeld;
        server.Channels.ChannelUnheld += onUnheld;
        server.Bridges.ChannelEntered += onBridgeEntered;
        server.Bridges.BridgeDestroyed += onBridgeDestroyed;
        server.Bridges.TransferOccurred += onTransfer;
        server.Queues.CallerJoined += onCallerJoined;
        server.Agents.AgentConnected += onAgentConnected;

        _serverSubs[serverId] = new ServerSubscriptions(server,
            onAdded, onRemoved, onStateChanged, onDialBegin, onDialEnd,
            onHeld, onUnheld, onBridgeEntered, onBridgeDestroyed, onTransfer, onCallerJoined,
            onAgentConnected);
    }

    public void DetachFromServer(string serverId)
    {
        if (_serverSubs.TryRemove(serverId, out var subs))
            subs.Detach();
    }

    // --- Event Handlers ---

    private void OnChannelAdded(AsteriskChannel channel, string serverId)
    {
        var linkedId = channel.LinkedId;
        if (string.IsNullOrEmpty(linkedId)) linkedId = channel.UniqueId;

        if (_byLinkedId.TryGetValue(linkedId, out var existing))
        {
            // Add participant to existing session
            lock (existing.SyncRoot)
            {
                var role = SessionCorrelator.InferRole(channel.Name, existing.Participants.Count);
                existing.AddParticipant(new SessionParticipant
                {
                    UniqueId = channel.UniqueId,
                    Channel = channel.Name,
                    Technology = SessionCorrelator.ExtractTechnology(channel.Name),
                    Role = role,
                    CallerIdNum = channel.CallerIdNum,
                    CallerIdName = channel.CallerIdName,
                    JoinedAt = DateTimeOffset.UtcNow
                });
                existing.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                    CallSessionEventType.ParticipantJoined, channel.Name, null, role.ToString()));
            }
            _byChannelId[channel.UniqueId] = existing;
            _ = PersistAsync(existing);
            return;
        }

        // Create new session
        var direction = _correlator.InferDirection(channel.Context, channel.Extension);
        var session = new CallSession(Guid.NewGuid().ToString("N"), linkedId, serverId, direction);

        session.Context = channel.Context;
        session.Extension = channel.Extension;

        var callerRole = SessionCorrelator.InferRole(channel.Name, 0);
        session.AddParticipant(new SessionParticipant
        {
            UniqueId = channel.UniqueId,
            Channel = channel.Name,
            Technology = SessionCorrelator.ExtractTechnology(channel.Name),
            Role = callerRole,
            CallerIdNum = channel.CallerIdNum,
            CallerIdName = channel.CallerIdName,
            JoinedAt = DateTimeOffset.UtcNow
        });
        session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
            CallSessionEventType.Created, channel.Name, null, null));

        _sessions[session.SessionId] = session;
        _byLinkedId[linkedId] = session;
        _byChannelId[channel.UniqueId] = session;
        SessionMetrics.SessionsCreated.Add(1);

        _events.OnNext(new CallStartedEvent(session.SessionId, serverId,
            DateTimeOffset.UtcNow, direction, channel.CallerIdNum));

        _ = PersistAsync(session);
    }

    private void OnChannelRemoved(AsteriskChannel channel)
    {
        if (!_byChannelId.TryGetValue(channel.UniqueId, out var session)) return;

        lock (session.SyncRoot)
        {
            var participant = session.Participants.FirstOrDefault(p => p.UniqueId == channel.UniqueId);
            if (participant is not null)
            {
                participant.LeftAt = DateTimeOffset.UtcNow;
                participant.HangupCause = channel.HangupCause;
            }

            session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                CallSessionEventType.ParticipantLeft, channel.Name, null, channel.HangupCause.ToString()));

            // Check if all participants have left
            if (session.Participants.All(p => p.LeftAt.HasValue))
            {
                session.HangupCause = channel.HangupCause;
                var targetState = channel.HangupCause == HangupCause.NormalClearing
                    ? CallSessionState.Completed
                    : CallSessionState.Failed;

                // Try the natural progression if needed
                if (session.State == CallSessionState.Created)
                {
                    session.TryTransition(CallSessionState.Failed);
                }
                else if (!session.TryTransition(targetState))
                {
                    session.TryTransition(CallSessionState.Failed);
                }

                OnSessionCompleted(session);
            }
            else
            {
                _ = PersistAsync(session);
            }
        }

        _byChannelId.TryRemove(channel.UniqueId, out _);
    }

    private void OnChannelStateChanged(AsteriskChannel channel)
    {
        if (!_byChannelId.TryGetValue(channel.UniqueId, out var session)) return;

        lock (session.SyncRoot)
        {
            var changed = false;
            switch (channel.State)
            {
                case ChannelState.Ringing or ChannelState.Ring:
                    if (session.TryTransition(CallSessionState.Ringing))
                    {
                        session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                            CallSessionEventType.Ringing, channel.Name, null, null));
                        changed = true;
                    }
                    break;

                case ChannelState.Up:
                    if (session.TryTransition(CallSessionState.Connected))
                    {
                        session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                            CallSessionEventType.Connected, channel.Name, null, null));
                        changed = true;
                    }
                    break;
            }

            if (changed)
                _ = PersistAsync(session);
        }
    }

    private void OnChannelHeld(AsteriskChannel channel)
    {
        if (!_byChannelId.TryGetValue(channel.UniqueId, out var session)) return;

        lock (session.SyncRoot)
        {
            if (session.TryTransition(CallSessionState.OnHold))
            {
                session.StartHold();
                session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                    CallSessionEventType.Hold, channel.Name, null, channel.HoldMusicClass));
                _events.OnNext(new CallHeldEvent(session.SessionId, session.ServerId, DateTimeOffset.UtcNow));
                _ = PersistAsync(session);
            }
        }
    }

    private void OnChannelUnheld(AsteriskChannel channel)
    {
        if (!_byChannelId.TryGetValue(channel.UniqueId, out var session)) return;

        lock (session.SyncRoot)
        {
            if (session.TryTransition(CallSessionState.Connected))
            {
                session.EndHold();
                session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                    CallSessionEventType.Unhold, channel.Name, null, null));
                _events.OnNext(new CallResumedEvent(session.SessionId, session.ServerId, DateTimeOffset.UtcNow));
                _ = PersistAsync(session);
            }
        }
    }

    private void OnBridgeChannelEntered(AsteriskBridge bridge, string uniqueId)
    {
        if (!_byChannelId.TryGetValue(uniqueId, out var session)) return;

        lock (session.SyncRoot)
        {
            session.BridgeId = bridge.BridgeUniqueid;
            _bridgeToSession[bridge.BridgeUniqueid] = session.SessionId;

            if (session.TryTransition(CallSessionState.Connected))
            {
                session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                    CallSessionEventType.Connected, null, null, $"bridge:{bridge.BridgeUniqueid}"));

                var waitTime = session.WaitTime ?? TimeSpan.Zero;
                _events.OnNext(new CallConnectedEvent(session.SessionId, session.ServerId,
                    DateTimeOffset.UtcNow, session.AgentId, session.QueueName, waitTime));

                if (waitTime > TimeSpan.Zero)
                    SessionMetrics.WaitTimeMs.Record(waitTime.TotalMilliseconds);

                _ = PersistAsync(session);
            }
        }
    }

    private void OnTransfer(BridgeTransferInfo info)
    {
        var session = GetByBridgeId(info.BridgeId);
        if (session is null) return;

        lock (session.SyncRoot)
        {
            if (session.TryTransition(CallSessionState.Transferring))
            {
                session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                    CallSessionEventType.Transfer, null, info.TargetChannel, info.TransferType));
                _events.OnNext(new CallTransferredEvent(session.SessionId, session.ServerId,
                    DateTimeOffset.UtcNow, info.TransferType, info.TargetChannel));
                _ = PersistAsync(session);
            }
        }
    }

    private void OnQueueCallerJoined(string queueName, AsteriskQueueEntry entry)
    {
        // Find session by channel name matching
        var session = _byChannelId.Values.FirstOrDefault(s =>
            s.Participants.Any(p => p.Channel == entry.Channel));
        if (session is null) return;

        lock (session.SyncRoot)
        {
            session.QueueName = queueName;
            session.TryTransition(CallSessionState.Queued);
            session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                CallSessionEventType.QueueJoined, entry.Channel, null, queueName));
        }

        _events.OnNext(new CallQueuedEvent(session.SessionId, session.ServerId,
            DateTimeOffset.UtcNow, queueName, entry.Position));
        _ = PersistAsync(session);
    }

    private void OnSessionCompleted(CallSession session)
    {
        _completedOrder.Enqueue(session.SessionId);

        // Record metrics
        SessionMetrics.DurationMs.Record(session.Duration.TotalMilliseconds);
        if (session.TalkTime.HasValue)
            SessionMetrics.TalkTimeMs.Record(session.TalkTime.Value.TotalMilliseconds);
        if (session.HoldTime > TimeSpan.Zero)
            SessionMetrics.HoldTimeMs.Record(session.HoldTime.TotalMilliseconds);

        switch (session.State)
        {
            case CallSessionState.Completed:
                SessionMetrics.SessionsCompleted.Add(1);
                break;
            case CallSessionState.Failed:
                SessionMetrics.SessionsFailed.Add(1);
                break;
            case CallSessionState.TimedOut:
                SessionMetrics.SessionsTimedOut.Add(1);
                break;
        }

        _events.OnNext(new CallEndedEvent(session.SessionId, session.ServerId,
            DateTimeOffset.UtcNow, session.HangupCause, session.Duration, session.TalkTime));

        _ = PersistAsync(session);
        EvictStaleCompleted();
    }

    private void EvictStaleCompleted()
    {
        var cutoff = DateTimeOffset.UtcNow - _options.CompletedRetention;
        while (_completedOrder.TryPeek(out var oldId) &&
               _sessions.TryGetValue(oldId, out var old) &&
               old.CompletedAt < cutoff)
        {
            _completedOrder.TryDequeue(out _);
            _sessions.TryRemove(oldId, out _);
            _byLinkedId.TryRemove(old.LinkedId, out _);
        }
    }

    public bool RegisterReconstructedSession(CallSession session)
    {
        if (!_byLinkedId.TryAdd(session.LinkedId, session))
            return false;

        _sessions.TryAdd(session.SessionId, session);

        foreach (var participant in session.Participants)
            _byChannelId.TryAdd(participant.UniqueId, session);

        if (session.BridgeId is not null)
            _bridgeToSession.TryAdd(session.BridgeId, session.SessionId);

        _ = PersistAsync(session);

        return true;
    }

    public ValueTask DisposeAsync()
    {
        foreach (var serverId in _serverSubs.Keys.ToArray())
            DetachFromServer(serverId);
        _events.OnCompleted();
        _events.Dispose();
        return ValueTask.CompletedTask;
    }

    // --- Agent event handlers ---

    private void OnAgentConnected(string agentId, string? linkedId, string? interface_)
    {
        if (linkedId is null || !_byLinkedId.TryGetValue(linkedId, out var session))
            return;

        lock (session.SyncRoot)
        {
            session.AgentId = agentId;
            session.AgentInterface = interface_;
            session.AddEvent(new CallSessionEvent(
                DateTimeOffset.UtcNow,
                CallSessionEventType.AgentConnected,
                interface_,
                null,
                $"Agent {agentId}"));

            if (session.TryTransition(CallSessionState.Connected))
            {
                session.ConnectedAt ??= DateTimeOffset.UtcNow;
            }
        }

        _events.OnNext(new CallConnectedEvent(
            session.SessionId, session.ServerId, DateTimeOffset.UtcNow,
            agentId, session.QueueName, session.WaitTime ?? TimeSpan.Zero));

        _ = PersistAsync(session);
    }

    // --- Dial event handlers ---

    private void OnChannelDialBegin(AsteriskChannel channel)
    {
        if (!_byChannelId.TryGetValue(channel.UniqueId, out var session)) return;

        lock (session.SyncRoot)
        {
            if (session.TryTransition(CallSessionState.Dialing))
            {
                session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                    CallSessionEventType.Dialing, channel.Name, channel.DialedChannel, null));
                _ = PersistAsync(session);
            }
        }
    }

    private void OnChannelDialEnd(AsteriskChannel channel)
    {
        if (!_byChannelId.TryGetValue(channel.UniqueId, out var session)) return;

        lock (session.SyncRoot)
        {
            session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                CallSessionEventType.Connected, channel.Name, null, $"dial:{channel.DialStatus}"));

            if (channel.DialStatus == "ANSWER")
                session.TryTransition(CallSessionState.Connected);
        }
        _ = PersistAsync(session);
    }

    // --- Bridge destroyed handler ---

    private void OnBridgeDestroyed(AsteriskBridge bridge)
    {
        if (_bridgeToSession.TryRemove(bridge.BridgeUniqueid, out var sessionId) &&
            _sessions.TryGetValue(sessionId, out var session))
        {
            lock (session.SyncRoot)
            {
                if (session.BridgeId == bridge.BridgeUniqueid)
                    session.BridgeId = null;
            }
        }
    }

    // Subscription management: stores typed delegates for proper -= unsubscribe
    private sealed class ServerSubscriptions(
        AsteriskServer server,
        Action<AsteriskChannel> onAdded,
        Action<AsteriskChannel> onRemoved,
        Action<AsteriskChannel> onStateChanged,
        Action<AsteriskChannel> onDialBegin,
        Action<AsteriskChannel> onDialEnd,
        Action<AsteriskChannel> onHeld,
        Action<AsteriskChannel> onUnheld,
        Action<AsteriskBridge, string> onBridgeEntered,
        Action<AsteriskBridge> onBridgeDestroyed,
        Action<BridgeTransferInfo> onTransfer,
        Action<string, AsteriskQueueEntry> onCallerJoined,
        Action<string, string?, string?> onAgentConnected)
    {
        public void Detach()
        {
            server.Channels.ChannelAdded -= onAdded;
            server.Channels.ChannelRemoved -= onRemoved;
            server.Channels.ChannelStateChanged -= onStateChanged;
            server.Channels.ChannelDialBegin -= onDialBegin;
            server.Channels.ChannelDialEnd -= onDialEnd;
            server.Channels.ChannelHeld -= onHeld;
            server.Channels.ChannelUnheld -= onUnheld;
            server.Bridges.ChannelEntered -= onBridgeEntered;
            server.Bridges.BridgeDestroyed -= onBridgeDestroyed;
            server.Bridges.TransferOccurred -= onTransfer;
            server.Queues.CallerJoined -= onCallerJoined;
            server.Agents.AgentConnected -= onAgentConnected;
        }
    }
}
