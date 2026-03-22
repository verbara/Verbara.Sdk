using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("PJSIPRegister")]
public sealed class PJSipRegisterAction : ManagerAction
{
    public string? Registration { get; set; }
}
