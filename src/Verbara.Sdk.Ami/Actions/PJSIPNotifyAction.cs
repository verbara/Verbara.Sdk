using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("PJSIPNotify")]
public sealed class PJSIPNotifyAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? Endpoint { get; set; }
    public string? Uri { get; set; }
}

