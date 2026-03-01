using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("DongleCallStateChange")]
public sealed class DongleCallStateChangeEvent : ManagerEvent
{
    public string? Device { get; set; }
    public string? Callidx { get; set; }
    public string? Newstate { get; set; }
}

