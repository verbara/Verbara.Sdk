using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("PJSIPNotify")]
public sealed class PJSIPNotifyAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? Endpoint { get; set; }
    public string? Uri { get; set; }
}

