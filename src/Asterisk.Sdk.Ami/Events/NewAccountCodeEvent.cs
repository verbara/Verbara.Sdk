using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("NewAccountCode")]
public sealed class NewAccountCodeEvent : ManagerEvent
{
    public string? Channel { get; set; }
    public string? AccountCode { get; set; }
    public string? OldAccountCode { get; set; }
    public string? Language { get; set; }
    public string? LinkedId { get; set; }
}

