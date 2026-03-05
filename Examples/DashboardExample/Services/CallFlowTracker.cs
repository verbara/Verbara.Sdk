#pragma warning disable CS0618 // Legacy DialEvent still used for Asterisk < 12 backwards compat
using System.Collections.Concurrent;
using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Events;
using Asterisk.Sdk.Ami.Events.Base;
using Asterisk.Sdk.Enums;
using Microsoft.Extensions.Logging;

namespace DashboardExample.Services;

internal static partial class CallFlowLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "[CALL_FLOW] New call: call_id={CallId} server={ServerId} caller={CallerChannel}")]
    public static partial void NewCall(ILogger logger, string callId, string serverId, string? callerChannel);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[CALL_FLOW] State changed: call_id={CallId} state={State}")]
    public static partial void StateChanged(ILogger logger, string callId, CallFlowState state);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[CALL_FLOW] Completed: call_id={CallId} duration_secs={DurationSecs} cause={Cause}")]
    public static partial void Completed(ILogger logger, string callId, double durationSecs, HangupCause? cause);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[CALL_FLOW] Evicted stale: call_id={CallId}")]
    public static partial void Evicted(ILogger logger, string callId);
}

/// <summary>
/// Tracks call flows in real-time by correlating AMI events via LinkedId/UniqueId.
/// Supports all channel technologies: SIP, PJSIP, IAX2, DAHDI, Local, etc.
/// Maintains a circular buffer of recent calls for the dashboard.
/// </summary>
public sealed class CallFlowTracker
{
    private readonly ConcurrentDictionary<string, CallFlow> _calls = new();
    private readonly ConcurrentQueue<string> _completedOrder = new();
    private readonly ILogger<CallFlowTracker> _logger;
    private const int MaxCompletedCalls = 500;
    private static readonly TimeSpan CompletedRetention = TimeSpan.FromMinutes(5);

    public CallFlowTracker(ILogger<CallFlowTracker> logger) => _logger = logger;

    public IEnumerable<CallFlow> ActiveCalls =>
        _calls.Values.Where(c => c.State != CallFlowState.Completed);

    public IEnumerable<CallFlow> AllCalls => _calls.Values;

    public CallFlow? GetById(string callId) => _calls.GetValueOrDefault(callId);

    public IEnumerable<CallFlow> GetRecentCompleted(int count = 100) =>
        _calls.Values
            .Where(c => c.State == CallFlowState.Completed)
            .OrderByDescending(c => c.EndTime)
            .Take(count);

    /// <summary>Finds an active call where the given agent interface/channel is a participant.</summary>
    public CallFlow? FindActiveCallByChannel(string channel)
    {
        if (string.IsNullOrEmpty(channel)) return null;
        var shortCh = ShortChannel(channel);
        return _calls.Values.FirstOrDefault(c =>
            c.State != CallFlowState.Completed
            && (ShortChannel(c.AgentInterface) == shortCh
                || ShortChannel(c.Destination?.Channel) == shortCh
                || ShortChannel(c.Caller?.Channel) == shortCh
                || c.Participants.Any(p => p.LeftAt is null && ShortChannel(p.Channel) == shortCh)));
    }

    /// <summary>Searches calls by caller ID, agent, or queue name.</summary>
    public IEnumerable<CallFlow> Search(string? query, int limit = 100)
    {
        if (string.IsNullOrWhiteSpace(query))
            return GetRecentCompleted(limit);

        var q = query.Trim();
        return _calls.Values
            .Where(c =>
                (c.Caller?.CallerIdNum?.Contains(q, StringComparison.OrdinalIgnoreCase) == true)
                || (c.Caller?.CallerIdName?.Contains(q, StringComparison.OrdinalIgnoreCase) == true)
                || (c.Destination?.CallerIdNum?.Contains(q, StringComparison.OrdinalIgnoreCase) == true)
                || (c.AgentName?.Contains(q, StringComparison.OrdinalIgnoreCase) == true)
                || (c.AgentInterface?.Contains(q, StringComparison.OrdinalIgnoreCase) == true)
                || (c.QueueName?.Contains(q, StringComparison.OrdinalIgnoreCase) == true)
                || (c.CallId.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(c => c.StartTime)
            .Take(limit);
    }

    private static string ShortChannel(string? channel)
    {
        if (channel is null) return "";
        var dash = channel.LastIndexOf('-');
        return dash > 0 ? channel[..dash] : channel;
    }

    /// <summary>Creates an observer that feeds events into this tracker for a given server.</summary>
    public IObserver<ManagerEvent> CreateObserver(string serverId) => new CallFlowObserver(this, serverId);

    /// <summary>
    /// Resolves the call correlation ID from an AMI event.
    /// Uses Linkedid (Asterisk 12+) to group all channels of the same call.
    /// Works identically for SIP, PJSIP, IAX2, DAHDI, and Local channels.
    /// </summary>
    private static string ResolveCallId(ManagerEvent evt)
    {
        // ChannelEventBase.Linkedid (NewChannel, NewState, Hangup, Hold, Unhold, etc.)
        if (evt is ChannelEventBase ceb && ceb.Linkedid is not null)
            return ceb.Linkedid;

        // Fallback: check RawFields (case-insensitive dictionary)
        // Covers events that don't inherit from ChannelEventBase
        var linkedId = evt.RawFields?.GetValueOrDefault("Linkedid");
        if (linkedId is not null)
            return linkedId;

        // Last resort: UniqueId (creates per-channel CallFlows for Asterisk < 12)
        return evt.UniqueId ?? "";
    }

    private CallFlow GetOrCreateCall(string callId, string serverId, string? callerChannel = null)
    {
        var isNew = false;
        var call = _calls.GetOrAdd(callId, _ =>
        {
            isNew = true;
            return new CallFlow
            {
                CallId = callId,
                ServerId = serverId,
                StartTime = DateTimeOffset.UtcNow
            };
        });
        if (isNew)
            CallFlowLog.NewCall(_logger, callId, serverId, callerChannel);
        return call;
    }

    private static void AddEvent(CallFlow call, CallFlowEventType type, string? source, string? target, string? detail)
    {
        call.Events.Add(new CallFlowEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Type = type,
            Source = source,
            Target = target,
            Detail = detail
        });
    }

    private void MarkCompleted(CallFlow call, HangupCause? cause = null)
    {
        if (call.State == CallFlowState.Completed)
            return; // Already completed

        call.State = CallFlowState.Completed;
        call.EndTime = DateTimeOffset.UtcNow;
        if (cause.HasValue)
            call.HangupCause = cause.Value;

        CallFlowLog.Completed(_logger, call.CallId, call.Duration.TotalSeconds, cause);

        _completedOrder.Enqueue(call.CallId);
        EvictStaleCompleted();
    }

    private void EvictStaleCompleted()
    {
        var cutoff = DateTimeOffset.UtcNow - CompletedRetention;

        while (_completedOrder.Count > MaxCompletedCalls && _completedOrder.TryDequeue(out var oldId))
        {
            if (_calls.TryGetValue(oldId, out var old) && old.State == CallFlowState.Completed
                && old.EndTime < cutoff)
            {
                _calls.TryRemove(oldId, out _);
                CallFlowLog.Evicted(_logger, oldId);
            }
        }
    }

    /// <summary>
    /// Extracts the channel technology from a channel string.
    /// Examples: "PJSIP/2001-00000042" → "PJSIP", "SIP/2001-00000042" → "SIP",
    /// "IAX2/trunk-00000001" → "IAX2", "DAHDI/1-1" → "DAHDI", "Local/s@default-..." → "Local"
    /// </summary>
    internal static string ParseTechnology(string? channel)
    {
        if (channel is null) return "Unknown";
        var slash = channel.IndexOf('/');
        return slash > 0 ? channel[..slash] : "Unknown";
    }

    private static CallParticipant CreateParticipant(string channel, string uniqueId, string? callerIdNum, string? callerIdName)
    {
        return new CallParticipant
        {
            Channel = channel,
            UniqueId = uniqueId,
            Technology = ParseTechnology(channel),
            CallerIdNum = callerIdNum,
            CallerIdName = callerIdName,
            JoinedAt = DateTimeOffset.UtcNow
        };
    }

    private sealed class CallFlowObserver(CallFlowTracker tracker, string serverId) : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value)
        {
            switch (value)
            {
                case NewChannelEvent nce:
                    HandleNewChannel(nce);
                    break;

                case DialBeginEvent dbe:
                    HandleDialBegin(dbe);
                    break;

                case DialEndEvent dee:
                    HandleDialEnd(dee);
                    break;

                // DialStateEvent (Asterisk 13+): fires during dial progress with state changes
                case DialStateEvent dse:
                    HandleDialState(dse);
                    break;

                // Legacy DialEvent with SubEvent (Asterisk < 12)
                case DialEvent de when string.Equals(de.SubEvent, "Begin", StringComparison.OrdinalIgnoreCase):
                    HandleDialEventBegin(de);
                    break;

                case DialEvent de when string.Equals(de.SubEvent, "End", StringComparison.OrdinalIgnoreCase):
                    HandleDialEventEnd(de);
                    break;

                case NewStateEvent nse:
                    HandleNewState(nse);
                    break;

                case BridgeEnterEvent bee:
                    HandleBridgeEnter(bee);
                    break;

                case BridgeLeaveEvent ble:
                    HandleBridgeLeave(ble);
                    break;

                case HoldEvent he:
                    HandleHold(he);
                    break;

                case UnholdEvent uhe:
                    HandleUnhold(uhe);
                    break;

                case DtmfEndEvent dtmf:
                    HandleDtmf(dtmf);
                    break;

                case QueueCallerJoinEvent qcj:
                    HandleQueueJoin(qcj);
                    break;

                case AgentConnectEvent ace:
                    HandleAgentConnect(ace);
                    break;

                case HangupEvent he:
                    HandleHangup(he);
                    break;
            }
        }

        private void HandleNewChannel(NewChannelEvent nce)
        {
            var callId = ResolveCallId(nce);
            if (string.IsNullOrEmpty(callId)) return;

            var call = tracker.GetOrCreateCall(callId, serverId, nce.Channel);
            var channel = nce.Channel ?? "";
            var uniqueId = nce.UniqueId ?? "";

            // First channel is the caller
            if (call.Caller is null)
            {
                call.Caller = CreateParticipant(channel, uniqueId, nce.CallerIdNum, nce.CallerIdName);
                call.State = CallFlowState.Dialing;
            }
            else if (call.Destination is null && channel != call.Caller.Channel)
            {
                call.Destination = CreateParticipant(channel, uniqueId, nce.CallerIdNum, nce.CallerIdName);
            }

            // Track all participants
            if (!call.Participants.Any(p => p.UniqueId == uniqueId))
            {
                call.Participants.Add(CreateParticipant(channel, uniqueId, nce.CallerIdNum, nce.CallerIdName));
            }

            AddEvent(call, CallFlowEventType.NewChannel, channel, null, nce.ChannelStateDesc ?? nce.ChannelState);
        }

        private void HandleDialBegin(DialBeginEvent dbe)
        {
            var callId = dbe.Linkedid ?? ResolveCallId(dbe);
            if (string.IsNullOrEmpty(callId)) return;

            var call = tracker.GetOrCreateCall(callId, serverId);

            if (call.State < CallFlowState.Ringing)
                call.State = CallFlowState.Ringing;

            // Use typed properties (now populated by source generator)
            if (dbe.DestChannel is not null && call.Destination is null)
            {
                call.Destination = CreateParticipant(
                    dbe.DestChannel,
                    dbe.DestUniqueid ?? "",
                    dbe.DestCallerIdNum,
                    dbe.DestCallerIdName);
            }

            // Ensure destination is tracked as participant
            if (dbe.DestUniqueid is not null && !call.Participants.Any(p => p.UniqueId == dbe.DestUniqueid))
            {
                call.Participants.Add(CreateParticipant(
                    dbe.DestChannel ?? "",
                    dbe.DestUniqueid,
                    dbe.DestCallerIdNum,
                    dbe.DestCallerIdName));
            }

            AddEvent(call, CallFlowEventType.Dial, dbe.Channel, dbe.DestChannel, "Begin");
        }

        private void HandleDialEnd(DialEndEvent dee)
        {
            var callId = dee.Linkedid ?? ResolveCallId(dee);
            if (string.IsNullOrEmpty(callId)) return;

            var call = tracker._calls.GetValueOrDefault(callId);
            if (call is null) return;

            if (string.Equals(dee.DialStatus, "ANSWER", StringComparison.OrdinalIgnoreCase))
            {
                call.State = CallFlowState.Connected;
            }

            AddEvent(call, CallFlowEventType.Answer, dee.Channel, dee.DestChannel, dee.DialStatus);
        }

        private void HandleDialState(DialStateEvent dse)
        {
            var callId = dse.LinkedId ?? ResolveCallId(dse);
            if (string.IsNullOrEmpty(callId)) return;

            var call = tracker._calls.GetValueOrDefault(callId);
            if (call is null) return;

            // DialState fires for state transitions during dialing (RINGING, PROCEEDING, etc.)
            if (string.Equals(dse.DestChannelStateDesc, "Ringing", StringComparison.OrdinalIgnoreCase)
                || string.Equals(dse.DialStatus, "RINGING", StringComparison.OrdinalIgnoreCase))
            {
                if (call.State < CallFlowState.Ringing)
                    call.State = CallFlowState.Ringing;
            }

            // Update destination info if we don't have it yet
            if (dse.DestChannel is not null && call.Destination is null)
            {
                call.Destination = CreateParticipant(
                    dse.DestChannel,
                    dse.DestUniqueId ?? "",
                    dse.DestCallerIdNum,
                    dse.DestCallerIdName);
            }

            AddEvent(call, CallFlowEventType.Ring, dse.Channel, dse.DestChannel,
                dse.DestChannelStateDesc ?? dse.DialStatus);
        }

        private void HandleDialEventBegin(DialEvent de)
        {
            // Legacy event (Asterisk < 12): no Linkedid, use SrcUniqueId for correlation
            var callId = de.SrcUniqueId ?? de.UniqueId ?? "";
            if (string.IsNullOrEmpty(callId)) return;

            var call = FindCallByUniqueId(callId) ?? tracker.GetOrCreateCall(callId, serverId);

            if (call.State < CallFlowState.Ringing)
                call.State = CallFlowState.Ringing;

            if (de.DestChannel is not null && call.Destination is null)
            {
                call.Destination = CreateParticipant(
                    de.DestChannel,
                    de.DestUniqueId ?? "",
                    de.DestCallerIdNum,
                    de.DestCallerIdName);
            }

            AddEvent(call, CallFlowEventType.Dial,
                de.Channel ?? de.Src, de.DestChannel ?? de.Destination, "Begin");
        }

        private void HandleDialEventEnd(DialEvent de)
        {
            var callId = de.SrcUniqueId ?? de.UniqueId ?? "";
            if (string.IsNullOrEmpty(callId)) return;

            var call = FindCallByUniqueId(callId);
            if (call is null) return;

            if (string.Equals(de.DialStatus, "ANSWER", StringComparison.OrdinalIgnoreCase))
            {
                call.State = CallFlowState.Connected;
            }

            AddEvent(call, CallFlowEventType.Answer,
                de.Channel ?? de.Src, de.DestChannel, de.DialStatus);
        }

        private void HandleNewState(NewStateEvent nse)
        {
            var callId = ResolveCallId(nse);
            if (string.IsNullOrEmpty(callId)) return;

            var call = tracker._calls.GetValueOrDefault(callId);
            if (call is null) return;

            // Channel states are the same for SIP, PJSIP, and all other technologies:
            // 5 = Ringing, 6 = Up (connected)
            if (string.Equals(nse.ChannelState, "6", StringComparison.Ordinal)
                || string.Equals(nse.ChannelStateDesc, "Up", StringComparison.OrdinalIgnoreCase))
            {
                if (call.State < CallFlowState.Connected)
                    call.State = CallFlowState.Connected;
            }
            else if (string.Equals(nse.ChannelState, "5", StringComparison.Ordinal)
                     || string.Equals(nse.ChannelStateDesc, "Ringing", StringComparison.OrdinalIgnoreCase))
            {
                if (call.State < CallFlowState.Ringing)
                    call.State = CallFlowState.Ringing;
            }
        }

        private void HandleBridgeEnter(BridgeEnterEvent bee)
        {
            var callId = bee.LinkedId ?? ResolveCallId(bee);
            if (string.IsNullOrEmpty(callId)) return;

            var call = FindCallByUniqueId(bee.UniqueId) ?? tracker._calls.GetValueOrDefault(callId);
            if (call is null) return;

            if (call.State != CallFlowState.Hold)
                call.State = CallFlowState.Connected;

            call.BridgeId = bee.BridgeUniqueid;
            AddEvent(call, CallFlowEventType.Bridge, bee.Channel, bee.BridgeUniqueid, "Enter");
        }

        private void HandleBridgeLeave(BridgeLeaveEvent ble)
        {
            var callId = ble.LinkedId ?? ResolveCallId(ble);
            if (string.IsNullOrEmpty(callId)) return;

            var call = FindCallByUniqueId(ble.UniqueId) ?? tracker._calls.GetValueOrDefault(callId);
            if (call is null) return;

            AddEvent(call, CallFlowEventType.Bridge, ble.Channel, ble.BridgeUniqueid, "Leave");
        }

        private void HandleHold(HoldEvent he)
        {
            var callId = ResolveCallId(he);
            if (string.IsNullOrEmpty(callId)) return;

            var call = tracker._calls.GetValueOrDefault(callId);
            if (call is null) return;

            call.State = CallFlowState.Hold;
            AddEvent(call, CallFlowEventType.Hold, he.Channel, null, he.MusicClass);
        }

        private void HandleUnhold(UnholdEvent uhe)
        {
            var callId = ResolveCallId(uhe);
            if (string.IsNullOrEmpty(callId)) return;

            var call = tracker._calls.GetValueOrDefault(callId);
            if (call is null) return;

            call.State = CallFlowState.Connected;
            AddEvent(call, CallFlowEventType.Unhold, uhe.Channel, null, null);
        }

        private void HandleDtmf(DtmfEndEvent dtmf)
        {
            var callId = ResolveCallId(dtmf);
            if (string.IsNullOrEmpty(callId)) return;

            var call = tracker._calls.GetValueOrDefault(callId);
            if (call is null) return;

            var digit = dtmf.RawFields?.GetValueOrDefault("Digit")
                     ?? dtmf.RawFields?.GetValueOrDefault("DTMF");
            AddEvent(call, CallFlowEventType.Dtmf,
                dtmf.RawFields?.GetValueOrDefault("Channel"), null, digit);
        }

        private void HandleQueueJoin(QueueCallerJoinEvent qcj)
        {
            var callId = qcj.LinkedId ?? ResolveCallId(qcj);
            if (string.IsNullOrEmpty(callId)) return;

            // QueueCallerJoin can arrive before NewChannel in some edge cases
            var call = tracker._calls.GetValueOrDefault(callId)
                    ?? tracker.GetOrCreateCall(callId, serverId);

            var queueName = qcj.RawFields?.GetValueOrDefault("Queue");
            call.QueueName = queueName;
            call.State = CallFlowState.Queued;

            AddEvent(call, CallFlowEventType.QueueJoin,
                qcj.RawFields?.GetValueOrDefault("Channel"), queueName,
                $"Position {qcj.Position}");
        }

        private void HandleAgentConnect(AgentConnectEvent ace)
        {
            var callId = ace.LinkedId ?? ResolveCallId(ace);
            if (string.IsNullOrEmpty(callId)) return;

            var call = FindCallByUniqueId(ace.UniqueId) ?? tracker._calls.GetValueOrDefault(callId);
            if (call is null) return;

            var memberName = ace.RawFields?.GetValueOrDefault("MemberName");
            call.AgentName = memberName ?? ace.Agent;
            call.AgentInterface = ace.Interface;
            call.State = CallFlowState.Connected;

            if (ace.DestChannel is not null && call.Destination is null)
            {
                call.Destination = CreateParticipant(
                    ace.DestChannel,
                    ace.DestUniqueId ?? "",
                    ace.DestCallerIdNum,
                    ace.DestCallerIdName);
            }

            // Track agent as participant
            if (ace.DestUniqueId is not null && !call.Participants.Any(p => p.UniqueId == ace.DestUniqueId))
            {
                call.Participants.Add(CreateParticipant(
                    ace.DestChannel ?? ace.Interface ?? "",
                    ace.DestUniqueId,
                    ace.DestCallerIdNum,
                    ace.DestCallerIdName));
            }

            AddEvent(call, CallFlowEventType.AgentConnect,
                ace.Interface, ace.Channel, memberName ?? ace.Agent);
        }

        private void HandleHangup(HangupEvent he)
        {
            var callId = ResolveCallId(he);
            if (string.IsNullOrEmpty(callId)) return;

            var call = tracker._calls.GetValueOrDefault(callId);
            if (call is null) return;

            // Mark participant as left
            var participant = call.Participants.FirstOrDefault(p => p.UniqueId == he.UniqueId);
            if (participant is not null)
                participant.LeftAt = DateTimeOffset.UtcNow;

            var cause = he.Cause is not null && Enum.IsDefined(typeof(HangupCause), he.Cause.Value)
                ? (HangupCause)he.Cause.Value
                : HangupCause.NormalClearing;

            AddEvent(call, CallFlowEventType.Hangup, he.Channel, null, cause.ToString());

            // Complete only when ALL participants have hung up
            // This handles multi-party calls (bridges, queues) correctly
            var allHungUp = call.Participants.Count > 0
                && call.Participants.All(p => p.LeftAt.HasValue);
            if (allHungUp)
            {
                tracker.MarkCompleted(call, cause);
            }
        }

        private CallFlow? FindCallByUniqueId(string? uniqueId)
        {
            if (uniqueId is null) return null;
            return tracker._calls.Values.FirstOrDefault(c =>
                c.Caller?.UniqueId == uniqueId
                || c.Destination?.UniqueId == uniqueId
                || c.Participants.Any(p => p.UniqueId == uniqueId));
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}

/// <summary>Represents a tracked call flow with all participants and events.</summary>
public sealed class CallFlow
{
    public string CallId { get; init; } = string.Empty;
    public string ServerId { get; init; } = string.Empty;
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset? EndTime { get; set; }
    public CallFlowState State { get; set; }
    public CallParticipant? Caller { get; set; }
    public CallParticipant? Destination { get; set; }
    public string? QueueName { get; set; }
    public string? AgentName { get; set; }
    public string? AgentInterface { get; set; }
    public string? BridgeId { get; set; }
    public HangupCause? HangupCause { get; set; }
    public List<CallFlowEvent> Events { get; } = [];
    public List<CallParticipant> Participants { get; } = [];

    public TimeSpan Duration => (EndTime ?? DateTimeOffset.UtcNow) - StartTime;
}

/// <summary>Represents a participant in a call flow.</summary>
public sealed class CallParticipant
{
    public string Channel { get; init; } = string.Empty;
    public string UniqueId { get; init; } = string.Empty;
    /// <summary>Channel technology: "SIP", "PJSIP", "IAX2", "DAHDI", "Local", etc.</summary>
    public string Technology { get; init; } = "Unknown";
    public string? CallerIdNum { get; set; }
    public string? CallerIdName { get; set; }
    public DateTimeOffset? JoinedAt { get; set; }
    public DateTimeOffset? LeftAt { get; set; }
}

/// <summary>A single event in the call flow timeline.</summary>
public sealed class CallFlowEvent
{
    public DateTimeOffset Timestamp { get; init; }
    public CallFlowEventType Type { get; init; }
    public string? Source { get; init; }
    public string? Target { get; init; }
    public string? Detail { get; init; }
}

public enum CallFlowState
{
    Dialing,
    Ringing,
    Queued,
    Connected,
    Hold,
    Completed
}

public enum CallFlowEventType
{
    NewChannel,
    Dial,
    Ring,
    Answer,
    Bridge,
    Hold,
    Unhold,
    Dtmf,
    QueueJoin,
    AgentConnect,
    Transfer,
    Hangup
}
