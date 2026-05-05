using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("ChallengeResponseFailed")]
public sealed class ChallengeResponseFailedEvent : ManagerEvent
{
    public string? Severity { get; set; }
    public string? Eventversion { get; set; }
    public string? Service { get; set; }
    public string? RemoteAddress { get; set; }
    public string? LocalAddress { get; set; }
    public string? AccountId { get; set; }
    public string? Module { get; set; }
    public string? Sessiontv { get; set; }
    public string? SessionId { get; set; }
    public string? Challange { get; set; }
    public string? Response { get; set; }
    public string? ExpectedResponse { get; set; }
}

