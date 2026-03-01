using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("DongleSendSMS")]
public sealed class DongleSendSMSAction : ManagerAction
{
    public string? Device { get; set; }
    public string? Number { get; set; }
    public string? Message { get; set; }
}

