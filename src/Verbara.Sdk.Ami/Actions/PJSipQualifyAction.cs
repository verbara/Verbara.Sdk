using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("PJSIPQualify")]
public sealed class PJSipQualifyAction : ManagerAction
{
    public string? Endpoint { get; set; }
}
