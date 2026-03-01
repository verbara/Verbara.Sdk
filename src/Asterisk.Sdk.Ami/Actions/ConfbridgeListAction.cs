using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("ConfbridgeList")]
public sealed class ConfbridgeListAction : ManagerAction, IEventGeneratingAction
{
    public string? Conference { get; set; }
}

