using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("BridgeInfo")]
public sealed class BridgeInfoAction : ManagerAction, IEventGeneratingAction
{
    public string? BridgeUniqueid { get; set; }
}
