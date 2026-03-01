using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("SuccessfulAuth")]
public sealed class SuccessfulAuthEvent : ManagerEvent
{
    public string? Severity { get; set; }
    public int? EventVersion { get; set; }
    public string? AccountId { get; set; }
    public int? UsingPassword { get; set; }
    public string? Sessiontv { get; set; }
    public string? Service { get; set; }
    public string? Eventtv { get; set; }
    public string? RemoteAddress { get; set; }
    public string? LocalAddress { get; set; }
    public string? SessionId { get; set; }
    public string? Module { get; set; }
}

