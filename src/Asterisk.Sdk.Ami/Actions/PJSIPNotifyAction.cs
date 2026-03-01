using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("PJSIPNotify")]
public sealed class PJSIPNotifyAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? Endpoint { get; set; }
    public string? Uri { get; set; }
}

