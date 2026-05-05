using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("ZapDNDOn")]
public sealed class ZapDndOnAction : ManagerAction
{
    public int? ZapChannel { get; set; }
}

