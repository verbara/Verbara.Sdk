using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("Flash")]
public sealed class SendFlashAction : ManagerAction
{
    public string? Channel { get; set; }
}
