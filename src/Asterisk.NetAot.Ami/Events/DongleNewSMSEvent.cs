using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("DongleNewSMS")]
public sealed class DongleNewSMSEvent : ManagerEvent
{
    public string? Device { get; set; }
    public string? From { get; set; }
    public string? Linecount { get; set; }
    public string? Messageline0 { get; set; }
}

