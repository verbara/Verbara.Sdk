using Asterisk.Sdk.Enums;

namespace Asterisk.Sdk.Sessions;

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
}

public enum ParticipantRole { Caller, Destination, Agent, Transfer, Conference, Internal }
