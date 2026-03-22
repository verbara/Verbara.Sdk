using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("BridgeTechnologySuspend")]
public sealed class BridgeTechnologySuspendAction : ManagerAction
{
    public string? BridgeTechnology { get; set; }
}
