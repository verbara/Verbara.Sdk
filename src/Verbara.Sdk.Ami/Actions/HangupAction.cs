using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("Hangup")]
public sealed class HangupAction : ManagerAction, IEventGeneratingAction
{
    public string? Channel { get; set; }
    public int? Cause { get; set; }
}

