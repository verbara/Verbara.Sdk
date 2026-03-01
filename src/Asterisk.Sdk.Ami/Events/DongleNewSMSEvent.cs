using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("DongleNewSMS")]
public sealed class DongleNewSMSEvent : ManagerEvent
{
    public string? Device { get; set; }
    public string? From { get; set; }
    public string? Linecount { get; set; }
    public string? Messageline0 { get; set; }
}

