using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("NewConnectedLine")]
public sealed class NewConnectedLineEvent : ManagerEvent
{
    public string? Language { get; set; }
    public string? Channel { get; set; }
    public string? AccountCode { get; set; }
    public string? LinkedId { get; set; }
}

