using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("DialEnd")]
public sealed class DialEndEvent : ManagerEvent
{
    public string? Language { get; set; }
    public string? DestLanguage { get; set; }
    public string? AccountCode { get; set; }
    public string? DestAccountCode { get; set; }
    public string? DestLinkedId { get; set; }
    public string? LinkedId { get; set; }
    public string? Forward { get; set; }
}

