namespace Verbara.Sdk.Sessions;

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
    Completed, Failed,

    /// <summary>
    /// Not produced: the SDK records no event of this type since 2.7.0, since it ends no call in
    /// <see cref="CallSessionState.TimedOut"/>.
    /// <para>Kept so that stored rows written by earlier versions keep binding.</para>
    /// </summary>
    TimedOut
}
