using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("SendText")]
public sealed class SendTextAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? Message { get; set; }
}

