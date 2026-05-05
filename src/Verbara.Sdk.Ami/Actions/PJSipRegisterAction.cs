using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("PJSIPRegister")]
public sealed class PJSipRegisterAction : ManagerAction
{
    public string? Registration { get; set; }
}
