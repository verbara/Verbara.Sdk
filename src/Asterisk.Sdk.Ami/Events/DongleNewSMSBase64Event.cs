using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("DongleNewSMSBase64")]
public sealed class DongleNewSMSBase64Event : ManagerEvent
{
    public string? Device { get; set; }
    public string? From { get; set; }
    public string? Message { get; set; }
}

