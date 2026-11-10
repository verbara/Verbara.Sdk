using Verbara.Sdk.Enums;

namespace Verbara.Sdk.Sessions;

public sealed class SessionParticipant
{
    public required string UniqueId { get; init; }
    public required string Channel { get; init; }
    public required string Technology { get; init; }
    public ParticipantRole Role { get; set; }
    public string? CallerIdNum { get; set; }
    public string? CallerIdName { get; set; }
    public DateTimeOffset JoinedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LeftAt { get; set; }
    public HangupCause? HangupCause { get; set; }

    /// <summary>
    /// Whether a decision of the call found this leg's channel being removed from its server's table: the proof that
    /// it left before every departure the call recorded afterwards. Internal, never persisted.
    /// </summary>
    internal bool FoundDeparting { get; set; }
}

public enum ParticipantRole { Caller, Destination, Agent, Transfer, Conference, Internal }
