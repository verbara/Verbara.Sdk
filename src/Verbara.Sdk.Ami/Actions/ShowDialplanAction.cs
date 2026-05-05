using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("ShowDialplan")]
public sealed class ShowDialplanAction : ManagerAction, IEventGeneratingAction
{
    public string? Context { get; set; }
}

