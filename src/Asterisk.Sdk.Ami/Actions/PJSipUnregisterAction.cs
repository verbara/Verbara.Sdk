using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("PJSIPUnregister")]
public sealed class PJSipUnregisterAction : ManagerAction
{
    public string? Registration { get; set; }
}
