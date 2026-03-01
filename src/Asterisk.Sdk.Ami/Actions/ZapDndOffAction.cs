using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("ZapDNDOff")]
public sealed class ZapDndOffAction : ManagerAction
{
    public int? ZapChannel { get; set; }
}

