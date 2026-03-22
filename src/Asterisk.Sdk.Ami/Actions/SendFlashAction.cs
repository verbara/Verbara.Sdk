using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("Flash")]
public sealed class SendFlashAction : ManagerAction
{
    public string? Channel { get; set; }
}
