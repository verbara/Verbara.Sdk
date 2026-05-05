using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("ConfbridgeList")]
public sealed class ConfbridgeListAction : ManagerAction, IEventGeneratingAction
{
    public string? Conference { get; set; }
}

