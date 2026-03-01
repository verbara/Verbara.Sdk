using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("DongleNewSMSBase64")]
public sealed class DongleNewSMSBase64Event : ManagerEvent
{
    public string? Device { get; set; }
    public string? From { get; set; }
    public string? Message { get; set; }
}

