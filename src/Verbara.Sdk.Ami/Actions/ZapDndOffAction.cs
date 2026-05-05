using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("ZapDNDOff")]
public sealed class ZapDndOffAction : ManagerAction
{
    public int? ZapChannel { get; set; }
}

