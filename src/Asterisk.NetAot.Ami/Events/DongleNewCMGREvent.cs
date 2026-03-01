using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("DongleNewCMGR")]
public sealed class DongleNewCMGREvent : ManagerEvent
{
    public string? Device { get; set; }
}

