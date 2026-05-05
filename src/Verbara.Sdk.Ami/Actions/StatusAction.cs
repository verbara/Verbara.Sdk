using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("Status")]
public sealed class StatusAction : ManagerAction, IEventGeneratingAction
{
    public string? Channel { get; set; }
    public string? Variables { get; set; }
}

