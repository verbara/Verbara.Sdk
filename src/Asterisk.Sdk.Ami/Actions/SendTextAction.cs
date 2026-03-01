using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("SendText")]
public sealed class SendTextAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? Message { get; set; }
}

