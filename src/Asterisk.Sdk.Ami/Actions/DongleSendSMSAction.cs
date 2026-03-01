using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("DongleSendSMS")]
public sealed class DongleSendSMSAction : ManagerAction
{
    public string? Device { get; set; }
    public string? Number { get; set; }
    public string? Message { get; set; }
}

