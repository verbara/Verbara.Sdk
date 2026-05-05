using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("DongleSendSMS")]
public sealed class DongleSendSMSAction : ManagerAction
{
    public string? Device { get; set; }
    public string? Number { get; set; }
    public string? Message { get; set; }
}

