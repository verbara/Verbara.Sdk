namespace Asterisk.Sdk.Sessions;

public sealed record CallSessionEvent(
    DateTimeOffset Timestamp,
    CallSessionEventType Type,
    string? SourceChannel,
    string? TargetChannel,
    string? Detail);

public enum CallSessionEventType
{
    Created, Dialing, Ringing, Connected,
    Hold, Unhold, Transfer, Conference,
    ParticipantJoined, ParticipantLeft,
    QueueJoined, AgentConnected,
    Completed, Failed, TimedOut
}
