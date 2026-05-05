using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("AGI")]
public sealed class AgiAction : ManagerAction, IEventGeneratingAction
{
    public string? Channel { get; set; }
    public string? Command { get; set; }
    public string? CommandId { get; set; }
}

