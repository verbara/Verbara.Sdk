using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("DongleCallStateChange")]
public sealed class DongleCallStateChangeEvent : ManagerEvent
{
    public string? Device { get; set; }
    public string? Callidx { get; set; }
    public string? Newstate { get; set; }
}

