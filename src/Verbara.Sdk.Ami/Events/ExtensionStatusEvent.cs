using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("ExtensionStatus")]
public sealed class ExtensionStatusEvent : ManagerEvent
{
    public string? Hint { get; set; }
    public int? Status { get; set; }
    public string? CallerId { get; set; }
    public string? Statustext { get; set; }
}

