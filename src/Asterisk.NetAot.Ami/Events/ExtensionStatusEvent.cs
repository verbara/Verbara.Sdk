using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("ExtensionStatus")]
public sealed class ExtensionStatusEvent : ManagerEvent
{
    public string? Hint { get; set; }
    public int? Status { get; set; }
    public string? CallerId { get; set; }
    public string? Statustext { get; set; }
}

