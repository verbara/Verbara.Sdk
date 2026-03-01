using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("SipNotify")]
public sealed class SipNotifyAction : ManagerAction
{
    public string? Channel { get; set; }
}

