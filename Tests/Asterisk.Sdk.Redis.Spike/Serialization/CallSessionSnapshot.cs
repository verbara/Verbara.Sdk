using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Sessions;

namespace Asterisk.Sdk.Redis.Spike.Serialization;

public sealed class CallSessionSnapshot
{
    // Identity
    public required string SessionId { get; init; }
    public required string LinkedId { get; init; }
    public required string ServerId { get; init; }

    // State
    public CallSessionState State { get; init; }
    public CallDirection Direction { get; init; }

    // Dialplan context
    public string? Context { get; init; }
    public string? Extension { get; init; }

    // Call context
    public string? QueueName { get; init; }
    public string? AgentId { get; init; }
    public string? AgentInterface { get; init; }
    public string? BridgeId { get; init; }
    public HangupCause? HangupCause { get; init; }

    // Convenience — captured at snapshot time from Participants[0]
    public string? CallerIdNum { get; init; }
    public string? CallerIdName { get; init; }

    // Timestamps
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? DialingAt { get; init; }
    public DateTimeOffset? RingingAt { get; init; }
    public DateTimeOffset? QueuedAt { get; init; }
    public DateTimeOffset? ConnectedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }

    // Hold tracking (internal fields in CallSession)
    public DateTimeOffset? HoldStartedAt { get; init; }
    public TimeSpan AccumulatedHoldTime { get; init; }

    // Collections
    public List<SessionParticipant> Participants { get; init; } = [];
    public List<CallSessionEvent> Events { get; init; } = [];
    public Dictionary<string, string> Metadata { get; init; } = [];

    /// <summary>Captures all state from a CallSession.</summary>
    public static CallSessionSnapshot FromSession(CallSession session) => new()
    {
        SessionId = session.SessionId,
        LinkedId = session.LinkedId,
        ServerId = session.ServerId,
        State = session.State,
        Direction = session.Direction,
        Context = session.Context,
        Extension = session.Extension,
        QueueName = session.QueueName,
        AgentId = session.AgentId,
        AgentInterface = session.AgentInterface,
        BridgeId = session.BridgeId,
        HangupCause = session.HangupCause,
        CallerIdNum = session.CallerIdNum,
        CallerIdName = session.CallerIdName,
        CreatedAt = session.CreatedAt,
        DialingAt = session.DialingAt,
        RingingAt = session.RingingAt,
        QueuedAt = session.QueuedAt,
        ConnectedAt = session.ConnectedAt,
        CompletedAt = session.CompletedAt,
        HoldStartedAt = session._holdStartedAt,
        AccumulatedHoldTime = session._accumulatedHoldTime,
        Participants = [.. session.Participants],
        Events = [.. session.Events],
        Metadata = session.Metadata.ToDictionary(kv => kv.Key, kv => kv.Value),
    };

    /// <summary>Reconstructs a CallSession from this snapshot.</summary>
    public CallSession ToSession()
    {
        var session = new CallSession(SessionId, LinkedId, ServerId, Direction)
        {
            CreatedAt = CreatedAt,
        };

        session.State = State;
        session.Context = Context;
        session.Extension = Extension;
        session.QueueName = QueueName;
        session.AgentId = AgentId;
        session.AgentInterface = AgentInterface;
        session.BridgeId = BridgeId;
        session.HangupCause = HangupCause;
        session.DialingAt = DialingAt;
        session.RingingAt = RingingAt;
        session.QueuedAt = QueuedAt;
        session.ConnectedAt = ConnectedAt;
        session.CompletedAt = CompletedAt;
        session._holdStartedAt = HoldStartedAt;
        session._accumulatedHoldTime = AccumulatedHoldTime;

        foreach (var p in Participants)
            session.AddParticipant(p);
        foreach (var e in Events)
            session.AddEvent(e);
        foreach (var kv in Metadata)
            session.SetMetadata(kv.Key, kv.Value);

        return session;
    }
}
