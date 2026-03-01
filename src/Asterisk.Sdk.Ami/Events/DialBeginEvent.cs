using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("DialBegin")]
public sealed class DialBeginEvent : ManagerEvent
{
    public string? Language { get; set; }
    public string? DestLanguage { get; set; }
    public string? DestAccountCode { get; set; }
    public string? LinkedId { get; set; }
    public string? DestLinkedId { get; set; }
}

