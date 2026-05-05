using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("CancelAtxfer")]
public sealed class CancelAtxferAction : ManagerAction
{
    public string? Channel { get; set; }
}
