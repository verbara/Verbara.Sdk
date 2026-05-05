using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("DongleNewSMSBase64")]
public sealed class DongleNewSMSBase64Event : ManagerEvent
{
    public string? Device { get; set; }
    public string? From { get; set; }
    public string? Message { get; set; }
}

