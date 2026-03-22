using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("BridgeTechnologyUnsuspend")]
public sealed class BridgeTechnologyUnsuspendAction : ManagerAction
{
    public string? BridgeTechnology { get; set; }
}
