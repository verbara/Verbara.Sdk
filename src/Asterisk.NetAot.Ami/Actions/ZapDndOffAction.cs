using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("ZapDNDOff")]
public sealed class ZapDndOffAction : ManagerAction
{
    public int? ZapChannel { get; set; }
}

