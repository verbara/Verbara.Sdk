using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("DongleStatus")]
public sealed class DongleStatusEvent : ManagerEvent
{
    public string? Device { get; set; }
    public string? Status { get; set; }
}

