using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("DongleNewCMGR")]
public sealed class DongleNewCMGREvent : ManagerEvent
{
    public string? Device { get; set; }
}

