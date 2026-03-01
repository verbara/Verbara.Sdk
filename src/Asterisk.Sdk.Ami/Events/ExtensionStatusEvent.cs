using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("ExtensionStatus")]
public sealed class ExtensionStatusEvent : ManagerEvent
{
    public string? Hint { get; set; }
    public int? Status { get; set; }
    public string? CallerId { get; set; }
    public string? Statustext { get; set; }
}

