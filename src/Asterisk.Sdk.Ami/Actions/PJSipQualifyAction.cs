using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("PJSIPQualify")]
public sealed class PJSipQualifyAction : ManagerAction
{
    public string? Endpoint { get; set; }
}
