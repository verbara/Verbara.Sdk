using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("SipNotify")]
public sealed class SipNotifyAction : ManagerAction
{
    public string? Channel { get; set; }
}

