using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("PJSIPUnregister")]
public sealed class PJSipUnregisterAction : ManagerAction
{
    public string? Registration { get; set; }
}
