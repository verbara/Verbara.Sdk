using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("ChallengeSent")]
public sealed class ChallengeSentEvent : ManagerEvent
{
    public string? Severity { get; set; }
    public int? EventVersion { get; set; }
    public string? AccountId { get; set; }
    public string? Service { get; set; }
    public string? Eventtv { get; set; }
    public string? RemoteAddress { get; set; }
    public string? LocalAddress { get; set; }
    public string? Challenge { get; set; }
    public string? SessionId { get; set; }
    public string? Module { get; set; }
    public string? Sessiontv { get; set; }
}

