using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("DongleStatus")]
public sealed class DongleStatusEvent : ManagerEvent
{
    public string? Device { get; set; }
    public string? Status { get; set; }
}

