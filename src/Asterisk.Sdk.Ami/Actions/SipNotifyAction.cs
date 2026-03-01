using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("SipNotify")]
public sealed class SipNotifyAction : ManagerAction
{
    public string? Channel { get; set; }
}

