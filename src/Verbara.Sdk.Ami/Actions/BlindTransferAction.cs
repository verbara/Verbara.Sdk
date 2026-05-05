using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("BlindTransfer")]
public sealed class BlindTransferAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? Context { get; set; }
    public string? Exten { get; set; }
}
