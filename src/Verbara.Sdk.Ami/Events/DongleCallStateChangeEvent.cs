using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("DongleCallStateChange")]
public sealed class DongleCallStateChangeEvent : ManagerEvent
{
    public string? Device { get; set; }
    public string? Callidx { get; set; }
    public string? Newstate { get; set; }
}

