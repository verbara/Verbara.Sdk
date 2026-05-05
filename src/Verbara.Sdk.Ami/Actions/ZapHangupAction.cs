using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("ZapHangup")]
public sealed class ZapHangupAction : ManagerAction
{
    public int? ZapChannel { get; set; }
}

